using System.Security.Cryptography;
using System.Text.Json.Serialization;

using LfgBot.Services;

namespace LfgBot.Models;

public enum SlotRole { Any, Dps, Tank, Healer }

public static class SlotRoleExtensions
{
    public static readonly SlotRole[] All = [SlotRole.Dps, SlotRole.Tank, SlotRole.Healer, SlotRole.Any];
    /// <summary>Роли, которыми записываются игроки: All — только тип слота (любая роль), записаться «All» нельзя.</summary>
    public static readonly SlotRole[] JoinRoles = [SlotRole.Dps, SlotRole.Tank, SlotRole.Healer];

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

/// <summary>
/// Требование слота. Код: <see cref="Inherit"/> — требование сбора (классы сбора), <see cref="Open"/> — без требования;
/// свои классы слота хранятся отдельно (<see cref="Slot.Classes"/>, «Templar или Gladiator»). Старые коды-id роли читаются как один класс.
/// На сайте одно значение на слот: "" — как у сбора, "none" — без требования, "id,id" — классы слота.
/// </summary>
public static class SlotRequirement
{
    public const ulong Inherit = 0;
    public const ulong Open = 1;
    public const string OpenDto = "none";

    /// <summary>Требования слотов: коды и классы (по одному элементу на слот; пустые списки — у всех как у сбора).</summary>
    public sealed record Set(List<ulong> Codes, List<List<ulong>> Classes)
    {
        public static readonly Set Empty = new([], []);
    }

    /// <summary>Значения сайта → требования для <paramref name="count"/> слотов. Классы — только из <paramref name="allowed"/>.</summary>
    public static Set Parse(IEnumerable<string?>? raw, int count, IReadOnlyCollection<ulong> allowed)
    {
        var codes = new List<ulong>();
        var classes = new List<List<ulong>>();
        foreach (var v in (raw ?? []).Take(count))
        {
            var t = (v ?? "").Trim();
            if (t == OpenDto) { codes.Add(Open); classes.Add([]); continue; }
            var ids = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => ulong.TryParse(x, out var id) ? id : 0).Where(id => id > Open && allowed.Contains(id)).Distinct().Take(10).ToList();
            codes.Add(Inherit);
            classes.Add(ids);
        }
        while (codes.Count < count) { codes.Add(Inherit); classes.Add([]); }
        return new Set(codes.All(x => x == Inherit) ? [] : codes, classes.All(x => x.Count == 0) ? [] : classes);
    }

    /// <summary>Значения для сайта: по одному на слот.</summary>
    public static IEnumerable<string> ToDto(IReadOnlyList<ulong>? codes, IReadOnlyList<List<ulong>>? classes, int count) =>
        Enumerable.Range(0, count).Select(i =>
        {
            if (classes is not null && i < classes.Count && classes[i].Count > 0) return string.Join(",", classes[i]);
            var code = codes is not null && i < codes.Count ? codes[i] : Inherit;
            return code switch { Inherit => "", Open => OpenDto, _ => code.ToString() };
        });

    /// <summary>Проставить требования слотам (лишние/недостающие — как у сбора).</summary>
    public static void Apply(IReadOnlyList<Slot> slots, IReadOnlyList<ulong>? codes, IReadOnlyList<List<ulong>>? classes = null)
    {
        for (var i = 0; i < slots.Count; i++)
        {
            var code = codes is not null && i < codes.Count ? codes[i] : Inherit;
            var own = classes is not null && i < classes.Count ? classes[i].ToList() : [];
            if (code > Open) { own = own.Count > 0 ? own : [code]; code = Inherit; } // старый формат: id одной роли
            slots[i].Requirement = code;
            slots[i].Classes = own;
        }
    }

    /// <summary>Коды слотов (пусто — у всех как у сбора).</summary>
    public static List<ulong> Of(IEnumerable<Slot> slots)
    {
        var list = slots.Select(s => s.Requirement).ToList();
        return list.All(x => x == Inherit) ? [] : list;
    }

    /// <summary>Классы слотов (пусто — ни у одного слота своих нет).</summary>
    public static List<List<ulong>> ClassesOf(IEnumerable<Slot> slots)
    {
        var list = slots.Select(s => s.Classes.ToList()).ToList();
        return list.All(x => x.Count == 0) ? [] : list;
    }
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
    /// <summary>Сокращение игрового сервера персонажа (TRI, LUD…) — для /invite Имя[LUD] игрокам с других серверов.</summary>
    public string? Server { get; set; }
    /// <summary>Боевая мощь (CP) из официальной оружейной AION 2 (снимок).</summary>
    public int? Cp { get; set; }
    /// <summary>Явка (отмечает организатор после старта): true — пришёл, false — не пришёл, null — не отмечено.</summary>
    public bool? Attended { get; set; }
    /// <summary>
    /// Требование слота (настройка, не участник): код <see cref="SlotRequirement"/> —
    /// 0 — общие требуемые роли сбора, 1 — без требования роли, иначе id одной из требуемых ролей.
    /// </summary>
    public ulong Requirement { get; set; }
    /// <summary>Свои классы слота (любой из); пусто — по <see cref="Requirement"/>.</summary>
    public List<ulong> Classes { get; set; } = [];
    /// <summary>Нажал «✅ Готов» в напоминании перед стартом.</summary>
    public bool Ready { get; set; }
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
        Server = null;
        Cp = null;
        Attended = null;
        Ready = false;
    }
}

public sealed class LfgMirror
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
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
    public string? Server { get; set; }
    public int? Cp { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Activity
{
    public string Key { get; set; } = IdGen.New();
    public string Name { get; set; } = "";
    public ulong RoleId { get; set; }
    /// <summary>Проект (игра). null — активность доступна во всех проектах.</summary>
    public string? ProjectKey { get; set; }
    /// <summary>Минимальный GS: персонаж с меньшим GS не видит активность при создании сбора; у сбора это требование GS.</summary>
    public int? MinGs { get; set; }
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
    /// <summary>Проект (игра): из панели LFG, на которой нажали Create LFG. null — проект по умолчанию.</summary>
    public string? ProjectKey { get; set; }
    /// <summary>Создать голосовую комнату сбора при публикации.</summary>
    public bool CreateVoice { get; set; }
    /// <summary>Требования (из шаблона): нужна одна из ролей; минимальный GS персонажа.</summary>
    public List<ulong> RequiredRoleIds { get; set; } = [];
    public int? MinGs { get; set; }
    /// <summary>Персонаж, с которым идёт организатор (выбирается первым шагом), и его GS.</summary>
    public string? OwnerCharacter { get; set; }
    public int? OwnerGs { get; set; }
    /// <summary>Показать полный выбор размера (2–25), а не только избранные 5 / 10 / 20.</summary>
    public bool AllSizes { get; set; }
    /// <summary>Заметка организатора к сбору (необязательная), показывается в посте.</summary>
    public string? Note { get; set; }

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
        Slots = draft.Slots.Select(s => new Slot { Role = s.Role, Requirement = s.Requirement, Classes = s.Classes.ToList() }).ToList();
        RequiredRoleIds = draft.RequiredRoleIds.ToList();
        MinGs = draft.MinGs;
        ProjectKey = draft.ProjectKey;
        Note = LfgNote.Clean(draft.Note);
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
    /// <summary>Персонаж, с которым идёт организатор (выбирается первым шагом), и его GS.</summary>
    public string? OwnerCharacter { get; set; }
    public int? OwnerGs { get; set; }
    /// <summary>Показать полный выбор размера (2–25), а не только избранные 5 / 10 / 20.</summary>
    public bool AllSizes { get; set; }
    /// <summary>Заметка организатора к сбору (необязательная), показывается в посте.</summary>
    public string? Note { get; set; }
    [JsonIgnore] public bool HasRequirements => RequiredRoleIds.Count > 0 || MinGs is not null;

    /// <summary>Какие классы нужны для слота (любой из; пусто — без требования).</summary>
    public IReadOnlyList<ulong> RequiredFor(Slot s) => s.Classes.Count > 0 ? s.Classes : s.Requirement switch
    {
        SlotRequirement.Inherit => RequiredRoleIds,
        SlotRequirement.Open => [],
        var id => [id]
    };

    /// <summary>У сбора есть требования по классам (общие или у слотов).</summary>
    [JsonIgnore] public bool HasClassRequirements => RequiredRoleIds.Count > 0 || Slots.Any(s => s.Classes.Count > 0 || s.Requirement > SlotRequirement.Open);

    /// <summary>Напоминание за 10 минут уже отправлено (или не нужно).</summary>
    public bool Reminded { get; set; }
    /// <summary>Проект (игра) сбора: персонажи берутся из него, записаться можно только с ролью проекта.</summary>
    public string? ProjectKey { get; set; }
    /// <summary>Версия вида поста, с которой он нарисован (<see cref="LfgBot.Services.LfgRenderer.PostVersion"/>): другая — пост перерисуется при старте.</summary>
    public int PostVersion { get; set; }
    /// <summary>Сервер проекта по умолчанию (сокращение) — «свой» сервер, если у организатора сервер не указан.</summary>
    public string? HomeServer { get; set; }
    /// <summary>Копии поста в других каналах (обновляются и удаляются вместе с основным).</summary>
    public List<LfgMirror> Mirrors { get; set; } = [];
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
    /// <param name="fits">Подходит ли игрок под требование слота (null — под любой).</param>
    public int FindFreeSlot(SlotRole wanted, Func<Slot, bool>? fits = null)
    {
        bool Free(Slot s) => s.IsFree && (fits is null || fits(s));
        if (wanted == SlotRole.Any)
        {
            var anySlot = Slots.FindIndex(s => Free(s) && s.Role == SlotRole.Any);
            return anySlot >= 0 ? anySlot : Slots.FindIndex(Free);
        }

        var exact = Slots.FindIndex(s => Free(s) && s.Role == wanted);
        return exact >= 0 ? exact : Slots.FindIndex(s => Free(s) && s.Role == SlotRole.Any);
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
        if (IsClosed) return Tx.Get(GuildId, "lfg.err.closed");

        var current = Slots.Find(s => s.UserId == userId);
        if (current is null)
        {
            if (FindFreeSlot(wanted) >= 0 || FindReserve(userId) is not null) return null;
            return Reserve.Count >= MaxReserve ? Tx.Get(GuildId, "lfg.err.reserveFull") : null;
        }

        if (current.TakenAs == wanted) return null; // смена персонажа на той же роли

        var snapshot = (current.UserId, current.TakenAs, current.CharacterName, current.ClassRoleId, current.ClassIcon, current.Comment, current.Gs, current.Server, current.Cp);
        current.Clear();
        var fits = FindFreeSlot(wanted) >= 0;
        (current.UserId, current.TakenAs, current.CharacterName, current.ClassRoleId, current.ClassIcon, current.Comment, current.Gs, current.Server, current.Cp) = snapshot;

        return fits ? null : Tx.Get(GuildId, "lfg.err.noSlots", ("role", $"{wanted.Emoji()} {wanted.Label()}"));
    }

    /// <summary>
    /// Записывает в слот или, если места нет, в резерв. Возвращает текст ошибки или null;
    /// reserved = true, если человек попал (или остался) в резерве.
    /// </summary>
    /// <param name="forceReserve">Игрок не подходит под требования сбора — только резерв (если он ещё не в группе).</param>
    /// <param name="fits">Подходит ли игрок под требование роли слота (null — под любой).</param>
    public string? TryJoin(ulong userId, SlotRole wanted, string characterName, out bool reserved, bool forceReserve = false,
        Func<Slot, bool>? fits = null)
    {
        reserved = false;
        if (IsClosed) return Tx.Get(GuildId, "lfg.err.closed");

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
                if (Reserve.Count >= MaxReserve) return Tx.Get(GuildId, "lfg.err.reserveFull");
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

        var snapshot = current is null ? default : (current.UserId, current.TakenAs, current.CharacterName, current.ClassRoleId, current.ClassIcon, current.Comment, current.Gs, current.Server, current.Cp);
        current?.Clear();

        var index = FindFreeSlot(wanted, fits);
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
            (current.UserId, current.TakenAs, current.CharacterName, current.ClassRoleId, current.ClassIcon, current.Comment, current.Gs, current.Server, current.Cp) = snapshot;
            return Tx.Get(GuildId, "lfg.err.noSlots", ("role", $"{wanted.Emoji()} {wanted.Label()}"));
        }

        // Мест нет — в резерв (или обновить запись в резерве)
        var entry = FindReserve(userId);
        if (entry is null)
        {
            if (Reserve.Count >= MaxReserve) return Tx.Get(GuildId, "lfg.err.reserveFull");
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

        return Reserve.RemoveAll(r => r.UserId == userId) > 0 ? null : Tx.Get(GuildId, "lfg.err.notInGroup");
    }

    /// <summary>
    /// Организатор переводит участника из слота в резерв (в конец списка) с его ролью и персонажем.
    /// Полностью убрать человека можно потом через «Remove from the reserve».
    /// </summary>
    public string? MoveToReserve(ulong userId)
    {
        if (userId == OwnerId) return Tx.Get(GuildId, "lfg.err.moveSelf");
        var slot = Slots.Find(s => s.UserId == userId);
        if (slot is null) return Tx.Get(GuildId, "lfg.err.memberGone");
        if (Reserve.Count >= MaxReserve) return Tx.Get(GuildId, "lfg.err.reserveFullManage");

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
        Reserve.RemoveAll(r => r.UserId == userId) > 0 ? null : Tx.Get(GuildId, "lfg.err.reserveGone");

    /// <summary>Перевести запись резерва в конкретный слот (автозамена).</summary>
    public Slot PromoteInto(ReserveEntry entry, int index)
    {
        Reserve.Remove(entry);
        var slot = Slots[index];
        slot.UserId = entry.UserId;
        slot.TakenAs = entry.Role;
        slot.CharacterName = entry.CharacterName;
        slot.ClassRoleId = entry.ClassRoleId;
        slot.ClassIcon = entry.ClassIcon;
        slot.Comment = entry.Comment;
        slot.Gs = entry.Gs;
        slot.Server = entry.Server;
        slot.Cp = entry.Cp;
        return slot;
    }

    /// <summary>
    /// Организатор переводит человека из резерва в группу: сначала подходящий по роли слот,
    /// иначе любой свободный (решение организатора).
    /// </summary>
    public string? Promote(ulong userId, out Slot? slot)
    {
        slot = null;
        var entry = FindReserve(userId);
        if (entry is null) return Tx.Get(GuildId, "lfg.err.reserveGone");

        var index = FindFreeSlot(entry.Role);
        if (index < 0) index = Slots.FindIndex(s => s.IsFree);
        if (index < 0) return Tx.Get(GuildId, "lfg.err.noSlotsManage");

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

/// <summary>Заметка организатора к сбору: обрезка, пустая — null.</summary>
public static class LfgNote
{
    public const int MaxLength = 300;

    public static string? Clean(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length <= MaxLength ? t : t[..MaxLength];
    }
}
