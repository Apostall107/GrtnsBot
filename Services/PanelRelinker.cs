using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Finds the bot's own panels in the channels and remembers them again when their record was lost
/// (e.g. the bot couldn't see the server for a while): module panels (LFG / tickets / rooms), role panels
/// (by their "rp:…:{panelId}" buttons and menus) and ticket panels (by the exact set of their ticket buttons).
/// Only panels that are NOT marked as posted are linked, so nothing that is already known is touched.
/// </summary>
public sealed class PanelRelinker(DiscordSocketClient client, PanelService panels, CommunityStore roles,
    TicketPanelStore ticketPanels, TicketPanelService tickets, ChannelPageStore pages, AuditLog audit, ILogger<PanelRelinker> logger)
{
    public async Task<Dictionary<string, int>> RelinkAsync(SocketGuild guild, int messagesPerChannel = 50)
    {
        var found = new Dictionary<string, int>();
        var lostRoles = roles.GetPanels(guild.Id).Where(p => p.MessageId is null).ToDictionary(p => p.Id);
        // Role panels placed on a channel page: the page still knows their message — the most reliable source
        foreach (var page in pages.Get(guild.Id).Where(p => p.ChannelId is not null))
        foreach (var b in page.Blocks.Where(b => b.Type == "rolepanel" && b.RolePanelId is not null && b.MessageIds.Count > 0))
        {
            if (!lostRoles.ContainsKey(b.RolePanelId!) || guild.GetTextChannel(page.ChannelId!.Value) is not { } ch) continue;
            try
            {
                if (await ch.GetMessageAsync(b.MessageIds[0]) is not IUserMessage m || m.Author.Id != client.CurrentUser.Id) continue;
                roles.SetPosted(b.RolePanelId!, ch.Id, m.Id);
                lostRoles.Remove(b.RolePanelId!);
                found["roles"] = found.GetValueOrDefault("roles") + 1;
            }
            catch (Discord.Net.HttpException) { /* no access — the scan below may still find it */ }
        }
        var lostTickets = ticketPanels.Get(guild.Id).Where(p => p.MessageId is null)
            .Select(p => (Panel: p, Ids: PanelService.CustomIds(tickets.Render(p).Components.Components)))
            .Where(x => x.Ids.Count > 0).ToList();
        var me = guild.CurrentUser;

        foreach (var channel in guild.TextChannels.Where(c => c is not SocketThreadChannel))
        {
            var perms = me.GetPermissions(channel);
            if (!perms.ViewChannel || !perms.ReadMessageHistory) continue;
            try
            {
                var messages = await channel.GetMessagesAsync(messagesPerChannel).FlattenAsync();
                foreach (var message in messages.OfType<IUserMessage>().Where(m => m.Author.Id == client.CurrentUser.Id))
                {
                    var ids = PanelService.CustomIds(message);
                    if (ids.Count == 0) continue;
                    panels.Consider(guild.Id, channel.Id, message.Id, ids, found);

                    // role panel: rp:s:{id}, rp:b:{id}:{binding}, rp:my:{id}
                    foreach (var id in ids.Where(i => i.StartsWith("rp:")).Select(i => i.Split(':')).Where(p => p.Length >= 3).Select(p => p[2]).Distinct())
                        if (lostRoles.Remove(id))
                        {
                            roles.SetPosted(id, channel.Id, message.Id);
                            found["roles"] = found.GetValueOrDefault("roles") + 1;
                        }

                    // ticket panel: the same set of "ticket:open:{type}" buttons
                    var match = lostTickets.FirstOrDefault(x => x.Ids.SetEquals(ids));
                    if (match.Panel is not null)
                    {
                        lostTickets.Remove(match);
                        ticketPanels.SetPosted(match.Panel.Id, channel.Id, message.Id);
                        found["ticket panels"] = found.GetValueOrDefault("ticket panels") + 1;
                    }
                }
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogDebug(ex, "Can't read history of #{Channel}", channel.Name);
            }
        }

        if (found.Values.Sum() > 0)
            audit.FromSystem(guild.Id, "posts", "Panels found in the channels and linked again", string.Join(", ", found.Select(f => $"{f.Key}: {f.Value}")));
        if (found.Values.Sum() > 0)
            logger.LogInformation("Panels linked again on {Guild}: {Found}", guild.Name, string.Join(", ", found.Select(f => $"{f.Key} {f.Value}")));
        return found;
    }
}
