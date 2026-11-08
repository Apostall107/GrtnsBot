using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Рендер и публикация панелей подпроектов (кнопка на каждую тикет-систему).</summary>
public sealed class TicketPanelService(DiscordSocketClient client, TicketPanelStore panels, TicketStore tickets, ILogger<TicketPanelService> logger)
{
    /// <summary>Тикет-системы панели в её порядке (удалённые пропускаются).</summary>
    public IReadOnlyList<TicketProject> TypesOf(TicketPanel panel)
    {
        var all = tickets.GetProjects(panel.GuildId);
        return panel.TicketKeys.Select(k => all.FirstOrDefault(p => p.Key == k)).OfType<TicketProject>().Take(TicketPanel.MaxTypes).ToList();
    }

    public (Embed Embed, MessageComponent Components) Render(TicketPanel panel)
    {
        var embed = new EmbedBuilder()
            .WithTitle(Tx.Clip(panel.Title, 256))
            .WithColor(Tx.ParseColor(panel.Color) ?? Tx.Color(panel.GuildId, "color.roles.panel"));
        if (!string.IsNullOrWhiteSpace(panel.Text)) embed.WithDescription(Tx.Clip(panel.Text, 4096));
        var comps = new ComponentBuilder();
        var types = TypesOf(panel);
        for (var i = 0; i < types.Count; i++)
            comps.WithButton(Tx.Clip(types[i].Name, 80), $"ticket:open:{types[i].Key}", ButtonStyle.Primary, new Emoji("🎫"), row: i / 5);
        return (embed.Build(), comps.Build());
    }

    /// <summary>Опубликовать панель в канал (старое сообщение удаляется). null — успех, иначе текст ошибки.</summary>
    public async Task<string?> PostAsync(TicketPanel panel, ulong channelId)
    {
        if (client.GetGuild(panel.GuildId)?.GetTextChannel(channelId) is not { } channel) return "Channel not found.";
        if (TypesOf(panel).Count == 0) return "Add at least one ticket type to the panel first.";
        var (embed, comps) = Render(panel);
        IUserMessage message;
        try { message = await channel.SendMessageAsync(embed: embed, components: comps); }
        catch (Discord.Net.HttpException) { return "I can't post there. Give the bot View Channel, Send Messages and Embed Links in that channel."; }

        var (oldChannel, oldMessage) = (panel.ChannelId, panel.MessageId);
        panels.SetPosted(panel.Id, channel.Id, message.Id);
        if (oldChannel is { } oc && oldMessage is { } om && om != message.Id) await DeleteAsync(panel.GuildId, oc, om);
        return null;
    }

    /// <summary>Перерисовать уже опубликованную панель (после правки). Сообщение удалено — панель считается неопубликованной.</summary>
    public async Task RefreshAsync(TicketPanel panel)
    {
        if (panel.ChannelId is not { } channelId || panel.MessageId is not { } messageId) return;
        if (client.GetGuild(panel.GuildId)?.GetTextChannel(channelId) is not { } channel ||
            await channel.GetMessageAsync(messageId) is not IUserMessage message)
        {
            panels.SetPosted(panel.Id, null, null);
            return;
        }
        var (embed, comps) = Render(panel);
        try { await message.ModifyAsync(m => { m.Embeds = new[] { embed }; m.Components = comps; }); }
        catch (Discord.Net.HttpException ex) { logger.LogWarning(ex, "Ticket panel {Panel}: refresh failed", panel.Id); }
    }

    /// <summary>Перерисовать все опубликованные панели сервера (например, после переименования тикет-системы).</summary>
    public async Task RefreshAllAsync(ulong guildId)
    {
        foreach (var p in panels.Get(guildId).Where(p => p.MessageId is not null)) await RefreshAsync(p);
    }

    public async Task DeleteAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        try
        {
            if (client.GetGuild(guildId)?.GetTextChannel(channelId) is { } ch) await ch.DeleteMessageAsync(messageId);
        }
        catch (Discord.Net.HttpException) { /* уже удалено */ }
    }
}
