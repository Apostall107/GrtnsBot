using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace LfgBot.Models;

public enum SlotRole { Any, Dps, Tank, Healer }

public static class SlotRoleExtensions
{
    public static readonly SlotRole[] All = [SlotRole.Dps, SlotRole.Tank, SlotRole.Healer, SlotRole.Any];

    public static string Label(this SlotRole role) => role switch
    {
        SlotRole.Dps => "DD",
        SlotRole.Tank => "Tank",
        SlotRole.Healer => "Sup",
        _ => "All"
    };

    public static string Emoji(this SlotRole role) => role switch
    {
        SlotRole.Dps => "⚔️",
        SlotRole.Tank => "🛡️",
        SlotRole.Healer => "💚",
        _ => "🎲"
    };
}

public static class IdGen
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
}

public sealed class Slot
{
    public SlotRole Role { get; set; } = SlotRole.Any;
    public ulong? UserId { get; set; }
    /// <summary>Роль, которую выбрал участник при записи.</summary>
    public SlotRole? TakenAs { get; set; }
    /// <summary>Ник персонажа, с которым записался участник.</summary>
    public string? CharacterName { get; set; }
    /// <summary>Класс персонажа — роль сервера (снимок на момент записи).</summary>
    public ulong? ClassRoleId { get; set; }
    /// <summary>Юникод-эмодзи роли класса, если у роли есть (для показа рядом с ником).</summary>
    public string? ClassIcon { get; set; }
    /// <summary>Заметка игрока о персонаже.</summary>
    public string? Comment { get; set; }
    /// <summary>GS персонажа (снимок на момент записи).</summary>
    public int? Gs { get; set; }
    /// <summary>Явка (отмечает организатор после старта): true — пришёл, false — не пришёл, null — не отмечено.</summary>
    public bool? Attended { get; set; }
    [JsonIgnore] public bool IsFree => UserId is null;

    public void Clear()
    {
        UserId = null;
        TakenAs = null;
        CharacterName = null;
        ClassRoleId = null;
        ClassIcon = null;
        Comment = null;
        Gs = null;
        Attended = null;
    }
}

public sealed class ReserveEntry
{
    public ulong UserId { get; set; }
    public SlotRole Role { get; set; }
    public string? CharacterName { get; set; }
    public ulong? ClassRoleId { get; set; }
    public string? ClassIcon { get; set; }
    public string? Comment { get; set; }
    public int? Gs { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Activity
{
    public string Key { get; set; } = IdGen.New();
    public string Name { get; set; } = "";
    public ulong RoleId { get; set; }
}

/// <summary>Черновик сбора, пока организатор проходит шаги в эфемерном сообщении.</summary>
public sealed class LfgDraft(ulong guildId, ulong channelId, ulong ownerId)
{
    public string Id { get; } = IdGen.New();
    public ulong GuildId { get; } = guildId;
    public ulong ChannelId { get; } = channelId;
    public ulong OwnerId { get; } = ownerId;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    public string? ActivityName { get; set; }
    public ulong? PingRoleId { get; set; }
    public List<Slot> Slots { get; set; } = [];
    public HashSet<int> Selected { get; set; } = [];
    /// <summary>Разослать ЛС всем с ролью активности при публикации.</summary>
    public bool NotifyByDm { get; set; } = true;
    /// <summary>Требования (из шаблона): нужна одна из ролей; минимальный GS персонажа.</summary>
    public List<ulong> RequiredRoleIds { get; set; } = [];
    public int? MinGs { get; set; }

    // Время начала в часовом поясе сервера (Lfg:TimeZone)
    public DateOnly? EventDate { get; set; }
    public int? EventHour { get; set; }
    public int EventMinute { get; set; }
    public bool HasTime => EventDate is not null && EventHour is not null;

    public void ClearTime()
    {
        EventDate = null;
        EventHour = null;
        EventMinute = 0;
    }
}

/// <summary>Опубликованный сбор.</summary>
public sealed class LfgGroup
{
    /// <summary>Для загрузки из файла.</summary>
    [JsonConstructor]
    public LfgGroup() { }

    public LfgGroup(LfgDraft draft)
    {
        GuildId = draft.GuildId;
        ChannelId = draft.ChannelId;
        OwnerId = draft.OwnerId;
        ActivityName = draft.ActivityName ?? "Activity";
        PingRoleId = draft.PingRoleId;
        Slots = draft.Slots.Select(s => new Slot { Role = s.Role }).ToList();
        RequiredRoleIds = draft.RequiredRoleIds.ToList();
        MinGs = draft.MinGs;
    }

    // Сеттеры нужны для сохранения сборов в файл (data/lfg-groups.json)
    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong OwnerId { get; set; }
    public ulong MessageId { get; set; }
    public string ActivityName { get; set; } = "Activity";
    public ulong? PingRoleId { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    /// <summary>Когда пост будет удалён автоматически.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    public List<Slot> Slots { get; set; } = [];
    /// <summary>Запас: записались, когда подходящих слотов не было.</summary>
    public List<ReserveEntry> Reserve { get; set; } = [];
    public bool IsClosed { get; set; }
    /// <summary>Требования: у игрока одна из этих ролей (пусто — любой). Не подходит — запись только в резерв.</summary>
    public List<ulong> RequiredRoleIds { get; set; } = [];
    /// <summary>Минимальный GS персонажа (из /character gs или сайта). Не указан или меньше — резерв.</summary>
    public int? MinGs { get; set; }
    [JsonIgnore] public bool HasRequirements => RequiredRoleIds.Count > 0 || MinGs is not null;
    /// <summary>Напоминание за 10 минут уже отправлено (или не нужно).</summary>
    public bool Reminded { get; set; }
    /// <summary>Голосовая комната сбора (кнопка 🔊 в посте): все видят и могут зайти, говорить — только участники группы.</summary>
    public ulong? VoiceChannelId { get; set; }
    /// <summary>Сообщение-напоминание бота, удаляется вместе с постом.</summary>
    public ulong? ReminderMessageId { get; set; }

    /// <summary>Сериализует изменения состава и обновление сообщения.</summary>
    [JsonIgnore] public SemaphoreSlim Gate { get; } = new(1, 1);

    [JsonIgnore] public bool IsFull => Slots.All(s => !s.IsFree);
    [JsonIgnore] public int FilledCount => Slots.Count(s => !s.IsFree);

    /// <summary>
    /// ДД/Танк/Хил: сначала слот с такой же ролью, потом слот «Любой».
    /// Любой: сначала слот «Любой», потом любой свободный слот.
    /// </summary>
    public int FindFreeSlot(SlotRole wanted)
    {
        if (wanted == SlotRole.Any)
        {
            var anySlot = Slots.FindIndex(s => s.IsFree && s.Role == SlotRole.Any);
            return anySlot >= 0 ? anySlot : Slots.FindIndex(s => s.IsFree);
        }

        var exact = Slots.FindIndex(s => s.IsFree && s.Role == wanted);
        return exact >= 0 ? exact : Slots.FindIndex(s => s.IsFree && s.Role == SlotRole.Any);
    }

    public bool CanJoinAs(SlotRole role) => !IsClosed && FindFreeSlot(role) >= 0;

    public const int MaxReserve = 25;

    public ReserveEntry? FindReserve(ulong userId) => Reserve.Find(r => r.UserId == userId);

    /// <summary>
    /// Проверка перед выбором персонажа. Человек без слота может записаться всегда:
    /// если места нет — попадёт в резерв. Ошибка только если набор закрыт, резерв полон
    /// или человек уже в слоте и хочет сменить роль на ту, где нет места.
    /// </summary>
    public string? CheckJoin(ulong userId, SlotRole wanted)
    {
        if (IsClosed) return "Signups are closed.";

        var current = Slots.Find(s => s.UserId == userId);
        if (current is null)
        {
            if (FindFreeSlot(wanted) >= 0 || FindReserve(userId) is not null) return null;
            return Reserve.Count >= MaxReserve ? "The reserve list is full." : null;
        }

        if (current.TakenAs == wanted) return null; // смена персонажа на той же роли

        var snapshot = (current.UserId, current.TakenAs, current.CharacterName, current.ClassRoleId, current.ClassIcon, current.Comment, current.Gs);
        current.Clear();
        var fits = FindFreeSlot(wanted) >= 0;
        (current.UserId, current.TakenAs, current.CharacterName, current.ClassRoleId, current.ClassIcon, current.Comment, current.Gs) = snapshot;

        return fits ? null : $"No free slots for {wanted.Emoji()} {wanted.Label()}.";
    }

    /// <summary>
    /// Записывает в слот или, если места нет, в резерв. Возвращает текст ошибки или null;
    /// reserved = true, если человек попал (или остался) в резерве.
    /// </summary>
    /// <param name="forceReserve">Игрок не подходит под требования сбора — только резерв (если он ещё не в группе).</param>
    public string? TryJoin(ulong userId, SlotRole wanted, string characterName, out bool reserved, bool forceReserve = false)
    {
        reserved = false;
        if (IsClosed) return "Signups are closed.";

        var current = Slots.Find(s => s.UserId == userId);
        if (current?.TakenAs == wanted)
        {
            current.CharacterName = characterName;
            return null;
        }

        if (forceReserve && current is null)
        {
            var existing = FindReserve(userId);
            if (existing is null)
            {
                if (Reserve.Count >= MaxReserve) return "The reserve list is full.";
                Reserve.Add(new ReserveEntry { UserId = userId, Role = wanted, CharacterName = characterName });
            }
            else
            {
                existing.Role = wanted;
                existing.CharacterName = characterName;
            }
            reserved = true;
            return null;
        }

        var snapshot = current is null ? default : (current.UserId, current.TakenAs, current.CharacterName, current.ClassRoleId, current.ClassIcon, current.Comment, current.Gs);
        current?.Clear();

        var index = FindFreeSlot(wanted);
        if (index >= 0)
        {
            Reserve.RemoveAll(r => r.UserId == userId); // из резерва — в группу
            Slots[index].UserId = userId;
            Slots[index].TakenAs = wanted;
            Slots[index].CharacterName = characterName;
            return null;
        }

        if (current is not null)
        {
            // Уже в группе: не выкидываем в резерв из-за смены роли
            (current.UserId, current.TakenAs, current.CharacterName, current.ClassRoleId, current.ClassIcon, current.Comment, current.Gs) = snapshot;
            return $"No free slots for {wanted.Emoji()} {wanted.Label()}.";
        }

        // Мест нет — в резерв (или обновить запись в резерве)
        var entry = FindReserve(userId);
        if (entry is null)
        {
            if (Reserve.Count >= MaxReserve) return "The reserve list is full.";
            Reserve.Add(new ReserveEntry { UserId = userId, Role = wanted, CharacterName = characterName });
        }
        else
        {
            entry.Role = wanted;
            entry.CharacterName = characterName;
        }

        reserved = true;
        return null;
    }

    /// <summary>Организатор сразу занимает слот 1 с ролью этого слота.</summary>
    public void SeatOwner(string? characterName)
    {
        if (Slots.Count == 0) return;
        var slot = Slots[0];
        slot.UserId = OwnerId;
        slot.TakenAs = slot.Role;
        slot.CharacterName = characterName;
    }

    /// <summary>Выйти из группы или из резерва.</summary>
    public string? Leave(ulong userId)
    {
        var slot = Slots.Find(s => s.UserId == userId);
        if (slot is not null)
        {
            slot.Clear();
            return null;
        }

        return Reserve.RemoveAll(r => r.UserId == userId) > 0 ? null : "You are not in this group.";
    }

    /// <summary>
    /// Организатор переводит участника из слота в резерв (в конец списка) с его ролью и персонажем.
    /// Полностью убрать человека можно потом через «Remove from the reserve».
    /// </summary>
    public string? MoveToReserve(ulong userId)
    {
        if (userId == OwnerId) return "You can't move yourself — use Leave.";
        var slot = Slots.Find(s => s.UserId == userId);
        if (slot is null) return "This member is no longer in the group.";
        if (Reserve.Count >= MaxReserve) return "The reserve list is full — remove someone from it first.";

        Reserve.Add(new ReserveEntry
        {
            UserId = userId,
            Role = slot.TakenAs ?? slot.Role,
            CharacterName = slot.CharacterName,
            ClassRoleId = slot.ClassRoleId,
            ClassIcon = slot.ClassIcon,
            Comment = slot.Comment,
            Gs = slot.Gs
        });
        slot.Clear();
        return null;
    }

    /// <summary>Организатор убирает человека из резерва.</summary>
    public string? RemoveFromReserve(ulong userId) =>
        Reserve.RemoveAll(r => r.UserId == userId) > 0 ? null : "This member is no longer in the reserve.";

    /// <summary>
    /// Организатор переводит человека из резерва в группу: сначала подходящий по роли слот,
    /// иначе любой свободный (решение организатора).
    /// </summary>
    public string? Promote(ulong userId, out Slot? slot)
    {
        slot = null;
        var entry = FindReserve(userId);
        if (entry is null) return "This member is no longer in the reserve.";

        var index = FindFreeSlot(entry.Role);
        if (index < 0) index = Slots.FindIndex(s => s.IsFree);
        if (index < 0) return "No free slots — move someone to the reserve first.";

        Reserve.Remove(entry);
        slot = Slots[index];
        slot.UserId = entry.UserId;
        slot.TakenAs = entry.Role;
        slot.CharacterName = entry.CharacterName;
        slot.ClassRoleId = entry.ClassRoleId;
        slot.ClassIcon = entry.ClassIcon;
        slot.Comment = entry.Comment;
        slot.Gs = entry.Gs;
        return null;
    }
}
