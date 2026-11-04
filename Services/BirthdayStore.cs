using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>День рождения участника: день и месяц (обязательно), год — необязательно (для возраста).</summary>
public sealed class Birthday
{
    public int Day { get; set; }
    public int Month { get; set; }
    public int? Year { get; set; }

    public bool IsValid => Month is >= 1 and <= 12 && Day >= 1 && Day <= DaysInMonth(Month) && (Year is null or (>= 1900 and <= 2100));

    /// <summary>Максимум дней в месяце (февраль — 29, чтобы принимать 29.02).</summary>
    public static int DaysInMonth(int month) => month switch
    {
        2 => 29,
        4 or 6 or 9 or 11 => 30,
        _ => 31
    };

    /// <summary>Сегодня ли день рождения в указанной локальной дате (29.02 в невисокосный год — 28.02).</summary>
    public bool IsOn(DateTime localDate)
    {
        var (m, d) = (Month, Day);
        if (m == 2 && d == 29 && !DateTime.IsLeapYear(localDate.Year)) d = 28;
        return localDate.Month == m && localDate.Day == d;
    }
}

/// <summary>Настройки и список дней рождения одного сервера.</summary>
public sealed class GuildBirthdays
{
    /// <summary>Включена ли автоматическая выдача роли в день рождения.</summary>
    public bool Enabled { get; set; }
    /// <summary>Роль «Участник»: только её обладатели получают роль дня рождения (null — все с указанной датой).</summary>
    public ulong? MemberRoleId { get; set; }
    /// <summary>Роль, которая выдаётся на день рождения и снимается на следующий день.</summary>
    public ulong? BirthdayRoleId { get; set; }
    /// <summary>Необязательно: канал для поздравления (null — не поздравлять).</summary>
    public ulong? AnnounceChannelId { get; set; }
    /// <summary>Дни рождения: Discord ID → дата.</summary>
    public Dictionary<ulong, Birthday> People { get; set; } = new();
}

/// <summary>data/birthdays.json — дни рождения участников и настройки модуля по серверам.</summary>
public sealed class BirthdayStore
{
    public const int MaxPeople = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, GuildBirthdays> _data;

    public BirthdayStore(IConfiguration config)
    {
        _path = config["Storage:BirthdaysPath"] ?? "data/birthdays.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<ulong, GuildBirthdays>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public GuildBirthdays Get(ulong guildId)
    {
        lock (_lock)
        {
            var g = _data.TryGetValue(guildId, out var x) ? x : new GuildBirthdays();
            return JsonSerializer.Deserialize<GuildBirthdays>(JsonSerializer.Serialize(g))!;
        }
    }

    public IReadOnlyList<ulong> Guilds()
    {
        lock (_lock) return _data.Keys.ToList();
    }

    /// <summary>Дата рождения участника (null — не задана).</summary>
    public Birthday? Of(ulong guildId, ulong userId)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var g) && g.People.TryGetValue(userId, out var b)
                ? new Birthday { Day = b.Day, Month = b.Month, Year = b.Year } : null;
    }

    /// <summary>Задать/обновить дату. false — список переполнен.</summary>
    public bool Set(ulong guildId, ulong userId, Birthday birthday)
    {
        lock (_lock)
        {
            var g = Mutable(guildId);
            if (!g.People.ContainsKey(userId) && g.People.Count >= MaxPeople) return false;
            g.People[userId] = birthday;
            Save();
            return true;
        }
    }

    /// <summary>Убрать дату участника. true — была и удалена.</summary>
    public bool Remove(ulong guildId, ulong userId)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var g) || !g.People.Remove(userId)) return false;
            Save();
            return true;
        }
    }

    public void UpdateSettings(ulong guildId, Action<GuildBirthdays> change)
    {
        lock (_lock)
        {
            change(Mutable(guildId));
            Save();
        }
    }

    private GuildBirthdays Mutable(ulong guildId)
    {
        if (!_data.TryGetValue(guildId, out var g)) _data[guildId] = g = new GuildBirthdays();
        return g;
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
