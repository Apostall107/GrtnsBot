using System.Text.Json;
using Discord;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>
/// Роли-админы бота по серверам (data/admins.json).
/// Админ бота = Administrator, Manage Server или роль из этого списка.
/// </summary>
public sealed class AdminStore
{
    public const int MaxRoles = 10;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, List<ulong>> _data;

    public AdminStore(IConfiguration config)
    {
        _path = config["Storage:AdminsPath"] ?? "data/admins.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<ulong, List<ulong>>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public IReadOnlyList<ulong> GetRoles(ulong guildId)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var list) ? list.ToList() : [];
    }

    public void SetRoles(ulong guildId, IEnumerable<ulong> roleIds)
    {
        lock (_lock)
        {
            _data[guildId] = roleIds.Distinct().Take(MaxRoles).ToList();
            Save();
        }
    }

    /// <summary>Administrator или Manage Server — может менять список ролей-админов.</summary>
    public static bool IsServerManager(IGuildUser user) =>
        user.GuildPermissions.Administrator || user.GuildPermissions.ManageGuild;

    public bool IsAdmin(IGuildUser user)
    {
        if (IsServerManager(user)) return true;
        var roles = GetRoles(user.GuildId);
        return user.RoleIds.Any(roles.Contains);
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
