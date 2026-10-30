using System.Security.Claims;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
        g.MapGet("/groups", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, LfgStore lfg, CharacterStore characters,
            LfgSignupService signup, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
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
                        blocked = signup.ProjectBlock(x, me) is { } b ? StripMarkdown(System.Text.RegularExpressions.Regex.Replace(b, "<@&(\\d+)>",
                            m => "@" + (guild.GetRole(ulong.Parse(m.Groups[1].Value))?.Name ?? "role"))) : null,
                        characters = characters.Get(guildId, me, signup.Scope(x)),
                        owner = Member(guild, x.OwnerId),
                        channel = guild.GetTextChannel(x.ChannelId)?.Name,
                        startsAt = x.StartsAt?.ToUnixTimeMilliseconds(),
                        filled = x.FilledCount,
                        size = x.Slots.Count,
                        jumpUrl = LfgSignupService.JumpUrl(x),
                        closed = x.IsClosed,
                        isOrganizer = x.OwnerId == me,
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
                            member = s.UserId is { } u ? Member(guild, u) : null,
                            character = s.CharacterName,
                            classRole = s.ClassRoleId is { } cr ? Role(guild, cr) : null,
                            classIcon = s.ClassIcon,
                            gs = s.Gs,
                            attended = s.Attended
                        }),
                        reserve = x.Reserve.Select(r => new
                        {
                            emoji = r.Role.Emoji(),
                            member = Member(guild, r.UserId),
                            character = r.CharacterName,
                            classRole = r.ClassRoleId is { } rr ? Role(guild, rr) : null,
                            classIcon = r.ClassIcon,
                            gs = r.Gs
                        })
                    };
                })
            });
        });

        // Записаться: роль + персонаж (новый персонаж сохраняется, как в Discord)
        g.MapPost("/groups/{groupId}/join", async (ulong guildId, string groupId, JoinBody body, ClaimsPrincipal user,
            LfgStore lfg, CharacterStore characters, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            if (signup.ProjectBlock(group, user.UserId()!.Value) is not null) return Error("You don't have the role of this group's project.");
            if (!Enum.TryParse<SlotRole>(body.Role, true, out var role)) return Error("Pick a role.");
            var name = (body.Character ?? "").Replace("`", "").Trim();
            if (name.Length is 0 or > CharacterStore.MaxNameLength) return Error($"Character name: 1–{CharacterStore.MaxNameLength} characters.");

            var me = user.UserId()!.Value;
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
            return error is null ? Results.Ok() : Error(error);
        });

        // Мои персонажи: класс-роль и заметка
        // Мои персонажи выбранного проекта (?project=key, пусто — по умолчанию): класс, GS, заметка
        g.MapGet("/characters", (ulong guildId, string? project, ClaimsPrincipal user, DiscordSocketClient client, CharacterStore characters,
            LfgProfileService profiles, LfgNotifyStore notify, LfgProjectStore projects) =>
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
                classRoles = profiles.ClassRoles(guildId, me).Select(r => new
                {
                    id = r.Id.ToString(),
                    name = r.Name,
                    color = r.Color.RawValue == 0 ? null : $"#{r.Color.RawValue:x6}",
                    icon = profiles.Icon(guildId, r)
                }),
                characters = characters.Get(guildId, me, scope).Select(name =>
                {
                    var info = characters.GetInfo(guildId, me, name, scope);
                    return new
                    {
                        name,
                        classRole = info?.ClassRoleId is { } id ? Role(guild, id) : null,
                        comment = info?.Comment,
                        gs = info?.Gs
                    };
                }),
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
            DiscordSocketClient client, CharacterStore characters, LfgProfileService profiles, LfgProjectStore projects) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var me = user.UserId()!.Value;
            var exact = characters.Get(guildId, me, scope.Value).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return Error("Character not found.");

            ulong? roleId = ParseId(body.ClassRoleId);
            if (roleId is { } r && profiles.ClassRoles(guildId, me).All(x => x.Id != r)) return Error("You can't pick this class role.");
            if (body.Comment is { Length: > CharacterStore.MaxCommentLength }) return Error($"Note: up to {CharacterStore.MaxCommentLength} characters.");
            if (body.Gs is < 0 or > 99999) return Error("GS: 0–99999.");

            characters.SetInfo(guildId, me, exact, true, roleId, true, body.Comment, scope.Value);
            characters.SetGs(guildId, me, exact, body.Gs is > 0 ? body.Gs : null, scope.Value);
            var groups = await profiles.RefreshCharacterAsync(guildId, me, exact, scope.Value);
            return Results.Ok(new { groupsUpdated = groups });
        });

        g.MapDelete("/characters/{name}", (ulong guildId, string name, string? project, ClaimsPrincipal user, DiscordSocketClient client,
            CharacterStore characters, LfgProjectStore projects) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            return characters.Remove(guildId, user.UserId()!.Value, name, scope.Value) ? Results.Ok() : Error("Character not found.");
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
            BotSettingsStore settings, PanelTextStore panels, LfgTemplateStore templates, LfgProjectStore projects, PanelAccess access) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var member = guild.GetUser(me);
            var channel = LfgChannel(guild, settings, panels);
            return Results.Ok(new
            {
                // Проекты, роли которых у меня есть, и канал, куда уйдёт сбор каждого
                projects = projects.GetProjects(guildId).Where(p => LfgProjectStore.CanUse(member, p)).Select(p => new
                {
                    key = p.Key,
                    name = p.Name,
                    isDefault = p.Key == projects.DefaultKey(guildId),
                    channel = LfgChannel(guild, settings, panels, p.Key) is { } c ? new { id = c.Id.ToString(), name = c.Name } : null
                }),
                // Админ может продублировать пост в другие каналы
                mirrorChannels = access.IsAdmin(me)
                    ? guild.TextChannels.Where(c => c is not SocketVoiceChannel and not SocketThreadChannel)
                        .Where(c => guild.CurrentUser.GetPermissions(c) is { ViewChannel: true, SendMessages: true, EmbedLinks: true })
                        .OrderBy(c => c.Position).Select(c => new { id = c.Id.ToString(), name = c.Name })
                    : null,
                activities = activities.Get(guildId).Select(a => new { key = a.Key, name = a.Name, role = Role(guild, a.RoleId) }),
                templates = TemplatesDto(guild, templates),
                // Роли для требований сбора (организатор выбирает, какие нужны)
                roles = guild.Roles.Where(r => !r.IsEveryone && !r.IsManaged).OrderByDescending(r => r.Position)
                    .Select(r => new { id = r.Id.ToString(), name = r.Name, color = r.Color.RawValue == 0 ? null : $"#{r.Color.RawValue:x6}" }),
                channel = channel is null ? null : new { id = channel.Id.ToString(), name = channel.Name }
            });
        });

        // Создать сбор (как /lfg): организатор — я
        g.MapPost("/create", async (ulong guildId, LfgCreateBody body, ClaimsPrincipal user, DiscordSocketClient client,
            ActivityStore activities, LfgPublisher publisher, BotSettingsStore settings, PanelTextStore panels,
            LfgProjectStore projects, PanelAccess access, [Microsoft.AspNetCore.Mvc.FromServices] TimeService time) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var project = projects.Effective(guildId, body.ProjectKey);
            if (!LfgProjectStore.CanUse(guild.GetUser(me), project)) return Error($"You need the {project!.Name} role to create its groups.");
            // Канал выбирает не участник, а админ — пост уходит в канал проекта / канал для сборов
            if (LfgChannel(guild, settings, panels, project?.Key) is not { } lfgChannel)
                return Error("The channel for groups is not set yet. Ask an admin (LFG → Channel for groups).");
            var (err, channelId, name, pingRole, slots) = ResolveLfgInput(guild, activities, lfgChannel.Id.ToString(), body.ActivityKey, body.CustomName, body.Slots);
            if (err is not null) return Error(err);

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
                var (group, jumpUrl, dms) = await publisher.PublishAsync(new LfgPublishRequest(
                    guildId, channelId, me, name!, pingRole, slots!, startsAt, body.NotifyByDm, true,
                    Requirements(guild, body.RequiredRoleIds), body.MinGs is > 0 and <= 99999 ? body.MinGs : null,
                    project?.Key,
                    // Дублировать пост в другие каналы может только админ
                    access.IsAdmin(me) ? ParseIds(body.MirrorChannelIds).Where(id => guild.GetTextChannel(id) is not null).Take(5).ToList() : null,
                    body.CreateVoice));
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

        g.MapPost("/groups/{groupId}/time", (ulong guildId, string groupId, TimeBody body, ClaimsPrincipal user, DiscordSocketClient client,
            LfgStore lfg, LfgSignupService signup, [Microsoft.AspNetCore.Mvc.FromServices] TimeService time) =>
            RescheduleEndpointAsync(guildId, groupId, body, user, client, lfg, signup, time, organizerOnly: true));

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
        g.MapGet("/tickets/projects", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, TicketStore tickets) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            return Results.Ok(tickets.GetProjects(guildId).Select(p =>
            {
                var open = tickets.FindOpenTicket(guildId, me, p.Key);
                return new
                {
                    key = p.Key,
                    name = p.Name,
                    openTicketUrl = open is not null && guild.GetTextChannel(open.TextChannelId) is not null
                        ? $"https://discord.com/channels/{guildId}/{open.TextChannelId}" : null,
                    questions = p.FormEnabled
                        ? p.Questions.Take(TicketQuestion.MaxQuestions).Select(q => new
                        {
                            id = q.Id, label = q.Label, placeholder = q.Placeholder, @long = q.Long, required = q.Required, maxLength = q.MaxLength
                        })
                        : null
                };
            }));
        });

        g.MapPost("/tickets", async (ulong guildId, ApplyBody body, ClaimsPrincipal user, DiscordSocketClient client,
            TicketStore tickets, TicketService service) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (guild.GetUser(user.UserId()!.Value) is not { } member) return Error("You are not on this server.");
            if (tickets.FindProject(guildId, body.ProjectKey ?? "") is not { } project) return Error("This project no longer exists.");

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

    /// <summary>Ключ хранения персонажей проекта; null — нет роли проекта (менять его персонажей нельзя).</summary>
    private static (string? Value, bool Ok)? CharacterScope(ulong guildId, string? project, ClaimsPrincipal user, DiscordSocketClient client,
        LfgProjectStore projects)
    {
        var current = projects.Effective(guildId, string.IsNullOrWhiteSpace(project) ? null : project);
        if (!LfgProjectStore.CanUse(client.GetGuild(guildId)?.GetUser(user.UserId()!.Value), current)) return null;
        return (projects.Scope(guildId, current?.Key), true);
    }

    /// <summary>Канал для сборов с сайта: из настроек, иначе канал первой LFG-панели.</summary>
    private static SocketTextChannel? LfgChannel(SocketGuild guild, BotSettingsStore settings, PanelTextStore panels, string? projectKey = null)
    {
        // У проекта своя LFG-панель (LFG → Projects) — сборы проекта идут в её канал
        if (projectKey is not null &&
            panels.GetPosted(guild.Id, "lfg").Where(p => p.ProjectKey == projectKey)
                .Select(p => guild.GetTextChannel(p.ChannelId)).FirstOrDefault(c => c is not null) is { } projectChannel)
            return projectChannel;
        if (settings.GetLfgChannel(guild.Id) is { } id && guild.GetTextChannel(id) is { } set) return set;
        return panels.GetPosted(guild.Id, "lfg").Select(p => guild.GetTextChannel(p.ChannelId)).FirstOrDefault(c => c is not null);
    }

    /// <summary>Ответы бота с Discord-разметкой → обычный текст для уведомления на сайте.</summary>
    private static string StripMarkdown(string text) => text.Replace("**", "").Replace("\n", " ");
}

public sealed record MemberRolesBody(List<string>? RoleIds);
public sealed record JoinBody(string? Role, string? Character);
public sealed record CharacterBody(string? Name);
public sealed record CharacterProfileBody(string? ClassRoleId, string? Comment, int? Gs = null);
public sealed record DmBody(bool Enabled);
public sealed record RoomPrefsBody(string? Name, int? Limit, bool Locked);
public sealed record ApplyBody(string? ProjectKey, Dictionary<string, string?>? Answers);
public sealed record RoomActionBody(string? Action, string? Name, int? Limit);
