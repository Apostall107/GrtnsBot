using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Какие ЛС бот может слать на сервере. По умолчанию — никакие: каждое включается в админке (Settings).</summary>
public sealed class DmPrefs
{
    /// <summary>Рассылка о новом сборе людям с ролью активности.</summary>
    public bool LfgNewGroup { get; set; }
    /// <summary>«Тебя перевели из резерва в группу».</summary>
    public bool LfgPromoted { get; set; }
    /// <summary>«Время сбора изменилось».</summary>
    public bool LfgTimeChanged { get; set; }
    /// <summary>Итог тикета: принят / отклонён.</summary>
    public bool TicketResult { get; set; }
    /// <summary>Напоминание обновить GS (если канал не задан или вместе с ним).</summary>
    public bool GsReminder { get; set; }
}

/// <summary>Настройки сервера для LFG и гильдии.</summary>
public sealed class GuildPrefs
{
    public DmPrefs Dm { get; set; } = new();

    /// <summary>Кнопка «✅ Готов» в напоминании перед стартом; кто нажал — отмечен в посте.</summary>
    public bool ReadyCheck { get; set; } = true;

    /// <summary>За сколько минут до старта бот сам переводит первого подходящего из резерва в освободившийся слот (0 — выкл.).</summary>
    public int AutoPromoteMinutes { get; set; } = 60;

    /// <summary>Напоминание обновить GS: включено, канал (null — только ЛС), раз в сколько дней GS считается устаревшим.</summary>
    public bool GsReminder { get; set; }
    public ulong? GsReminderChannelId { get; set; }
    public int GsReminderDays { get; set; } = 7;
    /// <summary>Когда напоминание отправлялось последний раз.</summary>
    public DateTimeOffset? GsReminderLastRun { get; set; }
}

/// <summary>data/guild-prefs.json — настройки по серверам (ЛС, готовность, автозамена, напоминание о GS).</summary>
public sealed class GuildPrefsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, GuildPrefs> _data;

    public GuildPrefsStore(IConfiguration config)
    {
        _path = config["Storage:GuildPrefsPath"] ?? "data/guild-prefs.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<ulong, GuildPrefs>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    /// <summary>Копия настроек сервера (нет записи — значения по умолчанию).</summary>
    public GuildPrefs Get(ulong guildId)
    {
        lock (_lock)
        {
            var p = _data.TryGetValue(guildId, out var x) ? x : new GuildPrefs();
            return JsonSerializer.Deserialize<GuildPrefs>(JsonSerializer.Serialize(p))!;
        }
    }

    public IReadOnlyList<ulong> Guilds()
    {
        lock (_lock) return _data.Keys.ToList();
    }

    public void Update(ulong guildId, Action<GuildPrefs> change)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var p)) _data[guildId] = p = new GuildPrefs();
            change(p);
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
    }

    public DmPrefs Dm(ulong guildId) => Get(guildId).Dm;
}
