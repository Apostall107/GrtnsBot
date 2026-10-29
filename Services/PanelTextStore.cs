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
        "2. Choose the group size.\n" +
        "3. Choose the activity. Members with its role will be pinged and get a DM " +
        "(turn them on or off for yourself with the 🔔 / 🔕 buttons below or `/lfg-dm`). " +
        "«Custom activity» lets you type a name, without a ping.\n" +
        "4. Select slots and assign roles ⚔️ DD / 🛡️ Tank / 💚 Sup / 🎲 All, then click **Next**.\n" +
        "5. Pick the start date and time (or «No time») and click **Publish**. " +
        "You take slot 1 automatically with your last used character. " +
        "Everyone sees the time in their own time zone.\n\n" +
        "**How to sign up**\n" +
        "• Click a role button under the group post.\n" +
        "• Pick one of your saved characters or enter a new name — it will be saved for next time.\n" +
        "• Under the reply pick your **class role** and add a **note** — shown next to / under your name in the post.\n" +
        "• Click the same role again to switch character, or another role to switch role.\n" +
        "• No free slot for your role? The grey role button puts you on the **reserve** list. " +
        "The organizer can move you into the group, and you'll get a DM.\n" +
        "• **Leave** frees your slot (or takes you off the reserve).\n" +
        "• ⚙️ **Manage** — organizer only: swap members between the group and the reserve.\n" +
        "• Everyone in the group gets a ⏰ reminder {reminder} minutes before the start.\n" +
        "• **Close** — organizer only. Shows the invite list and deletes the group post.\n" +
        "• Group posts are removed automatically {hours} h after the start time " +
        "(or after creation if no time was set).\n" +
        "• 📋 **Invites** shows ready-to-copy `/invite Name` commands for everyone in the group (except you).\n\n" +
        "**Characters:** `/character list`, `/character add`, `/character class`, `/character note`, `/character remove`",
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
        "1. Click **Create ticket**.\n" +
        "2. Choose the project you are applying to (e.g. WWM, Aion 2). Only you can see this menu.\n" +
        "3. The bot creates a private **text** and **voice** channel for you and the project leadership.\n" +
        "4. Talk with the leadership there. When they **Approve** your application, " +
        "you get the project role automatically and the ticket closes (you'll also get a DM).\n" +
        "5. Anyone in the ticket can **Close** it — both channels are deleted.\n\n" +
        "-# You can have one open ticket per project.",
        [new("start", "“Create ticket” button", "Create ticket")],
        "ticket:start");

    public static readonly PanelDef Rooms = new(
        "rooms", "Room control panel",
        "🎛️ Room controls",
        "Use these while you are **in your temporary room**. Only the room owner can use them, except **Claim**.\n\n" +
        "✏️ **Rename** · 👥 **Limit** (0 = no limit) · 🔒 **Lock / Unlock** — when locked, only people inside " +
        "or permitted can join · 👑 **Claim** — take the room if the owner left\n" +
        "✅ **Permit** a member to join even when locked · 🚫 **Kick** a member and block them from rejoining",
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

    public static PanelDef? Find(string key) => Array.Find(All, d => d.Key == key);
}

/// <summary>Свои тексты панелей и список опубликованных панелей по серверам (data/panels.json).</summary>
public sealed class PanelTextStore
{
    public const int MaxPostedPerPanel = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, GuildPanels> _data;

    public PanelTextStore(IConfiguration config)
    {
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
            return _data.TryGetValue(guildId, out var g) && g.Posted.TryGetValue(key, out var list) ? list.ToList() : [];
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
