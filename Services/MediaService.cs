using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Картинка/гифка из медиатеки сервера (живёт вложением в служебном канале Discord).</summary>
public sealed record MediaItem(ulong MessageId, string Name, string FileName, string Url, string? ContentType, int Size,
    int? Width, int? Height, DateTimeOffset At, bool ByBot);

/// <summary>
/// Медиатека: картинки и гифки хранятся НЕ на сервере бота, а вложениями в служебном канале Discord (Settings → Media).
/// Бот только помнит канал. При публикации файл скачивается в память и прикладывается к посту — у поста своя копия,
/// поэтому картинка не пропадёт, даже если её убрать из медиатеки. В текстах — метка [img:имя], в полях картинок панелей — она же.
/// </summary>
public sealed class MediaService(DiscordSocketClient client, GuildPrefsStore prefs, ILogger<MediaService> logger)
{
    public const int MaxBytes = 10 * 1024 * 1024; // лимит вложения для бота без бустов
    public const int MaxPerMessage = 10;
    public static readonly Regex TokenRe = new(@"\[img:([A-Za-z0-9_\-]{1,40})\]", RegexOptions.Compiled);
    private static readonly HttpClient Http = CreateHttp();
    private readonly Dictionary<ulong, (DateTimeOffset At, List<MediaItem> Items)> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; GreatnessBot/1.0)");
        return http;
    }

    public static bool IsToken(string? s) => s is not null && TokenRe.IsMatch(s.Trim()) && TokenRe.Match(s.Trim()).Length == s.Trim().Length;
    public static string Token(string name) => $"[img:{name}]";
    public static string StripTokens(string text) => TokenRe.IsMatch(text) ? Cleanup(TokenRe.Replace(text, "")) : text;

    public ulong? ChannelId(ulong guildId) => prefs.Get(guildId).MediaChannelId;

    public SocketTextChannel? Channel(ulong guildId) =>
        ChannelId(guildId) is { } id ? client.GetGuild(guildId)?.GetTextChannel(id) : null;

    // ---------- Список ----------

    /// <summary>Картинки медиатеки (новые сверху). Кэш 10 минут — ссылки Discord на вложения живут ~сутки.</summary>
    public async Task<List<MediaItem>> ListAsync(ulong guildId, bool fresh = false)
    {
        if (!fresh && _cache.TryGetValue(guildId, out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(10)) return c.Items;
        await _gate.WaitAsync();
        try
        {
            if (!fresh && _cache.TryGetValue(guildId, out var c2) && DateTimeOffset.UtcNow - c2.At < TimeSpan.FromMinutes(10)) return c2.Items;
            var items = new List<MediaItem>();
            if (Channel(guildId) is { } channel)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var messages = await channel.GetMessagesAsync(500).FlattenAsync();
                foreach (var m in messages.OrderByDescending(m => m.Id))
                {
                    var byBot = m.Author.Id == client.CurrentUser.Id;
                    var images = m.Attachments.Where(IsImage).ToList();
                    for (var i = 0; i < images.Count; i++)
                    {
                        var a = images[i];
                        // Имя: у загруженных ботом — текст сообщения, у залитых руками — по имени файла
                        var name = byBot && images.Count == 1 && Slug(m.Content) is { Length: > 0 } n ? n : Slug(Path.GetFileNameWithoutExtension(a.Filename));
                        if (string.IsNullOrEmpty(name)) name = "image";
                        var unique = name;
                        for (var k = 2; !names.Add(unique); k++) unique = $"{name}-{k}";
                        items.Add(new MediaItem(m.Id, unique, a.Filename, a.Url, a.ContentType, a.Size, a.Width, a.Height, m.Timestamp, byBot));
                    }
                }
            }
            _cache[guildId] = (DateTimeOffset.UtcNow, items);
            return items;
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Media library of {Guild} unavailable", guildId);
            return [];
        }
        finally { _gate.Release(); }
    }

    public void Invalidate(ulong guildId) => _cache.Remove(guildId);

    public async Task<MediaItem?> FindAsync(ulong guildId, string name) =>
        (await ListAsync(guildId)).FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsImage(IAttachment a) =>
        a.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
        Path.GetExtension(a.Filename).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";

    // ---------- Добавление ----------

    /// <summary>Залить файл в медиатеку. Возвращает (элемент, ошибка).</summary>
    public async Task<(MediaItem? Item, string? Error)> UploadAsync(ulong guildId, string? name, string fileName, byte[] data)
    {
        if (Channel(guildId) is not { } channel) return (null, "Choose the media channel first (Media → Storage channel).");
        if (data.Length == 0) return (null, "The file is empty.");
        if (data.Length > MaxBytes) return (null, $"The file is larger than {MaxBytes / 1024 / 1024} MB — Discord won't take it from a bot.");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")) ext = Sniff(data);
        if (ext is null) return (null, "Only PNG, JPG, GIF and WebP images.");

        var existing = await ListAsync(guildId);
        var baseName = Slug(name) is { Length: > 0 } s ? s : Slug(Path.GetFileNameWithoutExtension(fileName)) is { Length: > 0 } f ? f : "image";
        var unique = baseName;
        for (var k = 2; existing.Any(i => string.Equals(i.Name, unique, StringComparison.OrdinalIgnoreCase)); k++) unique = $"{baseName}-{k}";

        try
        {
            using var stream = new MemoryStream(data);
            await channel.SendFileAsync(stream, unique + ext, unique);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Media upload failed in {Guild}", guildId);
            return (null, "I can't post in the media channel — give the bot View Channel, Send Messages and Attach Files there.");
        }
        Invalidate(guildId);
        return (await FindAsync(guildId, unique), null);
    }

    /// <summary>Добавить по ссылке: прямая картинка, Tenor или Giphy — файл скачивается и кладётся в медиатеку.</summary>
    public async Task<(MediaItem? Item, string? Error)> AddFromUrlAsync(ulong guildId, string? name, string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return (null, "Paste a link that starts with https://");
        try
        {
            var direct = await DirectImageUrlAsync(uri);
            if (direct is null) return (null, "No image found at this link. Use a direct image link or a Tenor / Giphy link.");
            using var resp = await Http.GetAsync(direct, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return (null, $"The site answered {(int)resp.StatusCode}.");
            if (resp.Content.Headers.ContentLength > MaxBytes) return (null, "The image is larger than 10 MB.");
            var data = await resp.Content.ReadAsByteArrayAsync();
            var file = Path.GetFileName(direct.AbsolutePath);
            if (string.IsNullOrEmpty(Path.GetExtension(file))) file += Sniff(data) ?? ".png";
            var fallbackName = uri.Host.Contains("tenor") || uri.Host.Contains("giphy") ? Slug(uri.Segments.LastOrDefault()) : null;
            return await UploadAsync(guildId, name ?? fallbackName, file, data);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Couldn't download the image from that link.");
        }
    }

    /// <summary>Ссылка страницы (Tenor/Giphy) → прямая ссылка на гифку; прямая картинка — как есть.</summary>
    private static async Task<Uri?> DirectImageUrlAsync(Uri uri)
    {
        // giphy.com/gifs/slug-ID → media.giphy.com/media/ID/giphy.gif
        if (uri.Host.EndsWith("giphy.com") && !uri.Host.StartsWith("media"))
        {
            var id = uri.Segments.LastOrDefault()?.Trim('/').Split('-').LastOrDefault();
            if (!string.IsNullOrEmpty(id)) return new Uri($"https://media.giphy.com/media/{id}/giphy.gif");
        }

        using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return null;
        if (resp.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true) return uri;
        if (resp.Content.Headers.ContentType?.MediaType?.Contains("html") != true) return null;

        // Страница: og:image / contentUrl (Tenor кладёт туда .gif)
        var html = await resp.Content.ReadAsStringAsync();
        foreach (var pattern in new[]
                 {
                     @"""contentUrl""\s*:\s*""(https://[^""]+?\.gif)""",
                     @"<meta[^>]+property=""og:image""[^>]+content=""([^""]+)""",
                     @"<meta[^>]+content=""([^""]+)""[^>]+property=""og:image"""
                 })
            if (Regex.Match(html, pattern, RegexOptions.IgnoreCase) is { Success: true } m &&
                Uri.TryCreate(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Replace("\\u002F", "/")), UriKind.Absolute, out var found))
                return found;
        return null;
    }

    public async Task<string?> DeleteAsync(ulong guildId, ulong messageId)
    {
        if (Channel(guildId) is not { } channel) return "No media channel.";
        try { await channel.DeleteMessageAsync(messageId); }
        catch (Discord.Net.HttpException) { return "I can't delete it — give the bot Manage Messages in the media channel or delete it there."; }
        Invalidate(guildId);
        return null;
    }

    /// <summary>Переименовать (только загруженные ботом: имя — текст сообщения).</summary>
    public async Task<string?> RenameAsync(ulong guildId, ulong messageId, string name)
    {
        if (Channel(guildId) is not { } channel) return "No media channel.";
        var slug = Slug(name);
        if (string.IsNullOrEmpty(slug)) return "Use letters, digits, - and _.";
        if ((await ListAsync(guildId)).Any(i => i.MessageId != messageId && string.Equals(i.Name, slug, StringComparison.OrdinalIgnoreCase)))
            return "This name is taken.";
        try
        {
            if (await channel.GetMessageAsync(messageId) is not IUserMessage m || m.Author.Id != client.CurrentUser.Id)
                return "Only images uploaded through the site can be renamed (others are named by their file).";
            await m.ModifyAsync(x => x.Content = slug);
        }
        catch (Discord.Net.HttpException) { return "Discord refused the rename."; }
        Invalidate(guildId);
        return null;
    }

    // ---------- Публикация ----------

    /// <summary>Скачать картинку медиатеки в память как вложение.</summary>
    public async Task<FileAttachment?> OpenAsync(ulong guildId, string name, string? prefix = null)
    {
        if (await FindAsync(guildId, name) is not { } item) return null;
        try
        {
            var data = await Http.GetByteArrayAsync(item.Url);
            return new FileAttachment(new MemoryStream(data), FileNameFor(item, prefix));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Ссылка протухла раньше кэша — перечитать канал и попробовать ещё раз
            Invalidate(guildId);
            if (await FindAsync(guildId, name) is not { } again) return null;
            try { return new FileAttachment(new MemoryStream(await Http.GetByteArrayAsync(again.Url)), FileNameFor(again, prefix)); }
            catch (Exception ex2) when (ex2 is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex2, "Media {Name} download failed", name);
                return null;
            }
        }
    }

    private static string FileNameFor(MediaItem item, string? prefix)
    {
        var ext = Path.GetExtension(item.FileName);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        return $"{prefix}{item.Name}{ext.ToLowerInvariant()}";
    }

    /// <summary>Имена файлов, которые получит текст (по меткам [img:…]) — чтобы понять, менялись ли картинки.</summary>
    public async Task<List<string>> FileNamesAsync(ulong guildId, string text)
    {
        var list = new List<string>();
        foreach (Match m in TokenRe.Matches(text))
            if (await FindAsync(guildId, m.Groups[1].Value) is { } item) list.Add(FileNameFor(item, null));
        return list;
    }

    /// <summary>Текст сообщения → (текст без меток [img:…], вложения по меткам). Неизвестные метки просто убираются.</summary>
    public async Task<(string Text, List<FileAttachment> Files)> PrepareTextAsync(ulong guildId, string text)
    {
        var files = new List<FileAttachment>();
        if (!text.Contains("[img:")) return (text, files);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TokenRe.Matches(text))
        {
            var name = m.Groups[1].Value;
            if (files.Count >= MaxPerMessage || !used.Add(name)) continue;
            if (await OpenAsync(guildId, name) is { } f) files.Add(f);
        }
        return (StripTokens(text), files);
    }

    /// <summary>
    /// Поле картинки embed: ссылка — как есть; [img:имя] — вложение и attachment://… для embed. slot различает картинку и миниатюру.
    /// </summary>
    public async Task<(string? Url, FileAttachment? File)> PrepareEmbedImageAsync(ulong guildId, string? value, string slot)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var m = TokenRe.Match(value.Trim());
        if (!m.Success) return (CommunityService.IsUrl(value) ? value.Trim() : null, null);
        if (await OpenAsync(guildId, m.Groups[1].Value, slot + "-") is not { } file) return (null, null);
        return ($"attachment://{file.FileName}", file);
    }

    /// <summary>Свежая ссылка на картинку медиатеки — для превью на сайте.</summary>
    public async Task<string?> PreviewUrlAsync(ulong guildId, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = TokenRe.Match(value.Trim());
        if (!m.Success) return CommunityService.IsUrl(value) ? value.Trim() : null;
        return (await FindAsync(guildId, m.Groups[1].Value))?.Url;
    }

    // ---------- Мелочи ----------

    public static string Slug(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var slug = Regex.Replace(s.Trim().ToLowerInvariant(), @"[^a-z0-9а-яёіїєґ_\-]+", "-");
        slug = Regex.Replace(Transliterate(slug), @"[^a-z0-9_\-]+", "-").Trim('-');
        slug = Regex.Replace(slug, "-{2,}", "-");
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }

    private static string Transliterate(string s)
    {
        const string from = "абвгдеёжзийклмнопрстуфхцчшщъыьэюяіїєґ";
        string[] to = ["a","b","v","g","d","e","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","h","ts","ch","sh","sch","","y","","e","yu","ya","i","yi","ye","g"];
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            var i = from.IndexOf(ch);
            sb.Append(i >= 0 ? to[i] : ch.ToString());
        }
        return sb.ToString();
    }

    private static string? Sniff(byte[] d) =>
        d.Length < 12 ? null
        : d[0] == 0x89 && d[1] == 0x50 ? ".png"
        : d[0] == 0xFF && d[1] == 0xD8 ? ".jpg"
        : d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F' ? ".gif"
        : d[0] == (byte)'R' && d[1] == (byte)'I' && d[8] == (byte)'W' && d[9] == (byte)'E' ? ".webp"
        : null;

    /// <summary>Убрать пустые строки, оставшиеся от меток.</summary>
    private static string Cleanup(string text) =>
        Regex.Replace(Regex.Replace(text, @"[ \t]+(\r?\n)", "$1").Replace("\r\n", "\n"), @"\n{3,}", "\n\n").Trim('\n', ' ');
}
