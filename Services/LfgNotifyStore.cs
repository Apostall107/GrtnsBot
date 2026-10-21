using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Кто отказался от ЛС-оповещений LFG (по серверу).</summary>
public sealed class LfgNotifyStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly HashSet<string> _optedOut;

    public LfgNotifyStore(IConfiguration config)
    {
        _path = config["Storage:LfgNotifyPath"] ?? "data/lfg-notify.json";
        _optedOut = File.Exists(_path)
            ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    private static string Key(ulong guildId, ulong userId) => $"{guildId}:{userId}";

    public bool IsEnabled(ulong guildId, ulong userId)
    {
        lock (_lock) return !_optedOut.Contains(Key(guildId, userId));
    }

    public void SetEnabled(ulong guildId, ulong userId, bool enabled)
    {
        lock (_lock)
        {
            var changed = enabled ? _optedOut.Remove(Key(guildId, userId)) : _optedOut.Add(Key(guildId, userId));
            if (!changed) return;

            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_optedOut));
        }
    }
}
