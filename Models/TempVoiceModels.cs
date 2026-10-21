namespace LfgBot.Models;

/// <summary>Голосовой канал-«создатель»: зашёл в него — получил свою комнату.</summary>
public sealed class TempVoiceCreator
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    /// <summary>
    /// Шаблон имени комнаты, {user} заменяется на ник.
    /// null — автоматически: название канала-создателя без «Create room» + ник.
    /// </summary>
    public string? NameTemplate { get; set; }
    public int? UserLimit { get; set; }
}

/// <summary>Временная комната, удаляется, когда в ней никого не осталось.</summary>
public sealed class TempVoiceRoom
{
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong OwnerId { get; set; }
    public ulong CreatorChannelId { get; set; }
    public bool Locked { get; set; }
}

public sealed class TempVoiceData
{
    public List<TempVoiceCreator> Creators { get; set; } = [];
    public List<TempVoiceRoom> Rooms { get; set; } = [];
}
