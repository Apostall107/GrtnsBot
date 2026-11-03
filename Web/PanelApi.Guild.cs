using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Гильдия: настройки LFG (готовность, автозамена, напоминание о GS), ЛС бота, посещаемость, календарь, ростер, неактивные.
/// </summary>
public static partial class PanelApi
{
    private static void MapGuildTools(RouteGroupBuilder lfg, RouteGroupBuilder dm)
    {
        // ----- Настройки LFG сервера -----
        lfg.MapGet("/prefs", (ulong guildId, DiscordSocketClient client, [FromServices] GuildPrefsStore prefs) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var p = prefs.Get(guildId);
            return Results.Ok(new
            {
                readyCheck = p.ReadyCheck,
                autoPromoteMinutes = p.AutoPromoteMinutes,
                gsReminder = p.GsReminder,
                gsReminderChannelId = p.GsReminderChannelId?.ToString(),
                gsReminderDays = p.GsReminderDays,
                gsReminderLastRun = p.GsReminderLastRun?.ToUnixTimeMilliseconds(),
                gsReminderDm = p.Dm.GsReminder
            });
        });

        lfg.MapPut("/prefs", (ulong guildId, LfgPrefsBody body, DiscordSocketClient client, [FromServices] GuildPrefsStore prefs) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (body.AutoPromoteMinutes is < 0 or > 1440) return Error("Auto-replace: 0–1440 minutes.");
            if (body.GsReminderDays is < 1 or > 365) return Error("GS is outdated after 1–365 days.");
            ulong? channel = ParseId(body.GsReminderChannelId) is { } id && guild.GetTextChannel(id) is not null ? id : null;
            prefs.Update(guildId, p =>
            {
                p.ReadyCheck = body.ReadyCheck;
                p.AutoPromoteMinutes = body.AutoPromoteMinutes ?? p.AutoPromoteMinutes;
                p.GsReminder = body.GsReminder;
                p.GsReminderChannelId = channel;
                p.GsReminderDays = body.GsReminderDays ?? p.GsReminderDays;
                p.Dm.GsReminder = body.GsReminderDm;
            });
            return Results.Ok();
        });

        // Разослать напоминание о GS прямо сейчас
        lfg.MapPost("/gs-reminder/run", async (ulong guildId, [FromServices] GsReminderService reminder) =>
            Results.Ok(new { members = await reminder.RunAsync(guildId) }));

        // ----- Посещаемость: по людям за период (из истории закрытых сборов) -----
        lfg.MapGet("/attendance", (ulong guildId, string? project, int? days, DiscordSocketClient client,
            [FromServices] LfgHistoryStore history, [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 3650));
            var def = projects.DefaultKey(guildId);
            var entries = history.Get(guildId, since)
                .Where(e => string.IsNullOrEmpty(project) || (e.ProjectKey ?? def) == project)
                .ToList();
            var rows = entries
                .SelectMany(e => e.Members.Select(m => (e, m)))
                .GroupBy(x => x.m.UserId)
                .Select(g => new
                {
                    member = Member(guild, g.Key),
                    groups = g.Count(),
                    came = g.Count(x => x.m.Attended == true),
                    noShow = g.Count(x => x.m.Attended == false),
                    unmarked = g.Count(x => x.m.Attended is null),
                    organized = entries.Count(e => e.OwnerId == g.Key),
                    last = g.Max(x => (x.e.StartsAt ?? x.e.ClosedAt).ToUnixTimeMilliseconds()),
                    roles = g.GroupBy(x => x.m.Role).OrderByDescending(r => r.Count()).Select(r => new { role = r.Key, count = r.Count() })
                })
                .OrderByDescending(x => x.came).ThenByDescending(x => x.groups)
                .ToList();
            return Results.Ok(new { groups = entries.Count, marked = entries.Count(e => e.Members.Any(m => m.Attended is not null)), rows });
        });

        // ----- Календарь: сборы и регулярные сборы на 7 дней вперёд -----
        lfg.MapGet("/calendar", (ulong guildId, DiscordSocketClient client, [FromServices] LfgStore store,
            [FromServices] RecurringLfgStore recurring, [FromServices] TimeService time, [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var names = projects.GetProjects(guildId).ToDictionary(p => p.Key, p => p.Name);
            string? ProjectName(string? key) => key is not null && names.TryGetValue(key, out var n) ? n : null;
            var now = DateTimeOffset.UtcNow;
            var until = now.AddDays(8);

            var groups = store.Groups.Values.Where(g => g.GuildId == guildId && !g.IsClosed && g.StartsAt is { } s && s >= now.AddHours(-6) && s <= until)
                .Select(g => new
                {
                    id = g.Id,
                    title = g.ActivityName,
                    start = g.StartsAt!.Value.ToUnixTimeMilliseconds(),
                    project = ProjectName(g.ProjectKey),
                    channel = guild.GetChannel(g.ChannelId)?.Name,
                    filled = g.FilledCount,
                    total = g.Slots.Count,
                    reserve = g.Reserve.Count,
                    organizer = Member(guild, g.OwnerId),
                    note = g.Note,
                    jumpUrl = LfgSignupService.JumpUrl(g),
                    recurring = false
                }).ToList<object>();

            var posted = store.Groups.Values.Where(g => g.GuildId == guildId && g.StartsAt is not null)
                .Select(g => (g.ActivityName, g.StartsAt!.Value)).ToHashSet();
            foreach (var r in recurring.Get(guildId).Where(r => r.Enabled))
            {
                for (var i = 0; i < 8; i++)
                {
                    var date = time.Today.AddDays(i);
                    if (!r.Days.Contains(date.DayOfWeek)) continue;
                    var start = time.ToUtc(date, r.Hour, r.Minute);
                    if (start < now || start > until || posted.Contains((r.ActivityName, start)) || r.LastStart == start) continue;
                    groups.Add(new
                    {
                        id = $"{r.Id}:{start.ToUnixTimeSeconds()}",
                        title = r.ActivityName,
                        start = start.ToUnixTimeMilliseconds(),
                        project = ProjectName(r.ProjectKey),
                        channel = guild.GetChannel(r.ChannelId)?.Name,
                        filled = 0,
                        total = r.Slots.Count,
                        reserve = 0,
                        organizer = Member(guild, r.OwnerId),
                        note = r.Note,
                        jumpUrl = (string?)null,
                        recurring = true
                    });
                }
            }
            return Results.Ok(new { zone = time.ZoneName, events = groups });
        });

        // ----- Ростер гильдии: персонажи, классы, роли в группе, GS, на сервере с, последняя активность -----
        lfg.MapGet("/roster", (ulong guildId, DiscordSocketClient client, [FromServices] CharacterStore characters,
            [FromServices] LfgProjectStore projects, [FromServices] MemberActivityStore activity) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var def = projects.DefaultKey(guildId);
            var list = characters.All(guildId)
                .Select(c =>
                {
                    var user = guild.GetUser(c.UserId);
                    var projectKey = c.Scope ?? def;
                    var classId = c.Info?.ClassRoleId;
                    var slotRoles = classId is { } id && projects.EffectiveClassSlotRoles(guildId, projectKey).TryGetValue(id, out var sr)
                        ? sr.Select(r => r.ToString()).ToList() : [];
                    var act = activity.Get(guildId, c.UserId);
                    return new
                    {
                        member = Member(guild, c.UserId),
                        onServer = user is not null,
                        character = c.Name,
                        project = projectKey,
                        classRole = classId is { } cid ? Role(guild, cid) : null,
                        classIcon = classId is { } ci ? projects.ClassIcon(guildId, guild.GetRole(ci), projectKey) : null,
                        slotRoles,
                        gs = c.Info?.Gs,
                        gsUpdated = c.Info?.GsUpdatedAt?.ToUnixTimeMilliseconds(),
                        joined = user?.JoinedAt?.ToUnixTimeMilliseconds(),
                        lastActive = act?.Last?.ToUnixTimeMilliseconds()
                    };
                })
                .OrderBy(x => x.character, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Results.Ok(new
            {
                projects = projects.GetProjects(guildId).Select(p => new { key = p.Key, name = p.Name, isDefault = p.Key == def }),
                characters = list,
                trackingSince = activity.TrackingSince.ToUnixTimeMilliseconds()
            });
        });

        // ----- Неактивные: кто N дней не писал и не заходил в войс -----
        lfg.MapGet("/inactive", (ulong guildId, int? days, string? roleId, DiscordSocketClient client,
            [FromServices] MemberActivityStore activity, [FromServices] CharacterStore characters) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var limit = Math.Clamp(days ?? 14, 1, 365);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-limit);
            var role = ParseId(roleId);
            var charCount = characters.All(guildId).GroupBy(c => c.UserId).ToDictionary(g => g.Key, g => g.Count());
            var rows = guild.Users.Where(u => !u.IsBot && (role is null || u.Roles.Any(r => r.Id == role)))
                .Select(u => (u, a: activity.Get(guildId, u.Id)))
                .Where(x => (x.a?.Last ?? DateTimeOffset.MinValue) < cutoff)
                .OrderBy(x => x.a?.Last ?? DateTimeOffset.MinValue).ThenBy(x => x.u.JoinedAt)
                .Select(x => new
                {
                    member = Member(guild, x.u.Id),
                    joined = x.u.JoinedAt?.ToUnixTimeMilliseconds(),
                    lastMessage = x.a?.LastMessage?.ToUnixTimeMilliseconds(),
                    lastVoice = x.a?.LastVoice?.ToUnixTimeMilliseconds(),
                    roles = x.u.Roles.Where(r => !r.IsEveryone).OrderByDescending(r => r.Position).Take(3).Select(r => Role(guild, r.Id)),
                    characters = charCount.TryGetValue(x.u.Id, out var n) ? n : 0
                })
                .ToList();
            return Results.Ok(new
            {
                trackingSince = activity.TrackingSince.ToUnixTimeMilliseconds(),
                members = guild.Users.Count(u => !u.IsBot),
                rows
            });
        });

        // ----- ЛС бота (Settings): по умолчанию всё выключено -----
        dm.MapGet("", (ulong guildId, [FromServices] GuildPrefsStore prefs) => Results.Ok(prefs.Dm(guildId)));
        dm.MapPut("", (ulong guildId, DmPrefs body, [FromServices] GuildPrefsStore prefs) =>
        {
            prefs.Update(guildId, p => p.Dm = body);
            return Results.Ok();
        });
    }
}

public sealed record LfgPrefsBody(bool ReadyCheck, int? AutoPromoteMinutes, bool GsReminder, string? GsReminderChannelId, int? GsReminderDays,
    bool GsReminderDm);
