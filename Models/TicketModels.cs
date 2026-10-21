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
}

public sealed class Ticket
{
    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public string ProjectKey { get; set; } = "";
    public ulong UserId { get; set; }
    public ulong TextChannelId { get; set; }
    public ulong VoiceChannelId { get; set; }
    public ulong? ApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TicketData
{
    public Dictionary<ulong, List<TicketProject>> Projects { get; set; } = new();
    public List<Ticket> Tickets { get; set; } = [];
}
