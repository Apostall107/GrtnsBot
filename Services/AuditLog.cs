using System.Text.Json;
using Discord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

public sealed class AuditEntry
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public ulong GuildId { get; set; }
    /// <summary>Кто сделал. 0 — сам бот (автоудаление, повторяющийся сбор).</summary>
    public ulong ActorId { get; set; }
    public string ActorName { get; set; } = "";
    /// <summary>web / discord / system</summary>
    public string Source { get; set; } = "discord";
    /// <summary>Модуль: lfg, tickets, voice, texts, posts, access, settings.</summary>
    public string Module { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Details { get; set; }
}

/// <summary>
/// Журнал действий (data/audit.json): что делали в веб-админке и в боте. Хранятся последние <see cref="MaxEntries"/> записей.
/// Запись — в фоне (не тормозит ответы), раз в несколько секунд.
/// </summary>
public sealed class AuditLog : IDisposable
{
    public const int MaxEntries = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<AuditEntry> _entries;
    private readonly Timer _flush;
    private readonly ILogger<AuditLog> _logger;
    private bool _dirty;

    public AuditLog(IConfiguration config, ILogger<AuditLog> logger)
    {
        _logger = logger;
        _path = config["Storage:AuditPath"] ?? "data/audit.json";
        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<AuditEntry>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audit log {Path} is damaged, starting a new one", _path);
            _entries = [];
        }
        _flush = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Add(ulong guildId, ulong actorId, string actorName, string source, string module, string action, string? details = null)
    {
        lock (_lock)
        {
            _entries.Add(new AuditEntry
            {
                GuildId = guildId,
                ActorId = actorId,
                ActorName = actorName,
                Source = source,
                Module = module,
                Action = action,
                Details = details is { Length: > 500 } ? details[..500] + "…" : details
            });
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            _dirty = true;
        }
    }

    /// <summary>Действие участника в Discord.</summary>
    public void FromDiscord(ulong guildId, IUser user, string module, string action, string? details = null) =>
        Add(guildId, user.Id, (user as IGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username, "discord", module, action, details);

    /// <summary>Действие самого бота (по таймеру).</summary>
    public void FromSystem(ulong guildId, string module, string action, string? details = null) =>
        Add(guildId, 0, "Bot", "system", module, action, details);

    /// <summary>Новые сверху, с фильтрами.</summary>
    public IReadOnlyList<AuditEntry> Query(ulong guildId, ulong? actorId, string? module, string? search, int skip, int take)
    {
        lock (_lock)
        {
            IEnumerable<AuditEntry> q = _entries.Where(e => e.GuildId == guildId || e.GuildId == 0);
            if (actorId is { } a) q = q.Where(e => e.ActorId == a);
            if (!string.IsNullOrWhiteSpace(module)) q = q.Where(e => e.Module == module);
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(e => e.Action.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                 (e.Details?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 e.ActorName.Contains(search, StringComparison.OrdinalIgnoreCase));
            return q.Reverse().Skip(skip).Take(take).ToList();
        }
    }

    /// <summary>Кто встречается в журнале — для фильтра.</summary>
    public IReadOnlyList<(ulong Id, string Name, int Count)> Actors(ulong guildId)
    {
        lock (_lock)
            return _entries.Where(e => e.GuildId == guildId || e.GuildId == 0)
                .GroupBy(e => e.ActorId)
                .Select(g => (g.Key, g.Last().ActorName, g.Count()))
                .OrderByDescending(x => x.Item3)
                .ToList();
    }

    private void Flush()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_entries, JsonOptions);
            _dirty = false;
        }

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save the audit log");
        }
    }

    public void Dispose()
    {
        _flush.Dispose();
        Flush();
    }
}
