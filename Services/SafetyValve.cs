using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Fuse for everything the bot does on its own (no person clicked anything): forgetting panels, closing groups and tickets,
/// removing roles, deleting rooms. Normally these happen a few at a time; a burst means something is wrong (a Discord
/// outage, the cache not loaded, a wrong setting) — then the bot stops doing it, keeps the data and writes
/// "Safety stop" to the server's Log (Dashboard → Log), so an admin can look and do it by hand if it was right.
/// Costs nothing: a few timestamps in memory.
/// </summary>
public static class SafetyValve
{
    private static AuditLog? _audit;
    private static ILogger? _logger;
    private static readonly ConcurrentDictionary<string, Queue<DateTime>> Hits = new();
    private static readonly ConcurrentDictionary<string, DateTime> Reported = new();

    public static void Init(AuditLog audit, ILogger logger) { _audit = audit; _logger = logger; }

    /// <summary>One more automatic action of this kind — allowed while there were fewer than <paramref name="limit"/> in the window.</summary>
    public static bool Allow(ulong guildId, string kind, int limit, TimeSpan window, string what)
    {
        var key = $"{guildId}:{kind}";
        var q = Hits.GetOrAdd(key, _ => new Queue<DateTime>());
        lock (q)
        {
            var now = DateTime.UtcNow;
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
            if (q.Count < limit) { q.Enqueue(now); return true; }
        }
        Report(guildId, key, window, $"Safety stop: {what}",
            $"More than {limit} in {Describe(window)} — the bot stopped doing it on its own and kept the data. If it's right, do it by hand.");
        return false;
    }

    /// <summary>A batch decided at once (e.g. roles to remove): allowed only up to <paramref name="limit"/> items.</summary>
    public static bool AllowBatch(ulong guildId, string kind, int count, int limit, string what)
    {
        if (count <= limit) return true;
        Report(guildId, $"{guildId}:{kind}", TimeSpan.FromHours(6), $"Safety stop: {what}",
            $"{count} at once (the limit is {limit}) — nothing was done. If it's right, do it by hand.");
        return false;
    }

    private static void Report(ulong guildId, string key, TimeSpan window, string action, string details)
    {
        var now = DateTime.UtcNow;
        // once per window, so the Log isn't flooded
        if (Reported.TryGetValue(key, out var last) && now - last < window) return;
        Reported[key] = now;
        _logger?.LogWarning("{Action} on {Guild}: {Details}", action, guildId, details);
        _audit?.FromSystem(guildId, "safety", action, details);
    }

    private static string Describe(TimeSpan t) => t.TotalHours >= 1 ? $"{t.TotalHours:0} h" : $"{t.TotalMinutes:0} min";
}
