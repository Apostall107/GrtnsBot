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
    private const string NoAccessText =
        "I can't access this channel. Give the bot **View Channel**, **Send Messages** and **Embed Links** here.";

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
            await RespondAsync("No projects yet. A moderator can add them with `/ticket-project add`.", ephemeral: true);
            return;
        }

        await RespondAsync("Choose the project you are applying to:",
            components: TicketRenderer.ProjectPicker(projects), ephemeral: true);
    }

    // ================= Создание тикета =================

    [ComponentInteraction("ticket:project")]
    public async Task OnProjectSelected(string[] values)
    {
        var project = store.FindProject(Context.Guild.Id, values[0]);
        if (project is null)
        {
            await RespondAsync("This project no longer exists.", ephemeral: true);
            return;
        }

        if (tickets.OpenTicket(Context.Guild, Context.User.Id, project) is { } open)
        {
            await RespondAsync($"You already have an open {project.Name} ticket: <#{open.TextChannelId}>", ephemeral: true);
            return;
        }

        // Анкета включена — сначала окно с вопросами, тикет создаётся после отправки
        if (project.FormEnabled && project.Questions.Count > 0)
        {
            await RespondWithModalAsync(TicketRenderer.FormModal(project));
            return;
        }

        await DeferAsync(ephemeral: true);
        var (ticket, error) = await tickets.CreateAsync(Context.Guild, (IGuildUser)Context.User, project, [], "discord");
        await Component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = ticket is not null ? $"✅ Ticket created: <#{ticket.TextChannelId}>" : $"❌ {error}";
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ModalInteraction("ticket:form:*")]
    public async Task OnFormSubmitted(string projectKey)
    {
        var project = store.FindProject(Context.Guild.Id, projectKey);
        if (project is null)
        {
            await RespondAsync("This project no longer exists.", ephemeral: true);
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
        await FollowupAsync(ticket is not null ? $"✅ Ticket created: <#{ticket.TextChannelId}>" : $"❌ {error}", ephemeral: true);
    }

    // ================= Approve =================

    [ComponentInteraction("ticket:approve:*")]
    public async Task OnApprove(string ticketId)
    {
        var ticket = FindOwnTicket(ticketId);
        if (ticket is null)
        {
            await RespondAsync("This ticket is no longer active.", ephemeral: true);
            return;
        }

        var project = store.FindProject(ticket.GuildId, ticket.ProjectKey);
        if (project is null)
        {
            await RespondAsync("This project was deleted.", ephemeral: true);
            return;
        }

        if (!IsStaff(project))
        {
            await RespondAsync($"Only {project.Name} leadership can approve applications.", ephemeral: true);
            return;
        }

        if (ticket.ApprovedBy is not null)
        {
            await RespondAsync("This application is already approved.", ephemeral: true);
            return;
        }

        var roleIds = project.MemberRoleIds.ToList();
        if (roleIds.Count == 0)
        {
            await RespondAsync("This project has no roles to give. Add one with `/ticket-project member-role-add`.", ephemeral: true);
            return;
        }

        await DeferAsync();

        var applicant = await Context.Client.Rest.GetGuildUserAsync(ticket.GuildId, ticket.UserId);
        if (applicant is null)
        {
            await FollowupAsync("The applicant has left the server.", ephemeral: true);
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
            await FollowupAsync(
                $"I couldn't give {Mentions(failed)}. Make sure I have **Manage Roles** and my role is **above** them " +
                "in Server Settings → Roles.", ephemeral: true, allowedMentions: AllowedMentions.None);
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
        await FollowupAsync($"🎉 <@{ticket.UserId}> approved by <@{Context.User.Id}> — {Mentions(granted)} granted.",
            allowedMentions: new AllowedMentions { UserIds = [ticket.UserId] });

        // Не все роли выдались — тикет не закрываем, чтобы руководство могло исправить и выдать вручную
        if (failed.Count > 0)
        {
            await FollowupAsync($"⚠️ Couldn't give {Mentions(failed)} — move my role above them in Server Settings → Roles. " +
                                "The ticket stays open; close it when the roles are sorted out.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        await FollowupAsync($"🔒 This ticket will close in {(int)ApproveCloseDelay.TotalSeconds} seconds.");
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
            await applicant.SendMessageAsync(
                $"✅ Your application to **{Format.Sanitize(project.Name)}** on **{Format.Sanitize(Context.Guild.Name)}** " +
                $"was approved. You got: **{roles}**.");
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
            await RespondAsync("This ticket is no longer active.", ephemeral: true);
            return;
        }

        var buttons = new ComponentBuilder()
            .WithButton("Close ticket", $"ticket:closeok:{ticketId}", ButtonStyle.Danger, new Emoji("🗑️"))
            .Build();

        await RespondAsync("Close this ticket? Both the text and voice channels will be deleted.",
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
                m.Content = "This ticket is already closed.";
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        await Component.UpdateAsync(m =>
        {
            m.Content = "Closing…";
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
            await RespondAsync("This ticket is no longer active.", ephemeral: true);
            return;
        }
        if (!IsStaff(project))
        {
            await RespondAsync($"Only {project.Name} leadership can deny applications.", ephemeral: true);
            return;
        }
        if (ticket.ApprovedBy is not null)
        {
            await RespondAsync("This application is already approved.", ephemeral: true);
            return;
        }
        await RespondWithModalAsync(TicketRenderer.DenyModal(ticket.Id));
    }

    [ModalInteraction("ticket:denyok:*")]
    public async Task OnDenyConfirmed(string ticketId)
    {
        var ticket = FindOwnTicket(ticketId);
        var project = ticket is null ? null : store.FindProject(ticket.GuildId, ticket.ProjectKey);
        if (ticket is null || project is null || !IsStaff(project))
        {
            await RespondAsync("This ticket is no longer active.", ephemeral: true);
            return;
        }

        var reason = ((SocketModal)Context.Interaction).Data.Components.FirstOrDefault(c => c.CustomId == "reason")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(reason)) reason = null;

        await RespondAsync($"❌ <@{ticket.UserId}>'s application was denied by <@{Context.User.Id}>." +
                           (reason is null ? "" : $"\n**Reason:** {Format.Sanitize(reason)}") +
                           $"\n🔒 This ticket will close in {(int)ApproveCloseDelay.TotalSeconds} seconds.",
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
