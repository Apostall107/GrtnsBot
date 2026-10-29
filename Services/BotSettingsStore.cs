using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Настройки, которые меняются с сайта. null — значение из appsettings.json.</summary>
public sealed class BotSettings
{
    public string? TimeZone { get; set; }
    public int? AutoDeleteHours { get; set; }
    public int? ReminderMinutes { get; set; }
    /// <summary>Роли «участник» по серверам: у кого есть хоть одна — видит раздел Member на сайте.</summary>
    public Dictionary<ulong, List<ulong>> MemberRoles { get; set; } = new();
}

/// <summary>data/settings.json — настройки бота из веб-админки.</summary>
public sealed class BotSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private BotSettings _data;

    public BotSettingsStore(IConfiguration config)
    {
        _path = config["Storage:SettingsPath"] ?? "data/settings.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<BotSettings>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public BotSettings Get()
    {
        lock (_lock)
            return new BotSettings
            {
                TimeZone = _data.TimeZone,
                AutoDeleteHours = _data.AutoDeleteHours,
                ReminderMinutes = _data.ReminderMinutes,
                MemberRoles = _data.MemberRoles.ToDictionary(x => x.Key, x => x.Value.ToList())
            };
    }

    public IReadOnlyList<ulong> GetMemberRoles(ulong guildId)
    {
        lock (_lock)
            return _data.MemberRoles.TryGetValue(guildId, out var list) ? list.ToList() : [];
    }

    public void SetMemberRoles(ulong guildId, IEnumerable<ulong> roleIds)
    {
        var copy = Get();
        var list = roleIds.Distinct().Take(10).ToList();
        if (list.Count == 0) copy.MemberRoles.Remove(guildId);
        else copy.MemberRoles[guildId] = list;
        Save(copy);
    }

    public void Save(BotSettings settings)
    {
        lock (_lock)
        {
            _data = settings;
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
    }
}
