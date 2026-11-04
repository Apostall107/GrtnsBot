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

    // ---- Настройки с сайта (Temp Voice → ⚙ Settings) ----
    /// <summary>Где появляются комнаты: below — под создателем, above — над ним, top — в начале категории, bottom — в конце.</summary>
    public string Placement { get; set; } = TempRoomPlacement.Below;
    /// <summary>Категория для комнат (null — та же, что у создателя).</summary>
    public ulong? RoomCategoryId { get; set; }
    /// <summary>Битрейт комнат, кбит/с (null — как у создателя).</summary>
    public int? BitrateKbps { get; set; }
    /// <summary>Через сколько секунд удалять опустевшую комнату (0 — сразу; вернулись раньше — комната остаётся).</summary>
    public int DeleteDelaySeconds { get; set; }
    /// <summary>Комната сразу закрыта (зайти могут только приглашённые).</summary>
    public bool StartLocked { get; set; }
    /// <summary>Личные настройки участника (имя, лимит, «сразу закрыть») важнее настроек создателя.</summary>
    public bool AllowMemberPrefs { get; set; } = true;
    /// <summary>Отправлять панель управления в чат комнаты.</summary>
    public bool PostPanel { get; set; } = true;
    /// <summary>Сколько комнат одновременно может быть от этого создателя (0 — без ограничения).</summary>
    public int MaxRooms { get; set; }

    /// <summary>Перенести настройки с сайта (при пересоздании записи командой или окном).</summary>
    public void CopySettingsFrom(TempVoiceCreator other)
    {
        Placement = other.Placement;
        RoomCategoryId = other.RoomCategoryId;
        BitrateKbps = other.BitrateKbps;
        DeleteDelaySeconds = other.DeleteDelaySeconds;
        StartLocked = other.StartLocked;
        AllowMemberPrefs = other.AllowMemberPrefs;
        PostPanel = other.PostPanel;
        MaxRooms = other.MaxRooms;
    }
}

public static class TempRoomPlacement
{
    public const string Below = "below", Above = "above", Top = "top", Bottom = "bottom";
    public static readonly string[] All = [Below, Above, Top, Bottom];
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
