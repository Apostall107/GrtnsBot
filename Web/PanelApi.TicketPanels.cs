using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>Панели подпроектов: отдельные сообщения с кнопками тикет-систем одного проекта.</summary>
public static partial class PanelApi
{
    public sealed record TicketPanelBody(string? Title, string? Text, string? Color, string? ProjectKey, List<string>? TicketKeys);

    private static object TicketPanelDto(SocketGuild? guild, TicketPanel p) => new
    {
        id = p.Id, title = p.Title, text = p.Text, color = p.Color, projectKey = p.ProjectKey, ticketKeys = p.TicketKeys,
        channelId = p.ChannelId?.ToString(),
        channel = p.ChannelId is { } c ? guild?.GetTextChannel(c)?.Name : null,
        jumpUrl = p.ChannelId is { } ch && p.MessageId is { } m ? $"https://discord.com/channels/{p.GuildId}/{ch}/{m}" : null
    };

    private static void MapTicketPanels(RouteGroupBuilder g)
    {
        g.MapGet("/panels", (ulong guildId, DiscordSocketClient client, TicketStore tickets,
            [FromServices] TicketPanelStore panels, [FromServices] LfgProjectStore lfgProjects) =>
        {
            var guild = client.GetGuild(guildId);
            return Results.Ok(new
            {
                panels = panels.Get(guildId).Select(p => TicketPanelDto(guild, p)),
                types = tickets.GetProjects(guildId).Select(p => new { key = p.Key, name = p.Name, groupProjectKey = p.GroupProjectKey }),
                groups = lfgProjects.GetProjects(guildId).Select(p => new { key = p.Key, name = p.Name })
            });
        });

        g.MapPost("/panels", (ulong guildId, TicketPanelBody body, DiscordSocketClient client, [FromServices] TicketPanelStore panels) =>
        {
            var panel = new TicketPanel
            {
                GuildId = guildId,
                Title = string.IsNullOrWhiteSpace(body.Title) ? "Tickets" : Tx.Clip(body.Title.Trim(), 256),
                ProjectKey = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey
            };
            if (panels.Save(panel) is { } error) return Error(error);
            return Results.Ok(TicketPanelDto(client.GetGuild(guildId), panel));
        });

        g.MapPut("/panels/{id}", async (ulong guildId, string id, TicketPanelBody body, DiscordSocketClient client, TicketStore tickets,
            [FromServices] TicketPanelStore panels, [FromServices] TicketPanelService service) =>
        {
            if (panels.Find(id) is not { } panel || panel.GuildId != guildId) return Error("This panel no longer exists.");
            var existing = tickets.GetProjects(guildId).Select(p => p.Key).ToHashSet();
            var keys = (body.TicketKeys ?? []).Where(existing.Contains).Distinct().ToList();
            if (keys.Count > TicketPanel.MaxTypes) return Error($"Up to {TicketPanel.MaxTypes} ticket types per panel (Discord: 25 buttons per message).");
            if (body.Text is { Length: > 4096 }) return Error("The text is limited to 4096 characters.");
            panel.Title = string.IsNullOrWhiteSpace(body.Title) ? panel.Title : Tx.Clip(body.Title.Trim(), 256);
            panel.Text = string.IsNullOrWhiteSpace(body.Text) ? null : body.Text.Trim();
            panel.Color = string.IsNullOrWhiteSpace(body.Color) ? null : body.Color.Trim();
            panel.ProjectKey = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey;
            panel.TicketKeys = keys;
            if (panels.Save(panel) is { } error) return Error(error);
            await service.RefreshAsync(panel); // уже опубликована — сообщение обновится на месте
            return Results.Ok(TicketPanelDto(client.GetGuild(guildId), panels.Find(id)!));
        });

        g.MapPost("/panels/{id}/post", async (ulong guildId, string id, ChannelBody body,
            [FromServices] TicketPanelStore panels, [FromServices] TicketPanelService service) =>
        {
            if (panels.Find(id) is not { } panel || panel.GuildId != guildId) return Error("This panel no longer exists.");
            if (ParseId(body.ChannelId) is not { } channelId) return Error("Choose a channel.");
            if (await service.PostAsync(panel, channelId) is { } error) return Error(error);
            return Results.Ok(new { ok = true });
        });

        g.MapDelete("/panels/{id}", async (ulong guildId, string id, [FromServices] TicketPanelStore panels, [FromServices] TicketPanelService service) =>
        {
            if (panels.Find(id) is not { } panel || panel.GuildId != guildId) return Error("This panel no longer exists.");
            if (panel.ChannelId is { } c && panel.MessageId is { } m) await service.DeleteAsync(guildId, c, m);
            panels.Delete(id);
            return Results.Ok(new { ok = true });
        });
    }
}
