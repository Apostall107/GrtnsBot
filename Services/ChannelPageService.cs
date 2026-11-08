using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Публикует «страницу канала» — упорядоченный набор блоков (текст, панель ролей, панель модуля) —
/// в один канал как единое целое: удаляет прошлые сообщения страницы и постит блоки по порядку.
/// Интеракции панелей обрабатывают их собственные модули (по MessageId), сюда только оркестрация.
/// </summary>
public sealed class ChannelPageService(DiscordSocketClient client, ChannelPageStore store, AnnouncementService announce,
    CommunityStore community, CommunityService roles, PanelService panels, ILogger<ChannelPageService> logger)
{
    public static readonly string[] ModuleKeys = ["lfg", "tickets", "rooms"];

    /// <summary>Опубликовать страницу в канал. Возвращает текст ошибки или null.</summary>
    public async Task<string?> PublishAsync(string pageId, ulong channelId)
    {
        if (store.Find(pageId) is not { } page) return "Page not found.";
        if (client.GetGuild(page.GuildId) is not { } guild) return "Server unavailable.";
        if (guild.GetTextChannel(channelId) is not { } channel)
            return "Channel not found — choose a text channel.";
        if (page.Blocks.Count == 0) return "Add at least one block first.";

        // Снять прошлую публикацию (из любого канала)
        await DeletePostedAsync(page);

        var botCanPost = channel.GetPermissionOverwrite(guild.CurrentUser) is not { ViewChannel: PermValue.Deny }
            && guild.CurrentUser.GetPermissions(channel).SendMessages;
        if (!botCanPost) return "I can't post there. Give the bot View Channel, Send Messages and Embed Links in that channel.";

        var postedIds = new Dictionary<string, List<ulong>>();
        foreach (var block in page.Blocks)
        {
            try
            {
                var ids = await PostBlockAsync(guild, channel, block);
                postedIds[block.Id] = ids;
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogWarning(ex, "Channel page {Page}: block {Block} failed", page.Id, block.Id);
            }
        }

        store.Update(pageId, p =>
        {
            p.ChannelId = channelId;
            foreach (var b in p.Blocks)
                b.MessageIds = postedIds.TryGetValue(b.Id, out var ids) ? ids : [];
            p.PostedMessageIds = p.Blocks.SelectMany(b => b.MessageIds).ToList();
        });
        return null;
    }

    private async Task<List<ulong>> PostBlockAsync(SocketGuild guild, SocketTextChannel channel, PageBlock block)
    {
        switch (block.Type)
        {
            case "text":
                return string.IsNullOrWhiteSpace(block.Text) ? [] : await announce.PostTextAsync(guild.Id, channel, block.Text);

            case "rolepanel":
                if (community.GetPanels(guild.Id).FirstOrDefault(p => p.Id == block.RolePanelId) is not { } panel) return [];
                await roles.PostAsync(panel, channel.Id);
                return community.GetPanels(guild.Id).FirstOrDefault(p => p.Id == block.RolePanelId)?.MessageId is { } mid ? [mid] : [];

            case "modulepanel":
                if (!ModuleKeys.Contains(block.ModuleKey)) return [];
                var message = await panels.PostAsync(channel, guild.Id, block.ModuleKey!);
                return [message.Id];

            default:
                return [];
        }
    }

    /// <summary>Убрать опубликованную страницу: удалить все её сообщения и забыть канал.</summary>
    public async Task UnpublishAsync(string pageId)
    {
        if (store.Find(pageId) is not { } page) return;
        await DeletePostedAsync(page);
        store.Update(pageId, p =>
        {
            p.ChannelId = null;
            p.PostedMessageIds = [];
            foreach (var b in p.Blocks) b.MessageIds = [];
        });
    }

    private async Task DeletePostedAsync(ChannelPage page)
    {
        if (page.ChannelId is not { } channelId || client.GetGuild(page.GuildId)?.GetTextChannel(channelId) is not { } channel) return;
        var ids = page.PostedMessageIds.Count > 0 ? page.PostedMessageIds : page.Blocks.SelectMany(b => b.MessageIds).ToList();
        foreach (var id in ids)
        {
            try { await channel.DeleteMessageAsync(id); }
            catch (Discord.Net.HttpException) { /* уже удалено */ }
        }
    }
}
