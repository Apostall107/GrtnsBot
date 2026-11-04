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

    /// <summary>Сохранить создателя. Уже был — его настройки с сайта (размещение, категория, …) сохраняются.</summary>
    public void SetCreator(TempVoiceCreator creator)
    {
        lock (_lock)
        {
            if (_data.Creators.Find(c => c.ChannelId == creator.ChannelId) is { } existing && !ReferenceEquals(existing, creator))
                creator.CopySettingsFrom(existing);
            _data.Creators.RemoveAll(c => c.ChannelId == creator.ChannelId);
            _data.Creators.Add(creator);
            Save();
        }
    }

    /// <summary>Изменить настройки создателя.</summary>
    public bool UpdateCreator(ulong channelId, Action<TempVoiceCreator> update)
    {
        lock (_lock)
        {
            if (_data.Creators.Find(c => c.ChannelId == channelId) is not { } creator) return false;
            update(creator);
            Save();
            return true;
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

    /// <summary>Комната владельца; с creatorChannelId — только созданная через этот канал-создатель.</summary>
    public TempVoiceRoom? FindRoomByOwner(ulong guildId, ulong ownerId, ulong? creatorChannelId = null)
    {
        lock (_lock) return _data.Rooms.Find(r => r.GuildId == guildId && r.OwnerId == ownerId &&
                                                  (creatorChannelId is null || r.CreatorChannelId == creatorChannelId));
    }

    public IReadOnlyList<TempVoiceRoom> GetRooms(ulong guildId)
    {
        lock (_lock) return _data.Rooms.Where(r => r.GuildId == guildId).ToList();
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
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
