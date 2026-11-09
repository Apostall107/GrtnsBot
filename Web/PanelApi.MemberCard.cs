using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Карточка участника для панели: всё, что бот знает о человеке, одним запросом —
/// персонажи по проектам, ДР, активные группы, посещаемость, тикеты, роли и активность.
/// </summary>
public static partial class PanelApi
{
    private static IEnumerable<RouteHandlerBuilder> MapMemberCard(RouteGroupBuilder g)
    {
        yield return g.MapGet("/members/{userId}/card", (ulong guildId, string userId, DiscordSocketClient client,
            [FromServices] CharacterStore chars, [FromServices] LfgProjectStore projects, [FromServices] LfgStore lfg,
            [FromServices] LfgHistoryStore history, [FromServices] BirthdayStore birthdays, [FromServices] TicketStore tickets,
            [FromServices] TicketArchiveStore archive, [FromServices] MemberActivityStore activity) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (!ulong.TryParse(userId, out var id)) return Error("Unknown member.");
            var user = guild.GetUser(id);

            var projectList = projects.GetProjects(guildId);
            var defKey = projects.DefaultKey(guildId);
            string ProjectName(string? scope) =>
                projectList.FirstOrDefault(p => p.Key == (scope ?? defKey))?.Name ?? (scope ?? "—");

            var characters = chars.All(guildId).Where(c => c.UserId == id).Select(c => new
            {
                name = c.Name,
                project = ProjectName(c.Scope),
                classRole = c.Info?.ClassRoleId is { } cr ? Role(guild, cr) : null,
                gs = c.Info?.Gs,
                cp = c.Info?.Armory is { Error: null } a ? a.CombatPower : null,
                level = c.Info?.Armory is { Error: null } a2 ? a2.Level : (int?)null,
                server = c.Info?.Armory is { Error: null } a3 ? a3.ServerName : null,
                gsUpdatedAt = c.Info?.GsUpdatedAt?.ToUnixTimeMilliseconds()
            }).ToList();

            var groups = lfg.Groups.Values
                .Where(gr => gr.GuildId == guildId && !gr.IsClosed
                    && (gr.OwnerId == id || gr.Slots.Any(s => s.UserId == id) || gr.Reserve.Any(r => r.UserId == id)))
                .OrderBy(gr => gr.StartsAt ?? DateTimeOffset.MaxValue)
                .Select(gr => new
                {
                    id = gr.Id,
                    activity = gr.ActivityName,
                    startsAt = gr.StartsAt?.ToUnixTimeMilliseconds(),
                    organizer = gr.OwnerId == id,
                    reserve = gr.Reserve.Any(r => r.UserId == id),
                    character = gr.Slots.FirstOrDefault(s => s.UserId == id)?.CharacterName
                        ?? gr.Reserve.FirstOrDefault(r => r.UserId == id)?.CharacterName
                }).ToList();

            // Посещаемость за 90 дней (как на странице Guild → Attendance)
            var since = DateTimeOffset.UtcNow.AddDays(-90);
            var stats = LfgStats.Players(history.Get(guildId, since)).FirstOrDefault(p => p.UserId == id);

            var openTickets = tickets.GetTickets(guildId).Where(t => t.UserId == id).Select(t => new
            {
                id = t.Id,
                project = tickets.FindProject(guildId, t.ProjectKey)?.Name ?? t.ProjectKey,
                openedAt = t.CreatedAt.ToUnixTimeMilliseconds()
            }).ToList();
            var pastTickets = archive.Query(guildId, null, null, id, 0, 10).Select(e => new
            {
                project = e.Project,
                outcome = e.Outcome,
                closedAt = e.ClosedAt.ToUnixTimeMilliseconds(),
                by = e.ByName
            }).ToList();

            var bd = birthdays.Of(guildId, id);
            var act = activity.Get(guildId, id);

            return Results.Ok(new
            {
                member = Member(guild, id),
                joinedAt = user?.JoinedAt?.ToUnixTimeMilliseconds(),
                createdAt = (user?.CreatedAt ?? Discord.SnowflakeUtils.FromSnowflake(id)).ToUnixTimeMilliseconds(),
                roles = user is null ? [] : user.Roles.Where(r => !r.IsEveryone).OrderByDescending(r => r.Position).Take(12)
                    .Select(r => Role(guild, r.Id)).ToList(),
                lastMessage = act?.LastMessage?.ToUnixTimeMilliseconds(),
                lastVoice = act?.LastVoice?.ToUnixTimeMilliseconds(),
                voiceNow = user?.VoiceChannel?.Name,
                birthday = bd is null ? null : new { day = bd.Day, month = bd.Month, year = bd.Year },
                characters,
                groups,
                attendance = stats is null ? null : new
                {
                    days = 90, groups = stats.Groups, organized = stats.Organized, attended = stats.Attended,
                    noShows = stats.NoShows, unmarked = stats.Unmarked, rate = stats.Reliability
                },
                openTickets,
                pastTickets
            });
        });
        // «Требует внимания»: тикеты ждут решения, группы скоро стартуют и не набраны, ДР сегодня, неотправленные посты.
        // Каждый пункт помечен модулем — панель показывает только то, на что у человека есть право.
        yield return g.MapGet("/attention", (ulong guildId, DiscordSocketClient client, [FromServices] TicketStore tickets,
            [FromServices] LfgStore lfg, [FromServices] BirthdayService bdays, [FromServices] AnnouncementStore announcements) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var now = DateTimeOffset.UtcNow;
            var waiting = tickets.GetTickets(guildId).OrderBy(t => t.CreatedAt).Select(t => new
            {
                id = t.Id,
                member = Member(guild, t.UserId),
                project = tickets.FindProject(guildId, t.ProjectKey)?.Name ?? t.ProjectKey,
                openedAt = t.CreatedAt.ToUnixTimeMilliseconds()
            }).ToList();
            var soon = lfg.Groups.Values
                .Where(gr => gr.GuildId == guildId && !gr.IsClosed && gr.StartsAt is { } s && s > now.AddMinutes(-15) && s < now.AddHours(3)
                    && gr.FilledCount < gr.Slots.Count)
                .OrderBy(gr => gr.StartsAt)
                .Select(gr => new
                {
                    id = gr.Id, activity = gr.ActivityName, startsAt = gr.StartsAt!.Value.ToUnixTimeMilliseconds(),
                    filled = gr.FilledCount, size = gr.Slots.Count, reserve = gr.Reserve.Count,
                    owner = gr.OwnerId == 0 ? null : Member(guild, gr.OwnerId)
                }).ToList();
            var birthdays = bdays.Celebrants(guildId).Select(u => Member(guild, u.Id)).ToList();
            var failed = announcements.Get(guildId)
                .Where(a => a.Status == "failed" && (a.SentAt ?? a.SendAt ?? a.CreatedAt) > now.AddDays(-3))
                .Select(a => new { id = a.Id, channel = guild.GetChannel(a.ChannelId)?.Name, error = a.Error,
                    at = (a.SentAt ?? a.SendAt ?? a.CreatedAt).ToUnixTimeMilliseconds() }).ToList();
            return Results.Ok(new { tickets = waiting, groups = soon, birthdays, failedPosts = failed });
        });
    }
}
