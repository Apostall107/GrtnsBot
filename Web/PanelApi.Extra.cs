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
                    guildId, channelId, ownerId, name!, pingRole, slots!, startsAt, body.NotifyByDm, body.SeatOwner,
                    Requirements(guild, body.RequiredRoleIds), body.MinGs is > 0 ? body.MinGs : null));
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

        // ----- Проекты (игры): роли, проект по умолчанию, привязка LFG-панелей -----
        g.MapGet("/projects", (ulong guildId, DiscordSocketClient client, LfgProjectStore projects, PanelTextStore panels) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var def = projects.DefaultKey(guildId);
            return Results.Ok(new
            {
                projects = projects.GetProjects(guildId).Select(p => new
                {
                    key = p.Key,
                    name = p.Name,
                    isDefault = p.Key == def,
                    roles = p.RoleIds.Select(id => Role(guild, id))
                }),
                panels = panels.GetPosted(guildId, "lfg").Select(p => new
                {
                    messageId = p.MessageId.ToString(),
                    channel = guild.GetTextChannel(p.ChannelId)?.Name,
                    projectKey = p.ProjectKey,
                    jumpUrl = $"https://discord.com/channels/{guildId}/{p.ChannelId}/{p.MessageId}"
                })
            });
        });

        g.MapPut("/projects/{key}", (ulong guildId, string key, LfgProjectBody body, DiscordSocketClient client, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > 60) return Error("Project name: 1–60 characters.");
            var projectKey = key == "new" ? IdGen.New() : key;
            var error = projects.Save(guildId, new LfgProject
            {
                Key = projectKey,
                Name = name,
                RoleIds = ParseIds(body.RoleIds).Where(id => guild.GetRole(id) is not null).Take(10).ToList()
            });
            // Проект по умолчанию не переключается: в нём лежат персонажи, сборы и панели, созданные до проектов.
            // Его смена «переселила» бы эти данные в другой проект. Первый созданный проект — по умолчанию.
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapDelete("/projects/{key}", (ulong guildId, string key, LfgProjectStore projects) =>
        {
            if (key == projects.DefaultKey(guildId) && projects.GetProjects(guildId).Count > 1)
                return Error("This is the default project — it holds the characters, groups and panels made before projects. " +
                             "It can't be deleted while other projects exist (rename it instead).");
            return projects.Remove(guildId, key) ? Results.Ok() : Error("Project not found.");
        });

        // Под какой проект идут сборы с уже опубликованной LFG-панели
        g.MapPut("/panels/{messageId}/project", (ulong guildId, ulong messageId, LfgPanelProjectBody body, LfgProjectStore projects,
            PanelTextStore panels) =>
        {
            var key = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey;
            if (key is not null && projects.Find(guildId, key) is null) return Error("Project not found.");
            return panels.SetPanelProject(guildId, "lfg", messageId, key) ? Results.Ok() : Error("Panel not found — use Find posted panels first.");
        });

        // ----- Категория голосовых комнат сборов: ?project=key — проекта, без project — общая -----
        g.MapGet("/voice-category", (ulong guildId, string? project, DiscordSocketClient client, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var key = string.IsNullOrWhiteSpace(project) ? null : project;
            var own = projects.VoiceCategory(guildId, key);
            var effective = projects.EffectiveVoiceCategory(guildId, key);
            return Results.Ok(new
            {
                categoryId = own?.ToString(),
                effective = effective is { } e && guild.GetCategoryChannel(e) is { } c ? new { id = c.Id.ToString(), name = c.Name } : null,
                inherited = own is null && effective is not null
            });
        });

        g.MapPut("/voice-category", (ulong guildId, string? project, ChannelBody body, DiscordSocketClient client, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var key = string.IsNullOrWhiteSpace(project) ? null : project;
            if (key is not null && projects.Find(guildId, key) is null) return Error("Project not found.");
            ulong? id = null;
            if (ParseId(body.ChannelId) is { } cid)
            {
                if (guild.GetCategoryChannel(cid) is not { } cat) return Error("Category not found.");
                var perms = guild.CurrentUser.GetPermissions(cat);
                if (!perms.ManageChannel || !perms.ManageRoles)
                    return Error("The bot needs Manage Channels and Manage Roles (Manage Permissions) in this category.");
                id = cid;
            }
            projects.SetVoiceCategory(guildId, key, id);
            return Results.Ok();
        });

        // ----- Автодубли постов сборов: ?project=key — проекта, без project — общие (текстовые и голосовые каналы) -----
        g.MapGet("/auto-mirrors", (ulong guildId, string? project, DiscordSocketClient client, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var key = string.IsNullOrWhiteSpace(project) ? null : project;
            var own = projects.AutoMirrors(guildId, key);
            var effective = projects.EffectiveAutoMirrors(guildId, key);
            object Dto(ulong id) => guild.GetTextChannel(id) is { } c
                ? new { id = id.ToString(), name = c.Name, voice = c is SocketVoiceChannel, exists = true }
                : new { id = id.ToString(), name = "deleted channel", voice = false, exists = false };
            return Results.Ok(new
            {
                channels = own.Select(Dto),
                inherited = own.Count == 0 && effective.Count > 0 ? effective.Select(Dto) : null
            });
        });

        g.MapPut("/auto-mirrors", (ulong guildId, string? project, AutoMirrorsBody body, DiscordSocketClient client, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var key = string.IsNullOrWhiteSpace(project) ? null : project;
            if (key is not null && projects.Find(guildId, key) is null) return Error("Project not found.");
            var ids = new List<ulong>();
            foreach (var id in ParseIds(body.ChannelIds).Distinct())
            {
                if (guild.GetTextChannel(id) is not { } channel) return Error("Channel not found.");
                if (guild.CurrentUser.GetPermissions(channel) is not { ViewChannel: true, SendMessages: true, EmbedLinks: true })
                    return Error($"I can't post in #{channel.Name}. Give the bot View Channel, Send Messages and Embed Links there.");
                ids.Add(id);
            }
            if (ids.Count > LfgPublisher.MaxMirrors) return Error($"Up to {LfgPublisher.MaxMirrors} channels.");
            projects.SetAutoMirrors(guildId, key, ids);
            return Results.Ok();
        });

        // ----- Классы: какие роли — классы и их иконки (эмодзи или эмодзи сервера) -----
        // ?project=key — классы проекта; без project — общие (действуют в проектах без своих классов)
        g.MapGet("/classes", async (ulong guildId, string? project, DiscordSocketClient client, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var key = string.IsNullOrWhiteSpace(project) ? null : project;
            var emojis = guild.Emotes.OrderBy(e => e.Name)
                .Select(e => new EmojiDto(e.Name, $"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>", e.Url, false)).ToList();
            try
            {
                // Эмодзи приложения (бота) — сюда попадают и иконки, взятые из ролей
                foreach (var e in (await client.GetApplicationEmotesAsync()).OrderBy(e => e.Name))
                    emojis.Add(new EmojiDto(e.Name, $"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>", e.Url, true));
            }
            catch (Exception)
            {
                // нет доступа к эмодзи приложения — показываем только серверные
            }
            return Results.Ok(new
            {
                classes = projects.ClassIcons(guildId, key).Select(kv => new
                {
                    role = Role(guild, kv.Key),
                    icon = kv.Value,
                    roleIcon = RoleIcon(guild.GetRole(kv.Key))
                }),
                roleIcons = guild.Roles.Where(r => RoleIcon(r) is not null).ToDictionary(r => r.Id.ToString(), r => RoleIcon(r)),
                emojis
            });
        });

        // Иконки ролей → иконки классов. Эмодзи-иконка роли берётся как есть,
        // картинка-иконка загружается в эмодзи приложения бота (cls_<роль>) — их можно показывать в постах.
        g.MapPost("/classes/role-icons", async (ulong guildId, ClassRoleIconsBody body, DiscordSocketClient client,
            IHttpClientFactory http) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            IReadOnlyCollection<Emote> appEmotes;
            try { appEmotes = await client.GetApplicationEmotesAsync(); }
            catch (Exception) { appEmotes = []; }

            var icons = new Dictionary<string, string>();
            var skipped = new List<string>();
            foreach (var raw in (body.RoleIds ?? []).Distinct().Take(50))
            {
                if (ParseId(raw) is not { } id || guild.GetRole(id) is not { } role) continue;
                if (!string.IsNullOrEmpty(role.Emoji?.Name))
                {
                    icons[raw] = role.Emoji.Name;
                    continue;
                }
                if (string.IsNullOrEmpty(role.Icon))
                {
                    skipped.Add($"@{role.Name}: no role icon");
                    continue;
                }
                var name = EmoteName(role);
                try
                {
                    var emote = appEmotes.FirstOrDefault(e => e.Name == name);
                    if (emote is null)
                    {
                        var bytes = await http.CreateClient().GetByteArrayAsync(role.GetIconUrl());
                        using var stream = new MemoryStream(bytes);
                        emote = await client.CreateApplicationEmoteAsync(name, new Image(stream));
                    }
                    icons[raw] = $"<:{emote.Name}:{emote.Id}>";
                }
                catch (Exception ex)
                {
                    skipped.Add($"@{role.Name}: {ex.Message}");
                }
            }
            return Results.Ok(new { icons, skipped });
        });

        g.MapPut("/classes", (ulong guildId, string? project, ClassIconsBody body, DiscordSocketClient client, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var key = string.IsNullOrWhiteSpace(project) ? null : project;
            if (key is not null && projects.Find(guildId, key) is null) return Error("Project not found.");
            var map = new Dictionary<ulong, string>();
            foreach (var c in body.Classes ?? [])
            {
                if (ParseId(c.RoleId) is not { } id || guild.GetRole(id) is null) continue;
                var icon = (c.Icon ?? "").Trim();
                if (icon.Length > 80) return Error("Icon is too long.");
                map[id] = icon;
            }
            projects.SetClassIcons(guildId, key, map);
            return Results.Ok();
        });

        // ----- Шаблоны сборов -----
        g.MapGet("/templates", (ulong guildId, DiscordSocketClient client, LfgTemplateStore templates) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(TemplatesDto(guild, templates));
        });

        g.MapPut("/templates/{key}", (ulong guildId, string key, TemplateBody body, DiscordSocketClient client,
            ActivityStore activities, LfgTemplateStore templates, LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var projectKey = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey;
            if (projectKey is not null && projects.Find(guildId, projectKey) is null) return Error("Project not found.");
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > 80) return Error("Template name: 1–80 characters.");
            if (!string.IsNullOrWhiteSpace(body.ActivityKey) && activities.Find(guildId, body.ActivityKey) is null) return Error("Activity not found.");
            var slots = new List<SlotRole>();
            foreach (var t in body.Slots ?? [])
            {
                if (!Enum.TryParse<SlotRole>(t, true, out var r)) return Error($"Unknown slot role '{t}'.");
                slots.Add(r);
            }
            if (slots.Count is < LfgRenderer.MinSize or > LfgRenderer.MaxSize) return Error($"A group has {LfgRenderer.MinSize}–{LfgRenderer.MaxSize} slots.");
            if (body.MinGs is < 0 or > 99999) return Error("Minimum GS: 0–99999.");

            var error = templates.Save(guildId, new LfgTemplate
            {
                Key = key == "new" ? IdGen.New() : key,
                Name = name,
                ActivityKey = string.IsNullOrWhiteSpace(body.ActivityKey) ? null : body.ActivityKey,
                CustomActivity = string.IsNullOrWhiteSpace(body.ActivityKey) ? (body.CustomActivity ?? name).Trim() : null,
                Slots = slots,
                RequiredRoleIds = Requirements(guild, body.RequiredRoleIds) ?? [],
                MinGs = body.MinGs is > 0 ? body.MinGs : null,
                ProjectKey = projectKey
            });
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapDelete("/templates/{key}", (ulong guildId, string key, LfgTemplateStore templates) =>
            templates.Remove(guildId, key) ? Results.Ok() : Error("Template not found."));

        // ----- Перенос времени и явка (админ — любой сбор) -----
        g.MapPost("/groups/{groupId}/time", (ulong guildId, string groupId, TimeBody body, ClaimsPrincipal user, DiscordSocketClient client,
            LfgStore lfg, LfgSignupService signup, [FromServices] TimeService time) =>
            RescheduleEndpointAsync(guildId, groupId, body, user, client, lfg, signup, time, organizerOnly: false));

        g.MapPost("/groups/{groupId}/attendance", (ulong guildId, string groupId, AttendanceBody body, LfgStore lfg, LfgSignupService signup) =>
            AttendanceAsync(guildId, groupId, body, lfg, signup, null));

        // ----- Статистика -----
        g.MapGet("/stats", (ulong guildId, int? days, string? project, DiscordSocketClient client, LfgHistoryStore history,
            LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var since = days is > 0 ? DateTimeOffset.UtcNow.AddDays(-days.Value) : (DateTimeOffset?)null;
            var list = history.Get(guildId, since);
            if (!string.IsNullOrWhiteSpace(project))
            {
                // Старые сборы без проекта — в проекте по умолчанию
                var def = projects.DefaultKey(guildId);
                list = list.Where(e => (e.ProjectKey ?? def) == project).ToList();
            }
            var players = LfgStats.Players(list);
            return Results.Ok(new
            {
                groups = list.Count,
                attendanceMarked = list.Sum(e => e.Members.Count(m => m.Attended is not null)),
                activities = list.GroupBy(e => e.Activity).OrderByDescending(x => x.Count())
                    .Select(x => new { name = x.Key, groups = x.Count(), avgFilled = Math.Round(x.Average(e => e.Members.Count), 1) }),
                topPlayers = players.Where(p => p.Groups > 0).OrderByDescending(p => p.Groups).Take(15).Select(p => StatsDto(guild, p)),
                topOrganizers = players.Where(p => p.Organized > 0).OrderByDescending(p => p.Organized).Take(10).Select(p => StatsDto(guild, p)),
                noShows = players.Where(p => p.NoShows > 0).OrderByDescending(p => p.NoShows).Take(10).Select(p => StatsDto(guild, p))
            });
        });

        // Канал, куда уходят сборы, созданные участниками на сайте
        g.MapGet("/channel", (ulong guildId, DiscordSocketClient client, BotSettingsStore settings, PanelTextStore panels) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var current = LfgChannel(guild, settings, panels);
            return Results.Ok(new
            {
                channelId = settings.GetLfgChannel(guildId)?.ToString(),
                effective = current is null ? null : new { id = current.Id.ToString(), name = current.Name },
                fromPanel = settings.GetLfgChannel(guildId) is null && current is not null
            });
        });

        g.MapPut("/channel", (ulong guildId, ChannelBody body, DiscordSocketClient client, BotSettingsStore settings) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (ParseId(body.ChannelId) is { } id)
            {
                if (guild.GetTextChannel(id) is not { } channel) return Error("Text channel not found.");
                var p = guild.CurrentUser.GetPermissions(channel);
                if (!p.ViewChannel || !p.SendMessages || !p.EmbedLinks)
                    return Error("I can't post there. Give the bot View Channel, Send Messages and Embed Links.");
                settings.SetLfgChannel(guildId, id);
            }
            else settings.SetLfgChannel(guildId, null);
            return Results.Ok();
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
                    projectKey = r.ProjectKey,
                    requiredRoles = r.RequiredRoleIds.Select(id => Role(guild, id)),
                    minGs = r.MinGs,
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
                r.ProjectKey = draft.ProjectKey;
                r.RequiredRoleIds = draft.RequiredRoleIds;
                r.MinGs = draft.MinGs;
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
    /// <summary>Иконка роли: эмодзи или картинка (null — у роли нет иконки).</summary>
    private static object? RoleIcon(SocketRole? role)
    {
        if (role is null) return null;
        if (!string.IsNullOrEmpty(role.Emoji?.Name)) return new { emoji = role.Emoji.Name, url = (string?)null };
        if (!string.IsNullOrEmpty(role.Icon)) return new { emoji = (string?)null, url = role.GetIconUrl() };
        return null;
    }

    /// <summary>Имя эмодзи приложения для иконки роли: cls_имя_хеш (2–32 символа, буквы/цифры/_). Новая иконка роли — новое эмодзи.</summary>
    private static string EmoteName(SocketRole role)
    {
        var clean = new string(role.Name.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_').ToArray());
        if (clean.Length == 0) clean = role.Id.ToString()[^6..];
        if (clean.Length > 18) clean = clean[..18];
        var hash = role.Icon.Length > 6 ? role.Icon[..6] : role.Icon;
        return $"cls_{clean}_{hash}";
    }

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
                lfg.RecordClosed(group, "closed");
                lfg.SaveGroups();
                await signup.DeleteMessagesAsync(group);
            }
        }
        finally
        {
            group.Gate.Release();
        }
    }

    private static List<ulong>? Requirements(SocketGuild guild, List<string>? roleIds)
    {
        var ids = ParseIds(roleIds).Where(id => guild.GetRole(id) is not null).Take(10).ToList();
        return ids.Count == 0 ? null : ids;
    }

    private static object TemplatesDto(SocketGuild guild, LfgTemplateStore templates) =>
        templates.Get(guild.Id).Select(t => new
        {
            key = t.Key,
            name = t.Name,
            activityKey = t.ActivityKey,
            customActivity = t.CustomActivity,
            slots = t.Slots.Select(s => s.ToString()),
            requiredRoles = t.RequiredRoleIds.Select(id => Role(guild, id)),
            minGs = t.MinGs,
            projectKey = t.ProjectKey
        }).ToList();

    private static object StatsDto(SocketGuild guild, PlayerStats p) => new
    {
        member = Member(guild, p.UserId),
        groups = p.Groups,
        organized = p.Organized,
        attended = p.Attended,
        noShows = p.NoShows,
        unmarked = p.Unmarked,
        reliability = p.Reliability
    };

    /// <summary>Перенос времени сбора (админ — любой, организатор — свой). Всем записавшимся уходит ЛС.</summary>
    private static async Task<IResult> RescheduleEndpointAsync(ulong guildId, string groupId, TimeBody body, ClaimsPrincipal user,
        DiscordSocketClient client, LfgStore lfg, LfgSignupService signup, TimeService time, bool organizerOnly)
    {
        if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
        var me = user.UserId()!.Value;
        if (organizerOnly && group.OwnerId != me) return Error("Only the organizer can change the time.");

        DateTimeOffset? startsAt = null;
        if (!string.IsNullOrWhiteSpace(body.Date) || !string.IsNullOrWhiteSpace(body.Time))
        {
            if (!DateOnly.TryParseExact(body.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !TimeOnly.TryParseExact(body.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return Error("Set both the date and the time, or leave both empty.");
            startsAt = time.ToUtc(date, t.Hour, t.Minute);
            if (startsAt < DateTimeOffset.UtcNow.AddMinutes(-1)) return Error("This time is in the past.");
        }

        IUser by = client.GetUser(me) as IUser ?? await client.Rest.GetUserAsync(me);
        var error = await signup.RescheduleAsync(group, startsAt, time, by);
        return error is null ? Results.Ok() : Error(error);
    }

    /// <summary>Отметка явки участника слота (после старта или в сборе без времени).</summary>
    private static async Task<IResult> AttendanceAsync(ulong guildId, string groupId, AttendanceBody body, LfgStore lfg,
        LfgSignupService signup, ulong? organizerId)
    {
        if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
        if (organizerId is { } o && group.OwnerId != o) return Error("Only the organizer can mark attendance.");
        if (group.StartsAt > DateTimeOffset.UtcNow) return Error("Attendance can be marked after the start.");
        if (ParseId(body.UserId) is not { } userId) return Error("Unknown member.");

        await group.Gate.WaitAsync();
        try
        {
            if (group.Slots.Find(s => s.UserId == userId) is not { } slot) return Error("This member is not in the group.");
            slot.Attended = body.Attended;
            lfg.SaveGroups();
            await signup.RefreshPostAsync(group);
        }
        finally
        {
            group.Gate.Release();
        }
        return Results.Ok();
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
        item.ProjectKey = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey;
        item.RequiredRoleIds = ParseIds(body.RequiredRoleIds).Where(id => guild.GetRole(id) is not null).Take(10).ToList();
        if (body.MinGs is < 0 or > 99999) return "Minimum GS: 0–99999.";
        item.MinGs = body.MinGs is > 0 ? body.MinGs : null;
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
        ("PUT", "/lfg/channel", "lfg", "Set channel for groups"),
        ("PUT", "/lfg/templates/{key}", "lfg", "Saved LFG template"),
        ("PUT", "/lfg/projects/{key}", "lfg", "Saved LFG project"),
        ("DELETE", "/lfg/projects/{key}", "lfg", "Removed LFG project"),
        ("PUT", "/lfg/panels/{messageId}/project", "lfg", "Linked LFG panel to a project"),
        ("PUT", "/lfg/classes", "lfg", "Saved class icons"),
        ("PUT", "/lfg/voice-category", "lfg", "Set voice room category"),
        ("PUT", "/lfg/auto-mirrors", "lfg", "Set channels for duplicate posts"),
        ("POST", "/announce/", "posts", "Sent / scheduled a post"),
        ("PUT", "/announce/{id}", "posts", "Edited a scheduled post"),
        ("POST", "/announce/{id}/send-now", "posts", "Sent a scheduled post now"),
        ("DELETE", "/announce/{id}", "posts", "Cancelled / removed a post"),
        ("POST", "/announce/{id}/delete-messages", "posts", "Deleted a post's messages"),
        ("PUT", "/announce/templates/{key}", "posts", "Saved a post template"),
        ("DELETE", "/announce/templates/{key}", "posts", "Deleted a post template"),
        ("POST", "/lfg/classes/role-icons", "lfg", "Imported class icons from roles"),
        ("PUT", "/lfg/activities/{key}/project", "lfg", "Moved activity to a project"),
        ("PUT", "/lfg/activities/{key}", "lfg", "Edited activity"),
        ("POST", "/lfg/mirror", "lfg", "Duplicated LFG post"),
        ("DELETE", "/lfg/templates/{key}", "lfg", "Removed LFG template"),
        ("POST", "/lfg/groups/{groupId}/time", "lfg", "Changed LFG time"),
        ("POST", "/lfg/groups/{groupId}/attendance", "lfg", "Marked attendance"),
        ("POST", "/me/groups/{groupId}/time", "lfg", "Organizer changed time (site)"),
        ("POST", "/me/groups/{groupId}/attendance", "lfg", "Organizer marked attendance (site)"),
        ("POST", "/lfg/recurring", "lfg", "Added recurring LFG"),
        ("PUT", "/lfg/recurring/{id}", "lfg", "Edited recurring LFG"),
        ("DELETE", "/lfg/recurring/{id}", "lfg", "Removed recurring LFG"),
        ("POST", "/tickets/projects", "tickets", "Added ticket project"),
        ("PUT", "/tickets/projects/{key}", "tickets", "Edited ticket project"),
        ("DELETE", "/tickets/projects/{key}", "tickets", "Removed ticket project"),
        ("POST", "/tickets/open/{ticketId}/close", "tickets", "Force-closed ticket"),
        ("POST", "/tickets/open/{ticketId}/approve", "tickets", "Approved ticket (site)"),
        ("POST", "/tickets/open/{ticketId}/deny", "tickets", "Denied ticket (site)"),
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
        ("PUT", "/lfg/settings/", "lfg", "Changed LFG settings (time zone, auto-delete, reminder)"),
        ("POST", "/backup/restore", "settings", "Uploaded a data backup"),
        ("PUT", "/member-roles/", "settings", "Set member roles"),
        ("POST", "/me/groups/{groupId}/join", "lfg", "Signed up (site)"),
        ("POST", "/me/groups/{groupId}/leave", "lfg", "Left group (site)"),
        ("POST", "/me/characters", "lfg", "Added character (site)"),
        ("PUT", "/me/characters/{name}", "lfg", "Edited character class/note (site)"),
        ("POST", "/me/characters/{name}/rename", "lfg", "Renamed character (site)"),
        ("DELETE", "/me/characters/{name}", "lfg", "Removed character (site)"),
        ("PUT", "/me/dm", "lfg", "Changed LFG DMs (site)"),
        ("POST", "/me/create", "lfg", "Created LFG group (site)"),
        ("POST", "/me/groups/{groupId}/roster", "lfg", "Organizer changed roster (site)"),
        ("POST", "/me/groups/{groupId}/close", "lfg", "Organizer closed group (site)"),
        ("POST", "/me/room", "voice", "Changed own room (site)"),
        ("POST", "/me/tickets", "tickets", "Applied (site)"),
        ("PUT", "/me/room-prefs", "voice", "Saved room defaults (site)")
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
        if (suffix == "/announce/preview") return await next(ctx); // предпросмотр ничего не меняет
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
    string? Date, string? Time, bool NotifyByDm = true, bool SeatOwner = true, List<string>? RequiredRoleIds = null, int? MinGs = null,
    string? ProjectKey = null, List<string>? MirrorChannelIds = null, bool CreateVoice = false);
public sealed record RosterBody(string? Action, string? UserId, string? Character);
public sealed record RecurringBody(string? ChannelId, string? ActivityKey, string? CustomName, List<string>? Slots,
    List<int>? Days, string? Time, int PostHoursBefore = 24, bool NotifyByDm = true, bool SeatOwner = true, bool Enabled = true,
    string? OwnerId = null, List<string>? RequiredRoleIds = null, int? MinGs = null, string? ProjectKey = null);
public sealed record LfgProjectBody(string? Name, List<string>? RoleIds, bool IsDefault = false);
public sealed record LfgPanelProjectBody(string? ProjectKey);
public sealed record ClassIconBody(string? RoleId, string? Icon);
public sealed record ClassIconsBody(List<ClassIconBody>? Classes);
public sealed record ClassRoleIconsBody(List<string>? RoleIds);
public sealed record EmojiDto(string Name, string Code, string Url, bool App);
public sealed record TemplateBody(string? Name, string? ActivityKey, string? CustomActivity, List<string>? Slots,
    List<string>? RequiredRoleIds, int? MinGs, string? ProjectKey = null);
public sealed record TimeBody(string? Date, string? Time);
public sealed record AttendanceBody(string? UserId, bool? Attended);
public sealed record SettingsBody(string? TimeZone, int? AutoDeleteHours, int? ReminderMinutes);

public sealed record AutoMirrorsBody(List<string>? ChannelIds);
