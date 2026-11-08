using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Публикует «страницу канала» — упорядоченный набор блоков (текст, панель ролей, панель модуля).
/// Повторная публикация в тот же канал — «обновление»: неизменённые блоки не трогаются (реакции на панелях сохраняются),
/// изменённый текст правится на месте, удалённые блоки удаляются, новые в конце дописываются. Перепостится только
/// «хвост», начиная с места, где порядок нельзя сохранить (вставка в середину, перестановка). Панели ролей/модулей
/// при правке в своих разделах обновляются сами.
/// </summary>
public sealed class ChannelPageService(DiscordSocketClient client, ChannelPageStore store, AnnouncementService announce,
    CommunityStore community, CommunityService roles, PanelService panels, PanelTextStore panelTexts, ILogger<ChannelPageService> logger)
{
    public static readonly string[] ModuleKeys = ["lfg", "tickets", "rooms"];

    /// <summary>План публикации: что останется, что поправится на месте, что перепостится.</summary>
    public sealed record PublishPlan(bool Full, List<string> Keep, List<string> Edit, List<string> Repost, int Removed);

    private static string? KeyOf(PageBlock b) => b.Type switch
    {
        "text" => b.Text ?? "",
        "rolepanel" => b.RolePanelId,
        "modulepanel" => b.ModuleKey,
        _ => null
    };

    public async Task<PublishPlan> PlanAsync(ChannelPage page, ulong channelId, bool forceFull = false)
    {
        var all = page.Blocks.Select(b => b.Id).ToList();
        if (forceFull || page.ChannelId != channelId || page.PostedMessageIds.Count == 0)
            return new PublishPlan(true, [], [], all, 0);

        await AdoptAsync(page);

        var current = page.Blocks.SelectMany(b => b.MessageIds).ToHashSet();
        var removed = page.PostedMessageIds.Count(id => !current.Contains(id));
        List<string> keep = [], edit = [], repost = [];
        ulong last = 0;
        var broken = false;
        foreach (var b in page.Blocks)
        {
            if (!broken && await CanKeepAsync(page, b, last) is { } mode)
            {
                (mode == "edit" ? edit : keep).Add(b.Id);
                last = b.MessageIds.Max();
                continue;
            }
            broken = true;
            repost.Add(b.Id);
        }
        return new PublishPlan(false, keep, edit, repost, removed);
    }

    /// <summary>
    /// Восстановить привязку блоков к уже опубликованным сообщениям (страницы, опубликованные до появления «обновления»,
    /// или блоки, которые старый редактор пересоздал при сохранении): панели — по их сообщению в канале, тексты — по порядку.
    /// </summary>
    private async Task AdoptAsync(ChannelPage page)
    {
        var posted = page.PostedMessageIds.ToHashSet();
        var used = page.Blocks.SelectMany(b => b.MessageIds).ToHashSet();
        foreach (var b in page.Blocks.Where(b => b.MessageIds.Count == 0))
        {
            ulong? mid = b.Type switch
            {
                "rolepanel" => community.GetPanels(page.GuildId).FirstOrDefault(p => p.Id == b.RolePanelId) is { } rp
                    && rp.ChannelId == page.ChannelId && rp.MessageId is { } m && posted.Contains(m) && !used.Contains(m) ? m : null,
                "modulepanel" when b.ModuleKey is { } key => panelTexts.GetPosted(page.GuildId, key)
                    .Where(pp => pp.ChannelId == page.ChannelId && posted.Contains(pp.MessageId) && !used.Contains(pp.MessageId))
                    .Select(pp => (ulong?)pp.MessageId).FirstOrDefault(),
                _ => null
            };
            if (mid is not { } id) continue;
            b.MessageIds = [id];
            b.PostedKey = KeyOf(b);
            used.Add(id);
        }

        // Тексты: свободные сообщения страницы между соседними привязанными блоками, по порядку
        var free = page.PostedMessageIds.Where(id => !used.Contains(id)).OrderBy(id => id).ToList();
        ulong last = 0;
        for (var i = 0; i < page.Blocks.Count; i++)
        {
            var b = page.Blocks[i];
            if (b.MessageIds.Count > 0) { last = b.MessageIds.Max(); continue; }
            if (b.Type != "text" || string.IsNullOrWhiteSpace(b.Text)) continue;
            var next = page.Blocks.Skip(i + 1).Where(x => x.MessageIds.Count > 0).Select(x => x.MessageIds.Min()).DefaultIfEmpty(ulong.MaxValue).First();
            var count = PostSplitter.Split(await announce.ResolveEmotesAsync(page.GuildId, b.Text)).Count;
            var candidates = free.Where(id => id > last && id < next).Take(count).ToList();
            if (candidates.Count != count) continue;
            b.MessageIds = candidates;
            b.PostedKey = null; // содержимое сверится с сообщениями при планировании
            free.RemoveAll(candidates.Contains);
            last = candidates.Max();
        }
    }

    /// <summary>"keep" — блок не меняется, "edit" — текст правится на месте, null — нужен перепост.</summary>
    private async Task<string?> CanKeepAsync(ChannelPage page, PageBlock b, ulong after)
    {
        if (b.MessageIds.Count == 0 || b.MessageIds.Min() <= after) return null; // новый блок или нарушен порядок
        switch (b.Type)
        {
            case "text":
                if (b.PostedKey is not null && b.PostedKey == (b.Text ?? "")) return "keep";
                if (string.IsNullOrWhiteSpace(b.Text)) return null;
                var parts = PostSplitter.Split(await announce.ResolveEmotesAsync(page.GuildId, b.Text));
                if (parts.Count != b.MessageIds.Count) return null; // сообщений стало больше/меньше — на месте не поправить
                if (b.PostedKey is null && await SameContentAsync(page, b, parts))
                {
                    b.PostedKey = b.Text; // старая публикация без отметки: текст совпадает — не трогаем
                    return "keep";
                }
                return "edit";
            case "rolepanel":
                if (b.PostedKey is not null && b.PostedKey != b.RolePanelId) return null;
                var panel = community.GetPanels(page.GuildId).FirstOrDefault(p => p.Id == b.RolePanelId);
                // панель живёт в своём модуле; если её перепостили отдельно — сообщение уже другое
                return panel is not null && panel.ChannelId == page.ChannelId && panel.MessageId == b.MessageIds[0] ? "keep" : null;
            case "modulepanel":
                return b.PostedKey is null || b.PostedKey == b.ModuleKey ? "keep" : null;
            default:
                return null;
        }
    }

    private async Task<bool> SameContentAsync(ChannelPage page, PageBlock b, List<string> parts)
    {
        if (page.ChannelId is not { } cid || client.GetGuild(page.GuildId)?.GetTextChannel(cid) is not { } channel) return false;
        try
        {
            for (var i = 0; i < parts.Count; i++)
                if (await channel.GetMessageAsync(b.MessageIds[i]) is not IUserMessage m || m.Content.Trim() != MediaService.StripTokens(parts[i]).Trim()) return false;
            return true;
        }
        catch (Discord.Net.HttpException) { return false; }
    }

    /// <summary>Опубликовать или обновить страницу. full — удалить всё и выложить заново. Возвращает текст ошибки или null.</summary>
    public async Task<string?> PublishAsync(string pageId, ulong channelId, bool full = false)
    {
        if (store.Find(pageId) is not { } page) return "Page not found.";
        if (client.GetGuild(page.GuildId) is not { } guild) return "Server unavailable.";
        if (guild.GetTextChannel(channelId) is not { } channel) return "Channel not found — choose a text channel.";
        if (page.Blocks.Count == 0) return "Add at least one block first.";
        var perms = guild.CurrentUser.GetPermissions(channel);
        if (!perms.ViewChannel || !perms.SendMessages || !perms.EmbedLinks)
            return "I can't post there. Give the bot View Channel, Send Messages and Embed Links in that channel.";

        var plan = await PlanAsync(page, channelId, full);
        var blocks = page.Blocks.ToDictionary(b => b.Id);

        if (plan.Full)
        {
            await DeleteAsync(page.GuildId, page.ChannelId, page.PostedMessageIds.Count > 0
                ? page.PostedMessageIds : page.Blocks.SelectMany(b => b.MessageIds).ToList());
        }
        else
        {
            // Сообщения удалённых блоков и «хвоста», который перепостится
            var current = page.Blocks.SelectMany(b => b.MessageIds).ToHashSet();
            var toDelete = page.PostedMessageIds.Where(id => !current.Contains(id))
                .Concat(plan.Repost.SelectMany(id => blocks[id].MessageIds)).ToList();
            await DeleteAsync(page.GuildId, channelId, toDelete);

            foreach (var id in plan.Edit)
            {
                var b = blocks[id];
                try
                {
                    var parts = PostSplitter.Split(await announce.ResolveEmotesAsync(page.GuildId, b.Text!));
                    for (var i = 0; i < parts.Count; i++)
                    {
                        if (await channel.GetMessageAsync(b.MessageIds[i]) is not IUserMessage msg) throw new InvalidOperationException("gone");
                        await announce.EditPartAsync(page.GuildId, msg, parts[i], AllowedMentions.None);
                    }
                    b.PostedKey = b.Text;
                }
                catch (Exception ex) when (ex is Discord.Net.HttpException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Channel page {Page}: in-place edit of block {Block} failed", page.Id, b.Id);
                    return "A message of this page was deleted in Discord — use “Re-post all”.";
                }
            }
        }

        foreach (var id in plan.Repost)
        {
            var b = blocks[id];
            try
            {
                b.MessageIds = await PostBlockAsync(guild, channel, b);
                b.PostedKey = KeyOf(b);
            }
            catch (Discord.Net.HttpException ex)
            {
                b.MessageIds = [];
                logger.LogWarning(ex, "Channel page {Page}: block {Block} failed", page.Id, b.Id);
            }
        }

        store.Update(pageId, p =>
        {
            p.ChannelId = channelId;
            foreach (var b in p.Blocks)
                if (blocks.TryGetValue(b.Id, out var nb)) { b.MessageIds = nb.MessageIds; b.PostedKey = nb.PostedKey; }
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
        await DeleteAsync(page.GuildId, page.ChannelId, page.PostedMessageIds.Count > 0
            ? page.PostedMessageIds : page.Blocks.SelectMany(b => b.MessageIds).ToList());
        store.Update(pageId, p =>
        {
            p.ChannelId = null;
            p.PostedMessageIds = [];
            foreach (var b in p.Blocks) { b.MessageIds = []; b.PostedKey = null; }
        });
    }

    private async Task DeleteAsync(ulong guildId, ulong? channelId, IEnumerable<ulong> ids)
    {
        if (channelId is not { } cid || client.GetGuild(guildId)?.GetTextChannel(cid) is not { } channel) return;
        foreach (var id in ids.Distinct())
        {
            try { await channel.DeleteMessageAsync(id); }
            catch (Discord.Net.HttpException) { /* уже удалено */ }
        }
    }
}
