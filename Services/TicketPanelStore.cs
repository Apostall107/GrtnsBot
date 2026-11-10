using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>data/ticket-panels.json — панели подпроектов с кнопками тикет-систем.</summary>
public sealed class TicketPanelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<TicketPanel> _data;

    public TicketPanelStore(IConfiguration config)
    {
        _path = config["Storage:TicketPanelsPath"] ?? "data/ticket-panels.json";
        _data = DataFiles.Load<List<TicketPanel>>(_path) ?? [];
    }

    public IReadOnlyList<TicketPanel> Get(ulong guildId)
    {
        lock (_lock) return _data.Where(p => p.GuildId == guildId).Select(Clone).ToList();
    }

    public TicketPanel? Find(string id)
    {
        lock (_lock) return _data.Find(p => p.Id == id) is { } p ? Clone(p) : null;
    }

    public string? Save(TicketPanel panel)
    {
        lock (_lock)
        {
            var i = _data.FindIndex(p => p.Id == panel.Id);
            if (i < 0 && _data.Count(p => p.GuildId == panel.GuildId) >= TicketPanel.MaxPerGuild)
                return $"Limit of {TicketPanel.MaxPerGuild} panels reached.";
            if (i < 0) _data.Add(Clone(panel)); else _data[i] = Clone(panel);
            Save();
            return null;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var removed = _data.RemoveAll(p => p.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public void SetPosted(string id, ulong? channelId, ulong? messageId)
    {
        lock (_lock)
        {
            if (_data.Find(p => p.Id == id) is not { } p) return;
            p.ChannelId = channelId;
            p.MessageId = messageId;
            Save();
        }
    }

    private static TicketPanel Clone(TicketPanel p) => JsonSerializer.Deserialize<TicketPanel>(JsonSerializer.Serialize(p))!;

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
