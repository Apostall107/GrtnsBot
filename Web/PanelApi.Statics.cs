using System.Security.Claims;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

// Статики (КП) на сайте: список и поиск, создание, заявки; организатор — принять / отклонить (и перетаскиванием), состав, пинг
public static partial class PanelApi
{
    private static void MapMemberStatics(RouteGroupBuilder g)
    {
        g.MapGet("/statics", async (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, [FromServices] StaticStore store,
            [FromServices] StaticService statics, [FromServices] LfgProjectStore projects, [FromServices] LfgProfileService profiles,
            [FromServices] PanelAccess access, [FromServices] PanelTextStore panels) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var list = store.ForGuild(guildId).ToList();
            await WarmMembersAsync(client, guild, list.SelectMany(s => s.Members.Select(m => m.UserId).Concat(s.Applicants.Select(a => a.UserId))).Distinct());
            var me = user.UserId()!.Value;
            var admin = access.Has(me, PanelPermissions.Lfg);
            var member = guild.GetUser(me);
            var myProjects = projects.GetProjects(guildId).Where(p => LfgProjectStore.CanUse(member, p)).ToList();
            return Results.Ok(new
            {
                me = me.ToString(),
                // Создать можно, если где-то висит панель статиков (там появится пост)
                panelPosted = panels.GetPosted(guildId, "statics").Count > 0,
                maxOwned = StaticStore.MaxOwnedPerUser,
                maxPingChannels = StaticService.MaxPingChannels,
                // Каналы для «Ping members» выбирают только админы (право LFG): где пишут и они, и бот
                myChannels = member is not null && admin
                    ? guild.TextChannels.Where(c => StaticService.CanWrite(member, c) && StaticService.CanWrite(guild.CurrentUser, c))
                        .OrderBy(c => c.Category?.Position ?? -1).ThenBy(c => c.Position)
                        .Select(c => new { id = c.Id.ToString(), name = c.Name, category = c.Category?.Name })
                    : null,
                owned = store.OwnedBy(guildId, me),
                sizes = StaticService.FavoriteSizes,
                projects = myProjects.Select(p => new { key = p.Key, name = p.Name }),
                defaultProject = projects.Effective(guildId, null)?.Key,
                // Мои персонажи по проектам — для выбора при создании и заявке
                characters = (myProjects.Count > 0 ? myProjects.Select(p => (string?)p.Key) : [null]).Distinct()
                    .ToDictionary(k => k ?? "", k => statics.CharactersOf(guildId, me, k).Select(c => new { name = c.Name, icon = c.Icon, gs = c.Gs, cp = c.Cp, server = c.Server })),
                statics = list.Select(s =>
                {
                    var canManage = StaticService.CanManage(s, me, admin);
                    var mine = s.FindMember(me);
                    var myApp = s.FindApplicant(me);
                    return new
                    {
                        id = s.Id,
                        name = s.Name,
                        description = s.Description,
                        project = statics.ProjectName(s),
                        projectKey = projects.Effective(guildId, s.ProjectKey)?.Key,
                        owner = Member(guild, s.OwnerId),
                        size = s.Size,
                        count = s.Members.Count,
                        recruiting = s.Recruiting,
                        createdAt = s.CreatedAt.ToUnixTimeMilliseconds(),
                        jumpUrl = s.MessageId != 0 ? StaticService.JumpUrl(s) : null,
                        channel = guild.GetTextChannel(s.ChannelId)?.Name,
                        canManage,
                        isOwner = s.OwnerId == me,
                        status = mine is not null ? "member" : myApp is not null ? "applied" : null,
                        myCharacter = mine?.CharacterName ?? myApp?.CharacterName,
                        myMessage = myApp?.Message,
                        applicantCount = s.Applicants.Count,
                        pingChannels = canManage
                            ? (s.PingChannelIds.Count > 0 ? s.PingChannelIds : [s.ChannelId]).Select(c => new { id = c.ToString(), name = guild.GetTextChannel(c)?.Name ?? "deleted channel" })
                            : null,
                        // Класс, GS, CP и сервер — по актуальному профилю персонажа (удалён — по снимку)
                        members = s.Members.Select(m => (m, live: statics.Shown(s, m.UserId, m.CharacterName, m.ClassIcon, m.Gs, m.Cp))).Select(x => new
                        {
                            member = Member(guild, x.m.UserId),
                            character = x.m.CharacterName,
                            classIcon = x.live?.Icon,
                            gs = x.live?.Gs,
                            cp = x.live?.Cp,
                            server = x.live?.Server,
                            isOwner = x.m.UserId == s.OwnerId,
                            joinedAt = x.m.JoinedAt.ToUnixTimeMilliseconds()
                        }),
                        // Заявки и история — только тому, кто управляет статиком
                        applicants = canManage ? s.Applicants.Select(a => (a, live: statics.Shown(s, a.UserId, a.CharacterName, a.ClassIcon, a.Gs, a.Cp))).Select(x => new
                        {
                            member = Member(guild, x.a.UserId),
                            character = x.a.CharacterName,
                            classIcon = x.live?.Icon,
                            gs = x.live?.Gs,
                            cp = x.live?.Cp,
                            server = x.live?.Server,
                            message = x.a.Message,
                            appliedAt = x.a.AppliedAt.ToUnixTimeMilliseconds()
                        }) : null,
                        history = canManage
                            ? s.History.AsEnumerable().Reverse().Take(100).Select(h => new { at = h.At.ToUnixTimeMilliseconds(), member = Member(guild, h.UserId), text = h.Text })
                            : null
                    };
                })
            });
        });

        g.MapPost("/statics", async (ulong guildId, StaticCreateBody body, ClaimsPrincipal user, DiscordSocketClient client,
            [FromServices] StaticService statics, [FromServices] LfgProjectStore projects, [FromServices] PanelTextStore panels) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = user.UserId()!.Value;
            var project = projects.Effective(guildId, string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey);
            if (!LfgProjectStore.CanUse(guild.GetUser(me), project)) return Error("You don't have the role of this project.");
            // Пост — в канал панели статиков этого проекта, иначе любой панели статиков
            var posted = panels.GetPosted(guildId, "statics");
            var channelId = (posted.FirstOrDefault(p => p.ProjectKey is not null && p.ProjectKey == project?.Key) ?? posted.FirstOrDefault())?.ChannelId;
            if (channelId is null) return Error("The Statics panel isn't posted yet — ask an admin to post it in a channel.");
            if (await ActorAsync(client, guild, me) is not { } actor) return GuildNotFound();
            var (s, error) = await statics.CreateAsync(guildId, channelId.Value, actor, project?.Key, body.Name, body.Description,
                body.Size ?? 5, body.Character, "web");
            return s is null ? Error(StripMarkdown(error ?? "Something went wrong.")) : Results.Ok(new { id = s.Id, jumpUrl = StaticService.JumpUrl(s) });
        });

        g.MapPost("/statics/{id}/apply", async (ulong guildId, string id, StaticApplyBody body, ClaimsPrincipal user, DiscordSocketClient client,
            [FromServices] StaticStore store, [FromServices] StaticService statics, [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (store.Get(guildId, id) is not { } s) return Error("This static no longer exists.");
            var me = user.UserId()!.Value;
            if (!LfgProjectStore.CanUse(guild.GetUser(me), projects.Effective(guildId, s.ProjectKey))) return Error("You don't have the role of this static's project.");
            if (await ActorAsync(client, guild, me) is not { } actor) return GuildNotFound();
            var error = await statics.ApplyAsync(s, actor, body.Character, body.Message);
            return error is null ? Results.Ok() : Error(StripMarkdown(error));
        });

        // Все персонажи человека в проекте статика: класс, GS, а в проекте с оружейной AION — CP и сервер
        g.MapGet("/statics/{id}/characters/{userId}", (ulong guildId, string id, string userId, [FromServices] StaticStore store,
            [FromServices] StaticService statics) =>
        {
            if (store.Get(guildId, id) is not { } s) return Error("This static no longer exists.");
            if (ParseId(userId) is not { } uid) return Error("Unknown member.");
            return Results.Ok(new
            {
                project = statics.ProjectName(s),
                characters = statics.CharactersOf(guildId, uid, s.ProjectKey).Select(c => new { name = c.Name, classIcon = c.Icon, gs = c.Gs, cp = c.Cp, server = c.Server })
            });
        });

        g.MapPost("/statics/{id}/leave", async (ulong guildId, string id, ClaimsPrincipal user, [FromServices] StaticStore store,
            [FromServices] StaticService statics) =>
        {
            if (store.Get(guildId, id) is not { } s) return Error("This static no longer exists.");
            var (error, _) = await statics.LeaveAsync(s, user.UserId()!.Value);
            return error is null ? Results.Ok() : Error(StripMarkdown(error));
        });

        g.MapPost("/statics/{id}/character", async (ulong guildId, string id, StaticApplyBody body, ClaimsPrincipal user,
            [FromServices] StaticStore store, [FromServices] StaticService statics) =>
        {
            if (store.Get(guildId, id) is not { } s) return Error("This static no longer exists.");
            // Свой персонаж в составе или в заявке; сообщение — только к заявке
            var me = user.UserId()!.Value;
            var error = await statics.SetCharacterAsync(s, me, body.Character);
            if (error is null && body.Message is not null && s.FindApplicant(me) is not null) error = await statics.SetMessageAsync(s, me, body.Message);
            return error is null ? Results.Ok() : Error(StripMarkdown(error));
        });

        // ---------- Организатор (или владелец бота / админ с правом LFG) ----------

        g.MapPost("/statics/{id}/decide", (ulong guildId, string id, StaticDecideBody body, ClaimsPrincipal user, DiscordSocketClient client,
                [FromServices] StaticStore store, [FromServices] StaticService statics, [FromServices] PanelAccess access) =>
            ManageAsync(guildId, id, user, client, store, access, async (s, actor) =>
            {
                if (ParseId(body.UserId) is not { } target) return "Pick a person.";
                return body.Action switch
                {
                    "accept" => await statics.ApproveAsync(s, target, actor, "web"),
                    "decline" => await statics.DeclineAsync(s, target, actor, "web"),
                    "remove" => await statics.RemoveMemberAsync(s, target, actor, "web"),
                    "back" => await statics.ToApplicantsAsync(s, target, actor),
                    _ => "Unknown action."
                };
            }));

        g.MapPost("/statics/{id}/recruiting", (ulong guildId, string id, StaticRecruitingBody body, ClaimsPrincipal user, DiscordSocketClient client,
                [FromServices] StaticStore store, [FromServices] StaticService statics, [FromServices] PanelAccess access) =>
            ManageAsync(guildId, id, user, client, store, access, (s, actor) => statics.SetRecruitingAsync(s, body.Open, actor)));

        g.MapPut("/statics/{id}", (ulong guildId, string id, StaticEditBody body, ClaimsPrincipal user, DiscordSocketClient client,
                [FromServices] StaticStore store, [FromServices] StaticService statics, [FromServices] PanelAccess access) =>
            ManageAsync(guildId, id, user, client, store, access, (s, actor) => statics.EditAsync(s, body.Name, body.Description, body.Size ?? s.Size, actor)));

        g.MapDelete("/statics/{id}", (ulong guildId, string id, ClaimsPrincipal user, DiscordSocketClient client,
                [FromServices] StaticStore store, [FromServices] StaticService statics, [FromServices] PanelAccess access) =>
            ManageAsync(guildId, id, user, client, store, access, async (s, actor) => { await statics.DeleteAsync(s, actor, "web"); return null; }));

        g.MapPut("/statics/{id}/ping-channels", (ulong guildId, string id, StaticPingChannelsBody body, ClaimsPrincipal user, DiscordSocketClient client,
                [FromServices] StaticStore store, [FromServices] StaticService statics, [FromServices] PanelAccess access) =>
            !access.Has(user.UserId(), PanelPermissions.Lfg)
                ? Task.FromResult(Error("Only admins (LFG right) choose where pings of a static go."))
                : ManageAsync(guildId, id, user, client, store, access, (s, actor) =>
                statics.SetPingChannelsAsync(s, (body.ChannelIds ?? []).Select(ParseId).Where(c => c is not null).Select(c => c!.Value), actor)));

        g.MapPost("/statics/{id}/ping", async (ulong guildId, string id, StaticPingBody body, ClaimsPrincipal user, DiscordSocketClient client,
            [FromServices] StaticStore store, [FromServices] StaticService statics, [FromServices] PanelAccess access) =>
        {
            var count = 0;
            var result = await ManageAsync(guildId, id, user, client, store, access, async (s, actor) =>
            {
                var (error, n) = await statics.PingAsync(s, actor, body.Message, "web");
                count = n;
                return error;
            });
            return result is IStatusCodeHttpResult { StatusCode: 200 } ? Results.Ok(new { count }) : result;
        });

        // Пост удалили в Discord — опубликовать заново (в том же канале)
        g.MapPost("/statics/{id}/repost", (ulong guildId, string id, ClaimsPrincipal user, DiscordSocketClient client,
                [FromServices] StaticStore store, [FromServices] StaticService statics, [FromServices] PanelAccess access) =>
            ManageAsync(guildId, id, user, client, store, access, async (s, _) =>
            {
                if (s.MessageId != 0) return "The post is already there.";
                await s.Gate.WaitAsync();
                try { return await statics.PostAsync(s); }
                finally { s.Gate.Release(); }
            }));
    }

    /// <summary>Админ (право LFG): опубликовать панель статиков в канале.</summary>
    private static void MapStaticsAdmin(RouteGroupBuilder g)
    {
        // Опубликованные панели статиков и их проекты (статики с панели создаются под её проект)
        g.MapGet("/statics-panels", (ulong guildId, DiscordSocketClient client, PanelTextStore texts, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(new
            {
                projects = projects.GetProjects(guildId).Select(p => new { key = p.Key, name = p.Name }),
                defaultProject = projects.Effective(guildId, null)?.Key,
                panels = texts.GetPosted(guildId, "statics").Select(p => new
                {
                    messageId = p.MessageId.ToString(),
                    channelId = p.ChannelId.ToString(),
                    channel = guild.GetTextChannel(p.ChannelId)?.Name,
                    projectKey = p.ProjectKey,
                    sticky = p.Sticky,
                    jumpUrl = $"https://discord.com/channels/{guildId}/{p.ChannelId}/{p.MessageId}"
                })
            });
        });

        // ?project=key — панель сразу привязывается к проекту
        g.MapPost("/statics-panel", (ulong guildId, string? project, ChannelBody body, DiscordSocketClient client, PanelService panels,
            PanelTextStore texts, LfgProjectStore projects) =>
        {
            var key = string.IsNullOrWhiteSpace(project) ? null : project;
            if (key is not null && projects.Find(guildId, key) is null) return Task.FromResult(Error("Project not found."));
            return PostAsync(client, guildId, body.ChannelId, async ch =>
            {
                var message = await panels.PostAsync(ch, guildId, "statics");
                if (key is not null) texts.SetPanelProject(guildId, "statics", message.Id, key);
            });
        });

        // Sticky: держать панель внизу канала (бот публикует её заново после новых сообщений)
        g.MapPut("/statics-panels/{messageId}/sticky", (ulong guildId, ulong messageId, StickyBody body, PanelTextStore texts) =>
            texts.SetSticky(guildId, "statics", messageId, body.Sticky) ? Results.Ok() : Error("Panel not found — refresh the page."));

        g.MapPut("/statics-panels/{messageId}/project", (ulong guildId, ulong messageId, LfgPanelProjectBody body, LfgProjectStore projects,
            PanelTextStore texts) =>
        {
            var key = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey;
            if (key is not null && projects.Find(guildId, key) is null) return Error("Project not found.");
            return texts.SetPanelProject(guildId, "statics", messageId, key) ? Results.Ok() : Error("Panel not found.");
        });
    }

    /// <summary>Действие организатора: проверка прав (организатор, владелец бота, админ с правом LFG), ошибка — текстом.</summary>
    private static async Task<IResult> ManageAsync(ulong guildId, string id, ClaimsPrincipal user, DiscordSocketClient client, StaticStore store,
        PanelAccess access, Func<StaticGroup, IUser, Task<string?>> action)
    {
        if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
        if (store.Get(guildId, id) is not { } s) return Error("This static no longer exists.");
        var me = user.UserId()!.Value;
        if (!StaticService.CanManage(s, me, access.Has(me, PanelPermissions.Lfg))) return Error("Only the organizer can do this.");
        if (await ActorAsync(client, guild, me) is not { } actor) return GuildNotFound();
        var error = await action(s, actor);
        return error is null ? Results.Ok() : Error(StripMarkdown(error));
    }

    /// <summary>Человек с сайта как пользователь Discord (для журнала и ЛС): участник сервера, иначе через REST.</summary>
    private static async Task<IUser?> ActorAsync(DiscordSocketClient client, SocketGuild guild, ulong userId)
    {
        if (guild.GetUser(userId) is { } member) return member;
        try { return await client.Rest.GetUserAsync(userId); }
        catch (Discord.Net.HttpException) { return null; }
    }
}

public sealed record StaticCreateBody(string? ProjectKey, string? Name, string? Description, int? Size, string? Character);
public sealed record StaticApplyBody(string? Character, string? Message);
public sealed record StaticDecideBody(string? UserId, string? Action);
public sealed record StaticRecruitingBody(bool Open);
public sealed record StaticEditBody(string? Name, string? Description, int? Size);
public sealed record StaticPingBody(string? Message);
public sealed record StaticPingChannelsBody(List<string>? ChannelIds);
