using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Своя надпись для панели модуля. null-поля — текст по умолчанию.</summary>
public sealed class PanelText
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    /// <summary>Подписи кнопок/списков по id из <see cref="PanelDef.Labels"/>.</summary>
    public Dictionary<string, string> Labels { get; set; } = new();
}

/// <summary>Опубликованное сообщение панели — чтобы обновлять его после смены текста.</summary>
public sealed class PostedPanel
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    /// <summary>LFG-панель: проект (игра), под который идут сборы с этой панели. null — проект по умолчанию.</summary>
    public string? ProjectKey { get; set; }
}

public sealed class GuildPanels
{
    public Dictionary<string, PanelText> Texts { get; set; } = new();
    public Dictionary<string, List<PostedPanel>> Posted { get; set; } = new();
}

public sealed record PanelLabelDef(string Id, string Name, string Default, int MaxLength = 80);

/// <summary>Описание панели: ключ, тексты по умолчанию, редактируемые подписи, custom id кнопки для поиска.</summary>
public sealed record PanelDef(string Key, string Name, string DefaultTitle, string DefaultDescription,
    PanelLabelDef[] Labels, string MarkerCustomId, string? Placeholders = null)
{
    public const int MaxTitle = 256;
    public const int MaxDescription = 4000;

    public string Title(PanelText? t) => string.IsNullOrWhiteSpace(t?.Title) ? DefaultTitle : t.Title!;

    public string Description(PanelText? t) => string.IsNullOrWhiteSpace(t?.Description) ? DefaultDescription : t.Description!;

    public string Label(PanelText? t, string id)
    {
        if (t is not null && t.Labels.TryGetValue(id, out var custom) && !string.IsNullOrWhiteSpace(custom)) return custom;
        return Labels.First(l => l.Id == id).Default;
    }
}

/// <summary>Панели модулей с текстами по умолчанию (то, что было зашито в рендерах).</summary>
public static class PanelDefs
{
    public static readonly PanelDef Lfg = new(
        "lfg", "LFG panel",
        "📣 Group & Raid Finder",
        "**How to create a group**\n" +
        "1. Click **Create LFG** — a menu only you can see will open.\n" +
        "2. Pick a **template** for one click, or choose the group size.\n" +
        "3. Choose the activity of this game. Members with its role are pinged and get a DM " +
        "(turn DMs on or off for yourself with 🔔 / 🔕 below or `/lfg-dm`). «Custom activity» — your own name, no ping.\n" +
        "4. Select slots and set their roles ⚔️ DD / 🛡️ Tank / 💚 Sup / 🎲 All → **Next**.\n" +
        "5. Pick the start date and time (or «No time»), turn on 🔊 **Voice room** if you need one → **Publish**. " +
        "You take slot 1 with your last used character. Everyone sees the time in their own time zone.\n\n" +
        "**How to sign up**\n" +
        "• Click a role button under the group post, then pick a saved character or enter a new name.\n" +
        "• Under the reply pick your **class** (its icon is shown after your name), set your **GS** and a personal **note** (only you see it).\n" +
        "• Same role again — switch character; another role — switch role. **Leave** frees your slot.\n" +
        "• Grey role button — no free slot: you go to the **reserve**; the organizer can move you in (you get a DM).\n" +
        "• Some groups have **requirements** (a role or minimum GS).\n" +
        "• ⏰ Reminder {reminder} minutes before the start; the post is removed {hours} h after the start (or after creation without a time).\n\n" +
        "**In the group post**\n" +
        "• 📋 **Invite to group** — ready `/invite Name` commands; 🏰 **Invite to dungeon** — just the names.\n" +
        "• 🔊 **Voice room** (if turned on) — everyone can listen, only the group speaks.\n" +
        "• ⚙️ **Manage** (organizer) — group ⇄ reserve, attendance, 🕒 change time. **Close** — invites, then the post is deleted.\n\n" +
        "**Characters** (per game): `/character list`, `add`, `class`, `gs`, `note`, `rename`, `remove` — or on the website.",
        [
            new("start", "“Create LFG” button", "Create LFG"),
            new("dmon", "“DMs on” button", "DMs on"),
            new("dmoff", "“DMs off” button", "DMs off")
        ],
        "lfg:start",
        "{hours} = auto-delete hours, {reminder} = reminder minutes");

    public static readonly PanelDef Tickets = new(
        "tickets", "Ticket panel",
        "🎫 Applications & Tickets",
        "**How to apply**\n" +
        "1. Click **Create ticket** and choose the project you are applying to (only you see this menu).\n" +
        "2. If the project has an **application form**, answer the questions.\n" +
        "3. The bot creates a private **text** and **voice** channel for you and the project leadership.\n" +
        "4. Talk with the leadership there. **Approve** — you get the project role, a DM, and the ticket closes. " +
        "**Deny** — you get a DM with the reason.\n" +
        "5. Anyone in the ticket can **Close** it — both channels are deleted.\n\n" +
        "-# One open ticket per project. Inactive tickets may be closed automatically. You can also apply on the website.",
        [new("start", "“Create ticket” button", "Create ticket")],
        "ticket:start");

    public static readonly PanelDef Rooms = new(
        "rooms", "Room control panel",
        "🎛️ Room controls",
        "Use these while you are **in your temporary room**. Only the room owner can use them, except **Claim**.\n\n" +
        "✏️ **Rename** · 👥 **Limit** (0 = no limit) · 🔒 **Lock / Unlock** — when locked, only people inside " +
        "or permitted can join · 👑 **Claim** — take the room if the owner left\n" +
        "✅ **Permit** a member to join even when locked · 🚫 **Kick** a member and block them from rejoining\n\n" +
        "-# Your defaults for new rooms (name, limit, locked): `/room defaults` or on the website.",
        [
            new("rename", "“Rename” button", "Rename"),
            new("limit", "“Limit” button", "Limit"),
            new("lock", "“Lock / Unlock” button", "Lock / Unlock"),
            new("claim", "“Claim” button", "Claim"),
            new("permit", "Permit menu placeholder", "✅ Permit a member", 150),
            new("kick", "Kick menu placeholder", "🚫 Kick a member", 150)
        ],
        "room:rename");

    public static readonly PanelDef[] All = [Lfg, Tickets, Rooms];

    /// <summary>Кнопки в постах сборов (не панель: только подписи).</summary>
    public static readonly PanelDef LfgPost = new(
        "lfgpost", "LFG group post — buttons", "", "",
        [
            new("leave", "“Leave” button", "Leave"),
            new("invites", "“Invite to group” button", "Invite to group"),
            new("dungeon", "“Invite to dungeon” button", "Invite to dungeon"),
            new("manage", "“Manage” button", "Manage"),
            new("close", "“Close” button", "Close"),
            new("voice", "“Voice room” link button", "Voice room")
        ],
        "");

    /// <summary>Кнопки в сообщении тикета.</summary>
    public static readonly PanelDef TicketMessage = new(
        "ticketmsg", "Ticket message — buttons", "", "",
        [
            new("approve", "“Approve” button", "Approve"),
            new("deny", "“Deny” button", "Deny"),
            new("close", "“Close” button", "Close")
        ],
        "");

    /// <summary>Наборы подписей для сообщений бота (не панели — их не публикуют и не ищут).</summary>
    public static readonly PanelDef[] Messages = [LfgPost, TicketMessage];

    public static PanelDef? Find(string key) => Array.Find(All, d => d.Key == key) ?? Array.Find(Messages, d => d.Key == key);

    /// <summary>Подпись кнопки с учётом своих текстов сервера (для статических рендереров).</summary>
    public static string Label(ulong guildId, PanelDef def, string id) => def.Label(PanelTextStore.Instance?.GetText(guildId, def.Key), id);
}

/// <summary>Свои тексты панелей и список опубликованных панелей по серверам (data/panels.json).</summary>
public sealed class PanelTextStore
{
    public const int MaxPostedPerPanel = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, GuildPanels> _data;

    /// <summary>Для статических рендереров постов (подписи кнопок из веб-админки).</summary>
    public static PanelTextStore? Instance { get; private set; }

    public PanelTextStore(IConfiguration config)
    {
        Instance = this;
        _path = config["Storage:PanelsPath"] ?? "data/panels.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<ulong, GuildPanels>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public PanelText? GetText(ulong guildId, string key)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var g) || !g.Texts.TryGetValue(key, out var t)) return null;
            return new PanelText { Title = t.Title, Description = t.Description, Labels = new(t.Labels) };
        }
    }

    /// <summary>null — вернуть тексты по умолчанию.</summary>
    public void SetText(ulong guildId, string key, PanelText? text)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            if (text is null) g.Texts.Remove(key);
            else g.Texts[key] = text;
            Save();
        }
    }

    public IReadOnlyList<PostedPanel> GetPosted(ulong guildId, string key)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var g) && g.Posted.TryGetValue(key, out var list)
                ? list.Select(p => new PostedPanel { ChannelId = p.ChannelId, MessageId = p.MessageId, ProjectKey = p.ProjectKey }).ToList()
                : [];
    }

    public bool Track(ulong guildId, string key, ulong channelId, ulong messageId)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            if (!g.Posted.TryGetValue(key, out var list)) g.Posted[key] = list = [];
            if (list.Any(p => p.MessageId == messageId)) return false;
            list.Add(new PostedPanel { ChannelId = channelId, MessageId = messageId });
            if (list.Count > MaxPostedPerPanel) list.RemoveAt(0);
            Save();
            return true;
        }
    }

    /// <summary>Опубликованная панель по id сообщения (null — не запомнена).</summary>
    public PostedPanel? FindPosted(ulong guildId, string key, ulong messageId)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var g) && g.Posted.TryGetValue(key, out var list) &&
                   list.Find(p => p.MessageId == messageId) is { } found
                ? new PostedPanel { ChannelId = found.ChannelId, MessageId = found.MessageId, ProjectKey = found.ProjectKey }
                : null;
    }

    /// <summary>Привязать LFG-панель к проекту.</summary>
    public bool SetPanelProject(ulong guildId, string key, ulong messageId, string? projectKey)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var g) || !g.Posted.TryGetValue(key, out var list) ||
                list.Find(p => p.MessageId == messageId) is not { } found) return false;
            found.ProjectKey = projectKey;
            Save();
            return true;
        }
    }

    public void Untrack(ulong guildId, string key, ulong messageId)
    {
        lock (_lock)
        {
            if (_data.TryGetValue(guildId, out var g) && g.Posted.TryGetValue(key, out var list) &&
                list.RemoveAll(p => p.MessageId == messageId) > 0)
                Save();
        }
    }

    /// <summary>Какой панелью является сообщение (null — не запомненная панель).</summary>
    public string? KeyOf(ulong guildId, ulong messageId)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var g)) return null;
            foreach (var (key, list) in g.Posted)
                if (list.Any(p => p.MessageId == messageId)) return key;
            return null;
        }
    }

    private GuildPanels Guild(ulong guildId)
    {
        if (!_data.TryGetValue(guildId, out var g)) _data[guildId] = g = new GuildPanels();
        return g;
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
