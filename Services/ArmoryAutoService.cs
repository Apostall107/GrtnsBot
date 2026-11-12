using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Automatic link &amp; update of characters from the official armory (LFG → Settings → Official armory): at the set time
/// (bot's time zone, default 05:00) and then every N hours (default 24). The same as the "Link &amp; update all" button:
/// linked characters are refreshed, the others are searched by exact name and linked when exactly one is found.
/// One server at a time, one character every 3 s; if the armory refuses, it waits and tries again later.
/// </summary>
public sealed class ArmoryAutoService(DiscordSocketClient client, GuildPrefsStore prefs, ArmorySyncService sync, TimeService time,
    AuditLog audit, ILogger<ArmoryAutoService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try { Tick(); }
                catch (Exception ex) { logger.LogWarning(ex, "Armory auto update check failed"); }
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    private void Tick()
    {
        if (sync.Paused) return;
        // one server at a time — the armory shouldn't get several runs at once
        if (client.Guilds.Any(g => sync.BulkStatus(g.Id) is { Running: true })) return;
        foreach (var guild in client.Guilds.Where(GuildCache.Loaded))
        {
            var p = prefs.Get(guild.Id);
            if (!p.ArmoryAuto) continue;
            var latest = LatestSlot(p.ArmoryAutoTime, p.ArmoryAutoHours);
            if (p.ArmoryAutoLastRun is null)
            {
                // just turned on / first start: wait for the next scheduled time instead of running right away
                prefs.Update(guild.Id, x => x.ArmoryAutoLastRun = DateTimeOffset.UtcNow);
                continue;
            }
            if (p.ArmoryAutoLastRun >= latest) continue;
            if (!sync.StartBulk(guild.Id, null, linkNew: true)) continue;
            prefs.Update(guild.Id, x => x.ArmoryAutoLastRun = DateTimeOffset.UtcNow);
            var total = sync.BulkStatus(guild.Id)?.Total ?? 0;
            if (total == 0) continue;   // armory off for every project / no characters
            logger.LogInformation("Armory auto update started on {Guild}: {Count} character(s)", guild.Name, total);
            audit.FromSystem(guild.Id, "lfg", "Automatic armory link & update started", $"{total} character(s)");
            return;   // next server on a later tick
        }
    }

    /// <summary>The next scheduled moment (UTC) after now.</summary>
    public DateTimeOffset NextSlot(string? at, int hours)
    {
        var latest = LatestSlot(at, hours);
        var now = DateTimeOffset.UtcNow;
        if (latest > now) return latest;
        if (Math.Clamp(hours, 1, 24 * 14) == 24) return LatestSlotAt(at, now.AddDays(1).AddMinutes(-1)) is var n && n > now ? n : latest.AddDays(1);
        return latest + TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 14));
    }

    private DateTimeOffset LatestSlotAt(string? at, DateTimeOffset moment)
    {
        var t = TimeOnly.TryParse(at, out var parsed) ? parsed : new TimeOnly(5, 0);
        var local = TimeZoneInfo.ConvertTime(moment, time.Zone);
        var dayLocal = new DateTime(local.Year, local.Month, local.Day, t.Hour, t.Minute, 0, DateTimeKind.Unspecified);
        var day = new DateTimeOffset(dayLocal, time.Zone.GetUtcOffset(dayLocal));
        return day <= moment ? day : day.AddDays(-1);
    }

    /// <summary>The last scheduled moment (UTC) not later than now: the start time in the bot's time zone + every N hours.</summary>
    public DateTimeOffset LatestSlot(string? at, int hours)
    {
        var period = TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 14));
        var t = TimeOnly.TryParse(at, out var parsed) ? parsed : new TimeOnly(5, 0);
        // a fixed starting day long ago at that local time; slots repeat every period from it
        var anchorLocal = new DateTime(2026, 1, 5, t.Hour, t.Minute, 0, DateTimeKind.Unspecified);
        var anchor = new DateTimeOffset(anchorLocal, time.Zone.GetUtcOffset(anchorLocal));
        var now = DateTimeOffset.UtcNow;
        if (now < anchor) return anchor;
        // daily schedules follow the local clock (summer/winter time): today's (or yesterday's) start time
        if (period.TotalHours % 24 == 0 && period.TotalHours == 24)
        {
            var local = TimeZoneInfo.ConvertTime(now, time.Zone);
            var todayLocal = new DateTime(local.Year, local.Month, local.Day, t.Hour, t.Minute, 0, DateTimeKind.Unspecified);
            var today = new DateTimeOffset(todayLocal, time.Zone.GetUtcOffset(todayLocal));
            return today <= now ? today : today.AddDays(-1);
        }
        var n = Math.Floor((now - anchor) / period);
        return anchor + period * n;
    }
}
