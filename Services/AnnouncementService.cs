using System.Text.Json;
using System.Text.RegularExpressions;
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

    // :shortcode: → <:name:id>, не трогая уже готовые коды <:name:id> и время вида 21:00
    private static readonly Regex Shortcode = new(@"(?<![<\w]):([A-Za-z0-9_]{2,32}):(?!\d)", RegexOptions.Compiled);

    /// <summary>Заменяет :имя: на код эмодзи из карты (имя без учёта регистра). Нет совпадения — оставляет как есть.</summary>
    public static string ResolveEmotes(string text, IReadOnlyDictionary<string, string> byName)
    {
        if (string.IsNullOrEmpty(text) || byName.Count == 0 || !text.Contains(':')) return text;
        return Shortcode.Replace(text, m => byName.TryGetValue(m.Groups[1].Value, out var code) ? code : m.Value);
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
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
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
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}

/// <summary>Отправка постов: сразу (с сайта) и запланированных (проверка каждые 20 секунд).</summary>
public sealed class AnnouncementService(
    AnnouncementStore store,
    DiscordSocketClient client,
    AuditLog audit,
    MediaService media,
    ILogger<AnnouncementService> logger) : BackgroundService
{
    private (DateTimeOffset At, Dictionary<string, string> Map)? _appEmotes;
    private readonly SemaphoreSlim _appGate = new(1, 1);

    /// <summary>:имя: → &lt;:name:id&gt; по эмодзи сервера и эмодзи бота. Нераспознанные :имя: остаются как есть.</summary>
    public async Task<string> ResolveEmotesAsync(ulong guildId, string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(':')) return text;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (client.GetGuild(guildId) is { } guild)
            foreach (var e in guild.Emotes)
                map[e.Name] = $"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>";
        foreach (var (name, code) in await AppEmotesAsync())
            map.TryAdd(name, code); // эмодзи сервера приоритетнее эмодзи бота
        return AnnouncementText.ResolveEmotes(text, map);
    }

    private async Task<Dictionary<string, string>> AppEmotesAsync()
    {
        if (_appEmotes is { } c && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(5)) return c.Map;
        await _appGate.WaitAsync();
        try
        {
            if (_appEmotes is { } c2 && DateTimeOffset.UtcNow - c2.At < TimeSpan.FromMinutes(5)) return c2.Map;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in await client.GetApplicationEmotesAsync())
                    map[e.Name] = $"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>";
            }
            catch (Exception ex) { logger.LogDebug(ex, "Could not load application emotes"); }
            _appEmotes = (DateTimeOffset.UtcNow, map);
            return map;
        }
        finally { _appGate.Release(); }
    }

    /// <summary>
    /// Отправить произвольный текст в канал как один или несколько сообщений (резолв :эмодзи:, разбивка по 2000).
    /// Для сборщика страниц канала. Возвращает ID отправленных сообщений.
    /// </summary>
    public async Task<List<ulong>> PostTextAsync(ulong guildId, IMessageChannel channel, string text)
    {
        var ids = new List<ulong>();
        var resolved = await ResolveEmotesAsync(guildId, text);
        foreach (var block in PostSplitter.Split(resolved))
        {
            if (await SendPartAsync(guildId, channel, block, AllowedMentions.None) is { } message) ids.Add(message.Id);
        }
        return ids;
    }

    /// <summary>Отправить одно сообщение текста: метки [img:имя] становятся вложениями (картинки из медиатеки).</summary>
    public async Task<IUserMessage?> SendPartAsync(ulong guildId, IMessageChannel channel, string part, AllowedMentions mentions)
    {
        var (text, files) = await media.PrepareTextAsync(guildId, part);
        if (files.Count > 0)
            try { return await channel.SendFilesAsync(files, string.IsNullOrWhiteSpace(text) ? null : text, allowedMentions: mentions); }
            finally { foreach (var f in files) f.Dispose(); }
        return string.IsNullOrWhiteSpace(text) ? null : await channel.SendMessageAsync(text, allowedMentions: mentions);
    }

    /// <summary>Поправить сообщение на месте. Вложения перезаливаются, только если набор картинок изменился.</summary>
    public async Task EditPartAsync(ulong guildId, IUserMessage message, string part, AllowedMentions mentions)
    {
        var wanted = await media.FileNamesAsync(guildId, part);
        var current = message.Attachments.Select(a => a.Filename).ToList();
        if (wanted.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
        {
            var clean = MediaService.StripTokens(part);
            await message.ModifyAsync(m => { m.Content = clean; m.AllowedMentions = mentions; });
            return;
        }
        var (text, files) = await media.PrepareTextAsync(guildId, part);
        try
        {
            await message.ModifyAsync(m =>
            {
                m.Content = text;
                m.AllowedMentions = mentions;
                m.Attachments = files;
            });
        }
        finally { foreach (var f in files) f.Dispose(); }
    }

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
                var fullText = await ResolveEmotesAsync(post.GuildId, post.FullText());
                foreach (var block in PostSplitter.Split(fullText))
                {
                    if (await SendPartAsync(post.GuildId, channel, block, mentions) is { } message) sent.Add(message.Id);
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

    /// <summary>
    /// Изменить уже отправленный пост: сообщения редактируются на месте. Текст стал длиннее (больше сообщений) —
    /// недостающие дописываются следом, короче — лишние удаляются. Пинги при редактировании Discord не рассылает.
    /// Возвращает (ошибка, сколько сообщений добавлено).
    /// </summary>
    public async Task<(string? Error, int Added)> EditSentAsync(Announcement post, string content, List<ulong> roleIds, List<ulong> userIds,
        string? everyone, bool allowMentions)
    {
        if (client.GetGuild(post.GuildId)?.GetTextChannel(post.ChannelId) is not { } channel) return ("The channel no longer exists.", 0);
        var updated = new Announcement
        {
            Content = content, PingRoleIds = roleIds, PingUserIds = userIds, PingEveryone = everyone, AllowMentions = allowMentions
        };
        var blocks = PostSplitter.Split(await ResolveEmotesAsync(post.GuildId, updated.FullText()));
        var ids = post.MessageIds.ToList();
        var mentions = updated.Mentions();
        var added = 0;
        try
        {
            for (var i = 0; i < Math.Min(blocks.Count, ids.Count); i++)
            {
                if (await channel.GetMessageAsync(ids[i]) is not IUserMessage message || message.Author.Id != client.CurrentUser.Id)
                    return ("A message of this post was deleted in Discord — send it again instead (↩️ Reuse).", 0);
                await EditPartAsync(post.GuildId, message, blocks[i], mentions);
            }
            // Длиннее — дописать следом; короче — лишние удалить
            for (var i = ids.Count; i < blocks.Count; i++)
            {
                if (await SendPartAsync(post.GuildId, channel, blocks[i], mentions) is not { } message) continue;
                ids.Add(message.Id);
                added++;
            }
            for (var i = ids.Count - 1; i >= blocks.Count; i--)
            {
                try { await channel.DeleteMessageAsync(ids[i]); } catch (Discord.Net.HttpException) { /* уже удалено */ }
                ids.RemoveAt(i);
            }
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Editing post {Id} failed", post.Id);
            return ($"Discord refused the edit: {ex.Reason ?? ex.Message}", added);
        }

        store.Update(post.Id, a =>
        {
            a.Content = content;
            a.PingRoleIds = roleIds;
            a.PingUserIds = userIds;
            a.PingEveryone = everyone;
            a.AllowMentions = allowMentions;
            a.MessageIds = ids;
        });
        return (null, added);
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
