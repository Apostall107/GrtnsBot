using System.Security.Claims;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LfgBot.Web;

/// <summary>
/// JSON API веб-админки. Повторяет действия /admin (LFG, Tickets, Temp Voice) и работает
/// с теми же хранилищами, что и бот, поэтому изменения сразу видны и в Discord.
/// ID Discord отдаются строками: в JavaScript числа больше 2^53 теряют точность.
/// </summary>
public static partial class PanelApi
{
    private const int MaxNameLength = 100;
    private const string DefaultCreatorName = "➕ Create room";

    public static void Map(WebApplication app)
    {
        // Кто вошёл — доступно любому вошедшему, чтобы страница могла показать «нет доступа»
        app.MapGet("/api/me", (ClaimsPrincipal user, PanelAccess access, WebPanelState state) =>
        {
            if (user.Identity?.IsAuthenticated != true)
                return Results.Json(new { authenticated = false, loginConfigured = state.LoginConfigured }, statusCode: 401);

            var id = user.UserId();
            var avatar = user.FindFirst(PanelClaims.Avatar)?.Value;
            return Results.Ok(new
            {
                authenticated = true,
                allowed = access.IsAllowed(id),
                isOwner = access.IsOwner(id),
                viewAsMember = access.ViewingAsMember(id),
                isAdmin = access.IsAdmin(id),
                permissions = access.Permissions(id),
                memberGuilds = access.MemberGuilds(id).Select(x => x.ToString()),
                id = id?.ToString(),
                name = user.FindFirst(ClaimTypes.Name)?.Value,
                avatarUrl = avatar is null || id is null ? null : $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png?size=64"
            });
        });

        var api = app.MapGroup("/api")
            .RequireAuthorization(WebPanel.Policy)
            // Журнал: каждое успешное изменение с сайта (см. PanelApi.Audit.cs)
            .AddEndpointFilter(AuditFilter)
            .AddEndpointFilter(async (ctx, next) =>
            {
                // Защита от CSRF: свой заголовок нельзя отправить с чужого сайта без CORS
                var http = ctx.HttpContext;
                if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers[WebPanel.CsrfHeader] != "1")
                    return Results.BadRequest(new { error = "Missing X-Panel header." });
                return await next(ctx);
            });

        // Админ видит все сервера бота, участник — только те, где у него роль «участник»
        api.MapGet("/guilds", (ClaimsPrincipal user, PanelAccess access, DiscordSocketClient client) => Results.Ok(new
        {
            connection = client.ConnectionState.ToString(),
            latency = client.Latency,
            bot = client.CurrentUser is { } me ? new { id = me.Id.ToString(), name = me.Username, avatarUrl = me.GetAvatarUrl() } : null,
            guilds = client.Guilds
                .Where(g => access.IsAdmin(user.UserId()) || access.IsMember(user.UserId(), g.Id))
                .OrderBy(g => g.Name).Select(g => new
            {
                id = g.Id.ToString(),
                name = g.Name,
                isMember = access.IsMember(user.UserId(), g.Id),
                iconUrl = g.IconUrl,
                members = g.MemberCount
            })
        }));

        var guild = api.MapGroup("/guilds/{guildId}");
        MapCommon(guild);
        // Каждый модуль — своя группа с проверкой права (владелец бота проходит всегда)
        MapLfg(guild.MapGroup("/lfg").AddEndpointFilter(Require(PanelPermissions.Lfg)));
        MapTickets(guild.MapGroup("/tickets").AddEndpointFilter(Require(PanelPermissions.Tickets)));
        MapVoice(guild.MapGroup("/voice").AddEndpointFilter(Require(PanelPermissions.Voice)));
        MapTexts(guild.MapGroup("/texts").AddEndpointFilter(Require(PanelPermissions.Texts)));
        MapMember(guild.MapGroup("/me").AddEndpointFilter(RequireMember));
        MapMemberRoles(guild.MapGroup("/member-roles").AddEndpointFilter(Require(PanelPermissions.Settings)));
        MapLfgExtra(guild.MapGroup("/lfg").AddEndpointFilter(Require(PanelPermissions.Lfg)));
        MapLog(guild.MapGroup("/log").AddEndpointFilter(Require(PanelPermissions.Log)));
        MapSettings(api.MapGroup("/settings").AddEndpointFilter(Require(PanelPermissions.Settings)));
        // Часовой пояс, автоудаление, напоминание — это настройки LFG: на сайте они во вкладке LFG (право LFG)
        MapSettings(guild.MapGroup("/lfg/settings").AddEndpointFilter(Require(PanelPermissions.Lfg)));
        MapPosts(guild.MapGroup("/posts").AddEndpointFilter(Require(PanelPermissions.Posts)));
        MapAnnounce(guild.MapGroup("/announce").AddEndpointFilter(Require(PanelPermissions.Announce)));
        MapBackup(api.MapGroup("/backup").AddEndpointFilter(Require(PanelPermissions.Access)));
        MapAccess(api.MapGroup("/access").AddEndpointFilter(Require(PanelPermissions.Access)));
    }

    // ================= Общее: сводка, роли, каналы, участники =================

    private static void MapCommon(RouteGroupBuilder g)
    {
        // Общие данные сервера — только для админов (у участника свой раздел /me)
        foreach (var endpoint in MapCommonEndpoints(g)) endpoint.AddEndpointFilter(RequireAnyAdmin);
    }

    private static IEnumerable<RouteHandlerBuilder> MapCommonEndpoints(RouteGroupBuilder g)
    {
        yield return g.MapGet("/overview", (ulong guildId, DiscordSocketClient client, ActivityStore activities, LfgStore lfg,
            TicketStore tickets, TempVoiceStore voice) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(new
            {
                guild = new { id = guild.Id.ToString(), name = guild.Name, iconUrl = guild.IconUrl, members = guild.MemberCount },
                lfg = new { activities = activities.Get(guildId).Count, groups = ActiveGroups(lfg, guildId).Count },
                tickets = new { projects = tickets.GetProjects(guildId).Count, open = tickets.GetTickets(guildId).Count },
                voice = new { creators = voice.GetCreators(guildId).Count, rooms = LiveRooms(guild, voice).Count }
            });
        });

        yield return g.MapGet("/roles", (ulong guildId, DiscordSocketClient client) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(guild.Roles
                .Where(r => !r.IsEveryone && !r.IsManaged)
                .OrderByDescending(r => r.Position)
                .Select(r => new { id = r.Id.ToString(), name = r.Name, color = r.Colors.PrimaryColor.RawValue == 0 ? null : $"#{r.Colors.PrimaryColor.RawValue:x6}" }));
        });

        yield return g.MapGet("/channels", (ulong guildId, DiscordSocketClient client) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var me = guild.CurrentUser;

            var text = guild.TextChannels
                .Where(c => c is not SocketVoiceChannel and not SocketThreadChannel)
                .OrderBy(c => c.Position)
                .Select(c =>
                {
                    var p = me.GetPermissions(c);
                    return new
                    {
                        id = c.Id.ToString(),
                        name = c.Name,
                        category = c.Category?.Name,
                        canPost = p.ViewChannel && p.SendMessages && p.EmbedLinks
                    };
                });
            var voiceChannels = guild.VoiceChannels
                .Where(c => c is not SocketStageChannel)
                .OrderBy(c => c.Position)
                .Select(c => new { id = c.Id.ToString(), name = c.Name, category = c.Category?.Name });
            var categories = guild.CategoryChannels
                .OrderBy(c => c.Position)
                .Select(c => new { id = c.Id.ToString(), name = c.Name });

            return Results.Ok(new { text, voice = voiceChannels, categories });
        });

        yield return g.MapGet("/members", (ulong guildId, string? q, DiscordSocketClient client) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var query = (q ?? "").Trim();
            bool Has(string? text) => text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool Starts(string? text) => text is not null && text.StartsWith(query, StringComparison.OrdinalIgnoreCase);
            // Ник на сервере, глобальное имя и логин; сначала те, что начинаются с запроса
            var list = guild.Users
                .Where(u => !u.IsBot)
                .Where(u => query.Length == 0 || Has(u.DisplayName) || Has(u.GlobalName) || Has(u.Username))
                .OrderByDescending(u => query.Length > 0 && (Starts(u.DisplayName) || Starts(u.GlobalName) || Starts(u.Username)))
                .ThenBy(u => u.DisplayName)
                .Take(20)
                .Select(u => Member(guild, u.Id));
            return Results.Ok(list);
        });
    }

    // ================= LFG =================

    private static void MapLfg(RouteGroupBuilder g)
    {
        g.MapGet("/activities", (ulong guildId, DiscordSocketClient client, ActivityStore activities) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(activities.Get(guildId).Select(a => new
            {
                key = a.Key,
                name = a.Name,
                role = Role(guild, a.RoleId),
                projectKey = a.ProjectKey
            }));
        });

        g.MapPost("/activities", (ulong guildId, ActivityBody body, DiscordSocketClient client, ActivityStore activities, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > MaxNameLength) return Error($"The name must be 1–{MaxNameLength} characters.");
            if (ParseId(body.RoleId) is not { } roleId || guild.GetRole(roleId) is null) return Error("Pick a role to ping.");
            var projectKey = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey;
            if (projectKey is not null && projects.Find(guildId, projectKey) is null) return Error("Project not found.");

            var error = activities.Add(guildId, name, roleId, projectKey);
            return error is null ? Results.Ok() : Error(error);
        });

        // Переименовать активность / сменить роль. Уже опубликованные сборы не меняются.
        g.MapPut("/activities/{key}", (ulong guildId, string key, ActivityBody body, DiscordSocketClient client, ActivityStore activities) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > MaxNameLength) return Error($"The name must be 1–{MaxNameLength} characters.");
            if (ParseId(body.RoleId) is not { } roleId || guild.GetRole(roleId) is null) return Error("Pick a role to ping.");
            var error = activities.Update(guildId, key, name, roleId);
            return error is null ? Results.Ok() : Error(error);
        });

        // Активность → проект (пусто — общая для всех проектов)
        g.MapPut("/activities/{key}/project", (ulong guildId, string key, LfgPanelProjectBody body, ActivityStore activities, LfgProjectStore projects) =>
        {
            var projectKey = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey;
            if (projectKey is not null && projects.Find(guildId, projectKey) is null) return Error("Project not found.");
            return activities.SetProject(guildId, key, projectKey) ? Results.Ok() : Error("Activity not found.");
        });

        g.MapDelete("/activities/{key}", (ulong guildId, string key, ActivityStore activities) =>
            activities.Remove(guildId, key) is null ? Error("Activity not found.") : Results.Ok());

        g.MapGet("/groups", (ulong guildId, DiscordSocketClient client, LfgStore lfg, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var defaultProject = projects.DefaultKey(guildId);
            return Results.Ok(ActiveGroups(lfg, guildId).Select(x => new
            {
                id = x.Id,
                projectKey = x.ProjectKey ?? defaultProject,
                activity = x.ActivityName,
                owner = Member(guild, x.OwnerId),
                channel = guild.GetTextChannel(x.ChannelId)?.Name,
                startsAt = x.StartsAt?.ToUnixTimeMilliseconds(),
                expiresAt = x.ExpiresAt.ToUnixTimeMilliseconds(),
                filled = x.FilledCount,
                size = x.Slots.Count,
                jumpUrl = LfgSignupService.JumpUrl(x),
                voiceUrl = x.VoiceChannelId is { } vc ? $"https://discord.com/channels/{x.GuildId}/{vc}" : null,
                started = x.StartsAt is null || x.StartsAt <= DateTimeOffset.UtcNow,
                requirements = x.HasRequirements ? new { roles = x.RequiredRoleIds.Select(id => Role(guild, id)), minGs = x.MinGs } : null,
                slots = x.Slots.Select(s => new
                {
                    role = (s.TakenAs ?? s.Role).Label(),
                    emoji = (s.TakenAs ?? s.Role).Emoji(),
                    slotRole = s.Role.Label(),
                    member = s.UserId is { } u ? Member(guild, u) : null,
                    character = s.CharacterName,
                    classRole = s.ClassRoleId is { } cr ? Role(guild, cr) : null,
                    classIcon = s.ClassIcon,
                    comment = s.Comment,
                    gs = s.Gs,
                    attended = s.Attended
                }),
                reserve = x.Reserve.Select(r => new
                {
                    role = r.Role.Label(),
                    emoji = r.Role.Emoji(),
                    member = Member(guild, r.UserId),
                    character = r.CharacterName,
                    classRole = r.ClassRoleId is { } rr ? Role(guild, rr) : null,
                    classIcon = r.ClassIcon,
                    comment = r.Comment,
                    gs = r.Gs
                })
            }));
        });

        g.MapPost("/groups/{groupId}/close", async (ulong guildId, string groupId, LfgStore lfg, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId)
                return Error("This group is no longer active.");

            await group.Gate.WaitAsync();
            try
            {
                if (lfg.Groups.TryRemove(groupId, out _))
                {
                    group.IsClosed = true;
                    lfg.RecordClosed(group, "force-closed");
                    lfg.SaveGroups();
                    await signup.DeleteMessagesAsync(group); // пост и напоминание
                }
            }
            finally
            {
                group.Gate.Release();
            }
            return Results.Ok();
        });

        // ?project=key — панель сразу привязывается к проекту (сборы с неё идут под этот проект)
        g.MapPost("/panel", (ulong guildId, string? project, ChannelBody body, DiscordSocketClient client, PanelService panels,
            PanelTextStore texts, LfgProjectStore projects) =>
        {
            var key = string.IsNullOrWhiteSpace(project) ? null : project;
            if (key is not null && projects.Find(guildId, key) is null) return Task.FromResult(Error("Project not found."));
            return PostAsync(client, guildId, body.ChannelId, async ch =>
            {
                var message = await panels.PostAsync(ch, guildId, "lfg");
                if (key is not null) texts.SetPanelProject(guildId, "lfg", message.Id, key);
            });
        });
    }

    // ================= Tickets =================

    private static void MapTickets(RouteGroupBuilder g)
    {
        g.MapGet("/projects", (ulong guildId, DiscordSocketClient client, TicketStore tickets) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var open = tickets.GetTickets(guildId);
            return Results.Ok(tickets.GetProjects(guildId).Select(p => new
            {
                key = p.Key,
                name = p.Name,
                staffRoles = p.StaffRoleIds.Select(id => Role(guild, id)),
                memberRoles = p.MemberRoleIds.Select(id => Role(guild, id)),
                category = p.CategoryId is { } c ? new { id = c.ToString(), name = guild.GetCategoryChannel(c)?.Name } : null,
                customText = p.CustomText,
                formEnabled = p.FormEnabled,
                questions = p.Questions.Select(q => new
                {
                    id = q.Id, label = q.Label, placeholder = q.Placeholder, @long = q.Long, required = q.Required, maxLength = q.MaxLength
                }),
                autoCloseDays = p.AutoCloseDays,
                open = open.Count(t => t.ProjectKey == p.Key)
            }));
        });

        g.MapPost("/projects", (ulong guildId, ProjectBody body, DiscordSocketClient client, TicketStore tickets) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > MaxNameLength) return Error($"The name must be 1–{MaxNameLength} characters.");
            if (ParseId(body.MemberRoleId) is not { } roleId || guild.GetRole(roleId) is null)
                return Error("Pick the role given on Approve.");

            var error = tickets.AddProject(guildId, name, roleId);
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapPut("/projects/{key}", async (ulong guildId, string key, ProjectUpdateBody body, DiscordSocketClient client,
            TicketStore tickets, TicketMessageService ticketMessages) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var existing = tickets.FindProject(guildId, key);
            if (existing is null) return Error("This project no longer exists.");

            var staff = ParseIds(body.StaffRoleIds).Where(id => guild.GetRole(id) is not null).Take(10).ToList();
            var members = ParseIds(body.MemberRoleIds).Where(id => guild.GetRole(id) is not null).Take(10).ToList();
            ulong? category = ParseId(body.CategoryId) is { } c && guild.GetCategoryChannel(c) is not null ? c : null;
            var text = string.IsNullOrWhiteSpace(body.CustomText) ? null : body.CustomText.Trim();
            if (text is { Length: > TicketRenderer.MaxCustomTextLength })
                return Error($"The ticket text is limited to {TicketRenderer.MaxCustomTextLength} characters.");

            // Анкета: до 5 вопросов (лимит окна Discord), подпись до 45 символов
            List<TicketQuestion>? questions = null;
            if (body.Questions is not null)
            {
                if (body.Questions.Count > TicketQuestion.MaxQuestions) return Error($"Up to {TicketQuestion.MaxQuestions} questions (Discord form limit).");
                questions = [];
                foreach (var q in body.Questions)
                {
                    var label = (q.Label ?? "").Trim();
                    if (label.Length is 0 or > TicketQuestion.MaxLabel) return Error($"Question text: 1–{TicketQuestion.MaxLabel} characters.");
                    var placeholder = string.IsNullOrWhiteSpace(q.Placeholder) ? null : q.Placeholder.Trim();
                    if (placeholder is { Length: > TicketQuestion.MaxPlaceholder }) return Error($"Hint: up to {TicketQuestion.MaxPlaceholder} characters.");
                    questions.Add(new TicketQuestion
                    {
                        Id = string.IsNullOrWhiteSpace(q.Id) ? IdGen.New() : q.Id,
                        Label = label,
                        Placeholder = placeholder,
                        Long = q.Long,
                        Required = q.Required,
                        MaxLength = Math.Clamp(q.MaxLength ?? (q.Long ? 1000 : 200), 1, 4000)
                    });
                }
            }
            if (body.FormEnabled == true && (questions ?? existing.Questions).Count == 0) return Error("Add at least one question to enable the form.");
            if (body.AutoCloseDays is < 2 or > 90) return Error("Auto-close: 2–90 days (empty = off).");
            var newName = string.IsNullOrWhiteSpace(body.Name) ? existing.Name : body.Name.Trim();
            if (newName.Length > MaxNameLength) return Error($"The name must be 1–{MaxNameLength} characters.");
            if (tickets.GetProjects(guildId).Any(x => x.Key != key && x.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                return Error($"Project \"{newName}\" already exists.");

            var textChanged = text != existing.CustomText || newName != existing.Name;
            tickets.UpdateProject(guildId, key, p =>
            {
                p.StaffRoleIds = staff;
                p.MemberRoleIds = members;
                p.CategoryId = category;
                p.CustomText = text;
                if (body.FormEnabled is { } fe) p.FormEnabled = fe;
                if (questions is not null) p.Questions = questions;
                p.AutoCloseDays = body.AutoCloseDays;
                p.Name = newName;
            });

            // Свой текст поменялся — перерисовываем уже открытые тикеты проекта
            var refreshed = textChanged && tickets.GetTickets(guildId).Any(t => t.ProjectKey == key)
                ? await ticketMessages.RefreshProjectAsync(guildId, key)
                : (Updated: 0, Total: 0);

            return Results.Ok(new
            {
                warning = members.Count == 0 ? "No roles to give on Approve — Approve won't work until you add one." : null,
                ticketsUpdated = refreshed.Updated,
                ticketsTotal = refreshed.Total
            });
        });

        g.MapDelete("/projects/{key}", (ulong guildId, string key, TicketStore tickets) =>
            tickets.RemoveProject(guildId, key) is null ? Error("Project not found.") : Results.Ok());

        g.MapGet("/open", (ulong guildId, DiscordSocketClient client, TicketStore tickets) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(tickets.GetTickets(guildId).OrderBy(t => t.CreatedAt).Select(t => new
            {
                id = t.Id,
                project = tickets.FindProject(guildId, t.ProjectKey)?.Name ?? "Deleted project",
                applicant = Member(guild, t.UserId),
                textChannel = guild.GetTextChannel(t.TextChannelId)?.Name,
                voiceChannel = guild.GetVoiceChannel(t.VoiceChannelId)?.Name,
                jumpUrl = $"https://discord.com/channels/{guildId}/{t.TextChannelId}",
                approvedBy = t.ApprovedBy is { } by ? Member(guild, by) : null,
                createdAt = t.CreatedAt.ToUnixTimeMilliseconds()
            }));
        });

        // Архив закрытых тикетов с поиском
        g.MapGet("/archive", (ulong guildId, string? outcome, string? q, int? skip, int? take, TicketArchiveStore archive) =>
        {
            var size = Math.Clamp(take ?? 50, 10, 200);
            var list = archive.Query(guildId, outcome, q, null, Math.Max(0, skip ?? 0), size + 1);
            return Results.Ok(new
            {
                more = list.Count > size,
                entries = list.Take(size).Select(e => new
                {
                    id = e.Id,
                    project = e.Project,
                    userId = e.UserId.ToString(),
                    userName = e.UserName,
                    openedAt = e.OpenedAt.ToUnixTimeMilliseconds(),
                    closedAt = e.ClosedAt.ToUnixTimeMilliseconds(),
                    outcome = e.Outcome,
                    byName = e.ByName,
                    reason = e.Reason,
                    answers = e.Answers.Select(a => new { question = a.Question, answer = a.Answer })
                })
            });
        });

        g.MapPost("/open/{ticketId}/close", async (ulong guildId, string ticketId, ClaimsPrincipal user, DiscordSocketClient client,
            TicketStore tickets, TicketService service) =>
        {
            var t = tickets.FindTicket(ticketId);
            if (t is null || t.GuildId != guildId) return Error("This ticket is already closed.");
            var by = user.UserId() is { } uid ? client.GetUser(uid) : null;
            await service.CloseAsync(t, "closed", by, "Force-closed on the site");
            return Results.Ok();
        });

        // Approve / Deny с сайта — как кнопки в тикете: роли или ЛС с причиной, сообщение в тикете, закрытие через 15 с
        g.MapPost("/open/{ticketId}/approve", async (ulong guildId, string ticketId, ClaimsPrincipal user, DiscordSocketClient client,
            TicketStore tickets, TicketService service, TicketMessageService messages) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var t = tickets.FindTicket(ticketId);
            if (t is null || t.GuildId != guildId) return Error("This ticket is already closed.");
            if (t.ApprovedBy is not null) return Error("This application is already approved.");
            var project = tickets.FindProject(guildId, t.ProjectKey);
            if (project is null) return Error("This project was deleted.");
            var roleIds = project.MemberRoleIds.Where(id => guild.GetRole(id) is not null).ToList();
            if (roleIds.Count == 0) return Error("This project has no roles to give on Approve.");

            IGuildUser? applicant = guild.GetUser(t.UserId);
            applicant ??= await client.Rest.GetGuildUserAsync(guildId, t.UserId);
            if (applicant is null) return Error("The applicant has left the server.");

            var failed = new List<ulong>();
            foreach (var roleId in roleIds)
            {
                try { await applicant.AddRoleAsync(roleId); }
                catch (Discord.Net.HttpException) { failed.Add(roleId); }
            }
            if (failed.Count == roleIds.Count)
                return Error("I couldn't give the roles — the bot needs Manage Roles and its role above them in Server Settings → Roles.");

            var me = user.UserId()!.Value;
            var by = (IUser?)client.GetUser(me) ?? await client.Rest.GetUserAsync(me);
            var updated = tickets.UpdateTicket(ticketId, x => x.ApprovedBy = me) ?? t;
            try { await messages.RefreshAsync(updated, project); } catch (Exception) { /* сообщение тикета могли удалить */ }

            var granted = roleIds.Except(failed).ToList();
            var mentions = string.Join(", ", granted.Select(r => $"<@&{r}>"));
            var channel = guild.GetTextChannel(t.TextChannelId);
            if (failed.Count > 0)
            {
                if (channel is not null)
                    await channel.SendMessageAsync($"🎉 <@{t.UserId}> approved by <@{me}> (website) — {mentions} granted.\n" +
                        $"⚠️ Couldn't give {string.Join(", ", failed.Select(r => $"<@&{r}>"))} — the ticket stays open.",
                        allowedMentions: new AllowedMentions { UserIds = [t.UserId] });
                return Results.Ok(new { closed = false, warning = "Some roles couldn't be given — the ticket stays open." });
            }

            if (channel is not null)
                await channel.SendMessageAsync($"🎉 <@{t.UserId}> approved by <@{me}> (website) — {mentions} granted.\n" +
                    $"🔒 This ticket will close in {(int)TicketService.CloseDelay.TotalSeconds} seconds.",
                    allowedMentions: new AllowedMentions { UserIds = [t.UserId] });
            await service.NotifyApprovedAsync(updated, project, granted);
            service.CloseLater(updated, by, "approved", null);
            return Results.Ok(new { closed = true });
        });

        g.MapPost("/open/{ticketId}/deny", async (ulong guildId, string ticketId, DenyBody body, ClaimsPrincipal user, DiscordSocketClient client,
            TicketStore tickets, TicketService service) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var t = tickets.FindTicket(ticketId);
            if (t is null || t.GuildId != guildId) return Error("This ticket is already closed.");
            if (t.ApprovedBy is not null) return Error("This application is already approved.");
            var project = tickets.FindProject(guildId, t.ProjectKey);
            var reason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim();
            if (reason is { Length: > 500 }) return Error("Reason: up to 500 characters.");

            var me = user.UserId()!.Value;
            var by = (IUser?)client.GetUser(me) ?? await client.Rest.GetUserAsync(me);
            if (guild.GetTextChannel(t.TextChannelId) is { } channel)
                await channel.SendMessageAsync($"❌ <@{t.UserId}>'s application was denied by <@{me}> (website)." +
                    (reason is null ? "" : $"\n**Reason:** {Format.Sanitize(reason)}") +
                    $"\n🔒 This ticket will close in {(int)TicketService.CloseDelay.TotalSeconds} seconds.",
                    allowedMentions: new AllowedMentions { UserIds = [t.UserId] });
            await service.NotifyDeniedAsync(t, project, reason);
            service.CloseLater(t, by, "denied", reason);
            return Results.Ok();
        });

        g.MapPost("/panel", (ulong guildId, ChannelBody body, DiscordSocketClient client, PanelService panels) =>
            PostAsync(client, guildId, body.ChannelId, ch => panels.PostAsync(ch, guildId, "tickets")));
    }

    // ================= Temp Voice =================

    private static void MapVoice(RouteGroupBuilder g)
    {
        g.MapGet("/creators", (ulong guildId, DiscordSocketClient client, TempVoiceStore voice) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(voice.GetCreators(guildId).Select(c =>
            {
                var channel = guild.GetVoiceChannel(c.ChannelId);
                return new
                {
                    channelId = c.ChannelId.ToString(),
                    name = channel?.Name,
                    category = channel?.Category?.Name,
                    exists = channel is not null,
                    template = c.NameTemplate,
                    limit = c.UserLimit
                };
            }));
        });

        // Новый канал-создатель
        g.MapPost("/creators/new", async (ulong guildId, NewCreatorBody body, DiscordSocketClient client,
            TempVoiceStore voice, TempRoomManager rooms, ILoggerFactory logs) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (!ValidLimit(body.Limit)) return Error("The limit must be a number from 0 to 99.");

            var name = string.IsNullOrWhiteSpace(body.Name) ? DefaultCreatorName : body.Name.Trim();
            if (name.Length > MaxNameLength) return Error($"The name is limited to {MaxNameLength} characters.");
            ulong? category = ParseId(body.CategoryId) is { } c && guild.GetCategoryChannel(c) is not null ? c : null;

            IVoiceChannel channel;
            try
            {
                channel = await guild.CreateVoiceChannelAsync(name, p =>
                {
                    if (category is { } id) p.CategoryId = id;
                });
            }
            catch (Discord.Net.HttpException ex)
            {
                logs.CreateLogger("WebPanel").LogWarning(ex, "Failed to create a voice channel");
                return Error("I couldn't create the channel — I need Manage Channels.");
            }

            voice.SetCreator(new TempVoiceCreator
            {
                GuildId = guildId,
                ChannelId = channel.Id,
                NameTemplate = Template(body.Template),
                UserLimit = body.Limit is > 0 ? body.Limit : null
            });
            await rooms.ReorderCategoryAsync(guild, category, channel.Id);
            return Results.Ok();
        });

        // Существующий голосовой канал → создатель (или обновить настройки создателя)
        g.MapPut("/creators/{channelId}", async (ulong guildId, ulong channelId, CreatorBody body, DiscordSocketClient client,
            TempVoiceStore voice, TempRoomManager rooms) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (!ValidLimit(body.Limit)) return Error("The limit must be a number from 0 to 99.");
            var channel = guild.GetVoiceChannel(channelId);
            if (channel is null) return Error("Voice channel not found.");
            if (voice.GetRoom(channelId) is not null) return Error("That channel is a temporary room — pick a normal voice channel.");

            var isNew = voice.GetCreator(channelId) is null;
            voice.SetCreator(new TempVoiceCreator
            {
                GuildId = guildId,
                ChannelId = channelId,
                NameTemplate = Template(body.Template),
                UserLimit = body.Limit is > 0 ? body.Limit : null
            });
            if (isNew) await rooms.ReorderCategoryAsync(guild, channel.CategoryId, channel.Id);
            return Results.Ok();
        });

        g.MapDelete("/creators/{channelId}", (ulong guildId, ulong channelId, TempVoiceStore voice) =>
            voice.GetCreator(channelId)?.GuildId == guildId && voice.RemoveCreator(channelId)
                ? Results.Ok()
                : Error("That channel is not a 'join to create' channel."));

        g.MapGet("/rooms", (ulong guildId, DiscordSocketClient client, TempVoiceStore voice) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(LiveRooms(guild, voice).Select(r => new
            {
                channelId = r.Channel.Id.ToString(),
                name = r.Channel.Name,
                owner = Member(guild, r.Room.OwnerId),
                createdFrom = guild.GetVoiceChannel(r.Room.CreatorChannelId)?.Name,
                locked = r.Room.Locked,
                limit = r.Channel.UserLimit,
                inside = r.Channel.ConnectedUsers.Select(u => Member(guild, u.Id))
            }));
        });

        g.MapPost("/rooms/{channelId}/delete", async (ulong guildId, ulong channelId, DiscordSocketClient client, TempVoiceStore voice) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (FindRoom(guild, voice, channelId) is not { } r) return Error("This room no longer exists.");
            try
            {
                await r.Channel.DeleteAsync();
            }
            catch (Discord.Net.HttpException)
            {
                return Error("I couldn't delete the room — I need Manage Channels there.");
            }
            voice.RemoveRoom(channelId);
            return Results.Ok();
        });

        g.MapPost("/rooms/{channelId}/owner", async (ulong guildId, ulong channelId, UserBody body, DiscordSocketClient client,
            TempVoiceStore voice) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (FindRoom(guild, voice, channelId) is not { } r) return Error("This room no longer exists.");
            if (ParseId(body.UserId) is not { } targetId || guild.GetUser(targetId) is not { } target) return Error("Member not found.");
            if (target.IsBot) return Error("A bot can't own a room.");
            if (target.Id == r.Room.OwnerId) return Error("This member already owns the room.");

            try
            {
                IUser? oldOwner = (IUser?)guild.GetUser(r.Room.OwnerId) ?? await client.Rest.GetUserAsync(r.Room.OwnerId);
                if (oldOwner is not null) await r.Channel.RemovePermissionOverwriteAsync(oldOwner);
                await r.Channel.AddPermissionOverwriteAsync(target, TempVoiceService.OwnerPermissions);
                voice.UpdateRoom(channelId, room => room.OwnerId = target.Id);
            }
            catch (Discord.Net.HttpException)
            {
                return Error("I couldn't change the room permissions — I need Manage Channels there.");
            }
            return Results.Ok();
        });

        g.MapPost("/panel", (ulong guildId, ChannelBody body, DiscordSocketClient client, PanelService panels) =>
            PostAsync(client, guildId, body.ChannelId, ch => panels.PostAsync(ch, guildId, "rooms")));
    }

    // ================= Тексты панелей =================

    private static void MapTexts(RouteGroupBuilder g)
    {
        g.MapGet("/", (ulong guildId, PanelTextStore texts) => Results.Ok(PanelDefs.All.Concat(PanelDefs.Messages).Select(d =>
        {
            var custom = texts.GetText(guildId, d.Key);
            return new
            {
                key = d.Key,
                // panel — опубликованная панель (заголовок, текст, кнопки); labels — только подписи кнопок в постах бота
                kind = PanelDefs.Messages.Contains(d) ? "labels" : "panel",
                name = d.Name,
                placeholders = d.Placeholders,
                maxTitle = PanelDef.MaxTitle,
                maxDescription = PanelDef.MaxDescription,
                defaults = new { title = d.DefaultTitle, description = d.DefaultDescription },
                labels = d.Labels.Select(l => new
                {
                    id = l.Id,
                    name = l.Name,
                    @default = l.Default,
                    max = l.MaxLength,
                    value = custom is not null && custom.Labels.TryGetValue(l.Id, out var v) ? v : null
                }),
                title = custom?.Title,
                description = custom?.Description,
                posted = texts.GetPosted(guildId, d.Key).Count
            };
        })));

        g.MapPut("/{key}", async (ulong guildId, string key, PanelTextBody body, PanelTextStore texts, PanelService panels,
            LfgStore lfg, LfgSignupService signup, TicketStore tickets, TicketMessageService ticketMessages) =>
        {
            if (PanelDefs.Find(key) is not { } def) return Error("Unknown panel.");
            var labelsOnly = PanelDefs.Messages.Contains(def);

            // Пустой заголовок = панель без заголовка ("-" тоже); стандартный — кнопкой Reset to default
            var title = labelsOnly ? null
                : body.Title is not null && (string.IsNullOrWhiteSpace(body.Title) || body.Title.Trim() == PanelDef.NoTitle) ? PanelDef.NoTitle
                : Clean(body.Title, def.DefaultTitle);
            var description = labelsOnly ? null : Clean(body.Description, def.DefaultDescription);
            if (title is { Length: > PanelDef.MaxTitle }) return Error($"The title is limited to {PanelDef.MaxTitle} characters.");
            if (description is { Length: > PanelDef.MaxDescription }) return Error($"The text is limited to {PanelDef.MaxDescription} characters.");

            var labels = new Dictionary<string, string>();
            foreach (var l in def.Labels)
            {
                var value = body.Labels is not null && body.Labels.TryGetValue(l.Id, out var v) ? Clean(v, l.Default) : null;
                if (value is null) continue;
                if (value.Length > l.MaxLength) return Error($"{l.Name}: up to {l.MaxLength} characters.");
                labels[l.Id] = value;
            }

            var custom = title is null && description is null && labels.Count == 0
                ? null
                : new PanelText { Title = title, Description = description, Labels = labels };
            texts.SetText(guildId, key, custom);

            var (updated, total) = await RefreshTextsAsync(guildId, key, panels, lfg, signup, tickets, ticketMessages);
            return Results.Ok(new { panelsUpdated = updated, panelsTotal = total });
        });

        g.MapDelete("/{key}", async (ulong guildId, string key, PanelTextStore texts, PanelService panels,
            LfgStore lfg, LfgSignupService signup, TicketStore tickets, TicketMessageService ticketMessages) =>
        {
            if (PanelDefs.Find(key) is null) return Error("Unknown panel.");
            texts.SetText(guildId, key, null);
            var (updated, total) = await RefreshTextsAsync(guildId, key, panels, lfg, signup, tickets, ticketMessages);
            return Results.Ok(new { panelsUpdated = updated, panelsTotal = total });
        });

        // Найти панели, опубликованные до того, как бот начал их запоминать
        g.MapPost("/discover", async (ulong guildId, DiscordSocketClient client, PanelService panels) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(await panels.DiscoverAsync(guild));
        });
    }

    /// <summary>Пусто или совпадает с текстом по умолчанию — null (храним только отличия).</summary>
    private static string? Clean(string? value, string @default)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Replace("\r\n", "\n").Trim();
        return text == @default ? null : text;
    }

    /// <summary>После смены текстов: панели — перерисовать; подписи постов — активные сборы или открытые тикеты.</summary>
    private static async Task<(int Updated, int Total)> RefreshTextsAsync(ulong guildId, string key, PanelService panels,
        LfgStore lfg, LfgSignupService signup, TicketStore tickets, TicketMessageService ticketMessages)
    {
        if (key == PanelDefs.LfgPost.Key)
        {
            var groups = ActiveGroups(lfg, guildId).ToList();
            foreach (var group in groups)
            {
                await group.Gate.WaitAsync();
                try { await signup.RefreshPostAsync(group); }
                finally { group.Gate.Release(); }
            }
            return (groups.Count, groups.Count);
        }
        if (key == PanelDefs.TicketMessage.Key)
        {
            var (updated, total) = (0, 0);
            foreach (var project in tickets.GetProjects(guildId))
            {
                var r = await ticketMessages.RefreshProjectAsync(guildId, project.Key);
                updated += r.Updated;
                total += r.Total;
            }
            return (updated, total);
        }
        return await panels.RefreshAsync(guildId, key);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, (DateTimeOffset At, List<object> List)> BotChannelsCache = new();

    // ================= Сообщения бота: панели и посты =================

    private static void MapPosts(RouteGroupBuilder g)
    {
        // Все запомненные панели с каналами
        g.MapGet("/panels", (ulong guildId, DiscordSocketClient client, PanelTextStore texts) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(PanelDefs.All.Select(d => new
            {
                key = d.Key,
                name = d.Name,
                messages = texts.GetPosted(guildId, d.Key).Select(p => new
                {
                    channelId = p.ChannelId.ToString(),
                    messageId = p.MessageId.ToString(),
                    channel = guild.GetTextChannel(p.ChannelId)?.Name,
                    createdAt = SnowflakeUtils.FromSnowflake(p.MessageId).ToUnixTimeMilliseconds(),
                    jumpUrl = $"https://discord.com/channels/{guildId}/{p.ChannelId}/{p.MessageId}"
                })
            }));
        });

        // Перерисовать все панели типа текущим текстом
        g.MapPost("/panels/{key}/refresh", async (ulong guildId, string key, PanelService panels) =>
        {
            if (PanelDefs.Find(key) is null) return Error("Unknown panel.");
            var (updated, total) = await panels.RefreshAsync(guildId, key);
            return Results.Ok(new { panelsUpdated = updated, panelsTotal = total });
        });

        // Последние сообщения бота в канале
        // Каналы, где у бота есть сообщения (последние 50 сообщений каждого канала; кэш 2 минуты, ?refresh=true — заново)
        g.MapGet("/channels", async (ulong guildId, bool? refresh, DiscordSocketClient client) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (refresh != true && BotChannelsCache.TryGetValue(guildId, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(2))
                return Results.Ok(cached.List);

            var me = client.CurrentUser.Id;
            var gate = new SemaphoreSlim(5);
            var channels = guild.TextChannels.Where(c => c is not SocketThreadChannel)
                .Where(c => guild.CurrentUser.GetPermissions(c) is { ViewChannel: true, ReadMessageHistory: true }).ToList();
            var found = await Task.WhenAll(channels.Select(async c =>
            {
                await gate.WaitAsync();
                try
                {
                    var messages = await c.GetMessagesAsync(50).FlattenAsync();
                    var mine = messages.Where(m => m.Author.Id == me).ToList();
                    return (Channel: c, Count: mine.Count, Last: mine.Count > 0 ? mine.Max(m => m.Timestamp) : (DateTimeOffset?)null);
                }
                catch (Discord.Net.HttpException) { return (Channel: c, Count: 0, Last: (DateTimeOffset?)null); }
                finally { gate.Release(); }
            }));
            var list = found.Where(x => x.Count > 0).OrderByDescending(x => x.Last)
                .Select(x => (object)new
                {
                    id = x.Channel.Id.ToString(),
                    name = x.Channel.Name,
                    voice = x.Channel is SocketVoiceChannel,
                    category = x.Channel.Category?.Name,
                    count = x.Count,
                    lastAt = x.Last?.ToUnixTimeMilliseconds()
                }).ToList();
            BotChannelsCache[guildId] = (DateTimeOffset.UtcNow, list);
            return Results.Ok(list);
        });

        g.MapGet("/channels/{channelId}/messages", async (ulong guildId, ulong channelId, int? limit, DiscordSocketClient client,
            PanelTextStore texts, LfgStore lfg, TicketStore tickets) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (guild.GetTextChannel(channelId) is not { } channel) return Error("Text channel not found.");
            var perms = guild.CurrentUser.GetPermissions(channel);
            if (!perms.ViewChannel || !perms.ReadMessageHistory) return Error("I can't read the history of this channel.");

            var take = Math.Clamp(limit ?? 100, 10, 500);
            IEnumerable<IMessage> messages;
            try
            {
                messages = await channel.GetMessagesAsync(take).FlattenAsync();
            }
            catch (Discord.Net.HttpException)
            {
                return Error("I can't read the history of this channel.");
            }

            var ticketChannels = tickets.GetTickets(guildId).Select(t => t.TextChannelId).ToHashSet();
            return Results.Ok(messages
                .Where(m => m.Author.Id == client.CurrentUser.Id)
                .OrderByDescending(m => m.Timestamp)
                .Select(m => new
                {
                    messageId = m.Id.ToString(),
                    createdAt = m.Timestamp.ToUnixTimeMilliseconds(),
                    kind = Classify(guildId, channelId, m, texts, lfg, ticketChannels),
                    title = m.Embeds.FirstOrDefault()?.Title,
                    preview = Preview(m),
                    jumpUrl = $"https://discord.com/channels/{guildId}/{channelId}/{m.Id}"
                }));
        });

        // Удалить сообщения бота (по одному или списком). Пост LFG-сбора закрывает сбор, панель забывается.
        g.MapPost("/channels/{channelId}/delete", async (ulong guildId, ulong channelId, DeleteMessagesBody body, DiscordSocketClient client,
            PanelTextStore texts, LfgStore lfg, LfgSignupService signup) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (guild.GetTextChannel(channelId) is not { } channel) return Error("Text channel not found.");
            var ids = ParseIds(body.MessageIds).Take(100).ToList();
            if (ids.Count == 0) return Error("Nothing selected.");

            var deleted = 0;
            var failed = 0;
            foreach (var id in ids)
            {
                // Пост сбора — закрываем сбор, как кнопка Force close (удалит пост и напоминание)
                var group = lfg.Groups.Values.FirstOrDefault(x => x.GuildId == guildId && x.MessageId == id);
                if (group is not null)
                {
                    await group.Gate.WaitAsync();
                    try
                    {
                        if (lfg.Groups.TryRemove(group.Id, out _))
                        {
                            group.IsClosed = true;
                            lfg.RecordClosed(group, "force-closed");
                            lfg.SaveGroups();
                            await signup.DeleteMessagesAsync(group);
                        }
                    }
                    finally
                    {
                        group.Gate.Release();
                    }
                    deleted++;
                    continue;
                }

                try
                {
                    if (await channel.GetMessageAsync(id) is { } message && message.Author.Id == client.CurrentUser.Id)
                    {
                        await message.DeleteAsync();
                        deleted++;
                    }
                    else failed++;
                }
                catch (Discord.Net.HttpException)
                {
                    failed++;
                }

                if (texts.KeyOf(guildId, id) is { } key) texts.Untrack(guildId, key, id);
            }

            return Results.Ok(new { deleted, failed });
        });
    }

    private static string Classify(ulong guildId, ulong channelId, IMessage m, PanelTextStore texts, LfgStore lfg, HashSet<ulong> ticketChannels)
    {
        if (texts.KeyOf(guildId, m.Id) is { } key) return $"panel:{key}";

        var ids = m.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components)
            .Select(c => (c as ButtonComponent)?.CustomId ?? (c as SelectMenuComponent)?.CustomId)
            .OfType<string>().ToList();
        foreach (var def in PanelDefs.All)
            if (ids.Contains(def.MarkerCustomId)) return $"panel:{def.Key}";

        if (lfg.Groups.Values.Any(x => x.GuildId == guildId && (x.MessageId == m.Id || x.ReminderMessageId == m.Id)))
            return "lfg-group";
        if (ticketChannels.Contains(channelId)) return "ticket";
        return "other";
    }

    private static string Preview(IMessage m)
    {
        var text = !string.IsNullOrWhiteSpace(m.Content) ? m.Content : m.Embeds.FirstOrDefault()?.Description ?? "";
        text = text.Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..159] + "…";
    }

    // ================= Доступ к панели (только владелец) =================

    private static void MapAccess(RouteGroupBuilder g)
    {
        g.MapGet("/", (WebAccessStore grants, BotOwner owner, DiscordSocketClient client) => Results.Ok(new
        {
            permissions = PanelPermissions.Grantable,
            owner = FindMember(client, owner.Id),
            grants = grants.GetAll().OrderBy(x => x.Name).Select(x => new
            {
                user = FindMember(client, x.UserId, x.Name),
                permissions = x.Permissions,
                grantedAt = x.GrantedAt.ToUnixTimeMilliseconds()
            })
        }));

        g.MapPut("/{userId}", async (ulong userId, AccessBody body, WebAccessStore grants, BotOwner owner, DiscordSocketClient client) =>
        {
            if (owner.Is(userId)) return Error("The bot owner always has full access.");

            // Имя для списка: из кэша сервера или через REST (если человек не на сервере с ботом)
            string? name = client.Guilds.Select(x => x.GetUser(userId)).FirstOrDefault(u => u is not null)?.DisplayName;
            if (name is null)
            {
                try
                {
                    var user = await client.Rest.GetUserAsync(userId);
                    if (user is null) return Error("Discord user not found — check the ID.");
                    if (user.IsBot) return Error("Bots can't get panel access.");
                    name = user.GlobalName ?? user.Username;
                }
                catch (Discord.Net.HttpException)
                {
                    return Error("Discord user not found — check the ID.");
                }
            }

            var error = grants.Set(userId, name, body.Permissions ?? new List<string>());
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapDelete("/{userId}", (ulong userId, WebAccessStore grants) =>
            grants.Remove(userId) ? Results.Ok() : Error("This person has no access."));
    }

    /// <summary>Участник любого сервера с ботом (для списка доступа).</summary>
    private static object FindMember(DiscordSocketClient client, ulong id, string? fallbackName = null)
    {
        var user = client.Guilds.Select(x => x.GetUser(id)).FirstOrDefault(u => u is not null);
        return new
        {
            id = id.ToString(),
            name = user?.DisplayName ?? fallbackName ?? id.ToString(),
            username = user?.Username,
            avatarUrl = user?.GetDisplayAvatarUrl(size: 64),
            onServer = user is not null
        };
    }

    /// <summary>Фильтр группы: пускает только с правом <paramref name="permission"/>.</summary>
    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(string permission) =>
        async (ctx, next) =>
        {
            var access = ctx.HttpContext.RequestServices.GetRequiredService<PanelAccess>();
            if (!access.Has(ctx.HttpContext.User.UserId(), permission))
                return Results.Json(new { error = "You don't have access to this section." }, statusCode: StatusCodes.Status403Forbidden);
            return await next(ctx);
        };

    // ================= Хелперы =================

    private static IResult GuildNotFound() => Results.NotFound(new { error = "The bot is not on this server (or is still connecting)." });

    private static IResult Error(string message) => Results.BadRequest(new { error = message });

    private static ulong? ParseId(string? text) => ulong.TryParse(text, out var id) && id != 0 ? id : null;

    private static List<ulong> ParseIds(IEnumerable<string>? values) =>
        (values ?? []).Select(ParseId).OfType<ulong>().Distinct().ToList();

    private static bool ValidLimit(int? limit) => limit is null or (>= 0 and <= 99);

    private static string? Template(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, MaxNameLength)];

    private static object Role(SocketGuild guild, ulong id)
    {
        var role = guild.GetRole(id);
        return new
        {
            id = id.ToString(),
            name = role?.Name ?? "deleted role",
            color = role is null || role.Colors.PrimaryColor.RawValue == 0 ? null : $"#{role.Colors.PrimaryColor.RawValue:x6}",
            exists = role is not null
        };
    }

    private static object Member(SocketGuild guild, ulong id)
    {
        var user = guild.GetUser(id);
        return new
        {
            id = id.ToString(),
            name = user?.DisplayName ?? id.ToString(),
            username = user?.Username,
            avatarUrl = user?.GetDisplayAvatarUrl(size: 64),
            onServer = user is not null
        };
    }

    private static List<LfgGroup> ActiveGroups(LfgStore lfg, ulong guildId) => lfg.Groups.Values
        .Where(x => x.GuildId == guildId && !x.IsClosed)
        .OrderBy(x => x.StartsAt ?? x.ExpiresAt)
        .ToList();

    private static List<RoomRef> LiveRooms(SocketGuild guild, TempVoiceStore voice) => voice.GetRooms(guild.Id)
        .Select(r => guild.GetVoiceChannel(r.ChannelId) is { } ch ? new RoomRef(ch, r) : null)
        .OfType<RoomRef>()
        .ToList();

    private static RoomRef? FindRoom(SocketGuild guild, TempVoiceStore voice, ulong channelId)
    {
        var room = voice.GetRoom(channelId);
        if (room is null || room.GuildId != guild.Id) return null;
        return guild.GetVoiceChannel(channelId) is { } channel ? new RoomRef(channel, room) : null;
    }

    /// <summary>Публикует панель в текстовый канал сервера.</summary>
    private static async Task<IResult> PostAsync(DiscordSocketClient client, ulong guildId, string? channelIdText,
        Func<SocketTextChannel, Task> send)
    {
        if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
        if (ParseId(channelIdText) is not { } channelId || guild.GetTextChannel(channelId) is not { } channel)
            return Error("Pick a text channel.");

        try
        {
            await send(channel);
        }
        catch (Discord.Net.HttpException)
        {
            return Error("I can't post there. Give the bot View Channel, Send Messages and Embed Links in that channel.");
        }
        return Results.Ok();
    }

    private static async Task DeleteChannelAsync(SocketGuild? guild, ulong channelId)
    {
        try
        {
            if (guild?.GetChannel(channelId) is { } channel) await channel.DeleteAsync();
        }
        catch (Discord.Net.HttpException)
        {
            // уже удалён или нет прав
        }
    }
}

public sealed record ActivityBody(string? Name, string? RoleId, string? ProjectKey = null);
public sealed record ProjectBody(string? Name, string? MemberRoleId);
public sealed record ProjectUpdateBody(List<string>? StaffRoleIds, List<string>? MemberRoleIds, string? CategoryId, string? CustomText,
    bool? FormEnabled = null, List<QuestionBody>? Questions = null, int? AutoCloseDays = null, string? Name = null);
public sealed record QuestionBody(string? Id, string? Label, string? Placeholder, bool Long, bool Required, int? MaxLength);
public sealed record ChannelBody(string? ChannelId);
public sealed record UserBody(string? UserId);
public sealed record NewCreatorBody(string? Name, string? CategoryId, string? Template, int? Limit);
public sealed record CreatorBody(string? Template, int? Limit);
public sealed record PanelTextBody(string? Title, string? Description, Dictionary<string, string>? Labels);
public sealed record AccessBody(List<string>? Permissions);
public sealed record DeleteMessagesBody(List<string>? MessageIds);

public sealed record DenyBody(string? Reason);
