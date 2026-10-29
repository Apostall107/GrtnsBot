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
