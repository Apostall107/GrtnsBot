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
    private static void MapGuildTools(RouteGroupBuilder lfg, RouteGroupBuilder guildTools, RouteGroupBuilder dm)
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
                gsReminderDm = p.Dm.GsReminder,
                memberTop = p.MemberTop,
                armoryGs = p.ArmoryGs
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
                p.MemberTop = body.MemberTop;
                if (body.ArmoryGs is "itemLevel" or "combatPower" or "none") p.ArmoryGs = body.ArmoryGs;
            });
            return Results.Ok();
        });

        // ----- Официальная оружейная AION 2: проекты (вкл., фракция и сервер по умолчанию), массовая привязка -----
        lfg.MapGet("/armory", async (ulong guildId, DiscordSocketClient client, [FromServices] GuildPrefsStore prefs,
            [FromServices] LfgProjectStore projects, [FromServices] OfficialArmory armory, [FromServices] ArmorySyncService sync,
            [FromServices] CharacterStore characters, CancellationToken ct) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var def = projects.DefaultKey(guildId);
            var all = characters.All(guildId).Where(c => OnServer(guild, c.UserId)).ToList();
            var list = projects.GetProjects(guildId).Select(p =>
            {
                var a = prefs.ArmoryFor(guildId, p.Key, p.Name);
                var mine = all.Where(c => (c.Scope ?? def) == p.Key).ToList();
                return new
                {
                    key = p.Key, name = p.Name, enabled = a.Enabled, race = a.Race, serverId = a.ServerId,
                    characters = mine.Count, linked = mine.Count(c => c.Info?.Armory is not null)
                };
            });
            return Results.Ok(new { projects = list, servers = await ArmoryServersDto(armory, ct), bulk = BulkDto(sync.BulkStatus(guildId)) });
        });

        lfg.MapPut("/armory", (ulong guildId, ArmoryPrefsBody body, DiscordSocketClient client, [FromServices] GuildPrefsStore prefs,
            [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is null) return GuildNotFound();
            var keys = projects.GetProjects(guildId).Select(p => p.Key).ToHashSet();
            prefs.Update(guildId, p => p.Armory = (body.Projects ?? []).Where(x => x.Key is not null && keys.Contains(x.Key))
                .ToDictionary(x => x.Key!, x => new ArmoryProjectPrefs
                {
                    Enabled = x.Enabled, Race = x.Race is 1 ? 1 : 2, ServerId = x.ServerId is > 0 and < 100000 ? x.ServerId : null
                }));

            return Results.Ok();
        });

        lfg.MapPost("/armory/bulk", (ulong guildId, ArmoryBulkBody body, [FromServices] ArmorySyncService sync, [FromServices] LfgProjectStore projects) =>
        {
            if (sync.Paused) return Error("The official armory isn't answering right now — try again later.");
            var key = string.IsNullOrWhiteSpace(body.Project) ? null : projects.Find(guildId, body.Project)?.Key;
            return sync.StartBulk(guildId, key, body.LinkNew) ? Results.Ok(BulkDto(sync.BulkStatus(guildId))) : Error("Already running.");
        });

        lfg.MapGet("/armory/bulk", (ulong guildId, [FromServices] ArmorySyncService sync) => Results.Ok(BulkDto(sync.BulkStatus(guildId))));

        // Разослать напоминание о GS прямо сейчас
        lfg.MapPost("/gs-reminder/run", async (ulong guildId, [FromServices] GsReminderService reminder) =>
            Results.Ok(new { members = await reminder.RunAsync(guildId) }));

        // ----- Посещаемость: по людям за период (из истории закрытых сборов) -----
        guildTools.MapGet("/attendance", async (ulong guildId, string? project, int? days, DiscordSocketClient client,
            [FromServices] LfgHistoryStore history, [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 3650));
            var def = projects.DefaultKey(guildId);
            var entries = history.Get(guildId, since)
                .Where(e => string.IsNullOrEmpty(project) || (e.ProjectKey ?? def) == project)
                .ToList();
            await WarmMembersAsync(client, guild, entries.SelectMany(e => e.Members.Select(m => m.UserId)));
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
        guildTools.MapGet("/calendar", async (ulong guildId, DiscordSocketClient client, [FromServices] LfgStore store,
            [FromServices] RecurringLfgStore recurring, [FromServices] TimeService time, [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            await WarmMembersAsync(client, guild, CalendarOwners(guildId, store, recurring));
            return Results.Ok(new { zone = time.ZoneName, events = CalendarEvents(guild, store, recurring, time, projects, _ => true, null) });
        });

        // ----- Ростер гильдии: персонажи, классы, роли в группе, GS, на сервере с, последняя активность -----
        guildTools.MapGet("/roster", async (ulong guildId, DiscordSocketClient client, [FromServices] CharacterStore characters,
            [FromServices] LfgProjectStore projects, [FromServices] MemberActivityStore activity) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            await WarmMembersAsync(client, guild, characters.All(guildId).Select(c => c.UserId));
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
                        onServer = OnServer(guild, c.UserId),
                        character = c.Name,
                        project = projectKey,
                        classRole = classId is { } cid ? Role(guild, cid) : null,
                        classIcon = classId is { } ci ? projects.ClassIcon(guildId, guild.GetRole(ci), projectKey) : null,
                        slotRoles,
                        gs = c.Info?.Gs,
                        armory = ArmoryDto(c.Info?.Armory),
                        server = c.Info?.Armory?.ServerName ?? OfficialArmory.ServerName(c.Info?.ServerId),
                        // Фракция по серверу: 1xxx — Elyos, 2xxx — Asmodians
                        race = ArmorySyncService.RaceOfServer(c.Info?.Armory?.ServerId ?? c.Info?.ServerId ?? 0),
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
        guildTools.MapGet("/inactive", (ulong guildId, int? days, string? roleId, DiscordSocketClient client,
            [FromServices] MemberActivityStore activity, [FromServices] CharacterStore characters, [FromServices] BotSettingsStore settings) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            // Только участники гильдии — у кого есть роль участника (Settings → Member roles), т.е. кто может пользоваться сайтом
            var memberRoles = settings.GetMemberRoles(guildId).ToHashSet();
            bool IsMember(SocketGuildUser u) => !u.IsBot && (memberRoles.Count == 0 || u.Roles.Any(r => memberRoles.Contains(r.Id)));
            var limit = Math.Clamp(days ?? 14, 1, 365);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-limit);
            var role = ParseId(roleId);
            var charCount = characters.All(guildId).GroupBy(c => c.UserId).ToDictionary(g => g.Key, g => g.Count());
            var rows = guild.Users.Where(u => IsMember(u) && (role is null || u.Roles.Any(r => r.Id == role)))
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
                members = guild.Users.Count(IsMember),
                memberRoles = memberRoles.Select(id => Role(guild, id)),
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

    /// <summary>
    /// Сборы со временем и повторяющиеся сборы на 7 дней вперёд. <paramref name="allowed"/> — фильтр по ключу проекта;
    /// <paramref name="me"/> — участник (его статус в сборе: group / reserve).
    /// </summary>
    /// <summary>Организаторы сборов календаря (для подгрузки имён).</summary>
    private static IEnumerable<ulong> CalendarOwners(ulong guildId, LfgStore store, RecurringLfgStore recurring) =>
        store.Groups.Values.Where(g => g.GuildId == guildId && !g.IsClosed).Select(g => g.OwnerId)
            .Concat(recurring.Get(guildId).Select(r => r.OwnerId)).Where(id => id != 0);

    internal static List<object> CalendarEvents(SocketGuild guild, LfgStore store, RecurringLfgStore recurring, TimeService time,
        LfgProjectStore projects, Func<string?, bool> allowed, ulong? me)
    {
        var guildId = guild.Id;
        var names = projects.GetProjects(guildId).ToDictionary(p => p.Key, p => p.Name);
        string? ProjectName(string? key) => projects.Effective(guildId, key)?.Key is { } k && names.TryGetValue(k, out var n) ? n : null;
        var now = DateTimeOffset.UtcNow;
        var until = now.AddDays(8);

        var events = store.Groups.Values
            .Where(g => g.GuildId == guildId && !g.IsClosed && g.StartsAt is { } s && s >= now.AddHours(-6) && s <= until)
            .Where(g => allowed(projects.Effective(guildId, g.ProjectKey)?.Key))
            .Select(g => new
            {
                id = g.Id,
                title = g.ActivityName,
                start = g.StartsAt!.Value.ToUnixTimeMilliseconds(),
                project = ProjectName(g.ProjectKey),
                projectKey = projects.Effective(guildId, g.ProjectKey)?.Key,
                channel = guild.GetChannel(g.ChannelId)?.Name,
                filled = g.FilledCount,
                total = g.Slots.Count,
                reserve = g.Reserve.Count,
                organizer = g.OwnerId == 0 ? null : Member(guild, g.OwnerId),
                note = g.Note,
                jumpUrl = LfgSignupService.JumpUrl(g),
                recurring = false,
                mine = me is { } m ? g.Slots.Any(s => s.UserId == m) ? "group" : g.FindReserve(m) is not null ? "reserve" : null : null
            }).ToList<object>();

        var posted = store.Groups.Values.Where(g => g.GuildId == guildId && g.StartsAt is not null)
            .Select(g => (g.ActivityName, g.StartsAt!.Value)).ToHashSet();
        foreach (var r in recurring.Get(guildId).Where(r => r.Enabled && allowed(projects.Effective(guildId, r.ProjectKey)?.Key)))
        {
            for (var i = 0; i < 8; i++)
            {
                var date = time.Today.AddDays(i);
                if (!r.Days.Contains(date.DayOfWeek)) continue;
                var start = time.ToUtc(date, r.Hour, r.Minute);
                if (start < now || start > until || posted.Contains((r.ActivityName, start)) || r.LastStart == start) continue;
                events.Add(new
                {
                    id = $"{r.Id}:{start.ToUnixTimeSeconds()}",
                    title = r.ActivityName,
                    start = start.ToUnixTimeMilliseconds(),
                    project = ProjectName(r.ProjectKey),
                    projectKey = projects.Effective(guildId, r.ProjectKey)?.Key,
                    channel = guild.GetChannel(r.ChannelId)?.Name,
                    filled = 0,
                    total = r.Slots.Count,
                    reserve = 0,
                    organizer = r.OwnerId == 0 ? null : Member(guild, r.OwnerId),
                    note = r.Note,
                    jumpUrl = (string?)null,
                    recurring = true,
                    mine = (string?)null
                });
            }
        }
        return events;
    }
}

public sealed record ArmoryProjectBody(string? Key, bool Enabled, int Race, int? ServerId);
public sealed record ArmoryPrefsBody(List<ArmoryProjectBody>? Projects);
public sealed record ArmoryAutoBody(bool On, string? Time, int Hours);
public sealed record ArmoryBulkBody(string? Project, bool LinkNew = true);

public sealed record LfgPrefsBody(bool ReadyCheck, int? AutoPromoteMinutes, bool GsReminder, string? GsReminderChannelId, int? GsReminderDays,
    bool GsReminderDm, bool MemberTop = true, string? ArmoryGs = null);
