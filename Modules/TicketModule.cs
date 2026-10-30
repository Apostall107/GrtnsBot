using System.Collections.Concurrent;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;

namespace LfgBot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class TicketModule(TicketStore store, BotOwner botOwner, PanelService panels, AuditLog audit, TicketService tickets) : InteractionModuleBase<SocketInteractionContext>
{
    private string NoAccessText => T("lfg.noAccess");

    private string T(string key, params (string Name, object? Value)[] args) => Tx.Get(Context.Guild?.Id ?? 0, key, args);

    private SocketMessageComponent Component => (SocketMessageComponent)Context.Interaction;

    // ================= Панель =================

    [SlashCommand("ticket-panel", "Post the ticket panel with instructions in this channel")]
    [RequireBotAdmin]
    public async Task PanelCommand()
    {
        if (await GetChannelAsync(Context.Interaction.ChannelId ?? 0) is not { } channel)
        {
            await RespondAsync(NoAccessText, ephemeral: true);
            return;
        }

        await panels.PostAsync(channel, Context.Guild.Id, "tickets");
        await RespondAsync("Ticket panel posted.", ephemeral: true);
    }

    [ComponentInteraction("ticket:start")]
    public async Task OnStart()
    {
        var projects = store.GetProjects(Context.Guild.Id);
        if (projects.Count == 0)
        {
            await RespondAsync(T("ticket.noProjects"), ephemeral: true);
            return;
        }

        await RespondAsync(T("ticket.pick"),
            components: TicketRenderer.ProjectPicker(Context.Guild.Id, projects), ephemeral: true);
    }

    // ================= Создание тикета =================

    [ComponentInteraction("ticket:project")]
    public async Task OnProjectSelected(string[] values)
    {
        var project = store.FindProject(Context.Guild.Id, values[0]);
        if (project is null)
        {
            await RespondAsync(T("ticket.projectGone"), ephemeral: true);
            return;
        }

        if (tickets.OpenTicket(Context.Guild, Context.User.Id, project) is { } open)
        {
            await RespondAsync(T("ticket.alreadyOpen", ("project", project.Name), ("channel", $"<#{open.TextChannelId}>")), ephemeral: true);
            return;
        }

        // Анкета включена — сначала окно с вопросами, тикет создаётся после отправки
        if (project.FormEnabled && project.Questions.Count > 0)
        {
            await RespondWithModalAsync(TicketRenderer.FormModal(Context.Guild.Id, project));
            return;
        }

        await DeferAsync(ephemeral: true);
        var (ticket, error) = await tickets.CreateAsync(Context.Guild, (IGuildUser)Context.User, project, [], "discord");
        await Component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = ticket is not null ? T("ticket.created", ("channel", $"<#{ticket.TextChannelId}>")) : $"❌ {error}";
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ModalInteraction("ticket:form:*")]
    public async Task OnFormSubmitted(string projectKey, TicketFormModal modal)
    {
        var project = store.FindProject(Context.Guild.Id, projectKey);
        if (project is null)
        {
            await RespondAsync(T("ticket.projectGone"), ephemeral: true);
            return;
        }

        var data = ((SocketModal)Context.Interaction).Data.Components;
        var answers = project.Questions.Take(TicketQuestion.MaxQuestions)
            .Select((q, i) => new TicketAnswer
            {
                Question = q.Label,
                Answer = data.FirstOrDefault(c => c.CustomId == $"q{i}")?.Value ?? ""
            })
            .ToList();

        await DeferAsync(ephemeral: true);
        var (ticket, error) = await tickets.CreateAsync(Context.Guild, (IGuildUser)Context.User, project, answers, "discord");
        await FollowupAsync(ticket is not null ? T("ticket.created", ("channel", $"<#{ticket.TextChannelId}>")) : $"❌ {error}", ephemeral: true);
    }

    // ================= Approve =================

    [ComponentInteraction("ticket:approve:*")]
    public async Task OnApprove(string ticketId)
    {
        var ticket = FindOwnTicket(ticketId);
        if (ticket is null)
        {
            await RespondAsync(T("ticket.gone"), ephemeral: true);
            return;
        }

        var project = store.FindProject(ticket.GuildId, ticket.ProjectKey);
        if (project is null)
        {
            await RespondAsync(T("ticket.projectDeleted"), ephemeral: true);
            return;
        }

        if (!IsStaff(project))
        {
            await RespondAsync(T("ticket.onlyStaffApprove", ("project", project.Name)), ephemeral: true);
            return;
        }

        if (ticket.ApprovedBy is not null)
        {
            await RespondAsync(T("ticket.alreadyApproved"), ephemeral: true);
            return;
        }

        var roleIds = project.MemberRoleIds.ToList();
        if (roleIds.Count == 0)
        {
            await RespondAsync(T("ticket.noRoles"), ephemeral: true);
            return;
        }

        await DeferAsync();

        var applicant = await Context.Client.Rest.GetGuildUserAsync(ticket.GuildId, ticket.UserId);
        if (applicant is null)
        {
            await FollowupAsync(T("ticket.applicantLeft"), ephemeral: true);
            return;
        }

        // Выдаём по одной, чтобы при ошибке знать, какая роль не выдалась
        var failed = new List<ulong>();
        foreach (var roleId in roleIds)
        {
            try
            {
                await applicant.AddRoleAsync(roleId);
            }
            catch (Discord.Net.HttpException)
            {
                failed.Add(roleId);
            }
        }

        if (failed.Count == roleIds.Count)
        {
            await FollowupAsync(T("ticket.rolesFailed", ("roles", Mentions(failed))), ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        var updated = store.UpdateTicket(ticketId, t => t.ApprovedBy = Context.User.Id) ?? ticket;
        audit.FromDiscord(Context.Guild.Id, Context.User, "tickets", "Approved ticket",
            $"{project.Name} · applicant {(Context.Guild.GetUser(ticket.UserId)?.DisplayName ?? ticket.UserId.ToString())}");
        var (embeds, comps) = TicketRenderer.TicketMessage(updated, project);
        await Component.ModifyOriginalResponseAsync(m =>
        {
            m.Embeds = embeds;
            m.Components = comps;
        });

        var granted = roleIds.Except(failed).ToList();
        await FollowupAsync(T("ticket.approvedMsg", ("applicant", $"<@{ticket.UserId}>"), ("by", $"<@{Context.User.Id}>"), ("roles", Mentions(granted))),
            allowedMentions: new AllowedMentions { UserIds = [ticket.UserId] });

        // Не все роли выдались — тикет не закрываем, чтобы руководство могло исправить и выдать вручную
        if (failed.Count > 0)
        {
            await FollowupAsync(T("ticket.rolesPartlyFailed", ("roles", Mentions(failed))),
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        await FollowupAsync(T("ticket.closingIn", ("seconds", (int)ApproveCloseDelay.TotalSeconds)));
        await NotifyApprovedAsync(applicant, project, granted);
        _ = CloseAfterDelayAsync(updated, Context.User, "approved", null);
    }

    /// <summary>Сколько ждать после Approve перед удалением каналов — чтобы все успели увидеть сообщение.</summary>
    private static readonly TimeSpan ApproveCloseDelay = TimeSpan.FromSeconds(15);

    /// <summary>ЛС заявителю: каналы тикета сейчас исчезнут, а итог должен остаться у него.</summary>
    private async Task NotifyApprovedAsync(IUser applicant, TicketProject project, IEnumerable<ulong> roleIds)
    {
        var roles = string.Join(", ", roleIds.Select(id => Context.Guild.GetRole(id)?.Name ?? "role").Select(Format.Sanitize));
        try
        {
            await applicant.SendMessageAsync(T("ticket.dm.approved", ("project", Format.Sanitize(project.Name)),
                ("server", Format.Sanitize(Context.Guild.Name)), ("roles", roles)));
        }
        catch (Discord.Net.HttpException)
        {
            // у человека закрыты ЛС — ничего страшного
        }
    }

    private async Task CloseAfterDelayAsync(Ticket ticket, IUser by, string outcome, string? reason)
    {
        try
        {
            await Task.Delay(ApproveCloseDelay);
            await tickets.CloseAsync(ticket, outcome, by, reason); // в архив + удалить каналы
        }
        catch (Exception)
        {
            // каналы могли удалить вручную — тикет уже убран из списка
        }
    }

    private static string Mentions(IEnumerable<ulong> roleIds) => string.Join(", ", roleIds.Select(r => $"<@&{r}>"));

    // ================= Close =================

    [ComponentInteraction("ticket:close:*")]
    public async Task OnClose(string ticketId)
    {
        if (FindOwnTicket(ticketId) is null)
        {
            await RespondAsync(T("ticket.gone"), ephemeral: true);
            return;
        }

        var buttons = new ComponentBuilder()
            .WithButton(T("ticket.close.confirm"), $"ticket:closeok:{ticketId}", ButtonStyle.Danger, new Emoji("🗑️"))
            .Build();

        await RespondAsync(T("ticket.close.question"),
            components: buttons, ephemeral: true);
    }

    [ComponentInteraction("ticket:closeok:*")]
    public async Task OnCloseConfirmed(string ticketId)
    {
        var ticket = FindOwnTicket(ticketId);
        if (ticket is null)
        {
            await Component.UpdateAsync(m =>
            {
                m.Content = T("ticket.close.already");
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        await Component.UpdateAsync(m =>
        {
            m.Content = T("ticket.close.closing");
            m.Components = new ComponentBuilder().Build();
        });

        audit.FromDiscord(Context.Guild.Id, Context.User, "tickets", "Closed ticket",
            $"{store.FindProject(ticket.GuildId, ticket.ProjectKey)?.Name ?? "Deleted project"} · applicant " +
            (Context.Guild.GetUser(ticket.UserId)?.DisplayName ?? ticket.UserId.ToString()));
        await tickets.CloseAsync(ticket, "closed", Context.User, null);
    }

    // ================= Deny =================

    [ComponentInteraction("ticket:deny:*")]
    public async Task OnDeny(string ticketId)
    {
        var ticket = FindOwnTicket(ticketId);
        var project = ticket is null ? null : store.FindProject(ticket.GuildId, ticket.ProjectKey);
        if (ticket is null || project is null)
        {
            await RespondAsync(T("ticket.gone"), ephemeral: true);
            return;
        }
        if (!IsStaff(project))
        {
            await RespondAsync(T("ticket.onlyStaffDeny", ("project", project.Name)), ephemeral: true);
            return;
        }
        if (ticket.ApprovedBy is not null)
        {
            await RespondAsync(T("ticket.alreadyApproved"), ephemeral: true);
            return;
        }
        await RespondWithModalAsync(TicketRenderer.DenyModal(Context.Guild.Id, ticket.Id));
    }

    [ModalInteraction("ticket:denyok:*")]
    public async Task OnDenyConfirmed(string ticketId, TicketDenyModal modal)
    {
        var ticket = FindOwnTicket(ticketId);
        var project = ticket is null ? null : store.FindProject(ticket.GuildId, ticket.ProjectKey);
        if (ticket is null || project is null || !IsStaff(project))
        {
            await RespondAsync(T("ticket.gone"), ephemeral: true);
            return;
        }

        var reason = ((SocketModal)Context.Interaction).Data.Components.FirstOrDefault(c => c.CustomId == "reason")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(reason)) reason = null;

        await RespondAsync(T("ticket.deniedMsg", ("applicant", $"<@{ticket.UserId}>"), ("by", $"<@{Context.User.Id}>")) +
                           (reason is null ? "" : "\n" + T("ticket.reason", ("reason", Format.Sanitize(reason)))) +
                           "\n" + T("ticket.closingIn", ("seconds", (int)ApproveCloseDelay.TotalSeconds)),
            allowedMentions: new AllowedMentions { UserIds = [ticket.UserId] });
        audit.FromDiscord(Context.Guild.Id, Context.User, "tickets", "Denied ticket",
            $"{project.Name} · applicant {(Context.Guild.GetUser(ticket.UserId)?.DisplayName ?? ticket.UserId.ToString())}" +
            (reason is null ? "" : $" · {reason}"));
        await tickets.NotifyDeniedAsync(ticket, project, reason);
        _ = CloseAfterDelayAsync(ticket, Context.User, "denied", reason);
    }

    // ================= Хелперы =================

    private bool IsStaff(TicketProject project) =>
        botOwner.Is(Context.User.Id) ||
        (Context.User is IGuildUser user &&
         (user.GuildPermissions.Administrator || user.RoleIds.Any(id => project.StaffRoleIds.Contains(id))));

    /// <summary>
    /// Тикет этого сервера, и кнопка нажата внутри его текстового канала.
    /// Иначе null — бот ничего не делает с чужими каналами.
    /// </summary>
    private Ticket? FindOwnTicket(string ticketId)
    {
        var ticket = store.FindTicket(ticketId);
        if (ticket is null || ticket.GuildId != Context.Guild.Id) return null;
        return Context.Interaction.ChannelId == ticket.TextChannelId ? ticket : null;
    }

    /// <summary>Удаляет только канал, созданный для этого тикета, и только на этом сервере.</summary>
    private async Task DeleteTicketChannelAsync(Ticket ticket, ulong channelId)
    {
        if (channelId != ticket.TextChannelId && channelId != ticket.VoiceChannelId) return;
        try
        {
            var channel = Context.Guild.GetChannel(channelId);
            if (channel is not null && channel.Guild.Id == ticket.GuildId) await channel.DeleteAsync();
        }
        catch (Discord.Net.HttpException)
        {
            // канал уже удалён
        }
    }

    private async Task<IMessageChannel?> GetChannelAsync(ulong channelId)
    {
        if (Context.Client.GetChannel(channelId) is IMessageChannel cached) return cached;
        try
        {
            return await Context.Client.Rest.GetChannelAsync(channelId) as IMessageChannel;
        }
        catch (Discord.Net.HttpException)
        {
            return null;
        }
    }
}

// Discord.Net registers a [ModalInteraction] handler only when it takes an IModal parameter.
// The windows themselves are built in TicketRenderer; the fields are read from the raw submission.

/// <summary>Окно причины отказа (TicketRenderer.DenyModal).</summary>
public sealed class TicketDenyModal : IModal
{
    public string Title => "Deny the application";

    [ModalTextInput("reason", TextInputStyle.Paragraph)]
    public string? Reason { get; set; }
}

/// <summary>Анкета проекта (TicketRenderer.FormModal), поля q0..q4 — каких нет, остаются пустыми.</summary>
public sealed class TicketFormModal : IModal
{
    public string Title => "Application";

    [ModalTextInput("q0")] public string? Q0 { get; set; }
    [ModalTextInput("q1")] public string? Q1 { get; set; }
    [ModalTextInput("q2")] public string? Q2 { get; set; }
    [ModalTextInput("q3")] public string? Q3 { get; set; }
    [ModalTextInput("q4")] public string? Q4 { get; set; }
}
