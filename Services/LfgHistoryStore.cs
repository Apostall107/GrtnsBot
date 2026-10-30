using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

public sealed class LfgHistoryMember
{
    public ulong UserId { get; set; }
    public string? Character { get; set; }
    public string Role { get; set; } = "";
    /// <summary>Явка: true — пришёл, false — не пришёл, null — организатор не отметил.</summary>
    public bool? Attended { get; set; }
}

/// <summary>Завершённый сбор (закрыт или истёк) — для статистики.</summary>
public sealed class LfgHistoryEntry
{
    public string GroupId { get; set; } = "";
    public ulong GuildId { get; set; }
    public string Activity { get; set; } = "";
    public ulong OwnerId { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset ClosedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>closed / expired / force-closed</summary>
    public string How { get; set; } = "closed";
    public int Size { get; set; }
    public List<LfgHistoryMember> Members { get; set; } = [];
    public int Reserve { get; set; }
}

/// <summary>История сборов (data/lfg-history.json, последние 10000) и статистика по ней.</summary>
public sealed class LfgHistoryStore
{
    public const int MaxEntries = 10000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<LfgHistoryEntry> _data;

    public LfgHistoryStore(IConfiguration config)
    {
        _path = config["Storage:LfgHistoryPath"] ?? "data/lfg-history.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<LfgHistoryEntry>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    /// <summary>Записать сбор при закрытии. Повторно один и тот же сбор не пишется.</summary>
    public void Record(LfgGroup g, string how)
    {
        lock (_lock)
        {
            if (_data.Any(e => e.GroupId == g.Id)) return;
            _data.Add(new LfgHistoryEntry
            {
                GroupId = g.Id,
                GuildId = g.GuildId,
                Activity = g.ActivityName,
                OwnerId = g.OwnerId,
                StartsAt = g.StartsAt,
                How = how,
                Size = g.Slots.Count,
                Reserve = g.Reserve.Count,
                Members = g.Slots.Where(s => s.UserId is not null).Select(s => new LfgHistoryMember
                {
                    UserId = s.UserId!.Value,
                    Character = s.CharacterName,
                    Role = (s.TakenAs ?? s.Role).Label(),
                    Attended = s.Attended
                }).ToList()
            });
            if (_data.Count > MaxEntries) _data.RemoveRange(0, _data.Count - MaxEntries);
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
    }

    public IReadOnlyList<LfgHistoryEntry> Get(ulong guildId, DateTimeOffset? since)
    {
        lock (_lock)
            return _data.Where(e => e.GuildId == guildId && (since is null || e.ClosedAt >= since)).ToList();
    }
}

/// <summary>Сводка по игроку из истории.</summary>
public sealed record PlayerStats(ulong UserId, int Groups, int Organized, int Attended, int NoShows, int Unmarked)
{
    /// <summary>Доля явок среди отмеченных (null — нет отметок).</summary>
    public double? Reliability => Attended + NoShows == 0 ? null : Math.Round(100.0 * Attended / (Attended + NoShows));
}

public static class LfgStats
{
    public static List<PlayerStats> Players(IEnumerable<LfgHistoryEntry> history)
    {
        var map = new Dictionary<ulong, (int groups, int organized, int came, int noshow, int unmarked)>();
        foreach (var e in history)
        {
            var o = map.GetValueOrDefault(e.OwnerId);
            map[e.OwnerId] = o with { organized = o.organized + 1 };
            foreach (var m in e.Members)
            {
                var x = map.GetValueOrDefault(m.UserId);
                map[m.UserId] = x with
                {
                    groups = x.groups + 1,
                    came = x.came + (m.Attended == true ? 1 : 0),
                    noshow = x.noshow + (m.Attended == false ? 1 : 0),
                    unmarked = x.unmarked + (m.Attended is null ? 1 : 0)
                };
            }
        }
        return map.Select(kv => new PlayerStats(kv.Key, kv.Value.groups, kv.Value.organized, kv.Value.came, kv.Value.noshow, kv.Value.unmarked)).ToList();
    }
}
