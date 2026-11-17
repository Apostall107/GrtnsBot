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
    // Компактный JSON: файл пишется при каждой записи в сбор — меньше текста, быстрее запись
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
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

    // ---------- History of the roster: every save compares the roster with the previous one ----------
    // (works for every way a roster changes — Discord, site, auto-replace, no-show — without touching each of them)
    private readonly Dictionary<string, Dictionary<ulong, (bool InSlot, int Slot, SlotRole Role, string? Character)>> _seen = new();
    private const int MaxHistory = 150;

    private static Dictionary<ulong, (bool InSlot, int Slot, SlotRole Role, string? Character)> Roster(LfgGroup g)
    {
        var map = new Dictionary<ulong, (bool, int, SlotRole, string?)>();
        for (var i = 0; i < g.Slots.Count; i++)
            if (g.Slots[i].UserId is { } id) map[id] = (true, i, g.Slots[i].TakenAs ?? g.Slots[i].Role, g.Slots[i].CharacterName);
        foreach (var r in g.Reserve.ToList()) map.TryAdd(r.UserId, (false, -1, r.Role, r.CharacterName));
        return map;
    }

    private static void Log(LfgGroup g, ulong userId, string text)
    {
        g.History.Add(new GroupLogEntry { UserId = userId, Text = text });
        if (g.History.Count > MaxHistory) g.History.RemoveRange(0, g.History.Count - MaxHistory);
    }

    private void TrackRosters()
    {
        foreach (var g in Groups.Values)
        {
            try
            {
                var now = Roster(g);
                if (!_seen.TryGetValue(g.Id, out var before))
                {
                    _seen[g.Id] = now;   // first time seen (new group or after a restart): the starting point
                    if (g.History.Count == 0)
                    {
                        if (g.OwnerId != 0) Log(g, g.OwnerId, "created the group");
                        foreach (var (id, a) in now.Where(x => x.Key != g.OwnerId))
                            Log(g, id, a.InSlot ? $"in slot {a.Slot + 1} ({a.Role.Label()})" : $"in the reserve ({a.Role.Label()})");
                    }
                    continue;
                }
                foreach (var (id, a) in now)
                {
                    if (!before.TryGetValue(id, out var b))
                        Log(g, id, a.InSlot ? $"joined · slot {a.Slot + 1} ({a.Role.Label()}{(a.Character is { } c ? $", {c}" : "")})"
                            : $"joined the reserve ({a.Role.Label()}{(a.Character is { } c2 ? $", {c2}" : "")})");
                    else if (b.InSlot != a.InSlot)
                        Log(g, id, a.InSlot ? $"moved from the reserve into slot {a.Slot + 1}"
                            : g.NoShows.Any(n => n.UserId == id) ? "marked no-show → moved to the reserve" : "moved to the reserve");
                    else if (a.InSlot && a.Slot != b.Slot) Log(g, id, $"moved from slot {b.Slot + 1} to slot {a.Slot + 1}");
                    else if (a.Role != b.Role) Log(g, id, $"changed role {b.Role.Label()} → {a.Role.Label()}");
                    else if (a.Character != b.Character && a.Character is not null) Log(g, id, $"changed character → {a.Character}");
                }
                foreach (var (id, b) in before)
                    if (!now.ContainsKey(id)) Log(g, id, b.InSlot ? $"left the group (was in slot {b.Slot + 1})" : "left the reserve");
                _seen[g.Id] = now;
            }
            catch (InvalidOperationException) { /* changed while reading — the next save compares again */ }
        }
        foreach (var gone in _seen.Keys.Where(k => !Groups.ContainsKey(k)).ToList()) _seen.Remove(gone);
    }

    /// <summary>Записывает все активные сборы в файл. Вызывать после каждого изменения сбора.</summary>
    public void SaveGroups()
    {
        lock (_saveLock)
        {
            try
            {
                TrackRosters();
                var snapshot = Groups.Values.Where(g => !g.IsClosed).ToList();
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);

                // На диск — в фоне, атомарно (временный файл → замена)
                DataFiles.Save(_path, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save LFG groups");
            }
        }
    }

    private void Load()
    {
        try
        {
            var groups = DataFiles.Load<List<LfgGroup>>(_path) ?? [];
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
