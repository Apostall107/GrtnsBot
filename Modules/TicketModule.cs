using System.Collections.Concurrent;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;

namespace LfgBot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class TicketModule(TicketStore store, BotOwner botOwner, PanelService panels) : InteractionModuleBase<SocketInteractionContext>
{
    // Защита от двойного клика: пока создаётся тикет, второй не создаём
    private static readonly ConcurrentDictionary<string, byte> Creating = new();

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

        var lockKey = $"{Context.Guild.Id}:{Context.User.Id}:{project.Key}";
        if (!Creating.TryAdd(lockKey, 0))
        {
            await RespondAsync("Your ticket is already being created…", ephemeral: true);
            return;
        }

        try
        {
            var existing = store.FindOpenTicket(Context.Guild.Id, Context.User.Id, project.Key);
            if (existing is not null)
            {
                if (Context.Guild.GetTextChannel(existing.TextChannelId) is not null)
                {
                    await RespondAsync($"You already have an open {project.Name} ticket: <#{existing.TextChannelId}>",
                        ephemeral: true);
                    return;
                }

                store.RemoveTicket(existing.Id); // каналы удалили вручную — тикет устарел
            }

            await DeferAsync(ephemeral: true);

            string result;
            try
            {
                var ticket = await CreateTicketAsync(project);
                result = $"✅ Ticket created: <#{ticket.TextChannelId}>";
            }
            catch (Discord.Net.HttpException ex)
            {
                result = "❌ I couldn't create the ticket channels. Give the bot **Manage Channels** and **Manage Roles** " +
                         $"(Discord error: {ex.DiscordCode?.ToString() ?? ex.HttpCode.ToString()}).";
            }

            await Component.ModifyOriginalResponseAsync(m =>
            {
                m.Content = result;
                m.Components = new ComponentBuilder().Build();
            });
        }
        finally
        {
            Creating.TryRemove(lockKey, out _);
        }
    }

    private async Task<Ticket> CreateTicketAsync(TicketProject project)
    {
        var guild = Context.Guild;
        var user = (IGuildUser)Context.User;
        var categoryId = await EnsureCategoryAsync(project);
        var overwrites = new Optional<IEnumerable<Overwrite>>(BuildOverwrites(project, user.Id));

        var text = await guild.CreateTextChannelAsync(TicketRenderer.TextChannelName(project, user), p =>
        {
            p.CategoryId = categoryId;
            p.PermissionOverwrites = overwrites;
            p.Topic = $"{project.Name} application — {user.Username}";
        });

        IVoiceChannel voice;
        try
        {
            voice = await guild.CreateVoiceChannelAsync(TicketRenderer.VoiceChannelName(project, user), p =>
            {
                p.CategoryId = categoryId;
                p.PermissionOverwrites = overwrites;
            });
        }
        catch
        {
            await text.DeleteAsync(); // не оставляем полутикет
            throw;
        }

        var ticket = new Ticket
        {
            GuildId = guild.Id,
            ProjectKey = project.Key,
            UserId = user.Id,
            TextChannelId = text.Id,
            VoiceChannelId = voice.Id
        };
        store.AddTicket(ticket);

        var (embeds, comps) = TicketRenderer.TicketMessage(ticket, project);
        var pings = $"<@{user.Id}> " + string.Join(" ", project.StaffRoleIds.Select(r => $"<@&{r}>"));
        var message = await text.SendMessageAsync(pings, embeds: embeds, components: comps,
            allowedMentions: new AllowedMentions { UserIds = [user.Id], RoleIds = project.StaffRoleIds.ToList() });
        store.UpdateTicket(ticket.Id, t => t.MessageId = message.Id);

        return ticket;
    }

    /// <summary>Категория проекта, иначе общая «Tickets» (создаётся скрытой при первом тикете).</summary>
    private async Task<ulong?> EnsureCategoryAsync(TicketProject project)
    {
        if (project.CategoryId is { } id && Context.Guild.GetCategoryChannel(id) is not null)
            return id;

        var existing = Context.Guild.CategoryChannels.FirstOrDefault(c =>
            c.Name.Equals(TicketRenderer.DefaultCategoryName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;

        var hidden = new Overwrite[]
        {
            new(Context.Guild.EveryoneRole.Id, PermissionTarget.Role,
                OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Deny)),
            new(Context.Client.CurrentUser.Id, PermissionTarget.User,
                OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Allow, manageChannel: PermValue.Allow))
        };
        var created = await Context.Guild.CreateCategoryChannelAsync(TicketRenderer.DefaultCategoryName,
            p => p.PermissionOverwrites = new Optional<IEnumerable<Overwrite>>(hidden));
        return created.Id;
    }

    /// <summary>Канал видят только заявитель, руководство проекта и сам бот.</summary>
    private List<Overwrite> BuildOverwrites(TicketProject project, ulong applicantId)
    {
        var member = OverwritePermissions.InheritAll.Modify(
            viewChannel: PermValue.Allow,
            sendMessages: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            attachFiles: PermValue.Allow,
            embedLinks: PermValue.Allow,
            connect: PermValue.Allow,
            speak: PermValue.Allow);

        var overwrites = new List<Overwrite>
        {
            new(Context.Guild.EveryoneRole.Id, PermissionTarget.Role,
                OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Deny)),
            new(Context.Client.CurrentUser.Id, PermissionTarget.User, member.Modify(manageChannel: PermValue.Allow)),
            new(applicantId, PermissionTarget.User, member)
        };
        overwrites.AddRange(project.StaffRoleIds.Select(roleId => new Overwrite(roleId, PermissionTarget.Role, member)));
        return overwrites;
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
        _ = CloseAfterDelayAsync(updated);
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

    private async Task CloseAfterDelayAsync(Ticket ticket)
    {
        try
        {
            await Task.Delay(ApproveCloseDelay);
            if (store.FindTicket(ticket.Id) is null) return; // уже закрыли вручную
            store.RemoveTicket(ticket.Id);
            await DeleteTicketChannelAsync(ticket, ticket.VoiceChannelId);
            await DeleteTicketChannelAsync(ticket, ticket.TextChannelId);
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

        store.RemoveTicket(ticketId);
        await DeleteTicketChannelAsync(ticket, ticket.VoiceChannelId);
        await DeleteTicketChannelAsync(ticket, ticket.TextChannelId);
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
