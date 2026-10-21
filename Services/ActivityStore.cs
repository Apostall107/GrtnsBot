using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Активности сервера (название + роль для пинга), хранятся в JSON.</summary>
public sealed class ActivityStore
{
    // В выпадающем списке максимум 25 пунктов, один занят «Своей активностью»
    public const int MaxPerGuild = 24;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, List<Activity>> _data;

    public ActivityStore(IConfiguration config)
    {
        _path = config["Storage:ActivitiesPath"] ?? "data/activities.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<ulong, List<Activity>>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public IReadOnlyList<Activity> Get(ulong guildId)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var list) ? list.ToList() : [];
    }

    public Activity? Find(ulong guildId, string key)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var list) ? list.Find(a => a.Key == key) : null;
    }

    public string? Add(ulong guildId, string name, ulong roleId)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var list))
                _data[guildId] = list = [];

            if (list.Count >= MaxPerGuild) return $"Limit of {MaxPerGuild} activities reached.";
            if (list.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return $"Activity \"{name}\" already exists.";

            list.Add(new Activity { Name = name, RoleId = roleId });
            Save();
            return null;
        }
    }

    public Activity? Remove(ulong guildId, string key)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var list)) return null;
            var activity = list.Find(a => a.Key == key);
            if (activity is null) return null;
            list.Remove(activity);
            Save();
            return activity;
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
