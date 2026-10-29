using System.Security.Claims;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace LfgBot.Web;

/// <summary>
/// JSON API веб-админки. Повторяет действия /admin (LFG, Tickets, Temp Voice) и работает
/// с теми же хранилищами, что и бот, поэтому изменения сразу видны и в Discord.
/// ID Discord отдаются строками: в JavaScript числа больше 2^53 теряют точность.
/// </summary>
public static class PanelApi
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
                id = id?.ToString(),
                name = user.FindFirst(ClaimTypes.Name)?.Value,
                avatarUrl = avatar is null || id is null ? null : $"https://cdn.discordapp.com/avatars/{id}/{avatar}.png?size=64"
            });
        });

        var api = app.MapGroup("/api")
            .RequireAuthorization(WebPanel.Policy)
            .AddEndpointFilter(async (ctx, next) =>
            {
                // Защита от CSRF: свой заголовок нельзя отправить с чужого сайта без CORS
                var http = ctx.HttpContext;
                if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Headers[WebPanel.CsrfHeader] != "1")
                    return Results.BadRequest(new { error = "Missing X-Panel header." });
                return await next(ctx);
            });

        api.MapGet("/guilds", (DiscordSocketClient client) => Results.Ok(new
        {
            connection = client.ConnectionState.ToString(),
            latency = client.Latency,
            bot = client.CurrentUser is { } me ? new { id = me.Id.ToString(), name = me.Username, avatarUrl = me.GetAvatarUrl() } : null,
            guilds = client.Guilds.OrderBy(g => g.Name).Select(g => new
            {
                id = g.Id.ToString(),
                name = g.Name,
                iconUrl = g.IconUrl,
                members = g.MemberCount
            })
        }));

        var guild = api.MapGroup("/guilds/{guildId}");
        MapCommon(guild);
        MapLfg(guild);
        MapTickets(guild);
        MapVoice(guild);
    }

    // ================= Общее: сводка, роли, каналы, участники =================

    private static void MapCommon(RouteGroupBuilder g)
    {
        g.MapGet("/overview", (ulong guildId, DiscordSocketClient client, ActivityStore activities, LfgStore lfg,
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

        g.MapGet("/roles", (ulong guildId, DiscordSocketClient client) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(guild.Roles
                .Where(r => !r.IsEveryone && !r.IsManaged)
                .OrderByDescending(r => r.Position)
                .Select(r => new { id = r.Id.ToString(), name = r.Name, color = r.Color.RawValue == 0 ? null : $"#{r.Color.RawValue:x6}" }));
        });

        g.MapGet("/channels", (ulong guildId, DiscordSocketClient client) =>
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

        g.MapGet("/members", (ulong guildId, string? q, DiscordSocketClient client) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var query = (q ?? "").Trim();
            var list = guild.Users
                .Where(u => !u.IsBot)
                .Where(u => query.Length == 0 ||
                            u.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            u.Username.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(u => u.DisplayName)
                .Take(20)
                .Select(u => Member(guild, u.Id));
            return Results.Ok(list);
        });
    }

    // ================= LFG =================

    private static void MapLfg(RouteGroupBuilder g)
    {
        g.MapGet("/lfg/activities", (ulong guildId, DiscordSocketClient client, ActivityStore activities) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(activities.Get(guildId).Select(a => new
            {
                key = a.Key,
                name = a.Name,
                role = Role(guild, a.RoleId)
            }));
        });

        g.MapPost("/lfg/activities", (ulong guildId, ActivityBody body, DiscordSocketClient client, ActivityStore activities) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > MaxNameLength) return Error($"The name must be 1–{MaxNameLength} characters.");
            if (ParseId(body.RoleId) is not { } roleId || guild.GetRole(roleId) is null) return Error("Pick a role to ping.");

            var error = activities.Add(guildId, name, roleId);
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapDelete("/lfg/activities/{key}", (ulong guildId, string key, ActivityStore activities) =>
            activities.Remove(guildId, key) is null ? Error("Activity not found.") : Results.Ok());

        g.MapGet("/lfg/groups", (ulong guildId, DiscordSocketClient client, LfgStore lfg) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(ActiveGroups(lfg, guildId).Select(x => new
            {
                id = x.Id,
                activity = x.ActivityName,
                owner = Member(guild, x.OwnerId),
                channel = guild.GetTextChannel(x.ChannelId)?.Name,
                startsAt = x.StartsAt?.ToUnixTimeMilliseconds(),
                expiresAt = x.ExpiresAt.ToUnixTimeMilliseconds(),
                filled = x.FilledCount,
                size = x.Slots.Count,
                jumpUrl = LfgSignupService.JumpUrl(x),
                slots = x.Slots.Select(s => new
                {
                    role = (s.TakenAs ?? s.Role).Label(),
                    emoji = (s.TakenAs ?? s.Role).Emoji(),
                    slotRole = s.Role.Label(),
                    member = s.UserId is { } u ? Member(guild, u) : null,
                    character = s.CharacterName
                }),
                reserve = x.Reserve.Select(r => new
                {
                    role = r.Role.Label(),
                    emoji = r.Role.Emoji(),
                    member = Member(guild, r.UserId),
                    character = r.CharacterName
                })
            }));
        });

        g.MapPost("/lfg/groups/{groupId}/close", async (ulong guildId, string groupId, LfgStore lfg, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId)
                return Error("This group is no longer active.");

            await group.Gate.WaitAsync();
            try
            {
                if (lfg.Groups.TryRemove(groupId, out _))
                {
                    group.IsClosed = true;
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

        g.MapPost("/lfg/panel", (ulong guildId, ChannelBody body, DiscordSocketClient client,
            // [FromServices]: у TimeService есть метод TryParse, без атрибута minimal API пытается взять его из запроса
            [FromServices] TimeService time) =>
        {
            var panel = LfgRenderer.Panel(time.AutoDeleteHours);
            return PostAsync(client, guildId, body.ChannelId, ch => ch.SendMessageAsync(embed: panel.Embed, components: panel.Components));
        });
    }

    // ================= Tickets =================

    private static void MapTickets(RouteGroupBuilder g)
    {
        g.MapGet("/tickets/projects", (ulong guildId, DiscordSocketClient client, TicketStore tickets) =>
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
                open = open.Count(t => t.ProjectKey == p.Key)
            }));
        });

        g.MapPost("/tickets/projects", (ulong guildId, ProjectBody body, DiscordSocketClient client, TicketStore tickets) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > MaxNameLength) return Error($"The name must be 1–{MaxNameLength} characters.");
            if (ParseId(body.MemberRoleId) is not { } roleId || guild.GetRole(roleId) is null)
                return Error("Pick the role given on Approve.");

            var error = tickets.AddProject(guildId, name, roleId);
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapPut("/tickets/projects/{key}", async (ulong guildId, string key, ProjectUpdateBody body, DiscordSocketClient client,
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

            var textChanged = text != existing.CustomText;
            tickets.UpdateProject(guildId, key, p =>
            {
                p.StaffRoleIds = staff;
                p.MemberRoleIds = members;
                p.CategoryId = category;
                p.CustomText = text;
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

        g.MapDelete("/tickets/projects/{key}", (ulong guildId, string key, TicketStore tickets) =>
            tickets.RemoveProject(guildId, key) is null ? Error("Project not found.") : Results.Ok());

        g.MapGet("/tickets/open", (ulong guildId, DiscordSocketClient client, TicketStore tickets) =>
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

        g.MapPost("/tickets/open/{ticketId}/close", async (ulong guildId, string ticketId, DiscordSocketClient client, TicketStore tickets) =>
        {
            var t = tickets.FindTicket(ticketId);
            if (t is null || t.GuildId != guildId) return Error("This ticket is already closed.");

            tickets.RemoveTicket(ticketId);
            var guild = client.GetGuild(guildId);
            await DeleteChannelAsync(guild, t.VoiceChannelId);
            await DeleteChannelAsync(guild, t.TextChannelId);
            return Results.Ok();
        });

        g.MapPost("/tickets/panel", (ulong guildId, ChannelBody body, DiscordSocketClient client) =>
        {
            var (embed, comps) = TicketRenderer.Panel();
            return PostAsync(client, guildId, body.ChannelId, ch => ch.SendMessageAsync(embed: embed, components: comps));
        });
    }

    // ================= Temp Voice =================

    private static void MapVoice(RouteGroupBuilder g)
    {
        g.MapGet("/voice/creators", (ulong guildId, DiscordSocketClient client, TempVoiceStore voice) =>
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
        g.MapPost("/voice/creators/new", async (ulong guildId, NewCreatorBody body, DiscordSocketClient client,
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
        g.MapPut("/voice/creators/{channelId}", async (ulong guildId, ulong channelId, CreatorBody body, DiscordSocketClient client,
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

        g.MapDelete("/voice/creators/{channelId}", (ulong guildId, ulong channelId, TempVoiceStore voice) =>
            voice.GetCreator(channelId)?.GuildId == guildId && voice.RemoveCreator(channelId)
                ? Results.Ok()
                : Error("That channel is not a 'join to create' channel."));

        g.MapGet("/voice/rooms", (ulong guildId, DiscordSocketClient client, TempVoiceStore voice) =>
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

        g.MapPost("/voice/rooms/{channelId}/delete", async (ulong guildId, ulong channelId, DiscordSocketClient client, TempVoiceStore voice) =>
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

        g.MapPost("/voice/rooms/{channelId}/owner", async (ulong guildId, ulong channelId, UserBody body, DiscordSocketClient client,
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

        g.MapPost("/voice/panel", (ulong guildId, ChannelBody body, DiscordSocketClient client) =>
        {
            var (embed, comps) = TempRoomManager.Panel();
            return PostAsync(client, guildId, body.ChannelId, ch => ch.SendMessageAsync(embed: embed, components: comps));
        });
    }

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
            color = role is null || role.Color.RawValue == 0 ? null : $"#{role.Color.RawValue:x6}",
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

public sealed record ActivityBody(string? Name, string? RoleId);
public sealed record ProjectBody(string? Name, string? MemberRoleId);
public sealed record ProjectUpdateBody(List<string>? StaffRoleIds, List<string>? MemberRoleIds, string? CategoryId, string? CustomText);
public sealed record ChannelBody(string? ChannelId);
public sealed record UserBody(string? UserId);
public sealed record NewCreatorBody(string? Name, string? CategoryId, string? Template, int? Limit);
public sealed record CreatorBody(string? Template, int? Limit);
