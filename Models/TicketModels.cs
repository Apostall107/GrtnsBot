namespace LfgBot.Models;

/// <summary>Проект (игра), в который подаются заявки: WWM, Aion 2 и т.д.</summary>
public sealed class TicketProject
{
    public string Key { get; set; } = IdGen.New();
    public string Name { get; set; } = "";
    /// <summary>Роли, которые выдаются по Approve.</summary>
    public List<ulong> MemberRoleIds { get; set; } = [];
    /// <summary>Роли руководства: видят тикеты проекта и могут нажимать Approve.</summary>
    public List<ulong> StaffRoleIds { get; set; } = [];
    /// <summary>Категория для каналов тикетов. null — общая категория «Tickets».</summary>
    public ulong? CategoryId { get; set; }
    /// <summary>Свой текст в сообщении тикета: под системной информацией, над кнопками. null — нет блока.</summary>
    public string? CustomText { get; set; }
    /// <summary>Анкета включена: перед созданием тикета заявитель заполняет вопросы (окно Discord или сайт).</summary>
    public bool FormEnabled { get; set; }
    /// <summary>Вопросы анкеты (до 5 — лимит окна Discord).</summary>
    public List<TicketQuestion> Questions { get; set; } = [];
    /// <summary>Закрывать тикет без сообщений N дней (с предупреждением за сутки). null — не закрывать.</summary>
    public int? AutoCloseDays { get; set; }
}

/// <summary>Вопрос анкеты проекта.</summary>
public sealed class TicketQuestion
{
    public const int MaxQuestions = 5;
    public const int MaxLabel = 45;
    public const int MaxPlaceholder = 100;

    public string Id { get; set; } = IdGen.New();
    public string Label { get; set; } = "";
    public string? Placeholder { get; set; }
    /// <summary>true — многострочный ответ.</summary>
    public bool Long { get; set; }
    public bool Required { get; set; } = true;
    public int MaxLength { get; set; } = 200;
}

public sealed class TicketAnswer
{
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
}

public sealed class Ticket
{
    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public string ProjectKey { get; set; } = "";
    public ulong UserId { get; set; }
    public ulong TextChannelId { get; set; }
    public ulong VoiceChannelId { get; set; }
    /// <summary>Сообщение бота с кнопками Approve/Close. null — тикет создан до этого поля (ищется в канале).</summary>
    public ulong? MessageId { get; set; }
    public ulong? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Ответы анкеты.</summary>
    public List<TicketAnswer> Answers { get; set; } = [];
    /// <summary>Когда бот предупредил об авто-закрытии (null — не предупреждал).</summary>
    public DateTimeOffset? WarnedAt { get; set; }
}

/// <summary>Закрытый тикет в архиве (data/ticket-archive.json).</summary>
public sealed class TicketArchiveEntry
{
    public string Id { get; set; } = "";
    public ulong GuildId { get; set; }
    public string Project { get; set; } = "";
    public ulong UserId { get; set; }
    public string UserName { get; set; } = "";
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset ClosedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>approved / denied / closed / auto-closed</summary>
    public string Outcome { get; set; } = "closed";
    public ulong? ById { get; set; }
    public string? ByName { get; set; }
    public string? Reason { get; set; }
    public List<TicketAnswer> Answers { get; set; } = [];
}

public sealed class TicketData
{
    public Dictionary<ulong, List<TicketProject>> Projects { get; set; } = new();
    public List<Ticket> Tickets { get; set; } = [];
}
