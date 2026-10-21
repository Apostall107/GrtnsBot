using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Сохранённые ники персонажей пользователя на сервере, хранятся в JSON.</summary>
public sealed class CharacterStore
{
    // В выпадающем списке максимум 25 пунктов, один занят «Новым персонажем»
    public const int MaxPerUser = 24;
    public const int MaxNameLength = 32;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<string, List<string>> _data;

    public CharacterStore(IConfiguration config)
    {
        _path = config["Storage:CharactersPath"] ?? "data/characters.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    private static string Key(ulong guildId, ulong userId) => $"{guildId}:{userId}";

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
            if (removed) Save();
            return removed;
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
