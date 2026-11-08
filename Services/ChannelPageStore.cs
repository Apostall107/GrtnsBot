using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Блок страницы канала: текст, ссылка на панель ролей или панель модуля (LFG/Tickets/Rooms).</summary>
public sealed class PageBlock
{
    public string Id { get; set; } = IdGen.New();
    /// <summary>text | rolepanel | modulepanel</summary>
    public string Type { get; set; } = "text";
    public string? Text { get; set; }          // type=text
    public string? RolePanelId { get; set; }   // type=rolepanel
    public string? ModuleKey { get; set; }     // type=modulepanel: lfg|tickets|rooms
    /// <summary>ID сообщений этого блока в канале (с последней публикации).</summary>
    public List<ulong> MessageIds { get; set; } = [];
    /// <summary>Что было опубликовано: текст (text), id панели (rolepanel) или ключ модуля (modulepanel). Для обновления на месте.</summary>
    public string? PostedKey { get; set; }
}

/// <summary>Страница канала: упорядоченный набор блоков, публикуемый в один канал как единое целое.</summary>
public sealed class ChannelPage
{
    public const int MaxPerGuild = 50;
    public const int MaxBlocks = 40;

    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public string Name { get; set; } = "Channel page";
    /// <summary>Проект бота (LfgProjectStore) — для группировки страниц. null — общие.</summary>
    public string? ProjectKey { get; set; }
    /// <summary>Куда опубликована (null — не опубликована).</summary>
    public ulong? ChannelId { get; set; }
    public List<PageBlock> Blocks { get; set; } = [];
    /// <summary>Все ID сообщений с последней публикации — для удаления при переиздании (устойчиво к правке блоков).</summary>
    public List<ulong> PostedMessageIds { get; set; } = [];
}

/// <summary>data/channel-pages.json — страницы каналов (сборка поста из нескольких сообщений и панелей).</summary>
public sealed class ChannelPageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<ChannelPage> _data;

    public ChannelPageStore(IConfiguration config)
    {
        _path = config["Storage:ChannelPagesPath"] ?? "data/channel-pages.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<ChannelPage>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public IReadOnlyList<ChannelPage> Get(ulong guildId)
    {
        lock (_lock) return _data.Where(p => p.GuildId == guildId).Select(Clone).ToList();
    }

    public ChannelPage? Find(string id)
    {
        lock (_lock) return _data.Find(p => p.Id == id) is { } p ? Clone(p) : null;
    }

    /// <summary>Создать или обновить страницу. Возвращает текст ошибки или null.</summary>
    public string? Save(ChannelPage page)
    {
        lock (_lock)
        {
            var i = _data.FindIndex(p => p.Id == page.Id);
            if (i < 0 && _data.Count(p => p.GuildId == page.GuildId) >= ChannelPage.MaxPerGuild)
                return $"Limit of {ChannelPage.MaxPerGuild} pages reached.";
            if (page.Blocks.Count > ChannelPage.MaxBlocks)
                return $"A page can have at most {ChannelPage.MaxBlocks} blocks.";
            if (i < 0) _data.Add(page); else _data[i] = page;
            Save();
            return null;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _data.RemoveAll(p => p.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    /// <summary>Обновить страницу под блокировкой (например, после публикации записать ID сообщений).</summary>
    public ChannelPage? Update(string id, Action<ChannelPage> change)
    {
        lock (_lock)
        {
            if (_data.Find(p => p.Id == id) is not { } page) return null;
            change(page);
            Save();
            return Clone(page);
        }
    }

    private static ChannelPage Clone(ChannelPage p) => JsonSerializer.Deserialize<ChannelPage>(JsonSerializer.Serialize(p))!;

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
