using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Класс (роль сервера, которая есть у игрока) и заметка к персонажу.</summary>
public sealed class CharacterInfo
{
    public ulong? ClassRoleId { get; set; }
    public string? Comment { get; set; }
    /// <summary>Gear score персонажа (для требований сборов).</summary>
    public int? Gs { get; set; }
    /// <summary>Когда GS меняли последний раз (для напоминания обновить GS).</summary>
    public DateTimeOffset? GsUpdatedAt { get; set; }
    /// <summary>Привязка к официальной оружейной (aion2.plaync.com) и последние данные оттуда.</summary>
    public ArmoryLink? Armory { get; set; }
    /// <summary>Фракция персонажа (1 — Elyos, 2 — Asmodians) и сервер (ID с официального сайта).</summary>
    public int? Race { get; set; }
    public int? ServerId { get; set; }

    public bool IsEmpty => ClassRoleId is null && Comment is null && Gs is null && Armory is null && Race is null && ServerId is null;

    public CharacterInfo Copy() => new()
    {
        ClassRoleId = ClassRoleId, Comment = Comment, Gs = Gs, GsUpdatedAt = GsUpdatedAt, Race = Race, ServerId = ServerId,
        Armory = Armory is null ? null : Armory with { }
    };
}

/// <summary>Персонаж в официальной оружейной: ID, сервер и данные последней синхронизации.</summary>
public sealed record ArmoryLink
{
    public string CharacterId { get; init; } = "";
    public int ServerId { get; init; }
    public string? ServerName { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public int Level { get; init; }
    public int? CombatPower { get; init; }
    public int? ItemLevel { get; init; }
    public string? Legion { get; init; }
    public DateTimeOffset? SyncedAt { get; init; }
    /// <summary>Последняя ошибка синхронизации (например, персонаж не найден).</summary>
    public string? Error { get; init; }
}

/// <summary>Персонаж сервера для ростера: владелец, проект (scope), имя и профиль.</summary>
public sealed record RosterCharacter(ulong UserId, string? Scope, string Name, CharacterInfo? Info);

/// <summary>
/// Сохранённые ники персонажей пользователя на сервере (data/characters.json)
/// и их профиль — класс-роль и заметка (data/character-info.json).
/// </summary>
public sealed class CharacterStore
{
    public const int MaxCommentLength = 100;

    // В выпадающем списке максимум 25 пунктов, один занят «Новым персонажем»
    public const int MaxPerUser = 24;
    public const int MaxNameLength = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<string, List<string>> _data;
    private readonly string _infoPath;
    private readonly Dictionary<string, CharacterInfo> _info;

    private readonly GuildPrefsStore _prefs;

    public CharacterStore(IConfiguration config, GuildPrefsStore prefs)
    {
        _prefs = prefs;
        _path = config["Storage:CharactersPath"] ?? "data/characters.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path)) ?? new()
            : new();
        _infoPath = config["Storage:CharacterInfoPath"] ?? "data/character-info.json";
        _info = File.Exists(_infoPath)
            ? JsonSerializer.Deserialize<Dictionary<string, CharacterInfo>>(File.ReadAllText(_infoPath)) ?? new()
            : new();
    }

    /// <summary>scope — проект (см. LfgProjectStore.Scope); null — проект по умолчанию / персонажи до появления проектов.</summary>
    private static string Key(ulong guildId, ulong userId, string? scope = null) =>
        scope is null ? $"{guildId}:{userId}" : $"{guildId}:{userId}@{scope}";

    private static string InfoKey(ulong guildId, ulong userId, string name, string? scope = null) =>
        $"{Key(guildId, userId, scope)}:{name.Trim().ToLowerInvariant()}";

    /// <summary>Класс и заметка персонажа (null — не заданы).</summary>
    public CharacterInfo? GetInfo(ulong guildId, ulong userId, string? name, string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock)
            return _info.TryGetValue(InfoKey(guildId, userId, name, scope), out var i)
                ? i.Copy()
                : null;
    }

    /// <summary>Меняет класс и/или заметку. setClass/setComment = false — поле не трогать.</summary>
    /// <param name="fromArmory">Класс из официальной оружейной. Иначе класс привязанного персонажа не меняется.</param>
    public void SetInfo(ulong guildId, ulong userId, string name, bool setClass, ulong? classRoleId, bool setComment, string? comment,
        string? scope = null, bool fromArmory = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment is { Length: > MaxCommentLength }) comment = comment[..MaxCommentLength];

        lock (_lock)
        {
            var key = InfoKey(guildId, userId, name, scope);
            if (!_info.TryGetValue(key, out var info)) info = new CharacterInfo();
            if (setClass && (fromArmory || !IsLinked(info))) info.ClassRoleId = classRoleId;
            if (setComment) info.Comment = comment;

            if (info.IsEmpty) _info.Remove(key);
            else _info[key] = info;
            SaveInfo();
        }
    }

    /// <summary>GS персонажа: null — убрать.</summary>
    /// <param name="confirm">Обновить дату, даже если GS не изменился («GS актуален»).</param>
    /// <param name="fromArmory">Значение из официальной оружейной. Иначе GS привязанного персонажа не меняется.</param>
    /// <returns>false — GS этого персонажа берётся из оружейной, вручную не меняется.</returns>
    public bool SetGs(ulong guildId, ulong userId, string name, int? gs, string? scope = null, bool confirm = false, bool fromArmory = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_lock)
        {
            var key = InfoKey(guildId, userId, name, scope);
            if (!_info.TryGetValue(key, out var info)) info = new CharacterInfo();
            if (!fromArmory && GsFromArmory(guildId, info)) return false;
            var value = gs is { } v ? Math.Clamp(v, 0, 99999) : (int?)null;
            if (confirm || value != info.Gs || info.GsUpdatedAt is null) info.GsUpdatedAt = DateTimeOffset.UtcNow;
            info.Gs = value;
            if (info.IsEmpty) _info.Remove(key);
            else _info[key] = info;
            SaveInfo();
        }
        return true;
    }

    /// <summary>Персонаж привязан к официальной оружейной: класс и GS берутся оттуда и вручную не меняются.</summary>
    public static bool IsLinked(CharacterInfo? info) => info?.Armory is { Error: null };

    /// <summary>GS персонажа приходит из официальной оружейной (привязан, данные найдены, на сервере GS берётся оттуда).</summary>
    public bool GsFromArmory(ulong guildId, CharacterInfo? info) => info?.Armory is { Error: null } && _prefs.Get(guildId).ArmoryGs != "none";

    /// <summary>Фракция и сервер персонажа (null — не указано).</summary>
    public void SetRealm(ulong guildId, ulong userId, string name, int? race, int? serverId, string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var key = InfoKey(guildId, userId, name, scope);
            if (!_info.TryGetValue(key, out var info)) info = new CharacterInfo();
            info.Race = race;
            info.ServerId = serverId;
            if (info.IsEmpty) _info.Remove(key);
            else _info[key] = info;
            SaveInfo();
        }
    }

    /// <summary>Привязка к официальной оружейной (null — отвязать).</summary>
    public void SetArmory(ulong guildId, ulong userId, string name, ArmoryLink? link, string? scope = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            var key = InfoKey(guildId, userId, name, scope);
            if (!_info.TryGetValue(key, out var info)) info = new CharacterInfo();
            info.Armory = link;
            if (info.IsEmpty) _info.Remove(key);
            else _info[key] = info;
            SaveInfo();
        }
    }

    /// <summary>Все персонажи сервера (все проекты): для ростера и напоминаний.</summary>
    public IReadOnlyList<RosterCharacter> All(ulong guildId)
    {
        var prefix = $"{guildId}:";
        lock (_lock)
        {
            var result = new List<RosterCharacter>();
            foreach (var (key, names) in _data)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rest = key[prefix.Length..];
                var at = rest.IndexOf('@');
                var scope = at < 0 ? null : rest[(at + 1)..];
                if (!ulong.TryParse(at < 0 ? rest : rest[..at], out var userId)) continue;
                foreach (var name in names)
                {
                    _info.TryGetValue(InfoKey(guildId, userId, name, scope), out var i);
                    result.Add(new RosterCharacter(userId, scope, name, i?.Copy()));
                }
            }
            return result;
        }
    }

    public IReadOnlyList<string> Get(ulong guildId, ulong userId, string? scope = null)
    {
        lock (_lock)
            return _data.TryGetValue(Key(guildId, userId, scope), out var list) ? list.ToList() : [];
    }

    /// <summary>Добавляет персонажа или поднимает существующего в начало списка (последний использованный — первый).</summary>
    public void Add(ulong guildId, ulong userId, string name, string? scope = null)
    {
        name = name.Replace("`", "").Trim(); // обратные кавычки ломают блок кода с /invite
        if (name.Length == 0) return;
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];

        lock (_lock)
        {
            var key = Key(guildId, userId, scope);
            if (!_data.TryGetValue(key, out var list))
                _data[key] = list = [];

            list.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, name);
            if (list.Count > MaxPerUser) list.RemoveRange(MaxPerUser, list.Count - MaxPerUser);
            Save();
        }
    }

    /// <summary>Переименовать персонажа (класс, заметка и GS переходят к новому имени). null — успех, иначе текст ошибки.</summary>
    public string? Rename(ulong guildId, ulong userId, string oldName, string newName, string? scope = null)
    {
        newName = newName.Replace("`", "").Trim();
        if (newName.Length == 0) return Tx.Get(guildId, "char.enterName");
        if (newName.Length > MaxNameLength) return Tx.Get(guildId, "char.nameTooLong", ("max", MaxNameLength));

        lock (_lock)
        {
            if (!_data.TryGetValue(Key(guildId, userId, scope), out var list)) return Tx.Get(guildId, "char.notFound");
            var index = list.FindIndex(n => n.Equals(oldName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return Tx.Get(guildId, "char.notFound");
            if (list.Where((_, i) => i != index).Any(n => n.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                return Tx.Get(guildId, "char.duplicate");

            var oldKey = InfoKey(guildId, userId, list[index], scope);
            var newKey = InfoKey(guildId, userId, newName, scope);
            list[index] = newName;
            Save();
            if (oldKey != newKey && _info.Remove(oldKey, out var info))
            {
                _info[newKey] = info;
                SaveInfo();
            }
            return null;
        }
    }

    public bool Remove(ulong guildId, ulong userId, string name, string? scope = null)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(Key(guildId, userId, scope), out var list)) return false;
            var removed = list.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
                if (_info.Remove(InfoKey(guildId, userId, name, scope))) SaveInfo();
            }
            return removed;
        }
    }

    /// <summary>
    /// Импорт персонажа (перенос с другого инстанса): добавляет в конец списка, профиль — если его нет
    /// (или поверх, когда overwrite). Возвращает "added", "updated", "skipped" или "full".
    /// </summary>
    public string Import(ulong guildId, ulong userId, string? scope, string name, CharacterInfo? info, bool overwrite)
    {
        name = name.Replace("`", "").Trim();
        if (name.Length == 0) return "skipped";
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        lock (_lock)
        {
            var key = Key(guildId, userId, scope);
            if (!_data.TryGetValue(key, out var list)) _data[key] = list = [];
            var exists = list.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                if (list.Count >= MaxPerUser) return "full";
                list.Add(name);
                Save();
            }
            var infoKey = InfoKey(guildId, userId, name, scope);
            var hasInfo = _info.ContainsKey(infoKey);
            if (info is { IsEmpty: false } && (!hasInfo || overwrite))
            {
                _info[infoKey] = info.Copy();
                SaveInfo();
                return exists ? "updated" : "added";
            }
            return exists ? "skipped" : "added";
        }
    }

    private void SaveInfo()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_infoPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DataFiles.Save(_infoPath, JsonSerializer.Serialize(_info, JsonOptions));
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
