using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Приветствие, автороль и панели ролей (data/community.json). Наружу отдаются копии.</summary>
public sealed class CommunityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly CommunityData _data;

    public CommunityStore(IConfiguration config)
    {
        _path = config["Storage:CommunityPath"] ?? "data/community.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<CommunityData>(File.ReadAllText(_path)) ?? new()
            : new();
        // Старый формат (до разделения на Welcome и Role panels) — перенести один раз
        var migrated = false;
        foreach (var w in _data.Welcome) migrated |= w.Migrate();
        foreach (var p in _data.RolePanels) migrated |= p.Migrate();
        if (migrated) Save();
    }

    // ---------- Приветствие ----------

    public WelcomeSettings GetWelcome(ulong guildId)
    {
        lock (_lock) return Clone(_data.Welcome.Find(w => w.GuildId == guildId) ?? new WelcomeSettings { GuildId = guildId });
    }

    public void SetWelcome(WelcomeSettings settings)
    {
        lock (_lock)
        {
            _data.Welcome.RemoveAll(w => w.GuildId == settings.GuildId);
            _data.Welcome.Add(Clone(settings));
            Save();
        }
    }

    // ---------- Панели ролей ----------

    public IReadOnlyList<RolePanel> GetPanels(ulong guildId)
    {
        lock (_lock) return _data.RolePanels.Where(p => p.GuildId == guildId).Select(Clone).ToList();
    }

    public RolePanel? FindPanel(string id)
    {
        lock (_lock) return _data.RolePanels.Find(p => p.Id == id) is { } p ? Clone(p) : null;
    }

    public RolePanel? FindPanelByMessage(ulong messageId)
    {
        lock (_lock) return _data.RolePanels.Find(p => p.MessageId == messageId) is { } p ? Clone(p) : null;
    }

    /// <summary>Добавить или заменить панель. Ошибка — при превышении лимита.</summary>
    public string? SavePanel(RolePanel panel)
    {
        lock (_lock)
        {
            var index = _data.RolePanels.FindIndex(p => p.Id == panel.Id);
            if (index < 0)
            {
                if (_data.RolePanels.Count(p => p.GuildId == panel.GuildId) >= RolePanel.MaxPerGuild)
                    return $"A server can have up to {RolePanel.MaxPerGuild} role panels.";
                _data.RolePanels.Add(Clone(panel));
            }
            else
            {
                _data.RolePanels[index] = Clone(panel);
            }
            Save();
            return null;
        }
    }

    public void SetPosted(string panelId, ulong? channelId, ulong? messageId)
    {
        lock (_lock)
        {
            if (_data.RolePanels.Find(p => p.Id == panelId) is not { } p) return;
            p.ChannelId = channelId;
            p.MessageId = messageId;
            Save();
        }
    }

    public bool RemovePanel(string id)
    {
        lock (_lock)
        {
            var removed = _data.RolePanels.RemoveAll(p => p.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
