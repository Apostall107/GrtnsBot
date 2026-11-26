using System.Text.Json.Serialization;

namespace LfgBot.Models;

/// <summary>Участник статика (КП): организатор принял его заявку. Персонаж — необязательный, снимок на момент заявки.</summary>
public sealed class StaticMember
{
    public ulong UserId { get; set; }
    public string? CharacterName { get; set; }
    public string? ClassIcon { get; set; }
    public int? Gs { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Заявка в статик: ждёт решения организатора.</summary>
public sealed class StaticApplicant
{
    public ulong UserId { get; set; }
    public string? CharacterName { get; set; }
    public string? ClassIcon { get; set; }
    public int? Gs { get; set; }
    /// <summary>Сообщение организатору (необязательное).</summary>
    public string? Message { get; set; }
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Статик / постоянная группа (КП). В отличие от сбора LFG не удаляется сам: живёт, пока его не удалит организатор или админ.
/// В состав попадают только через заявку, которую принял организатор.
/// </summary>
public sealed class StaticGroup
{
    public const int MaxName = 80;
    public const int MaxDescription = 1500;
    public const int MaxMessage = 300;
    public const int MinSize = 2;
    public const int MaxSize = 100;
    public const int MaxApplicants = 50;
    public const int MaxHistory = 150;

    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    /// <summary>Пост статика в канале. 0 — поста нет (его удалили в Discord): статик живёт, пост можно опубликовать заново.</summary>
    public ulong MessageId { get; set; }
    public ulong OwnerId { get; set; }
    /// <summary>Проект (игра). null — проект по умолчанию.</summary>
    public string? ProjectKey { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Описание: цели, расписание, кого ищут.</summary>
    public string? Description { get; set; }
    public int Size { get; set; } = 5;
    /// <summary>Набор открыт: можно подавать заявки.</summary>
    public bool Recruiting { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<StaticMember> Members { get; set; } = [];
    public List<StaticApplicant> Applicants { get; set; } = [];
    public List<GroupLogEntry> History { get; set; } = [];

    /// <summary>Замок: изменения статика по очереди (Discord, сайт, ЛС).</summary>
    [JsonIgnore] public SemaphoreSlim Gate { get; } = new(1, 1);

    [JsonIgnore] public bool IsFull => Members.Count >= Size;

    public StaticMember? FindMember(ulong userId) => Members.Find(m => m.UserId == userId);
    public StaticApplicant? FindApplicant(ulong userId) => Applicants.Find(a => a.UserId == userId);

    public void Log(ulong userId, string text)
    {
        History.Add(new GroupLogEntry { UserId = userId, Text = text });
        if (History.Count > MaxHistory) History.RemoveRange(0, History.Count - MaxHistory);
    }
}

/// <summary>Черновик создания статика в Discord (только в памяти): проект, канал и персонаж организатора.</summary>
public sealed class StaticDraft(ulong guildId, ulong channelId, ulong ownerId, string? projectKey)
{
    public string Id { get; } = IdGen.New();
    public ulong GuildId { get; } = guildId;
    public ulong ChannelId { get; } = channelId;
    public ulong OwnerId { get; } = ownerId;
    public string? ProjectKey { get; } = projectKey;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public string? Character { get; set; }
}
