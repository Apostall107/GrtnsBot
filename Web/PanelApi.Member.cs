using System.Security.Claims;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Web;

// Раздел Member: то, что доступно каждому участнику сервера с ролью «участник» — сборы и свои персонажи.
public static partial class PanelApi
{
    /// <summary>Только админы (любое право из раздела Access).</summary>
    private static async ValueTask<object?> RequireAnyAdmin(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var access = ctx.HttpContext.RequestServices.GetRequiredService<PanelAccess>();
        if (!access.IsAdmin(ctx.HttpContext.User.UserId()))
            return Results.Json(new { error = "Admins only." }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    /// <summary>Участник этого сервера (роль «участник») или владелец бота.</summary>
    private static async ValueTask<object?> RequireMember(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var access = http.RequestServices.GetRequiredService<PanelAccess>();
        ulong.TryParse(http.Request.RouteValues["guildId"]?.ToString(), out var guildId);
        if (!access.IsMember(http.User.UserId(), guildId))
            return Results.Json(new { error = "This section is for server members only." }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    // ================= Роли «участник» (Settings) =================

    private static void MapMemberRoles(RouteGroupBuilder g)
    {
        g.MapGet("/", (ulong guildId, DiscordSocketClient client, BotSettingsStore settings) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(settings.GetMemberRoles(guildId).Select(id => Role(guild, id)));
        });

        g.MapPut("/", (ulong guildId, MemberRolesBody body, DiscordSocketClient client, BotSettingsStore settings) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var ids = ParseIds(body.RoleIds).Where(id => guild.GetRole(id) is not null).ToList();
            settings.SetMemberRoles(guildId, ids);
            return Results.Ok();
        });
    }

    // ================= Раздел Member =================

    private static void MapMember(RouteGroupBuilder g)
    {
        // Активные сборы + моё место в каждом + мои персонажи для записи
        g.MapGet("/groups", async (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, LfgStore lfg, CharacterStore characters,
            LfgSignupService signup, LfgProjectStore projects, [FromServices] GuildPrefsStore prefs, [FromServices] LfgProfileService profiles) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            await WarmMembersAsync(client, guild, GroupPeople(lfg, guildId));
            var me = user.UserId()!.Value;
            var readyCheck = prefs.Get(guildId).ReadyCheck;
            return Results.Ok(new
            {
                groups = ActiveGroups(lfg, guildId).Select(x =>
                {
                    var mySlot = x.Slots.Find(s => s.UserId == me);
                    var myReserve = x.FindReserve(me);
                    return new
                    {
                        id = x.Id,
                        activity = x.ActivityName,
                        // Проект сбора: мои персонажи этого проекта; без роли проекта записаться нельзя
                        project = projects.Effective(guildId, x.ProjectKey)?.Name,
                        projectKey = projects.Effective(guildId, x.ProjectKey)?.Key,
                        blocked = signup.ProjectBlock(x, me) is { } b ? StripMarkdown(System.Text.RegularExpressions.Regex.Replace(b, "<@&(\\d+)>",
                            m => "@" + (guild.GetRole(ulong.Parse(m.Groups[1].Value))?.Name ?? "role"))) : null,
                        characters = characters.Get(guildId, me, signup.Scope(x)),
                        // Иконка класса, GS и CP каждого персонажа — для меню выбора при записи
                        characterInfo = profiles.PickerList(x, me).ToDictionary(c => c.Name, c => new { icon = c.Icon, gs = c.Gs, cp = c.Cp }),
                        // Какими ролями может играть каждый персонаж (по его классу; null — любыми)
                        characterRoles = characters.Get(guildId, me, signup.Scope(x)).ToDictionary(n => n, n =>
                            characters.GetInfo(guildId, me, n, signup.Scope(x))?.ClassRoleId is { } cid &&
                            projects.EffectiveClassSlotRoles(guildId, x.ProjectKey).TryGetValue(cid, out var sr) && sr.Count > 0
                                ? sr.Select(r => r.ToString()).ToList() : null),
                        // Напоминание уже было: можно нажать «Готов»
                        readyCheck = readyCheck && x.ReminderMessageId is not null,
                        myReady = mySlot?.Ready ?? false,
                        owner = x.OwnerId == 0 ? null : Member(guild, x.OwnerId),
                        channel = guild.GetTextChannel(x.ChannelId)?.Name,
                        startsAt = x.StartsAt?.ToUnixTimeMilliseconds(),
                        filled = x.FilledCount,
                        size = x.Slots.Count,
                        jumpUrl = LfgSignupService.JumpUrl(x),
                voiceUrl = x.VoiceChannelId is { } vc ? $"https://discord.com/channels/{x.GuildId}/{vc}" : null,
                        closed = x.IsClosed,
                        isOrganizer = x.OwnerId == me,
                        note = x.Note,
                        requirements = x.HasRequirements ? new
                        {
                            roles = x.RequiredRoleIds.Select(id => Role(guild, id)),
                            minGs = x.MinGs
                        } : null,
                        started = x.StartsAt is null || x.StartsAt <= DateTimeOffset.UtcNow,
                        canJoin = SlotRoleExtensions.All.ToDictionary(r => r.ToString(), r => x.CanJoinAs(r)),
                        me = mySlot is not null
                            ? new { status = "group", role = (mySlot.TakenAs ?? mySlot.Role).ToString(), character = mySlot.CharacterName }
                            : myReserve is not null
                                ? new { status = "reserve", role = myReserve.Role.ToString(), character = myReserve.CharacterName }
                                : null,
                        slots = x.Slots.Select(s => new
                        {
                            emoji = (s.TakenAs ?? s.Role).Emoji(),
                            slotRole = s.Role.Label(),
                            role = (s.TakenAs ?? s.Role).Label(),
                            member = s.UserId is { } u ? Member(guild, u) : null,
                            character = s.CharacterName,
                            classRole = s.ClassRoleId is { } cr ? Role(guild, cr) : null,
                            classIcon = s.ClassIcon,
                            gs = s.Gs,
                            cp = s.Cp,
                            attended = s.Attended,
                            ready = s.Ready,
                            requiredRole = SlotRequiredRole(guild, s),
                            requiredClasses = SlotClasses(guild, s),
                            noRequirement = x.RequiredRoleIds.Count > 0 && s.Requirement == SlotRequirement.Open
                        }),
                        reserve = x.Reserve.Select(r => new
                        {
                            attended = x.NoShows.Any(n => n.UserId == r.UserId) ? false : (bool?)null,
                            emoji = r.Role.Emoji(),
                            role = r.Role.Label(),
                            member = Member(guild, r.UserId),
                            character = r.CharacterName,
                            classRole = r.ClassRoleId is { } rr ? Role(guild, rr) : null,
                            classIcon = r.ClassIcon,
                            gs = r.Gs,
                            cp = r.Cp
                        })
                    };
                })
            });
        });

        // Записаться: роль + персонаж (новый персонаж сохраняется, как в Discord)
        g.MapPost("/groups/{groupId}/join", async (ulong guildId, string groupId, JoinBody body, ClaimsPrincipal user,
            LfgStore lfg, CharacterStore characters, LfgSignupService signup, [FromServices] LfgProjectStore projects) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            if (signup.ProjectBlock(group, user.UserId()!.Value) is not null) return Error("You don't have the role of this group's project.");
            if (!Enum.TryParse<SlotRole>(body.Role, true, out var role) || role == SlotRole.Any) return Error("Pick a role: DD, Tank or Sup.");
            var name = (body.Character ?? "").Replace("`", "").Trim();
            if (name.Length is 0 or > CharacterStore.MaxNameLength) return Error($"Character name: 1–{CharacterStore.MaxNameLength} characters.");

            var me = user.UserId()!.Value;
            // Класс персонажа должен играть эту роль (LFG → Classes), как в Discord
            var classId = characters.GetInfo(guildId, me, name, signup.Scope(group))?.ClassRoleId;
            if (!projects.ClassFits(guildId, group.ProjectKey, classId, role))
                return Error($"{name}'s class doesn't play {role.Label()} — pick another character or role.");
            await group.Gate.WaitAsync();
            string? error;
            try { error = group.CheckJoin(me, role); }
            finally { group.Gate.Release(); }
            if (error is not null) return Error(error);

            characters.Add(guildId, me, name, signup.Scope(group));
            var result = await signup.JoinAsync(group, me, role, name);
            var ok = group.Slots.Any(s => s.UserId == me) || group.FindReserve(me) is not null;
            return ok ? Results.Ok(new { message = StripMarkdown(result) }) : Error(StripMarkdown(result));
        });

        g.MapPost("/groups/{groupId}/leave", async (ulong guildId, string groupId, ClaimsPrincipal user, LfgStore lfg, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            var me = user.UserId()!.Value;
            if (me == group.OwnerId) return Error("The organizer can't leave — close the group in Discord instead.");

            string? error;
            await group.Gate.WaitAsync();
            try
            {
                error = group.Leave(me);
                if (error is null)
                {
                    lfg.SaveGroups();
                    await signup.RefreshPostAsync(group);
                }
            }
            finally
            {
                group.Gate.Release();
            }
            if (error is null) _ = signup.TryAutoPromoteAsync(group);
            return error is null ? Results.Ok() : Error(error);
        });

        // Мои персонажи: класс-роль и заметка
        // Мои персонажи выбранного проекта (?project=key, пусто — по умолчанию): класс, GS, заметка
        g.MapGet("/characters", (ulong guildId, string? project, ClaimsPrincipal user, DiscordSocketClient client, CharacterStore characters,
            LfgProfileService profiles, LfgNotifyStore notify, LfgProjectStore projects, [FromServices] GuildPrefsStore prefs,
            [FromServices] ArmorySyncService armory) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var member = guild.GetUser(me);
            var current = projects.Effective(guildId, project);
            var scope = projects.Scope(guildId, current?.Key);
            return Results.Ok(new
            {
                dmEnabled = notify.IsEnabled(guildId, me),
                projects = projects.GetProjects(guildId).Select(p => new
                {
                    key = p.Key,
                    name = p.Name,
                    allowed = LfgProjectStore.CanUse(member, p),
                    roles = p.RoleIds.Select(id => Role(guild, id))
                }),
                project = current?.Key,
                allowed = LfgProjectStore.CanUse(member, current),
                classRoles = profiles.ClassRoles(guildId, me, current?.Key).Select(r => new
                {
                    id = r.Id.ToString(),
                    name = r.Name,
                    color = r.Colors.PrimaryColor.RawValue == 0 ? null : $"#{r.Colors.PrimaryColor.RawValue:x6}",
                    icon = profiles.Icon(guildId, r, current?.Key)
                }),
                characters = characters.Get(guildId, me, scope).Select(name =>
                {
                    var info = characters.GetInfo(guildId, me, name, scope);
                    return new
                    {
                        name,
                        classRole = info?.ClassRoleId is { } id ? Role(guild, id) : null,
                        comment = info?.Comment,
                        gs = info?.Gs,
                        gsUpdated = info?.GsUpdatedAt?.ToUnixTimeMilliseconds(),
                        armory = ArmoryDto(info?.Armory),
                        gsLocked = armory.GsLocked(guildId, info),
                        race = info?.Race,
                        serverId = info?.ServerId
                    };
                }),
                // Через сколько дней GS считается устаревшим (как в напоминании о GS)
                gsStaleDays = Math.Clamp(prefs.Get(guildId).GsReminderDays, 1, 365),
                armoryGs = prefs.Get(guildId).ArmoryGs,
                // Официальная оружейная AION 2 для этого проекта: включена, фракция и сервер по умолчанию
                armoryProject = armory.ProjectPrefs(guildId, current?.Key) is { } ap
                    ? new { enabled = ap.Enabled, race = ap.Race, serverId = ap.ServerId } : null,
                maxCharacters = CharacterStore.MaxPerUser,
                maxComment = CharacterStore.MaxCommentLength
            });
        });

        g.MapPost("/characters", (ulong guildId, string? project, CharacterBody body, ClaimsPrincipal user, DiscordSocketClient client,
            CharacterStore characters, LfgProjectStore projects) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var name = (body.Name ?? "").Replace("`", "").Trim();
            if (name.Length is 0 or > CharacterStore.MaxNameLength) return Error($"Character name: 1–{CharacterStore.MaxNameLength} characters.");
            characters.Add(guildId, user.UserId()!.Value, name, scope.Value);
            return Results.Ok();
        });

        g.MapPut("/characters/{name}", async (ulong guildId, string name, string? project, CharacterProfileBody body, ClaimsPrincipal user,
            DiscordSocketClient client, CharacterStore characters, LfgProfileService profiles, LfgProjectStore projects,
            [FromServices] ArmorySyncService armory) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var me = user.UserId()!.Value;
            var exact = characters.Get(guildId, me, scope.Value).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return Error("Character not found.");

            ulong? roleId = ParseId(body.ClassRoleId);
            if (roleId is { } r && profiles.ClassRoles(guildId, me, projects.Effective(guildId, string.IsNullOrWhiteSpace(project) ? null : project)?.Key)
                    .All(x => x.Id != r)) return Error("This class is not available in this project.");
            if (body.Comment is { Length: > CharacterStore.MaxCommentLength }) return Error($"Note: up to {CharacterStore.MaxCommentLength} characters.");
            if (body.Gs is < 0 or > 99999) return Error("GS: 0–99999.");

            characters.SetInfo(guildId, me, exact, true, roleId, true, body.Comment, scope.Value);
            // GS привязанного к оружейной персонажа приходит оттуда — с сайта не меняется
            characters.SetGs(guildId, me, exact, body.Gs is > 0 ? body.Gs : null, scope.Value);
            // Фракция и сервер: у привязанного к оружейной берутся оттуда
            if (characters.GetInfo(guildId, me, exact, scope.Value)?.Armory is null)
                characters.SetRealm(guildId, me, exact, body.Race is 1 or 2 ? body.Race : null,
                    body.ServerId is > 0 and < 100000 ? body.ServerId : null, scope.Value);
            var groups = await profiles.RefreshCharacterAsync(guildId, me, exact, scope.Value);
            return Results.Ok(new { groupsUpdated = groups });
        });

        // Переименовать персонажа: класс, заметка, GS и активные сборы переходят на новое имя
        g.MapPost("/characters/{name}/rename", async (ulong guildId, string name, string? project, CharacterRenameBody body, ClaimsPrincipal user,
            DiscordSocketClient client, CharacterStore characters, LfgProfileService profiles, LfgProjectStore projects) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var me = user.UserId()!.Value;
            var exact = characters.Get(guildId, me, scope.Value).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return Error("Character not found.");
            var newName = (body.Name ?? "").Replace("`", "").Trim();
            if (characters.Rename(guildId, me, exact, newName, scope.Value) is { } error) return Error(error);
            var groups = await profiles.RenameInGroupsAsync(guildId, me, exact, newName, scope.Value);
            return Results.Ok(new { groupsUpdated = groups });
        });

        g.MapDelete("/characters/{name}", (ulong guildId, string name, string? project, ClaimsPrincipal user, DiscordSocketClient client,
            CharacterStore characters, LfgProjectStore projects) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            return characters.Remove(guildId, user.UserId()!.Value, name, scope.Value) ? Results.Ok() : Error("Character not found.");
        });

        // Сводка сервера для главной участника (только числа, без подробностей)
        // The site mascot: the member caught the robot / saw it escape (with the spells it cast)
        g.MapPost("/mascot", (ulong guildId, MascotEventBody body, ClaimsPrincipal user, [FromServices] MascotStatsStore mascot) =>
        {
            var caught = mascot.Record(guildId, user.UserId()!.Value, body.Kind ?? "", body.Quick, body.Spells, body.Legendary, body.Legacy,
                body.Dodges ?? 0, body.Dashes ?? 0, body.Tired, body.Hour, body.Day, body.Fast, body.Long, body.Golden, body.Rainbow);
            return Results.Ok(new { caught = caught ?? mascot.Get(guildId, user.UserId()!.Value).Caught });
        });

        // Site activity for achievements (visit days, quick search, Member tabs) — not logged
        g.MapPost("/track", (ulong guildId, SiteTrackBody body, ClaimsPrincipal user, [FromServices] MascotStatsStore mascot) =>
        {
            mascot.Track(guildId, user.UserId()!.Value, body.What ?? "", body.Day, body.Tab);
            return Results.Ok();
        });

        // Achievements tab: my stats and the achievements (locked ones come without title and description)
        g.MapGet("/achievements", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, [FromServices] MascotStatsStore mascot,
            [FromServices] LfgHistoryStore history, [FromServices] CharacterStore chars, [FromServices] BirthdayStore birthdays,
            [FromServices] TicketStore tickets, [FromServices] TicketArchiveStore archive, [FromServices] AuditLog audit, [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            // Ranking: where I am among everyone on the server who played (the robot or LFG), per category; the top 3 for the hover
            var lfgStats = LfgStats.Players(history.Get(guildId, null)).ToDictionary(p => p.UserId);
            var charCount = chars.All(guildId).GroupBy(c => c.UserId).ToDictionary(x => x.Key, x => x.Count());
            var all = mascot.Players(guildId).ToList();
            foreach (var id in lfgStats.Keys.Concat(charCount.Keys).Append(me).Distinct())
                if (!all.Any(x => x.UserId == id)) all.Add((id, new MascotStats()));
            all = all.Where(x => guild.GetUser(x.UserId) is not null || x.UserId == me).ToList();
            // everything the bot already knows (so these count retroactively)
            var hist = history.Get(guildId, null).OrderBy(e => e.ClosedAt).ToList();
            var recurringPosts = audit.Query(guildId, 0, "lfg", "Posted recurring LFG group", 0, int.MaxValue)
                .Select(e => e.Details ?? "").ToHashSet();   // "Activity · 2026-11-09 20:00:00Z"
            bool IsRecurring(LfgHistoryEntry e) => e.StartsAt is { } st && recurringPosts.Contains($"{e.Activity} · {st:u}");
            var archived = archive.Query(guildId, null, null, null, 0, int.MaxValue);
            var openTickets = tickets.GetTickets(guildId);
            var gearActions = audit.Query(guildId, null, "lfg", null, 0, int.MaxValue)
                .Where(e => e.Action.Contains("armory", StringComparison.OrdinalIgnoreCase) || e.Action.Contains("class/note", StringComparison.OrdinalIgnoreCase)
                    || e.Action.Contains(" GS", StringComparison.Ordinal))
                .GroupBy(e => e.ActorId).ToDictionary(x => x.Key, x => x.Count());
            var myChars = chars.All(guildId).GroupBy(c => c.UserId).ToDictionary(x => x.Key, x => x.ToList());
            var defKey = projects.DefaultKey(guildId);
            foreach (var (id, st) in all)
            {
                if (lfgStats.TryGetValue(id, out var p)) { st.GroupsJoined = p.Groups; st.GroupsAttended = p.Attended; st.GroupsOrganized = p.Organized; }
                st.Characters = charCount.GetValueOrDefault(id);
                st.HasBirthday = birthdays.Of(guildId, id) is null ? 0 : 1;
                var mine = hist.Where(e => e.Members.Any(m => m.UserId == id)).ToList();
                int run = 0, best = 0;
                foreach (var e in mine)
                {
                    var m = e.Members.First(x => x.UserId == id);
                    if (m.Attended == true) best = Math.Max(best, ++run);
                    else if (m.Attended == false) run = 0;
                }
                st.BestAttendStreak = best;
                st.FullHouses = hist.Count(e => e.OwnerId == id && e.Size > 0 && e.Members.Count >= e.Size);
                st.Activities = mine.Select(e => e.Activity).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                st.RecurringJoined = mine.Count(IsRecurring);
                var roles = mine.Select(e => e.Members.First(x => x.UserId == id).Role).ToList();
                int RoleCount(string r) => roles.Count(x => x.Contains(r, StringComparison.OrdinalIgnoreCase));
                st.MinRoleGames = Math.Min(RoleCount("DD"), Math.Min(RoleCount("Tank"), RoleCount("Sup")));
                st.VoiceGroups = mine.Count(e => e.Voice);
                st.Applied = archived.Count(e => e.UserId == id) + openTickets.Count(t => t.UserId == id);
                st.Approved = archived.Count(e => e.UserId == id && e.Outcome == "approved");
                var cs = myChars.GetValueOrDefault(id) ?? [];
                st.ArmoryLinked = cs.Count(c => c.Info?.Armory is not null);
                st.Projects = cs.Select(c => c.Scope ?? defKey).Distinct().Count();
                st.GearUps = gearActions.GetValueOrDefault(id);
                st.MonthsOnServer = guild.GetUser(id)?.JoinedAt is { } j ? (int)((DateTimeOffset.UtcNow - j).TotalDays / 30.44) : 0;
            }
            var s = all.First(x => x.UserId == me).Stats;
            object Rank(string key, string label, string icon, Func<MascotStats, int> value)
            {
                var mine = value(s);
                var top = all.Select(x => (x.UserId, V: value(x.Stats))).Where(x => x.V > 0).OrderByDescending(x => x.V).Take(3)
                    .Select(x => new { member = Member(guild, x.UserId), value = x.V, me = x.UserId == me }).ToList();
                return new
                {
                    key, label, icon, value = mine,
                    rank = mine > 0 ? 1 + all.Count(x => value(x.Stats) > mine) : (int?)null,
                    of = all.Count(x => value(x.Stats) > 0),
                    top
                };
            }
            var ranking = new[]
            {
                Rank("caught", "Robots caught", "🤖", x => x.Caught),
                Rank("quick", "Quick catches", "⚡", x => x.QuickCatches),
                Rank("spells", "Spells seen", "✨", x => x.Spells.Values.Sum()),
                Rank("kinds", "Different spells", "📖", x => x.Spells.Count(kv => kv.Value > 0)),
                Rank("legendary", "Legendary casts", "🌟", x => x.Legendary),
                Rank("achievements", "Grade points", "🏆", x => MascotStatsStore.All.Sum(a => a.Points(x))),
            };
            // Full leaderboard by grade points (the Achievements card opens it): everyone with points, plus me; ties share a place
            var board = all.Select(x => (x.UserId, Points: MascotStatsStore.All.Sum(a => a.Points(x.Stats)), Unlocked: MascotStatsStore.All.Count(a => a.Grade(x.Stats) > 0),
                    Mythic: MascotStatsStore.All.Count(a => a.Grade(x.Stats) >= MascotStatsStore.GradeNames.Length)))
                .Where(x => x.Points > 0 || x.UserId == me).OrderByDescending(x => x.Points).ThenByDescending(x => x.Unlocked).ToList();
            var leaderboard = board.Select(x => new
            {
                rank = 1 + board.Count(y => y.Points > x.Points), member = Member(guild, x.UserId), points = x.Points,
                unlocked = x.Unlocked, mythic = x.Mythic, me = x.UserId == me
            });
            return Results.Ok(new
            {
                ranking, leaderboard,
                caught = s.Caught, quick = s.QuickCatches, escaped = s.Escaped, legendary = s.Legendary,
                spells = MascotStatsStore.Spells.Select(x => s.Spells.GetValueOrDefault(x.Key) is var n && n > 0
                    ? new { key = x.Key, icon = x.Icon, name = x.Name, count = n }
                    : new { key = x.Key, icon = "❔", name = "???", count = 0 }),
                total = MascotStatsStore.All.Count,
                grades = MascotStatsStore.GradeNames,
                points = MascotStatsStore.All.Sum(a => a.Points(s)),
                maxPoints = MascotStatsStore.All.Sum(a => a.MaxPoints),
                multipliers = new[] { 1, 2, 10, 50 },
                // graded: grade 1..7 (rarity colour), progress to the next grade; locked ones come without any text
                achievements = MascotStatsStore.All.Select(a =>
                {
                    var g = a.Grade(s);
                    // General and Guild ones are open goals: shown even before they're earned; the rest stay hidden ???
                    if (g == 0) return a.Group is "general" or "guild"
                        ? new { id = a.Id, group = a.Group, unlocked = false, visible = true, tierClass = a.TierClass, icon = a.Icon, title = a.Title,
                            description = string.Format(a.Description, a.Tiers[0]).Replace(" 1 times", " once"), value = a.Value(s), next = a.Tiers[0], maxGrade = a.Tiers.Length }
                        : (object)new { id = a.Id, group = a.Group, unlocked = false };
                    var v = a.Value(s);
                    var next = g < a.Tiers.Length ? a.Tiers[g] : (int?)null;
                    // my place for this achievement among everyone on the server + the top 3 (for the hover)
                    var vals = all.Select(x => (x.UserId, V: a.Value(x.Stats))).Where(x => x.V > 0).OrderByDescending(x => x.V).ToList();
                    var top = vals.Take(3).Select(x => new { member = Member(guild, x.UserId), value = x.V, me = x.UserId == me }).ToList();
                    return new
                    {
                        // achievement class: I — common climbs (×2 from 5), II — rare (single spells, Grimoire), III — legendary casts
                        // IV — anything with the ultra-rare poop barrage
                        tierClass = a.TierClass, points = a.Points(s),
                        id = a.Id, group = a.Group, unlocked = true, icon = a.Icon, title = a.Title, grade = g, gradeName = MascotStatsStore.GradeNames[g - 1],
                        rank = 1 + vals.Count(x => x.V > v), of = vals.Count, top,
                        description = string.Format(a.Description, a.Tiers[g - 1]).Replace(" 1 times", " once"), nextGrade = next is null ? null : MascotStatsStore.GradeNames[g], value = v, prev = a.Tiers[g - 1], next, maxGrade = a.Tiers.Length
                    };
                })
            });
        });

        g.MapGet("/summary", (ulong guildId, DiscordSocketClient client, LfgStore lfg, TicketStore tickets, TempVoiceStore voice,
            GuildStatsStore stats, [Microsoft.AspNetCore.Mvc.FromServices] TimeService time,
            [Microsoft.AspNetCore.Mvc.FromServices] TicketArchiveStore archive) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var groups = ActiveGroups(lfg, guildId);
            // Tickets for everyone: how many were opened and processed in the last 7 days (+ opened per day for 14 days)
            var now = DateTimeOffset.UtcNow;
            var past = archive.Query(guildId, null, null, null, 0, int.MaxValue);
            var openedAt = past.Select(e => e.OpenedAt).Concat(tickets.GetTickets(guildId).Select(t => t.CreatedAt)).Where(t => t > now.AddDays(-14)).ToList();
            DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, time.Zone).DateTime);
            var today = Day(now);
            return Results.Ok(new
            {
                trends = GuildStatsService.Trends(stats, time, guildId, guild.MemberCount, tickets.GetTickets(guildId).Count),
                server = new { id = guild.Id.ToString(), name = guild.Name, iconUrl = guild.IconUrl, members = guild.MemberCount },
                activeGroups = groups.Count,
                recruiting = groups.Count(x => !x.IsFull),
                openTickets = tickets.GetTickets(guildId).Count,
                ticketWeek = new
                {
                    opened = openedAt.Count(t => t > now.AddDays(-7)),
                    processed = past.Count(e => e.ClosedAt > now.AddDays(-7)),
                    history = Enumerable.Range(0, 14).Select(i => today.AddDays(i - 13))
                        .Select(d => new { date = d.ToString("yyyy-MM-dd"), value = openedAt.Count(t => Day(t) == d) })
                },
                voiceRooms = LiveRooms(guild, voice).Count
            });
        });

        // Главная участника: мои сборы, мои заявки (тикеты), моя голосовая комната
        g.MapGet("/home", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, LfgStore lfg, TicketStore tickets,
            TempVoiceStore voice) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var room = voice.FindRoomByOwner(guildId, me) is { } r && guild.GetVoiceChannel(r.ChannelId) is { } ch
                ? new { channelId = ch.Id.ToString(), name = ch.Name, limit = ch.UserLimit, locked = r.Locked, inside = ch.ConnectedUsers.Count }
                : null;
            return Results.Ok(new
            {
                groups = ActiveGroups(lfg, guildId)
                    .Where(x => x.OwnerId == me || x.Slots.Any(sl => sl.UserId == me) || x.FindReserve(me) is not null)
                    .Select(x => new
                    {
                        id = x.Id,
                        activity = x.ActivityName,
                        startsAt = x.StartsAt?.ToUnixTimeMilliseconds(),
                        filled = x.FilledCount,
                        size = x.Slots.Count,
                        status = x.OwnerId == me ? "organizer" : x.Slots.Any(sl => sl.UserId == me) ? "group" : "reserve",
                        character = x.Slots.Find(sl => sl.UserId == me)?.CharacterName ?? x.FindReserve(me)?.CharacterName,
                        jumpUrl = LfgSignupService.JumpUrl(x)
                    }),
                tickets = tickets.GetTickets(guildId).Where(t => t.UserId == me).Select(t => new
                {
                    project = tickets.FindProject(guildId, t.ProjectKey)?.Name ?? "Deleted project",
                    approved = t.ApprovedBy is not null,
                    createdAt = t.CreatedAt.ToUnixTimeMilliseconds(),
                    jumpUrl = $"https://discord.com/channels/{guildId}/{t.TextChannelId}"
                }),
                room
            });
        });

        // Моя временная комната: переименовать, лимит, закрыть/открыть (как кнопки Room controls)
        g.MapPost("/room", async (ulong guildId, RoomActionBody body, ClaimsPrincipal user, DiscordSocketClient client,
            TempVoiceStore voice, TempRoomManager rooms) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            if (voice.FindRoomByOwner(guildId, me) is not { } stored || guild.GetVoiceChannel(stored.ChannelId) is not { } channel)
                return Error("You don't own a temporary room right now.");
            var room = new RoomRef(channel, stored);
            try
            {
                var message = body.Action switch
                {
                    "rename" => await rooms.RenameAsync(room, body.Name ?? ""),
                    "limit" => await rooms.LimitAsync(room, body.Limit ?? 0),
                    "lock" => await rooms.LockAsync(room),
                    "unlock" => await rooms.UnlockAsync(room),
                    _ => null
                };
                return message is null ? Error("Unknown action.") : Results.Ok(new { message = StripMarkdown(message) });
            }
            catch (Discord.Net.HttpException)
            {
                return Error("Discord refused the change (rename limit is 2 per 10 minutes, or the bot lacks Manage Channels).");
            }
        });

        // Мои настройки временной комнаты по умолчанию
        g.MapGet("/room-prefs", (ulong guildId, ClaimsPrincipal user, RoomPrefsStore prefs) =>
        {
            var p = prefs.Get(guildId, user.UserId()!.Value);
            return Results.Ok(new { name = p?.Name, limit = p?.Limit, locked = p?.Locked ?? false });
        });

        g.MapPut("/room-prefs", (ulong guildId, RoomPrefsBody body, ClaimsPrincipal user, RoomPrefsStore prefs) =>
        {
            var name = string.IsNullOrWhiteSpace(body.Name) ? null : body.Name.Trim();
            if (name is { Length: > 100 }) return Error("Room name: up to 100 characters.");
            if (body.Limit is < 0 or > 99) return Error("Limit: 0–99 (0 = no limit).");
            prefs.Set(guildId, user.UserId()!.Value, new RoomPrefs { Name = name, Limit = body.Limit, Locked = body.Locked });
            return Results.Ok();
        });

        // Для создания сбора: активности и канал, куда уйдёт пост (выбирает админ в LFG → Channel for groups)
        g.MapGet("/create-options", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, ActivityStore activities,
            BotSettingsStore settings, PanelTextStore panels, LfgTemplateStore templates, LfgProjectStore projects, PanelAccess access,
            [FromServices] LfgProfileService profiles, [FromServices] LfgStore lfg) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var member = guild.GetUser(me);
            var channel = LfgChannel(guild, settings, panels, null, lfg);
            return Results.Ok(new
            {
                // Проекты, роли которых у меня есть, и канал, куда уйдёт сбор каждого
                projects = projects.GetProjects(guildId).Where(p => LfgProjectStore.CanUse(member, p)).Select(p => new
                {
                    key = p.Key,
                    name = p.Name,
                    isDefault = p.Key == projects.DefaultKey(guildId),
                    channel = LfgChannel(guild, settings, panels, p.Key, lfg) is { } c ? new { id = c.Id.ToString(), name = c.Name } : null
                }),
                // Админ может продублировать пост в другие каналы
                mirrorChannels = access.Has(me, PanelPermissions.Lfg)
                    ? guild.TextChannels.Where(c => c is not SocketThreadChannel)
                        .Where(c => guild.CurrentUser.GetPermissions(c) is { ViewChannel: true, SendMessages: true, EmbedLinks: true })
                        .OrderBy(c => c is SocketVoiceChannel).ThenBy(c => c.Position)
                        .Select(c => new { id = c.Id.ToString(), name = c is SocketVoiceChannel ? $"🔊 {c.Name}" : c.Name })
                    : null,
                activities = activities.Get(guildId).Select(a => new { key = a.Key, name = a.Name, role = Role(guild, a.RoleId), projectKey = a.ProjectKey, minGs = a.MinGs }),
                templates = TemplatesDto(guild, templates),
                // Мои персонажи по проектам: организатор выбирает, с кем идёт; от GS зависят доступные активности
                characters = projects.GetProjects(guildId).Where(p => LfgProjectStore.CanUse(member, p)).ToDictionary(p => p.Key,
                    p => profiles.PickerFor(guildId, me, p.Key).Select(c => new
                    {
                        name = c.Name, gs = c.Gs, cp = c.Cp, icon = c.Icon,
                        // Роль слота 1 (организатор) по классу персонажа
                        slotRole = profiles.OwnerSlotRole(guildId, me, c.Name, p.Key)?.ToString()
                    })),
                // Роли, связанные с каждым проектом — их показывают первыми при выборе требуемых ролей
                relatedRoles = RelatedRoles(guild, activities, projects),
                // Роли для требований сбора (организатор выбирает, какие нужны)
                roles = guild.Roles.Where(r => !r.IsEveryone && !r.IsManaged).OrderByDescending(r => r.Position)
                    .Select(r => new { id = r.Id.ToString(), name = r.Name, color = r.Colors.PrimaryColor.RawValue == 0 ? null : $"#{r.Colors.PrimaryColor.RawValue:x6}" }),
                channel = channel is null ? null : new { id = channel.Id.ToString(), name = channel.Name }
            });
        });

        // Создать сбор (как /lfg): организатор — я
        g.MapPost("/create", async (ulong guildId, LfgCreateBody body, ClaimsPrincipal user, DiscordSocketClient client,
            ActivityStore activities, LfgPublisher publisher, BotSettingsStore settings, PanelTextStore panels,
            LfgProjectStore projects, PanelAccess access, [Microsoft.AspNetCore.Mvc.FromServices] TimeService time,
            [FromServices] CharacterStore characters, [FromServices] LfgProfileService profiles, [FromServices] LfgStore lfg) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var project = projects.Effective(guildId, body.ProjectKey);
            if (!LfgProjectStore.CanUse(guild.GetUser(me), project)) return Error($"You need the {project!.Name} role to create its groups.");
            // Канал выбирает не участник, а админ — пост уходит в канал проекта / канал для сборов
            if (LfgChannel(guild, settings, panels, project?.Key, lfg) is not { } lfgChannel)
                return Error("The channel for groups is not set yet. Ask an admin (LFG → Channel for groups).");
            var (err, channelId, name, pingRole, slots) = ResolveLfgInput(guild, activities, lfgChannel.Id.ToString(), body.ActivityKey, body.CustomName, body.Slots);
            if (err is not null) return Error(err);

            // Персонаж организатора: от его GS зависит, какие активности ему доступны
            string? ownerCharacter = null;
            var activity = string.IsNullOrWhiteSpace(body.ActivityKey) ? null : activities.Find(guildId, body.ActivityKey);
            if (!string.IsNullOrWhiteSpace(body.OwnerCharacter))
            {
                var scope = projects.Scope(guildId, project?.Key);
                ownerCharacter = characters.Get(guildId, me, scope).FirstOrDefault(n => n.Equals(body.OwnerCharacter, StringComparison.OrdinalIgnoreCase));
                if (ownerCharacter is null) return Error("Character not found.");
                var ownerGs = characters.GetInfo(guildId, me, ownerCharacter, scope)?.Gs;
                if (activity is not null && !ActivityStore.GsFits(activity, ownerGs))
                    return Error($"{activity.Name} needs GS {activity.MinGs}+ — {ownerCharacter} has {ownerGs?.ToString() ?? "no GS"}.");
            }
            var minGs = body.MinGs is > 0 and <= 99999 ? body.MinGs : activity?.MinGs;
            // Слот 1 — организатор: роль по классу его персонажа, без требований
            if (profiles.OwnerSlotRole(guildId, me, ownerCharacter, project?.Key) is { } ownerRole && slots!.Count > 0)
            {
                slots[0] = ownerRole;
                if (body.SlotRequirements is { Count: > 0 } sr) sr[0] = null;
            }

            DateTimeOffset? startsAt = null;
            if (!string.IsNullOrWhiteSpace(body.Date) || !string.IsNullOrWhiteSpace(body.Time))
            {
                if (!DateOnly.TryParseExact(body.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date) ||
                    !TimeOnly.TryParseExact(body.Time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var t))
                    return Error("Set both the date and the time, or leave both empty.");
                startsAt = time.ToUtc(date, t.Hour, t.Minute);
                if (startsAt < DateTimeOffset.UtcNow.AddMinutes(-1)) return Error("This time is in the past.");
            }

            try
            {
                var required = Requirements(guild, body.RequiredRoleIds);
                var slotReq = SlotRequirement.Parse(body.SlotRequirements, slots!.Count, GuildRoleIds(guild));
                var (group, jumpUrl, dms) = await publisher.PublishAsync(new LfgPublishRequest(
                    guildId, channelId, me, name!, pingRole, slots!, startsAt, body.NotifyByDm, true,
                    required, minGs,
                    project?.Key,
                    // Дублировать пост в другие каналы может только админ
                    access.Has(me, PanelPermissions.Lfg) ? ParseIds(body.MirrorChannelIds).Where(id => guild.GetTextChannel(id) is not null).Take(LfgPublisher.MaxMirrors).ToList() : null,
                    body.CreateVoice,
                    slotReq.Codes,
                    LfgNote.Clean(body.Note), slotReq.Classes, ownerCharacter));
                return Results.Ok(new { id = group.Id, jumpUrl, dmsStarted = dms });
            }
            catch (Exception ex) when (ex is InvalidOperationException or Discord.Net.HttpException)
            {
                return Error("The bot can't post in that channel.");
            }
        });

        // Организатор своего сбора: резерв туда-обратно (как ⚙️ Manage) и закрытие
        g.MapPost("/groups/{groupId}/roster", async (ulong guildId, string groupId, RosterBody body, ClaimsPrincipal user,
            LfgStore lfg, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            if (group.OwnerId != user.UserId()) return Error("Only the organizer can manage this group.");
            var error = await RosterAsync(group, body, lfg, signup, OrganizerRosterActions);
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapPost("/groups/{groupId}/slot", async (ulong guildId, string groupId, SlotRoleBody body, ClaimsPrincipal user, LfgStore lfg, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            if (group.OwnerId != user.UserId()) return Error("Only the organizer can manage this group.");
            var error = await SlotRoleAsync(group, body, lfg, signup);
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapPost("/groups/{groupId}/time", (ulong guildId, string groupId, TimeBody body, ClaimsPrincipal user, DiscordSocketClient client,
            LfgStore lfg, LfgSignupService signup, [Microsoft.AspNetCore.Mvc.FromServices] TimeService time) =>
            RescheduleEndpointAsync(guildId, groupId, body, user, client, lfg, signup, time, organizerOnly: true));

        g.MapPost("/groups/{groupId}/note", (ulong guildId, string groupId, NoteBody body, ClaimsPrincipal user, LfgStore lfg,
            LfgSignupService signup) => NoteEndpointAsync(guildId, groupId, body, user, lfg, signup, organizerOnly: true));

        g.MapPost("/groups/{groupId}/attendance", (ulong guildId, string groupId, AttendanceBody body, ClaimsPrincipal user,
            LfgStore lfg, LfgSignupService signup) =>
            AttendanceAsync(guildId, groupId, body, lfg, signup, user.UserId()));

        // Моя статистика по сборам
        g.MapGet("/stats", (ulong guildId, ClaimsPrincipal user, LfgHistoryStore history) =>
        {
            var me = user.UserId()!.Value;
            var list = history.Get(guildId, null);
            var mine = LfgStats.Players(list).FirstOrDefault(p => p.UserId == me);
            return Results.Ok(new
            {
                groups = mine?.Groups ?? 0,
                organized = mine?.Organized ?? 0,
                attended = mine?.Attended ?? 0,
                noShows = mine?.NoShows ?? 0,
                reliability = mine?.Reliability,
                // За последние 30 дней: сборы, пришёл / не пришёл, любимая роль
                last30 = Last30(list, me),
                recent = list.Where(e => e.OwnerId == me || e.Members.Any(m => m.UserId == me))
                    .OrderByDescending(e => e.ClosedAt).Take(10)
                    .Select(e => new
                    {
                        activity = e.Activity,
                        at = (e.StartsAt ?? e.ClosedAt).ToUnixTimeMilliseconds(),
                        organizer = e.OwnerId == me,
                        attended = e.Members.FirstOrDefault(m => m.UserId == me)?.Attended
                    })
            });
        });

        g.MapPost("/groups/{groupId}/close", async (ulong guildId, string groupId, ClaimsPrincipal user, LfgStore lfg, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            if (group.OwnerId != user.UserId()) return Error("Only the organizer can close this group.");
            await CloseGroupAsync(group, lfg, signup);
            return Results.Ok();
        });

        // Подать заявку с сайта: проекты (с анкетой) и мои открытые заявки
        g.MapGet("/tickets/projects", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, TicketStore tickets,
            [Microsoft.AspNetCore.Mvc.FromServices] LfgProjectStore lfgProjects, [Microsoft.AspNetCore.Mvc.FromServices] BotOwner owner) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var member = guild.GetUser(me);
            // Только тикет-системы, которые человек может открыть (свои роли доступа или роли проекта)
            return Results.Ok(tickets.GetProjects(guildId)
                .Where(p => owner.Is(me) || (member is not null && TicketService.CanOpen(p, member, lfgProjects.Find(guildId, p.GroupProjectKey),
                    p.ParentKey is { } pk ? tickets.FindProject(guildId, pk) : null)))
                .Select(p =>
            {
                var open = tickets.FindOpenTicket(guildId, me, p.Key);
                var group = lfgProjects.Find(guildId, p.GroupProjectKey);
                return new
                {
                    key = p.Key,
                    name = p.Name,
                    group = (p.ParentKey is { } parentKey ? tickets.FindProject(guildId, parentKey)?.Name : null) ?? group?.Name,
                    hasRoles = member is not null && TicketService.AlreadyHasRoles(p, member) is not null,
                    openTicketUrl = open is not null && guild.GetTextChannel(open.TextChannelId) is not null
                        ? $"https://discord.com/channels/{guildId}/{open.TextChannelId}" : null,
                    questions = p.FormEnabled
                        ? p.Questions.Take(TicketQuestion.MaxQuestions).Select(q => new
                        {
                            id = q.Id, label = q.Label, placeholder = q.Placeholder, @long = q.Long, required = q.Required, maxLength = q.MaxLength, birthday = q.Birthday
                        })
                        : null
                };
            }));
        });

        g.MapPost("/tickets", async (ulong guildId, ApplyBody body, ClaimsPrincipal user, DiscordSocketClient client,
            TicketStore tickets, TicketService service, [Microsoft.AspNetCore.Mvc.FromServices] LfgProjectStore lfgProjects,
            [Microsoft.AspNetCore.Mvc.FromServices] BotOwner owner) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (guild.GetUser(user.UserId()!.Value) is not { } member) return Error("You are not on this server.");
            if (tickets.FindProject(guildId, body.ProjectKey ?? "") is not { } project) return Error("This project no longer exists.");
            if (!owner.Is(member.Id) && !TicketService.CanOpen(project, member, lfgProjects.Find(guildId, project.GroupProjectKey),
                    project.ParentKey is { } pk ? tickets.FindProject(guildId, pk) : null))
                return Error($"You don't have the role required to open {project.Name} tickets.");
            if (TicketService.AlreadyHasRoles(project, member) is not null)
                return Error($"You already have all the roles of {project.Name} — no need to open a ticket.");

            var answers = project.FormEnabled
                ? project.Questions.Take(TicketQuestion.MaxQuestions).Select(q => new TicketAnswer
                {
                    Question = q.Label,
                    Answer = body.Answers is not null && body.Answers.TryGetValue(q.Id, out var a) ? a ?? "" : ""
                }).ToList()
                : new List<TicketAnswer>();

            var (ticket, error) = await service.CreateAsync(guild, member, project, answers, "web");
            return ticket is null
                ? Error(StripMarkdown(error ?? "Couldn't create the ticket."))
                : Results.Ok(new { jumpUrl = $"https://discord.com/channels/{guildId}/{ticket.TextChannelId}" });
        });

        g.MapPut("/dm", (ulong guildId, DmBody body, ClaimsPrincipal user, LfgNotifyStore notify) =>
        {
            notify.SetEnabled(guildId, user.UserId()!.Value, body.Enabled);
            return Results.Ok();
        });
    }

    private static object Last30(IReadOnlyList<LfgHistoryEntry> list, ulong me)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var mine = list.Where(e => (e.StartsAt ?? e.ClosedAt) >= since)
            .SelectMany(e => e.Members.Where(m => m.UserId == me)).ToList();
        return new
        {
            groups = mine.Count,
            came = mine.Count(m => m.Attended == true),
            noShow = mine.Count(m => m.Attended == false),
            role = mine.GroupBy(m => m.Role).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault()
        };
    }

    /// <summary>Ключ хранения персонажей проекта; null — нет роли проекта (менять его персонажей нельзя).</summary>
    private static (string? Value, bool Ok)? CharacterScope(ulong guildId, string? project, ClaimsPrincipal user, DiscordSocketClient client,
        LfgProjectStore projects)
    {
        var current = projects.Effective(guildId, string.IsNullOrWhiteSpace(project) ? null : project);
        if (!LfgProjectStore.CanUse(client.GetGuild(guildId)?.GetUser(user.UserId()!.Value), current)) return null;
        return (projects.Scope(guildId, current?.Key), true);
    }

    /// <summary>Канал для сборов с сайта: из настроек, иначе канал первой LFG-панели.</summary>
    private static SocketTextChannel? LfgChannel(SocketGuild guild, BotSettingsStore settings, PanelTextStore panels, string? projectKey = null, LfgStore? lfg = null)
    {
        // У проекта своя LFG-панель (LFG → Projects) — сборы проекта идут в её канал
        if (projectKey is not null &&
            panels.GetPosted(guild.Id, "lfg").Where(p => p.ProjectKey == projectKey)
                .Select(p => guild.GetTextChannel(p.ChannelId)).FirstOrDefault(c => c is not null) is { } projectChannel)
            return projectChannel;
        if (settings.GetLfgChannel(guild.Id) is { } id && guild.GetTextChannel(id) is { } set) return set;
        if (panels.GetPosted(guild.Id, "lfg").Select(p => guild.GetTextChannel(p.ChannelId)).FirstOrDefault(c => c is not null) is { } panelChannel)
            return panelChannel;
        // Nothing set and no LFG panel known (e.g. its post was deleted or the setting was cleared): the channel where
        // the latest group of this project (or any group) was posted — so members can still create groups.
        if (lfg is null) return null;
        var recent = lfg.Groups.Values.Where(gr => gr.GuildId == guild.Id && gr.ChannelId != 0).OrderByDescending(gr => gr.MessageId).ToList();
        return recent.Where(gr => projectKey is null || gr.ProjectKey == projectKey).Concat(recent)
            .Select(gr => guild.GetTextChannel(gr.ChannelId)).FirstOrDefault(c => c is not null && c is not SocketThreadChannel);
    }

    /// <summary>Ответы бота с Discord-разметкой → обычный текст для уведомления на сайте.</summary>
    private static string StripMarkdown(string text) => text.Replace("**", "").Replace("\n", " ");
}

public sealed record MemberRolesBody(List<string>? RoleIds);
public sealed record MascotEventBody(string? Kind, bool Quick, List<string>? Spells, bool Legendary, int? Legacy = null,
    int? Dodges = null, int? Dashes = null, bool Tired = false, int? Hour = null, string? Day = null, bool Fast = false, bool Long = false, bool Golden = false, bool Rainbow = false);
public sealed record SiteTrackBody(string? What, string? Day, string? Tab);
public sealed record JoinBody(string? Role, string? Character);
public sealed record CharacterBody(string? Name);
public sealed record CharacterProfileBody(string? ClassRoleId, string? Comment, int? Gs = null, int? Race = null, int? ServerId = null);
public sealed record DmBody(bool Enabled);
public sealed record RoomPrefsBody(string? Name, int? Limit, bool Locked);
public sealed record ApplyBody(string? ProjectKey, Dictionary<string, string?>? Answers);
public sealed record RoomActionBody(string? Action, string? Name, int? Limit);

public sealed record CharacterRenameBody(string? Name);
