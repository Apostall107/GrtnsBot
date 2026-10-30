using System.Collections.Concurrent;
using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Черновики — только в памяти. Опубликованные сборы сохраняются в data/lfg-groups.json
/// после каждого изменения и загружаются при старте, поэтому переживают перезапуск бота.
/// </summary>
public sealed class LfgStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _saveLock = new();
    private readonly string _path;
    private readonly ILogger<LfgStore> _logger;

    public ConcurrentDictionary<string, LfgDraft> Drafts { get; } = new();
    public ConcurrentDictionary<string, LfgGroup> Groups { get; } = new();

    private readonly LfgHistoryStore _history;

    public LfgStore(IConfiguration config, ILogger<LfgStore> logger, LfgHistoryStore history)
    {
        _logger = logger;
        _history = history;
        _path = config["Storage:LfgGroupsPath"] ?? "data/lfg-groups.json";
        Load();
    }

    /// <summary>Сбор закрыт (организатором, админом, по таймеру) — в историю для явки и статистики.</summary>
    public void RecordClosed(LfgGroup group, string how) => _history.Record(group, how);

    public void PruneDrafts(TimeSpan ttl)
    {
        var border = DateTimeOffset.UtcNow - ttl;
        foreach (var (id, draft) in Drafts)
            if (draft.CreatedAt < border)
                Drafts.TryRemove(id, out _);
    }

    /// <summary>Записывает все активные сборы в файл. Вызывать после каждого изменения сбора.</summary>
    public void SaveGroups()
    {
        lock (_saveLock)
        {
            try
            {
                var snapshot = Groups.Values.Where(g => !g.IsClosed).ToList();
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);

                var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Сначала во временный файл, потом замена — файл не побьётся при сбое посреди записи
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save LFG groups");
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var groups = JsonSerializer.Deserialize<List<LfgGroup>>(File.ReadAllText(_path)) ?? [];
            foreach (var group in groups.Where(g => !g.IsClosed))
                Groups[group.Id] = group;
            _logger.LogInformation("Loaded {Count} LFG groups from {Path}", Groups.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load LFG groups from {Path}", _path);
        }
    }
}
