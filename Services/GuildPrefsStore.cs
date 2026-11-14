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

    /// <summary>Участникам на сайте: топ активных игроков за 30 дней (без no-show).</summary>
    public bool MemberTop { get; set; } = true;

    /// <summary>GS привязанных к официальной оружейной персонажей: itemLevel (по умолчанию), combatPower или none (вручную).</summary>
    public string ArmoryGs { get; set; } = "itemLevel";

    /// <summary>Официальная оружейная AION 2 по проектам (ключ проекта): включена, фракция и сервер по умолчанию.</summary>
    public Dictionary<string, ArmoryProjectPrefs>? Armory { get; set; }
    /// <summary>Автоматически привязывать и обновлять персонажей (как «Link &amp; update all»): с какого времени (часовой пояс бота) и раз в сколько часов.</summary>
    public bool ArmoryAuto { get; set; } = true;
    public string ArmoryAutoTime { get; set; } = "05:00";
    public int ArmoryAutoHours { get; set; } = 24;
    public DateTimeOffset? ArmoryAutoLastRun { get; set; }

    /// <summary>Служебный канал медиатеки: картинки и гифки для постов лежат там вложениями (на сервере бота — ничего).</summary>
    public ulong? MediaChannelId { get; set; }
    /// <summary>Быстрый поиск на сайте (Ctrl+K) ищет людей только с этими ролями; пусто — всех участников.</summary>
    public List<ulong> SearchRoleIds { get; set; } = [];
    /// <summary>Пасхалка-маскот на сайте: в среднем раз в столько минут (0 — выключен).</summary>
    public int MascotMinutes { get; set; } = 15;
    /// <summary>То же в секундах (задаётся на сайте); null — берётся MascotMinutes × 60.</summary>
    public int? MascotSeconds { get; set; }
    public int MascotEverySeconds => MascotSeconds ?? MascotMinutes * 60;
}

/// <summary>Проект на официальной оружейной: включён ли, фракция (1 — Elyos, 2 — Asmodians) и сервер по умолчанию.</summary>
public sealed class ArmoryProjectPrefs
{
    public bool Enabled { get; set; }
    public int Race { get; set; } = 2;
    public int? ServerId { get; set; }
    /// <summary>После привязки/обновления: роль класса персонажа с самой высокой боевой мощью — выдать, остальные роли классов проекта — снять.</summary>
    public bool ClassRole { get; set; } = true;
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
        _data = DataFiles.Load<Dictionary<ulong, GuildPrefs>>(_path) ?? new();
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
            DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
    }

    public DmPrefs Dm(ulong guildId) => Get(guildId).Dm;

    /// <summary>
    /// Настройки оружейной для проекта. Пока админ ничего не задал — включена для проектов со словом «Aion» в названии
    /// (Asmodians, Triniel).
    /// </summary>
    public ArmoryProjectPrefs ArmoryFor(ulong guildId, string? projectKey, string? projectName)
    {
        var all = Get(guildId).Armory;
        if (all is not null) return projectKey is not null && all.TryGetValue(projectKey, out var p) ? p : new ArmoryProjectPrefs();
        var aion = projectName?.Contains("aion", StringComparison.OrdinalIgnoreCase) == true || projectKey is null;
        return new ArmoryProjectPrefs { Enabled = aion, Race = 2, ServerId = 2303 };
    }
}

/// <summary>
/// Личные ЛС участника (data/user-dm.json): какие ЛС он отключил себе. ЛС приходит, только если её включил админ сервера
/// (Settings → Direct messages) и участник не отключил её в My characters. ЛС о новых сборах — отдельно (LfgNotifyStore, /lfg-dm).
/// </summary>
public sealed class UserDmStore
{
    public static readonly string[] Keys = ["lfgPromoted", "lfgTimeChanged", "ticketResult", "gsReminder"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<string, HashSet<string>> _off;
    private readonly GuildPrefsStore _guild;

    public UserDmStore(IConfiguration config, GuildPrefsStore guild)
    {
        _guild = guild;
        _path = config["Storage:UserDmPath"] ?? "data/user-dm.json";
        _off = DataFiles.Load<Dictionary<string, HashSet<string>>>(_path) ?? new();
    }

    private static string Key(ulong guildId, ulong userId) => $"{guildId}:{userId}";

    public bool IsOff(ulong guildId, ulong userId, string key)
    {
        lock (_lock) return _off.TryGetValue(Key(guildId, userId), out var set) && set.Contains(key);
    }

    public void Set(ulong guildId, ulong userId, string key, bool enabled)
    {
        lock (_lock)
        {
            var k = Key(guildId, userId);
            if (!_off.TryGetValue(k, out var set)) _off[k] = set = [];
            if (enabled) set.Remove(key); else set.Add(key);
            if (set.Count == 0) _off.Remove(k);
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            DataFiles.Save(_path, JsonSerializer.Serialize(_off, JsonOptions));
        }
    }

    /// <summary>Включена ли ЛС на сервере (админом).</summary>
    public bool GuildEnabled(ulong guildId, string key)
    {
        var dm = _guild.Dm(guildId);
        return key switch
        {
            "lfgPromoted" => dm.LfgPromoted,
            "lfgTimeChanged" => dm.LfgTimeChanged,
            "ticketResult" => dm.TicketResult,
            "gsReminder" => dm.GsReminder,
            "lfgNewGroup" => dm.LfgNewGroup,
            _ => false
        };
    }

    /// <summary>Можно ли слать эту ЛС человеку: включена на сервере и он её не отключил.</summary>
    public bool Allowed(ulong guildId, ulong userId, string key) => GuildEnabled(guildId, key) && !IsOff(guildId, userId, key);
}
