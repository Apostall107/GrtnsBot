using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Мои настройки временной комнаты: применяются при каждом её создании.</summary>
public sealed class RoomPrefs
{
    /// <summary>Имя комнаты, {user} — мой ник. null — как задано у канала-создателя.</summary>
    public string? Name { get; set; }
    /// <summary>Лимит (0 — без лимита). null — как у канала-создателя.</summary>
    public int? Limit { get; set; }
    /// <summary>Сразу закрыть комнату.</summary>
    public bool Locked { get; set; }
}

/// <summary>data/room-prefs.json — по ключу «сервер:пользователь».</summary>
public sealed class RoomPrefsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<string, RoomPrefs> _data;

    public RoomPrefsStore(IConfiguration config)
    {
        _path = config["Storage:RoomPrefsPath"] ?? "data/room-prefs.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, RoomPrefs>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    private static string Key(ulong guildId, ulong userId) => $"{guildId}:{userId}";

    public RoomPrefs? Get(ulong guildId, ulong userId)
    {
        lock (_lock)
            return _data.TryGetValue(Key(guildId, userId), out var p) ? new RoomPrefs { Name = p.Name, Limit = p.Limit, Locked = p.Locked } : null;
    }

    /// <summary>null — сбросить к настройкам канала-создателя.</summary>
    public void Set(ulong guildId, ulong userId, RoomPrefs? prefs)
    {
        lock (_lock)
        {
            if (prefs is null || (prefs.Name is null && prefs.Limit is null && !prefs.Locked)) _data.Remove(Key(guildId, userId));
            else _data[Key(guildId, userId)] = prefs;
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
    }
}
