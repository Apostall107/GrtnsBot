using Discord;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>Только владельцу бота: кто сейчас в каких голосовых каналах (все каналы, включая скрытые от участников).</summary>
public static partial class PanelApi
{
    private static void MapVoiceNow(RouteGroupBuilder g)
    {
        g.MapGet("/", (ulong guildId, DiscordSocketClient client, [FromServices] TempVoiceStore temp) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var everyone = guild.EveryoneRole;
            var channels = guild.VoiceChannels.Cast<SocketVoiceChannel>()
                .Where(c => c.ConnectedUsers.Count > 0)
                .OrderBy(c => c.Category?.Position ?? -1).ThenBy(c => c is SocketStageChannel).ThenBy(c => c.Position)
                .Select(c => new
                {
                    id = c.Id.ToString(),
                    name = c.Name,
                    category = c.Category?.Name,
                    stage = c is SocketStageChannel,
                    // Скрыт от @everyone (видят только по ролям)
                    hidden = c.GetPermissionOverwrite(everyone)?.ViewChannel == PermValue.Deny ||
                             (c.Category as SocketCategoryChannel)?.GetPermissionOverwrite(everyone)?.ViewChannel == PermValue.Deny &&
                             c.GetPermissionOverwrite(everyone)?.ViewChannel != PermValue.Allow,
                    tempRoom = temp.GetRoom(c.Id) is not null,
                    limit = c.UserLimit,
                    jumpUrl = $"https://discord.com/channels/{guildId}/{c.Id}",
                    users = c.ConnectedUsers.OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase).Select(u => new
                    {
                        member = Member(guild, u.Id),
                        bot = u.IsBot,
                        muted = u.IsSelfMuted || u.IsMuted,
                        deafened = u.IsSelfDeafened || u.IsDeafened,
                        serverMuted = u.IsMuted || u.IsDeafened,
                        streaming = u.IsStreaming,
                        video = u.IsVideoing
                    })
                })
                .ToList();
            return Results.Ok(new { total = channels.Sum(c => c.users.Count()), channels });
        });
    }
}
