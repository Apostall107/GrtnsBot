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
        g.MapGet("/groups", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, LfgStore lfg, CharacterStore characters) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            return Results.Ok(new
            {
                characters = characters.Get(guildId, me),
                groups = ActiveGroups(lfg, guildId).Select(x =>
                {
                    var mySlot = x.Slots.Find(s => s.UserId == me);
                    var myReserve = x.FindReserve(me);
                    return new
                    {
                        id = x.Id,
                        activity = x.ActivityName,
                        owner = Member(guild, x.OwnerId),
                        channel = guild.GetTextChannel(x.ChannelId)?.Name,
                        startsAt = x.StartsAt?.ToUnixTimeMilliseconds(),
                        filled = x.FilledCount,
                        size = x.Slots.Count,
                        jumpUrl = LfgSignupService.JumpUrl(x),
                        closed = x.IsClosed,
                        isOrganizer = x.OwnerId == me,
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
                            comment = s.Comment
                        }),
                        reserve = x.Reserve.Select(r => new
                        {
                            emoji = r.Role.Emoji(),
                            member = Member(guild, r.UserId),
                            character = r.CharacterName,
                            classRole = r.ClassRoleId is { } rr ? Role(guild, rr) : null,
                            classIcon = r.ClassIcon,
                            comment = r.Comment
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
            if (!Enum.TryParse<SlotRole>(body.Role, true, out var role)) return Error("Pick a role.");
            var name = (body.Character ?? "").Replace("`", "").Trim();
            if (name.Length is 0 or > CharacterStore.MaxNameLength) return Error($"Character name: 1–{CharacterStore.MaxNameLength} characters.");

            var me = user.UserId()!.Value;
            await group.Gate.WaitAsync();
            string? error;
            try { error = group.CheckJoin(me, role); }
            finally { group.Gate.Release(); }
            if (error is not null) return Error(error);

            characters.Add(guildId, me, name);
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
        g.MapGet("/characters", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, CharacterStore characters,
            LfgProfileService profiles, LfgNotifyStore notify) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            return Results.Ok(new
            {
                dmEnabled = notify.IsEnabled(guildId, me),
                classRoles = profiles.ClassRoles(guildId, me).Select(r => new
                {
                    id = r.Id.ToString(),
                    name = r.Name,
                    color = r.Color.RawValue == 0 ? null : $"#{r.Color.RawValue:x6}",
                    icon = LfgSignupService.IconOf(r)
                }),
                characters = characters.Get(guildId, me).Select(name =>
                {
                    var info = characters.GetInfo(guildId, me, name);
                    return new
                    {
                        name,
                        classRole = info?.ClassRoleId is { } id ? Role(guild, id) : null,
                        comment = info?.Comment
                    };
                }),
                maxCharacters = CharacterStore.MaxPerUser,
                maxComment = CharacterStore.MaxCommentLength
            });
        });

        g.MapPost("/characters", (ulong guildId, CharacterBody body, ClaimsPrincipal user, CharacterStore characters) =>
        {
            var name = (body.Name ?? "").Replace("`", "").Trim();
            if (name.Length is 0 or > CharacterStore.MaxNameLength) return Error($"Character name: 1–{CharacterStore.MaxNameLength} characters.");
            characters.Add(guildId, user.UserId()!.Value, name);
            return Results.Ok();
        });

        g.MapPut("/characters/{name}", async (ulong guildId, string name, CharacterProfileBody body, ClaimsPrincipal user,
            CharacterStore characters, LfgProfileService profiles) =>
        {
            var me = user.UserId()!.Value;
            var exact = characters.Get(guildId, me).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return Error("Character not found.");

            ulong? roleId = ParseId(body.ClassRoleId);
            if (roleId is { } r && profiles.ClassRoles(guildId, me).All(x => x.Id != r)) return Error("You don't have this role.");
            if (body.Comment is { Length: > CharacterStore.MaxCommentLength }) return Error($"Note: up to {CharacterStore.MaxCommentLength} characters.");

            characters.SetInfo(guildId, me, exact, true, roleId, true, body.Comment);
            var groups = await profiles.RefreshCharacterAsync(guildId, me, exact);
            return Results.Ok(new { groupsUpdated = groups });
        });

        g.MapDelete("/characters/{name}", (ulong guildId, string name, ClaimsPrincipal user, CharacterStore characters) =>
            characters.Remove(guildId, user.UserId()!.Value, name) ? Results.Ok() : Error("Character not found."));

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

        // Для создания сбора: активности и каналы, где и я, и бот можем писать (каналы с LFG-панелью — первыми)
        g.MapGet("/create-options", (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, ActivityStore activities,
            PanelTextStore panels) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var member = guild.GetUser(user.UserId()!.Value);
            var lfgChannels = panels.GetPosted(guildId, "lfg").Select(p => p.ChannelId).ToHashSet();
            return Results.Ok(new
            {
                activities = activities.Get(guildId).Select(a => new { key = a.Key, name = a.Name, role = Role(guild, a.RoleId) }),
                channels = guild.TextChannels
                    .Where(c => c is not SocketVoiceChannel and not SocketThreadChannel)
                    .Where(c =>
                    {
                        var bot = guild.CurrentUser.GetPermissions(c);
                        var mine = member?.GetPermissions(c);
                        return bot.ViewChannel && bot.SendMessages && bot.EmbedLinks && mine is { ViewChannel: true, SendMessages: true };
                    })
                    .OrderByDescending(c => lfgChannels.Contains(c.Id)).ThenBy(c => c.Position)
                    .Select(c => new { id = c.Id.ToString(), name = c.Name, category = c.Category?.Name, canPost = true, lfg = lfgChannels.Contains(c.Id) })
            });
        });

        // Создать сбор (как /lfg): организатор — я
        g.MapPost("/create", async (ulong guildId, LfgCreateBody body, ClaimsPrincipal user, DiscordSocketClient client,
            ActivityStore activities, LfgPublisher publisher, [Microsoft.AspNetCore.Mvc.FromServices] TimeService time) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var (err, channelId, name, pingRole, slots) = ResolveLfgInput(guild, activities, body.ChannelId, body.ActivityKey, body.CustomName, body.Slots);
            if (err is not null) return Error(err);
            var perms = guild.GetUser(me)?.GetPermissions(guild.GetTextChannel(channelId)!);
            if (perms is not { ViewChannel: true, SendMessages: true }) return Error("You can't write in that channel.");

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
                    guildId, channelId, me, name!, pingRole, slots!, startsAt, body.NotifyByDm, true));
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

        g.MapPost("/groups/{groupId}/close", async (ulong guildId, string groupId, ClaimsPrincipal user, LfgStore lfg, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            if (group.OwnerId != user.UserId()) return Error("Only the organizer can close this group.");
            await CloseGroupAsync(group, lfg, signup);
            return Results.Ok();
        });

        g.MapPut("/dm", (ulong guildId, DmBody body, ClaimsPrincipal user, LfgNotifyStore notify) =>
        {
            notify.SetEnabled(guildId, user.UserId()!.Value, body.Enabled);
            return Results.Ok();
        });
    }

    /// <summary>Ответы бота с Discord-разметкой → обычный текст для уведомления на сайте.</summary>
    private static string StripMarkdown(string text) => text.Replace("**", "").Replace("\n", " ");
}

public sealed record MemberRolesBody(List<string>? RoleIds);
public sealed record JoinBody(string? Role, string? Character);
public sealed record CharacterBody(string? Name);
public sealed record CharacterProfileBody(string? ClassRoleId, string? Comment);
public sealed record DmBody(bool Enabled);
public sealed record RoomActionBody(string? Action, string? Name, int? Limit);
