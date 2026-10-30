using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Активности сервера (название + роль для пинга), хранятся в JSON.</summary>
public sealed class ActivityStore
{
    /// <summary>Всего активностей на сервере (по проектам).</summary>
    public const int MaxPerGuild = 100;
    /// <summary>В выпадающем списке /lfg максимум 25 пунктов, один занят «Своей активностью».</summary>
    public const int MaxInMenu = 24;

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

    /// <summary>Активности проекта + общие (без проекта). projectKey null — все.</summary>
    public IReadOnlyList<Activity> ForProject(ulong guildId, string? projectKey) =>
        Get(guildId).Where(a => projectKey is null || a.ProjectKey is null || a.ProjectKey == projectKey).ToList();

    public Activity? Find(ulong guildId, string key)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var list) ? list.Find(a => a.Key == key) : null;
    }

    public string? Add(ulong guildId, string name, ulong roleId, string? projectKey = null)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var list))
                _data[guildId] = list = [];

            if (list.Count >= MaxPerGuild) return $"Limit of {MaxPerGuild} activities reached.";
            if (list.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && a.ProjectKey == projectKey))
                return $"Activity \"{name}\" already exists.";

            list.Add(new Activity { Name = name, RoleId = roleId, ProjectKey = projectKey });
            Save();
            return null;
        }
    }

    /// <summary>Переименовать активность и/или сменить роль для пинга.</summary>
    public string? Update(ulong guildId, string key, string name, ulong roleId)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var list) || list.Find(a => a.Key == key) is not { } activity) return "Activity not found.";
            if (list.Any(a => a.Key != key && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && a.ProjectKey == activity.ProjectKey))
                return $"Activity \"{name}\" already exists.";
            activity.Name = name;
            activity.RoleId = roleId;
            Save();
            return null;
        }
    }

    /// <summary>Перенести активность в проект (null — общая для всех проектов).</summary>
    public bool SetProject(ulong guildId, string key, string? projectKey)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var list) || list.Find(a => a.Key == key) is not { } activity) return false;
            activity.ProjectKey = projectKey;
            Save();
            return true;
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
