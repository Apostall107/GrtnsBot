using System.Text.Json;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Пост от имени бота: сразу или по времени. Длинный текст отправляется несколькими сообщениями.</summary>
public sealed class Announcement
{
    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public string Content { get; set; } = "";
    /// <summary>Пинговать упоминания (@everyone, роли, люди). Нет — упоминания видны, но без уведомления.</summary>
    public bool AllowMentions { get; set; }
    /// <summary>Когда отправить (null — сразу).</summary>
    public DateTimeOffset? SendAt { get; set; }
    public ulong CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>scheduled / sending / sent / failed</summary>
    public string Status { get; set; } = "scheduled";
    public DateTimeOffset? SentAt { get; set; }
    public List<ulong> MessageIds { get; set; } = [];
    public string? Error { get; set; }
    /// <summary>Кого пинговать: роли, люди, everyone/here — строкой в начале поста.</summary>
    public List<ulong> PingRoleIds { get; set; } = [];
    public List<ulong> PingUserIds { get; set; } = [];
    /// <summary>"everyone", "here" или null.</summary>
    public string? PingEveryone { get; set; }

    /// <summary>Текст поста со строкой пингов в начале.</summary>
    public string FullText() => AnnouncementText.Compose(Content, PingRoleIds, PingUserIds, PingEveryone);

    /// <summary>Какие упоминания реально пингуют: выбранные + (если включено) все из текста.</summary>
    public AllowedMentions Mentions() => AllowMentions
        ? AllowedMentions.All
        : new AllowedMentions
        {
            RoleIds = PingRoleIds.ToList(),
            UserIds = PingUserIds.ToList(),
            AllowedTypes = PingEveryone is null ? AllowedMentionTypes.None : AllowedMentionTypes.Everyone
        };
}

public static class AnnouncementText
{
    public static string Compose(string content, IEnumerable<ulong> roleIds, IEnumerable<ulong> userIds, string? everyone)
    {
        var parts = new List<string>();
        if (everyone is "everyone" or "here") parts.Add($"@{everyone}");
        parts.AddRange(roleIds.Select(id => $"<@&{id}>"));
        parts.AddRange(userIds.Select(id => $"<@{id}>"));
        return parts.Count == 0 ? content.Trim() : string.Join(" ", parts) + "\n" + content.Trim();
    }
}

/// <summary>Шаблон поста: название + канал, текст и пинги.</summary>
public sealed class AnnouncementTemplate
{
    public string Key { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public string Name { get; set; } = "";
    public ulong? ChannelId { get; set; }
    public string Content { get; set; } = "";
    public bool AllowMentions { get; set; }
    public List<ulong> PingRoleIds { get; set; } = [];
    public List<ulong> PingUserIds { get; set; } = [];
    public string? PingEveryone { get; set; }
}

/// <summary>data/announcement-templates.json</summary>
public sealed class AnnouncementTemplateStore
{
    public const int MaxPerGuild = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<AnnouncementTemplate> _data;

    public AnnouncementTemplateStore(IConfiguration config)
    {
        _path = config["Storage:AnnouncementTemplatesPath"] ?? "data/announcement-templates.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<AnnouncementTemplate>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public IReadOnlyList<AnnouncementTemplate> Get(ulong guildId)
    {
        lock (_lock) return _data.Where(t => t.GuildId == guildId).OrderBy(t => t.Name).Select(Clone).ToList();
    }

    /// <summary>Добавить или заменить (по Key). null — успех.</summary>
    public string? Save(AnnouncementTemplate template)
    {
        lock (_lock)
        {
            if (_data.Any(t => t.GuildId == template.GuildId && t.Key != template.Key &&
                               t.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase)))
                return $"Template \"{template.Name}\" already exists.";
            var i = _data.FindIndex(t => t.Key == template.Key && t.GuildId == template.GuildId);
            if (i >= 0) _data[i] = template;
            else
            {
                if (_data.Count(t => t.GuildId == template.GuildId) >= MaxPerGuild) return $"Limit of {MaxPerGuild} templates reached.";
                _data.Add(template);
            }
            Save();
            return null;
        }
    }

    public bool Remove(ulong guildId, string key)
    {
        lock (_lock)
        {
            if (_data.RemoveAll(t => t.GuildId == guildId && t.Key == key) == 0) return false;
            Save();
            return true;
        }
    }

    private static AnnouncementTemplate Clone(AnnouncementTemplate t) =>
        JsonSerializer.Deserialize<AnnouncementTemplate>(JsonSerializer.Serialize(t))!;

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}

/// <summary>Разбивка текста на сообщения Discord (до 2000 символов): по абзацам, строкам, предложениям, словам; блоки кода не рвутся.</summary>
public static class PostSplitter
{
    public const int MaxMessageLength = 2000;
    /// <summary>Максимум текста одного поста — до 10 сообщений.</summary>
    public const int MaxTotalLength = 20000;

    public static List<string> Split(string text, int max = MaxMessageLength)
    {
        text = text.Replace("\r\n", "\n").Trim();
        var blocks = new List<string>();
        var reopen = ""; // открытый блок кода переносится в следующее сообщение
        while (text.Length > 0)
        {
            string chunk;
            if (reopen.Length + text.Length <= max)
            {
                chunk = text;
                text = "";
            }
            else
            {
                var budget = max - reopen.Length - 4; // место под закрывающие ```
                var cut = FindCut(text, budget);
                chunk = text[..cut].TrimEnd();
                text = text[cut..].TrimStart('\n');
            }

            var piece = reopen + chunk;
            var open = OpenFence(piece);
            if (open is not null && text.Length > 0)
            {
                piece += "\n```";
                reopen = open + "\n";
            }
            else reopen = "";
            if (piece.Trim().Length > 0) blocks.Add(piece);
        }
        return blocks;
    }

    /// <summary>Где резать: последний разрыв абзаца, строки, предложения или пробел во второй половине окна.</summary>
    private static int FindCut(string text, int budget)
    {
        var limit = Math.Min(budget, text.Length);
        var min = limit / 2;
        foreach (var sep in new[] { "\n\n", "\n", ". ", "! ", "? ", " " })
        {
            var i = text.LastIndexOf(sep, limit - 1, StringComparison.Ordinal);
            if (i >= min && i + sep.Length <= limit) return i + sep.Length;
        }
        return limit;
    }

    /// <summary>Строка открытия незакрытого блока кода (```lang) или null.</summary>
    private static string? OpenFence(string text)
    {
        string? open = null;
        foreach (var line in text.Split('\n'))
        {
            var t = line.TrimStart();
            if (!t.StartsWith("```")) continue;
            open = open is null ? t.TrimEnd() : null;
        }
        return open;
    }
}

/// <summary>data/announcements.json — запланированные и отправленные посты (по 100 последних отправленных на сервер).</summary>
public sealed class AnnouncementStore
{
    public const int KeepSent = 100;
    public const int MaxScheduled = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<Announcement> _data;

    public AnnouncementStore(IConfiguration config)
    {
        _path = config["Storage:AnnouncementsPath"] ?? "data/announcements.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<Announcement>>(File.ReadAllText(_path)) ?? []
            : [];
        // Бот перезапустился посреди отправки — такой пост считается неудачным (часть сообщений могла уйти)
        foreach (var a in _data.Where(a => a.Status == "sending"))
        {
            a.Status = "failed";
            a.Error = "The bot restarted while sending this post.";
            a.SentAt ??= DateTimeOffset.UtcNow;
        }
    }

    public IReadOnlyList<Announcement> Get(ulong guildId)
    {
        lock (_lock) return _data.Where(a => a.GuildId == guildId).Select(Clone).ToList();
    }

    public IReadOnlyList<Announcement> Due(DateTimeOffset now)
    {
        lock (_lock) return _data.Where(a => a.Status == "scheduled" && a.SendAt is { } t && t <= now).Select(Clone).ToList();
    }

    public Announcement? Find(string id)
    {
        lock (_lock) return _data.Find(a => a.Id == id) is { } a ? Clone(a) : null;
    }

    public string? Add(Announcement item)
    {
        lock (_lock)
        {
            if (item.Status == "scheduled" && _data.Count(a => a.GuildId == item.GuildId && a.Status == "scheduled") >= MaxScheduled)
                return $"Limit of {MaxScheduled} scheduled posts reached.";
            _data.Add(item);
            Trim(item.GuildId);
            Save();
            return null;
        }
    }

    public Announcement? Update(string id, Action<Announcement> update)
    {
        lock (_lock)
        {
            if (_data.Find(a => a.Id == id) is not { } item) return null;
            update(item);
            Trim(item.GuildId);
            Save();
            return Clone(item);
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (_data.RemoveAll(a => a.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    /// <summary>Только запланированный пост переводится в «отправляется» (защита от двойной отправки).</summary>
    public bool TryStartSending(string id)
    {
        lock (_lock)
        {
            if (_data.Find(a => a.Id == id) is not { Status: "scheduled" } item) return false;
            item.Status = "sending";
            Save();
            return true;
        }
    }

    private void Trim(ulong guildId)
    {
        var old = _data.Where(a => a.GuildId == guildId && a.Status is "sent" or "failed")
            .OrderByDescending(a => a.SentAt ?? a.CreatedAt).Skip(KeepSent).ToList();
        foreach (var a in old) _data.Remove(a);
    }

    private static Announcement Clone(Announcement a) =>
        JsonSerializer.Deserialize<Announcement>(JsonSerializer.Serialize(a))!;

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}

/// <summary>Отправка постов: сразу (с сайта) и запланированных (проверка каждые 20 секунд).</summary>
public sealed class AnnouncementService(
    AnnouncementStore store,
    DiscordSocketClient client,
    AuditLog audit,
    ILogger<AnnouncementService> logger) : BackgroundService
{
    /// <summary>Отправить пост, который уже лежит в хранилище со статусом scheduled.</summary>
    public async Task<Announcement?> SendAsync(string id)
    {
        if (!store.TryStartSending(id) || store.Find(id) is not { } post) return store.Find(id);

        var sent = new List<ulong>();
        string? error = null;
        if (client.GetGuild(post.GuildId)?.GetTextChannel(post.ChannelId) is not { } channel)
            error = "The channel no longer exists.";
        else
        {
            var mentions = post.Mentions();
            try
            {
                foreach (var block in PostSplitter.Split(post.FullText()))
                {
                    var message = await channel.SendMessageAsync(block, allowedMentions: mentions);
                    sent.Add(message.Id);
                }
            }
            catch (Discord.Net.HttpException ex)
            {
                error = sent.Count == 0
                    ? "I can't post there — give the bot View Channel and Send Messages."
                    : $"Stopped after {sent.Count} message(s): {ex.Reason ?? ex.Message}";
            }
        }

        var result = store.Update(id, a =>
        {
            a.Status = error is null ? "sent" : "failed";
            a.SentAt = DateTimeOffset.UtcNow;
            a.MessageIds = sent;
            a.Error = error;
        });
        if (post.SendAt is not null)
            audit.FromSystem(post.GuildId, "posts", error is null ? "Sent a scheduled post" : "Scheduled post failed",
                error ?? $"{sent.Count} message(s) in #{client.GetGuild(post.GuildId)?.GetTextChannel(post.ChannelId)?.Name}");
        return result;
    }

    /// <summary>Удалить отправленные сообщения поста.</summary>
    public async Task<int> DeleteMessagesAsync(Announcement post)
    {
        if (client.GetGuild(post.GuildId)?.GetTextChannel(post.ChannelId) is not { } channel) return 0;
        var deleted = 0;
        foreach (var id in post.MessageIds)
        {
            try
            {
                await channel.DeleteMessageAsync(id);
                deleted++;
            }
            catch (Discord.Net.HttpException)
            {
                // уже удалено
            }
        }
        return deleted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (client.ConnectionState != ConnectionState.Connected) continue;
                foreach (var post in store.Due(DateTimeOffset.UtcNow))
                {
                    try
                    {
                        await SendAsync(post.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Scheduled post {Id} failed", post.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // остановка бота
        }
    }
}
