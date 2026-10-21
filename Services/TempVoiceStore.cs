using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Каналы-создатели и активные комнаты, хранятся в JSON — после перезапуска бот подчищает пустые комнаты.</summary>
public sealed class TempVoiceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly TempVoiceData _data;

    public TempVoiceStore(IConfiguration config)
    {
        _path = config["Storage:TempVoicePath"] ?? "data/tempvoice.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<TempVoiceData>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    // ---------- Создатели ----------

    public TempVoiceCreator? GetCreator(ulong channelId)
    {
        lock (_lock) return _data.Creators.Find(c => c.ChannelId == channelId);
    }

    public IReadOnlyList<TempVoiceCreator> GetCreators(ulong guildId)
    {
        lock (_lock) return _data.Creators.Where(c => c.GuildId == guildId).ToList();
    }

    public void SetCreator(TempVoiceCreator creator)
    {
        lock (_lock)
        {
            _data.Creators.RemoveAll(c => c.ChannelId == creator.ChannelId);
            _data.Creators.Add(creator);
            Save();
        }
    }

    public bool RemoveCreator(ulong channelId)
    {
        lock (_lock)
        {
            var removed = _data.Creators.RemoveAll(c => c.ChannelId == channelId) > 0;
            if (removed) Save();
            return removed;
        }
    }

    // ---------- Комнаты ----------

    public TempVoiceRoom? GetRoom(ulong channelId)
    {
        lock (_lock) return _data.Rooms.Find(r => r.ChannelId == channelId);
    }

    public TempVoiceRoom? FindRoomByOwner(ulong guildId, ulong ownerId)
    {
        lock (_lock) return _data.Rooms.Find(r => r.GuildId == guildId && r.OwnerId == ownerId);
    }

    public IReadOnlyList<TempVoiceRoom> GetAllRooms()
    {
        lock (_lock) return _data.Rooms.ToList();
    }

    public void AddRoom(TempVoiceRoom room)
    {
        lock (_lock)
        {
            _data.Rooms.Add(room);
            Save();
        }
    }

    public bool RemoveRoom(ulong channelId)
    {
        lock (_lock)
        {
            var removed = _data.Rooms.RemoveAll(r => r.ChannelId == channelId) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public TempVoiceRoom? UpdateRoom(ulong channelId, Action<TempVoiceRoom> update)
    {
        lock (_lock)
        {
            var room = _data.Rooms.Find(r => r.ChannelId == channelId);
            if (room is null) return null;
            update(room);
            Save();
            return room;
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
