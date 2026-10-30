using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Перерисовывает сообщения открытых тикетов (например, после смены своего текста проекта).</summary>
public sealed class TicketMessageService(DiscordSocketClient client, TicketStore store, ILogger<TicketMessageService> logger)
{
    /// <summary>Обновляет все открытые тикеты проекта. Возвращает (обновлено, всего).</summary>
    public async Task<(int Updated, int Total)> RefreshProjectAsync(ulong guildId, string projectKey)
    {
        var project = store.FindProject(guildId, projectKey);
        var open = store.GetTickets(guildId).Where(t => t.ProjectKey == projectKey).ToList();
        var updated = 0;

        foreach (var ticket in open)
        {
            try
            {
                if (await RefreshAsync(ticket, project)) updated++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh ticket {TicketId}", ticket.Id);
            }
        }

        return (updated, open.Count);
    }

    public async Task<bool> RefreshAsync(Ticket ticket, TicketProject? project)
    {
        if (client.GetGuild(ticket.GuildId)?.GetTextChannel(ticket.TextChannelId) is not { } channel) return false;

        var message = ticket.MessageId is { } id
            ? await channel.GetMessageAsync(id) as IUserMessage
            : await FindTicketMessageAsync(channel, ticket);
        if (message is null || message.Author.Id != client.CurrentUser.Id) return false;

        if (ticket.MessageId is null) store.UpdateTicket(ticket.Id, t => t.MessageId = message.Id);

        var (embeds, comps) = TicketRenderer.TicketMessage(ticket, project);
        await message.ModifyAsync(m =>
        {
            m.Embeds = embeds;
            m.Components = comps;
        });
        return true;
    }

    /// <summary>Для тикетов, созданных до сохранения MessageId: сообщение бота с кнопкой Approve этого тикета.</summary>
    private async Task<IUserMessage?> FindTicketMessageAsync(ITextChannel channel, Ticket ticket)
    {
        var approveId = $"ticket:approve:{ticket.Id}";
        var messages = await channel.GetMessagesAsync(100).FlattenAsync();
        return messages
            .OfType<IUserMessage>()
            .Where(m => m.Author.Id == client.CurrentUser.Id)
            .FirstOrDefault(m => m.Components
                .OfType<ActionRowComponent>()
                .SelectMany(r => r.Components)
                .OfType<ButtonComponent>()
                .Any(b => b.CustomId == approveId));
    }
}
