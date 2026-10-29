using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Web;

// Вторая часть API: создание сборов с сайта, состав, повторяющиеся сборы, журнал, настройки и запись в журнал.
// ВАЖНО: у TimeService есть метод TryParse — в обработчиках его всегда брать с [FromServices].
public static partial class PanelApi
{
    // ================= LFG: создание, состав, повторяющиеся =================

    private static void MapLfgExtra(RouteGroupBuilder g)
    {
        // Создать сбор с сайта. Организатор — тот, кто вошёл в панель.
        g.MapPost("/create", async (ulong guildId, LfgCreateBody body, ClaimsPrincipal user, DiscordSocketClient client,
            ActivityStore activities, LfgPublisher publisher, [FromServices] TimeService time) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (user.UserId() is not { } ownerId) return Error("Unknown user.");
            var (err, channelId, name, pingRole, slots) = ResolveLfgInput(guild, activities, body.ChannelId, body.ActivityKey, body.CustomName, body.Slots);
            if (err is not null) return Error(err);

            DateTimeOffset? startsAt = null;
            if (!string.IsNullOrWhiteSpace(body.Date) || !string.IsNullOrWhiteSpace(body.Time))
            {
                if (!DateOnly.TryParseExact(body.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    !TimeOnly.TryParseExact(body.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    return Error("Set both the date and the time, or leave both empty.");
                startsAt = time.ToUtc(date, t.Hour, t.Minute);
                if (startsAt < DateTimeOffset.UtcNow.AddMinutes(-1)) return Error("This time is in the past.");
            }

            try
            {
                var (group, jumpUrl, dms) = await publisher.PublishAsync(new LfgPublishRequest(
                    guildId, channelId, ownerId, name!, pingRole, slots!, startsAt, body.NotifyByDm, body.SeatOwner));
                return Results.Ok(new { id = group.Id, jumpUrl, dmsStarted = dms });
            }
            catch (Exception ex) when (ex is InvalidOperationException or Discord.Net.HttpException)
            {
                return Error("I can't post there. Give the bot View Channel, Send Messages and Embed Links in that channel.");
            }
        });

        // Состав сбора: в резерв, из резерва, убрать, сменить персонажа
        g.MapPost("/groups/{groupId}/roster", async (ulong guildId, string groupId, RosterBody body, LfgStore lfg, LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            var error = await RosterAsync(group, body, lfg, signup, AdminRosterActions);
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapGet("/recurring", (ulong guildId, DiscordSocketClient client, RecurringLfgStore recurring, [FromServices] TimeService time) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(recurring.Get(guildId).Select(r =>
            {
                var after = r.LastStart is { } last && last > now ? last : now;
                var next = r.NextStart(time, after);
                return new
                {
                    id = r.Id,
                    activity = r.ActivityName,
                    pingRole = r.PingRoleId is { } pr ? Role(guild, pr) : null,
                    channelId = r.ChannelId.ToString(),
                    channel = guild.GetTextChannel(r.ChannelId)?.Name,
                    owner = Member(guild, r.OwnerId),
                    slots = r.Slots.Select(x => x.ToString()),
                    days = r.Days.Select(d => (int)d),
                    time = $"{r.Hour:00}:{r.Minute:00}",
                    postHoursBefore = r.PostHoursBefore,
                    notifyByDm = r.NotifyByDm,
                    seatOwner = r.SeatOwner,
                    enabled = r.Enabled,
                    nextStart = next?.ToUnixTimeMilliseconds(),
                    nextPost = next?.AddHours(-r.PostHoursBefore).ToUnixTimeMilliseconds(),
                    lastPostedAt = r.LastPostedAt?.ToUnixTimeMilliseconds(),
                    lastError = r.LastError
                };
            }));
        });

        g.MapPost("/recurring", (ulong guildId, RecurringBody body, ClaimsPrincipal user, DiscordSocketClient client,
            ActivityStore activities, RecurringLfgStore recurring) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var item = new RecurringLfg { GuildId = guildId };
            var error = FillRecurring(item, body, guild, activities, user.UserId());
            if (error is not null) return Error(error);
            error = recurring.Add(item);
            return error is null ? Results.Ok(new { id = item.Id }) : Error(error);
        });

        g.MapPut("/recurring/{id}", (ulong guildId, string id, RecurringBody body, ClaimsPrincipal user, DiscordSocketClient client,
            ActivityStore activities, RecurringLfgStore recurring) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (recurring.Find(id) is not { } existing || existing.GuildId != guildId) return Error("This schedule no longer exists.");

            var draft = recurring.Find(id)!;
            var error = FillRecurring(draft, body, guild, activities, user.UserId());
            if (error is not null) return Error(error);

            // Сменили день/время — следующее повторение считается заново
            var timeChanged = draft.Hour != existing.Hour || draft.Minute != existing.Minute || !draft.Days.SequenceEqual(existing.Days);
            recurring.Update(id, r =>
            {
                r.ChannelId = draft.ChannelId;
                r.OwnerId = draft.OwnerId;
                r.ActivityName = draft.ActivityName;
                r.PingRoleId = draft.PingRoleId;
                r.Slots = draft.Slots;
                r.Days = draft.Days;
                r.Hour = draft.Hour;
                r.Minute = draft.Minute;
                r.PostHoursBefore = draft.PostHoursBefore;
                r.NotifyByDm = draft.NotifyByDm;
                r.SeatOwner = draft.SeatOwner;
                r.Enabled = draft.Enabled;
                if (timeChanged && r.LastStart > DateTimeOffset.UtcNow) r.LastStart = null;
                r.LastError = null;
            });
            return Results.Ok();
        });

        g.MapDelete("/recurring/{id}", (ulong guildId, string id, RecurringLfgStore recurring) =>
        {
            if (recurring.Find(id) is not { } r || r.GuildId != guildId) return Error("This schedule no longer exists.");
            recurring.Remove(id);
            return Results.Ok();
        });
    }

    /// <summary>Админ может всё; организатор на сайте — как в Discord (Manage): резерв туда-обратно.</summary>
    private static readonly string[] AdminRosterActions = ["toReserve", "promote", "removeReserve", "remove", "character"];
    private static readonly string[] OrganizerRosterActions = ["toReserve", "promote", "removeReserve"];

    /// <summary>Изменение состава сбора: сохранить, обновить пост, ЛС переведённому из резерва. null — успех.</summary>
    private static async Task<string?> RosterAsync(LfgGroup group, RosterBody body, LfgStore lfg, LfgSignupService signup, string[] allowed)
    {
        if (ParseId(body.UserId) is not { } userId) return "Unknown member.";
        if (body.Action is null || !allowed.Contains(body.Action)) return "This action is not allowed.";

        string? error;
        Slot? promoted = null;
        await group.Gate.WaitAsync();
        try
        {
            switch (body.Action)
            {
                case "toReserve":
                    error = userId == group.OwnerId
                        ? "The organizer can't be moved to the reserve."
                        : group.MoveToReserve(userId);
                    break;
                case "promote":
                    error = group.Promote(userId, out promoted);
                    break;
                case "removeReserve":
                    error = group.RemoveFromReserve(userId);
                    break;
                case "remove":
                    error = group.Slots.Any(x => x.UserId == userId) ? group.Leave(userId) : "This member is no longer in the group.";
                    break;
                case "character":
                    var name = (body.Character ?? "").Trim();
                    if (name.Length is 0 or > 32) { error = "The character name must be 1–32 characters."; break; }
                    var slot = group.Slots.Find(x => x.UserId == userId);
                    var reserve = group.FindReserve(userId);
                    if (slot is not null) slot.CharacterName = name;
                    else if (reserve is not null) reserve.CharacterName = name;
                    error = slot is null && reserve is null ? "This member is no longer in the group." : null;
                    if (error is null) signup.ApplyProfile(group, userId); // класс и заметка нового персонажа
                    break;
                default:
                    error = "Unknown action.";
                    break;
            }

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

        if (error is null && promoted is not null) await signup.NotifyPromotedAsync(group, userId, promoted);
        return error;
    }

    /// <summary>Закрыть сбор и удалить пост с напоминанием.</summary>
    private static async Task CloseGroupAsync(LfgGroup group, LfgStore lfg, LfgSignupService signup)
    {
        await group.Gate.WaitAsync();
        try
        {
            if (lfg.Groups.TryRemove(group.Id, out _))
            {
                group.IsClosed = true;
                lfg.SaveGroups();
                await signup.DeleteMessagesAsync(group);
            }
        }
        finally
        {
            group.Gate.Release();
        }
    }

    /// <summary>Общая проверка формы сбора: канал, активность (с ролью) или своё название, слоты.</summary>
    private static (string? Error, ulong ChannelId, string? Name, ulong? PingRole, List<SlotRole>? Slots) ResolveLfgInput(
        SocketGuild guild, ActivityStore activities, string? channelIdText, string? activityKey, string? customName, List<string>? slotTexts)
    {
        if (ParseId(channelIdText) is not { } channelId || guild.GetTextChannel(channelId) is null)
            return ("Pick a text channel.", 0, null, null, null);

        string name;
        ulong? pingRole = null;
        if (!string.IsNullOrWhiteSpace(activityKey))
        {
            if (activities.Find(guild.Id, activityKey) is not { } activity) return ("Activity not found.", 0, null, null, null);
            name = activity.Name;
            pingRole = activity.RoleId;
        }
        else
        {
            name = (customName ?? "").Trim();
            if (name.Length is 0 or > MaxNameLength) return ($"Pick an activity or enter a name (1–{MaxNameLength} characters).", 0, null, null, null);
        }

        var slots = new List<SlotRole>();
        foreach (var text in slotTexts ?? [])
        {
            if (!Enum.TryParse<SlotRole>(text, true, out var role)) return ($"Unknown slot role '{text}'.", 0, null, null, null);
            slots.Add(role);
        }
        if (slots.Count is < LfgRenderer.MinSize or > LfgRenderer.MaxSize)
            return ($"A group has {LfgRenderer.MinSize}–{LfgRenderer.MaxSize} slots.", 0, null, null, null);

        return (null, channelId, name, pingRole, slots);
    }

    private static string? FillRecurring(RecurringLfg item, RecurringBody body, SocketGuild guild, ActivityStore activities, ulong? currentUser)
    {
        var (error, channelId, name, pingRole, slots) = ResolveLfgInput(guild, activities, body.ChannelId, body.ActivityKey, body.CustomName, body.Slots);
        if (error is not null) return error;

        var days = (body.Days ?? []).Where(d => d is >= 0 and <= 6).Distinct().Select(d => (DayOfWeek)d).OrderBy(d => ((int)d + 6) % 7).ToList();
        if (days.Count == 0) return "Pick at least one day of the week.";
        if (!TimeOnly.TryParseExact(body.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return "Set the start time (HH:mm).";
        if (body.PostHoursBefore is < 0 or > 168) return "Post the group 0–168 hours before the start.";

        var owner = ParseId(body.OwnerId) ?? (item.OwnerId != 0 ? item.OwnerId : currentUser);
        if (owner is not { } ownerId) return "Pick the organizer.";

        item.ChannelId = channelId;
        item.ActivityName = name!;
        item.PingRoleId = pingRole;
        item.Slots = slots!;
        item.Days = days;
        item.Hour = t.Hour;
        item.Minute = t.Minute;
        item.PostHoursBefore = body.PostHoursBefore;
        item.NotifyByDm = body.NotifyByDm;
        item.SeatOwner = body.SeatOwner;
        item.Enabled = body.Enabled;
        item.OwnerId = ownerId;
        return null;
    }

    // ================= Журнал =================

    private static void MapLog(RouteGroupBuilder g)
    {
        g.MapGet("/", (ulong guildId, string? actor, string? module, string? q, int? skip, int? take, AuditLog audit) =>
        {
            var size = Math.Clamp(take ?? 100, 10, 500);
            var entries = audit.Query(guildId, ParseId(actor) ?? (actor == "0" ? (ulong?)0UL : null), module, q, Math.Max(0, skip ?? 0), size + 1);
            return Results.Ok(new
            {
                more = entries.Count > size,
                entries = entries.Take(size).Select(e => new
                {
                    at = e.At.ToUnixTimeMilliseconds(),
                    actorId = e.ActorId.ToString(),
                    actorName = e.ActorName,
                    source = e.Source,
                    module = e.Module,
                    action = e.Action,
                    details = e.Details
                }),
                actors = audit.Actors(guildId).Select(a => new { id = a.Id.ToString(), name = a.Name, count = a.Count })
            });
        });
    }

    // ================= Настройки бота =================

    private static void MapSettings(RouteGroupBuilder g)
    {
        g.MapGet("/", (BotSettingsStore settings, [FromServices] TimeService time) =>
        {
            var s = settings.Get();
            return Results.Ok(new
            {
                timeZone = s.TimeZone,
                autoDeleteHours = s.AutoDeleteHours,
                reminderMinutes = s.ReminderMinutes,
                current = new { timeZone = time.ZoneName, autoDeleteHours = time.AutoDeleteHours, reminderMinutes = time.ReminderMinutes },
                defaults = new { timeZone = time.DefaultZoneId, autoDeleteHours = time.DefaultAutoDeleteHours, reminderMinutes = time.DefaultReminderMinutes },
                zones = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Where(id => id.Contains('/')).OrderBy(id => id)
            });
        });

        g.MapPut("/", async (SettingsBody body, BotSettingsStore settings, DiscordSocketClient client, PanelService panels,
            [FromServices] TimeService time) =>
        {
            var zone = string.IsNullOrWhiteSpace(body.TimeZone) ? null : body.TimeZone.Trim();
            if (zone is not null && !TimeService.IsKnownZone(zone)) return Error($"Unknown time zone '{zone}'. Use an IANA name like Europe/Kyiv.");
            if (body.AutoDeleteHours is < 1 or > 72) return Error("Auto-delete: 1–72 hours.");
            if (body.ReminderMinutes is < 0 or > 180) return Error("Reminder: 0–180 minutes (0 = off).");

            var s = settings.Get(); // роли «участник» не трогаем
            s.TimeZone = zone;
            s.AutoDeleteHours = body.AutoDeleteHours;
            s.ReminderMinutes = body.ReminderMinutes;
            settings.Save(s);
            time.Apply(s);

            // В тексте LFG-панели есть {hours} и {reminder} — обновляем опубликованные панели
            foreach (var guild in client.Guilds) await panels.RefreshAsync(guild.Id, "lfg");
            return Results.Ok(new { timeZone = time.ZoneName, autoDeleteHours = time.AutoDeleteHours, reminderMinutes = time.ReminderMinutes });
        });
    }

    // ================= Запись в журнал =================

    private static readonly (string Method, string Suffix, string Module, string Action)[] AuditActions =
    [
        ("POST", "/lfg/activities", "lfg", "Added activity"),
        ("DELETE", "/lfg/activities/{key}", "lfg", "Removed activity"),
        ("POST", "/lfg/groups/{groupId}/close", "lfg", "Force-closed LFG group"),
        ("POST", "/lfg/groups/{groupId}/roster", "lfg", "Changed LFG roster"),
        ("POST", "/lfg/create", "lfg", "Created LFG group"),
        ("POST", "/lfg/panel", "lfg", "Posted LFG panel"),
        ("POST", "/lfg/recurring", "lfg", "Added recurring LFG"),
        ("PUT", "/lfg/recurring/{id}", "lfg", "Edited recurring LFG"),
        ("DELETE", "/lfg/recurring/{id}", "lfg", "Removed recurring LFG"),
        ("POST", "/tickets/projects", "tickets", "Added ticket project"),
        ("PUT", "/tickets/projects/{key}", "tickets", "Edited ticket project"),
        ("DELETE", "/tickets/projects/{key}", "tickets", "Removed ticket project"),
        ("POST", "/tickets/open/{ticketId}/close", "tickets", "Force-closed ticket"),
        ("POST", "/tickets/panel", "tickets", "Posted ticket panel"),
        ("POST", "/voice/creators/new", "voice", "Created 'join to create' channel"),
        ("PUT", "/voice/creators/{channelId}", "voice", "Set 'join to create' channel"),
        ("DELETE", "/voice/creators/{channelId}", "voice", "Removed 'join to create' channel"),
        ("POST", "/voice/rooms/{channelId}/delete", "voice", "Deleted temp room"),
        ("POST", "/voice/rooms/{channelId}/owner", "voice", "Transferred room ownership"),
        ("POST", "/voice/panel", "voice", "Posted room panel"),
        ("PUT", "/texts/{key}", "texts", "Edited panel text"),
        ("DELETE", "/texts/{key}", "texts", "Reset panel text"),
        ("POST", "/texts/discover", "texts", "Searched for posted panels"),
        ("POST", "/posts/panels/{key}/refresh", "posts", "Refreshed panels"),
        ("POST", "/posts/channels/{channelId}/delete", "posts", "Deleted bot messages"),
        ("PUT", "/access/{userId}", "access", "Set panel access"),
        ("DELETE", "/access/{userId}", "access", "Removed panel access"),
        ("PUT", "/settings/", "settings", "Changed bot settings"),
        ("PUT", "/member-roles/", "settings", "Set member roles"),
        ("POST", "/me/groups/{groupId}/join", "lfg", "Signed up (site)"),
        ("POST", "/me/groups/{groupId}/leave", "lfg", "Left group (site)"),
        ("POST", "/me/characters", "lfg", "Added character (site)"),
        ("PUT", "/me/characters/{name}", "lfg", "Edited character class/note (site)"),
        ("DELETE", "/me/characters/{name}", "lfg", "Removed character (site)"),
        ("PUT", "/me/dm", "lfg", "Changed LFG DMs (site)"),
        ("POST", "/me/create", "lfg", "Created LFG group (site)"),
        ("POST", "/me/groups/{groupId}/roster", "lfg", "Organizer changed roster (site)"),
        ("POST", "/me/groups/{groupId}/close", "lfg", "Organizer closed group (site)"),
        ("POST", "/me/room", "voice", "Changed own room (site)")
    ];

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Фильтр всего /api: после успешного изменения пишет в журнал «кто, что, с чем».
    /// Подробности собираются ДО выполнения (после удаления имя сбора/тикета уже не узнать).
    /// </summary>
    private static async ValueTask<object?> AuditFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method)) return await next(ctx);

        var pattern = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path.Value ?? "";
        var suffix = pattern.Replace("/api/guilds/{guildId}", "").Replace("/api", "");
        if (!suffix.EndsWith('/') && suffix == "/settings") suffix = "/settings/";
        var def = Array.Find(AuditActions, a => a.Method == http.Request.Method && a.Suffix == suffix);

        string? details = null;
        ulong guildId = 0;
        try
        {
            if (http.Request.RouteValues.TryGetValue("guildId", out var g)) ulong.TryParse(g?.ToString(), out guildId);
            details = Describe(http, guildId, ctx.Arguments);
        }
        catch
        {
            // журнал не должен ломать действие
        }

        var result = await next(ctx);

        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? http.Response.StatusCode;
        if (status is >= 200 and < 300)
        {
            var audit = http.RequestServices.GetRequiredService<AuditLog>();
            var user = http.User;
            audit.Add(guildId, user.UserId() ?? 0, user.FindFirst(ClaimTypes.Name)?.Value ?? "?", "web",
                def.Module ?? suffix.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "web",
                def.Action ?? $"{http.Request.Method} {suffix}", details);
        }
        return result;
    }

    /// <summary>Понятные подробности: имена вместо ID + тело запроса.</summary>
    private static string? Describe(HttpContext http, ulong guildId, IList<object?> arguments)
    {
        var services = http.RequestServices;
        var client = services.GetRequiredService<DiscordSocketClient>();
        var guild = client.GetGuild(guildId);
        var parts = new List<string>();

        foreach (var (key, value) in http.Request.RouteValues)
        {
            var text = value?.ToString();
            if (key == "guildId" || string.IsNullOrEmpty(text)) continue;
            switch (key)
            {
                case "groupId" when services.GetRequiredService<LfgStore>().Groups.TryGetValue(text, out var grp):
                    parts.Add($"group: {grp.ActivityName}");
                    break;
                case "ticketId" when services.GetRequiredService<TicketStore>().FindTicket(text) is { } t:
                    parts.Add($"ticket: {services.GetRequiredService<TicketStore>().FindProject(t.GuildId, t.ProjectKey)?.Name} · " +
                              (guild?.GetUser(t.UserId)?.DisplayName ?? t.UserId.ToString()));
                    break;
                case "channelId" when ulong.TryParse(text, out var ch) && guild?.GetChannel(ch) is { } channel:
                    parts.Add($"channel: #{channel.Name}");
                    break;
                case "userId" when ulong.TryParse(text, out var uid):
                    parts.Add($"user: {client.Guilds.Select(x => x.GetUser(uid)).FirstOrDefault(u => u is not null)?.DisplayName ?? text}");
                    break;
                case "key" when services.GetRequiredService<ActivityStore>().Find(guildId, text) is { } a:
                    parts.Add($"activity: {a.Name}");
                    break;
                case "key" when services.GetRequiredService<TicketStore>().FindProject(guildId, text) is { } p:
                    parts.Add($"project: {p.Name}");
                    break;
                case "id" when services.GetRequiredService<RecurringLfgStore>().Find(text) is { } r:
                    parts.Add($"schedule: {r.ActivityName}");
                    break;
                default:
                    parts.Add($"{key}: {text}");
                    break;
            }
        }

        // Тело запроса (записи *Body) — коротко, без длинных текстов
        foreach (var arg in arguments)
        {
            if (arg is null || !arg.GetType().Name.EndsWith("Body")) continue;
            var json = JsonSerializer.Serialize(arg, arg.GetType(), AuditJson);
            parts.Add(json.Length <= 300 ? json : json[..300] + "…");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

public sealed record LfgCreateBody(string? ChannelId, string? ActivityKey, string? CustomName, List<string>? Slots,
    string? Date, string? Time, bool NotifyByDm = true, bool SeatOwner = true);
public sealed record RosterBody(string? Action, string? UserId, string? Character);
public sealed record RecurringBody(string? ChannelId, string? ActivityKey, string? CustomName, List<string>? Slots,
    List<int>? Days, string? Time, int PostHoursBefore = 24, bool NotifyByDm = true, bool SeatOwner = true, bool Enabled = true,
    string? OwnerId = null);
public sealed record SettingsBody(string? TimeZone, int? AutoDeleteHours, int? ReminderMinutes);
