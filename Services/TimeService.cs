using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Работа со временем событий в часовом поясе сервера (Lfg:TimeZone).</summary>
public sealed class TimeService
{
    private static readonly string[] TimeOnlyFormats = ["H:mm", "H.mm", "HHmm"];
    private static readonly string[] DateTimeFormats = ["d.M H:mm", "d.M H.mm", "d.M.yyyy H:mm", "d.M.yyyy H.mm"];

    public TimeZoneInfo Zone { get; }
    public string ZoneName { get; }

    public TimeService(IConfiguration config, ILogger<TimeService> logger)
    {
        var id = config["Lfg:TimeZone"] ?? "Europe/Kyiv";
        Zone = Resolve(id);
        ZoneName = id;
        AutoDeleteHours = config.GetValue("Lfg:AutoDeleteHours", 3);
        if (Zone == TimeZoneInfo.Utc && id != "UTC")
        {
            logger.LogWarning("Time zone {Zone} not found, using UTC", id);
            ZoneName = "UTC";
        }
    }

    // Старые версии ICU/Windows знают Киев только как Europe/Kiev или FLE Standard Time
    private static TimeZoneInfo Resolve(string id)
    {
        string[] candidates = id is "Europe/Kyiv" or "Europe/Kiev"
            ? ["Europe/Kyiv", "Europe/Kiev", "FLE Standard Time"]
            : [id];

        foreach (var candidate in candidates)
            if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var zone))
                return zone;

        return TimeZoneInfo.Utc;
    }

    public DateTime LocalNow => TimeZoneInfo.ConvertTime(DateTime.UtcNow, Zone);
    public DateOnly Today => DateOnly.FromDateTime(LocalNow);

    /// <summary>Ближайший следующий час — значение по умолчанию.</summary>
    public int DefaultHour() => (LocalNow.Hour + 1) % 24;

    /// <summary>Через сколько часов после начала (или создания, если времени нет) удалять сбор.</summary>
    public int AutoDeleteHours { get; private init; } = 3;

    public DateTimeOffset ExpiresAt(DateTimeOffset? startsAt) =>
        (startsAt ?? DateTimeOffset.UtcNow).AddHours(AutoDeleteHours);

    public DateTimeOffset ToUtc(DateOnly date, int hour, int minute)
    {
        var local = date.ToDateTime(new TimeOnly(hour, minute)); // Kind = Unspecified
        if (Zone.IsInvalidTime(local)) local = local.AddHours(1); // переход на летнее время
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone), TimeSpan.Zero);
    }

    /// <summary>Разбирает «21:30», «2130», «15.10 21:30», «15.10.2026 21:30».</summary>
    public bool TryParse(string input, out DateOnly date, out int hour, out int minute)
    {
        input = input.Trim();
        date = default;
        hour = minute = 0;

        if (DateTime.TryParseExact(input, TimeOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            date = Today;
            (hour, minute) = (t.Hour, t.Minute);
            return true;
        }

        if (DateTime.TryParseExact(input, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            var hasYear = input.Count(c => c == '.') >= 2;
            if (!hasYear)
            {
                date = new DateOnly(Today.Year, date.Month, date.Day);
                if (date < Today.AddDays(-1)) date = date.AddYears(1); // «05.01» в декабре — следующий год
            }
            (hour, minute) = (dt.Hour, dt.Minute);
            return true;
        }

        return false;
    }
}
