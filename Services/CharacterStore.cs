using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Класс (роль сервера, которая есть у игрока) и заметка к персонажу.</summary>
public sealed class CharacterInfo
{
    public ulong? ClassRoleId { get; set; }
    public string? Comment { get; set; }
}

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

    public CharacterStore(IConfiguration config)
    {
        _path = config["Storage:CharactersPath"] ?? "data/characters.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path)) ?? new()
            : new();
        _infoPath = config["Storage:CharacterInfoPath"] ?? "data/character-info.json";
        _info = File.Exists(_infoPath)
            ? JsonSerializer.Deserialize<Dictionary<string, CharacterInfo>>(File.ReadAllText(_infoPath)) ?? new()
            : new();
    }

    private static string Key(ulong guildId, ulong userId) => $"{guildId}:{userId}";

    private static string InfoKey(ulong guildId, ulong userId, string name) => $"{guildId}:{userId}:{name.Trim().ToLowerInvariant()}";

    /// <summary>Класс и заметка персонажа (null — не заданы).</summary>
    public CharacterInfo? GetInfo(ulong guildId, ulong userId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_lock)
            return _info.TryGetValue(InfoKey(guildId, userId, name), out var i)
                ? new CharacterInfo { ClassRoleId = i.ClassRoleId, Comment = i.Comment }
                : null;
    }

    /// <summary>Меняет класс и/или заметку. setClass/setComment = false — поле не трогать.</summary>
    public void SetInfo(ulong guildId, ulong userId, string name, bool setClass, ulong? classRoleId, bool setComment, string? comment)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment is { Length: > MaxCommentLength }) comment = comment[..MaxCommentLength];

        lock (_lock)
        {
            var key = InfoKey(guildId, userId, name);
            if (!_info.TryGetValue(key, out var info)) info = new CharacterInfo();
            if (setClass) info.ClassRoleId = classRoleId;
            if (setComment) info.Comment = comment;

            if (info.ClassRoleId is null && info.Comment is null) _info.Remove(key);
            else _info[key] = info;
            SaveInfo();
        }
    }

    public IReadOnlyList<string> Get(ulong guildId, ulong userId)
    {
        lock (_lock)
            return _data.TryGetValue(Key(guildId, userId), out var list) ? list.ToList() : [];
    }

    /// <summary>Добавляет персонажа или поднимает существующего в начало списка (последний использованный — первый).</summary>
    public void Add(ulong guildId, ulong userId, string name)
    {
        name = name.Replace("`", "").Trim(); // обратные кавычки ломают блок кода с /invite
        if (name.Length == 0) return;
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];

        lock (_lock)
        {
            var key = Key(guildId, userId);
            if (!_data.TryGetValue(key, out var list))
                _data[key] = list = [];

            list.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, name);
            if (list.Count > MaxPerUser) list.RemoveRange(MaxPerUser, list.Count - MaxPerUser);
            Save();
        }
    }

    public bool Remove(ulong guildId, ulong userId, string name)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(Key(guildId, userId), out var list)) return false;
            var removed = list.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
                if (_info.Remove(InfoKey(guildId, userId, name))) SaveInfo();
            }
            return removed;
        }
    }

    private void SaveInfo()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_infoPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_infoPath, JsonSerializer.Serialize(_info, JsonOptions));
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
