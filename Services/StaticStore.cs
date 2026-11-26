using System.Collections.Concurrent;
using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Статики (КП) — data/statics.json. Сами не удаляются: только организатор или админ.
/// Черновики создания — только в памяти.
/// </summary>
public sealed class StaticStore
{
    /// <summary>Сколько статиков один человек может держать организатором на сервере.</summary>
    public const int MaxOwnedPerUser = 5;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _saveLock = new();
    private readonly string _path;
    private readonly ILogger<StaticStore> _logger;

    public ConcurrentDictionary<string, StaticGroup> Statics { get; } = new();
    public ConcurrentDictionary<string, StaticDraft> Drafts { get; } = new();

    public StaticStore(IConfiguration config, ILogger<StaticStore> logger)
    {
        _logger = logger;
        _path = config["Storage:StaticsPath"] ?? "data/statics.json";
        try
        {
            foreach (var s in DataFiles.Load<List<StaticGroup>>(_path) ?? [])
                Statics[s.Id] = s;
            _logger.LogInformation("Loaded {Count} statics from {Path}", Statics.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load statics from {Path}", _path);
        }
    }

    public IEnumerable<StaticGroup> ForGuild(ulong guildId) =>
        Statics.Values.Where(s => s.GuildId == guildId).OrderByDescending(s => s.CreatedAt);

    public StaticGroup? Get(ulong guildId, string id) =>
        Statics.TryGetValue(id, out var s) && s.GuildId == guildId ? s : null;

    public int OwnedBy(ulong guildId, ulong userId) => Statics.Values.Count(s => s.GuildId == guildId && s.OwnerId == userId);

    public void PruneDrafts()
    {
        var border = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
        foreach (var (id, d) in Drafts)
            if (d.CreatedAt < border) Drafts.TryRemove(id, out _);
    }

    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                DataFiles.Save(_path, JsonSerializer.Serialize(Statics.Values.ToList(), JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save statics");
            }
        }
    }
}
