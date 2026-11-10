using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Публикует панели модулей (LFG, Tickets, Room controls) со своими текстами и запоминает сообщения,
/// чтобы после смены текста в веб-админке обновить уже опубликованные панели.
/// </summary>
public sealed class PanelService(DiscordSocketClient client, PanelTextStore store, TimeService time, LfgStore lfg, AuditLog audit, ILogger<PanelService> logger)
{
    /// <summary>Project of an LFG panel nobody linked: the project most of the active groups in its channel belong to.</summary>
    public string? GuessLfgProject(ulong guildId, ulong channelId) =>
        lfg.Groups.Values.Where(g => g.GuildId == guildId && g.ChannelId == channelId && g.ProjectKey is not null)
            .GroupBy(g => g.ProjectKey!).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();

    /// <summary>An LFG panel message the bot doesn't remember (its record was lost): remember it again, with a guessed project.</summary>
    public PostedPanel? Adopt(ulong guildId, ulong channelId, ulong messageId)
    {
        if (store.Track(guildId, "lfg", channelId, messageId) && GuessLfgProject(guildId, channelId) is { } project)
            store.SetPanelProject(guildId, "lfg", messageId, project);
        return store.FindPosted(guildId, "lfg", messageId);
    }

    public (Embed Embed, MessageComponent Components) Build(ulong guildId, string key)
    {
        var text = store.GetText(guildId, key);
        if (key == "lfg")
        {
            var screen = LfgRenderer.Panel(time.AutoDeleteHours, text, time.ReminderMinutes);
            return (screen.Embed!, screen.Components);
        }

        return key switch
        {
            "tickets" => TicketRenderer.Panel(text),
            "rooms" => TempRoomManager.Panel(text),
            _ => throw new ArgumentException($"Unknown panel '{key}'", nameof(key))
        };
    }

    /// <summary>Отправляет панель в канал и запоминает сообщение.</summary>
    public async Task<IUserMessage> PostAsync(IMessageChannel channel, ulong guildId, string key)
    {
        var (embed, comps) = Build(guildId, key);
        var message = await channel.SendMessageAsync(embed: embed, components: comps);
        store.Track(guildId, key, channel.Id, message.Id);
        return message;
    }

    /// <summary>Перерисовывает запомненные панели. Удалённые сообщения забываются. Возвращает (обновлено, всего).</summary>
    public async Task<(int Updated, int Total)> RefreshAsync(ulong guildId, string key)
    {
        var posted = store.GetPosted(guildId, key);
        var (embed, comps) = Build(guildId, key);
        var updated = 0;

        foreach (var p in posted)
        {
            try
            {
                // Server or channel not in the cache (bot still connecting, Discord hiccup) — keep the record, try next time.
                // Only Discord saying "this message is gone" makes us forget a panel.
                if (client.GetGuild(guildId)?.GetTextChannel(p.ChannelId) is not { } channel) continue;

                if (await channel.GetMessageAsync(p.MessageId) is not IUserMessage message || message.Author.Id != client.CurrentUser.Id)
                {
                    if (!SafetyValve.Allow(guildId, "forget-panel", 3, TimeSpan.FromHours(1), "forgetting posted panels whose messages Discord reports as gone")) continue;
                    store.Untrack(guildId, key, p.MessageId);
                    audit.FromSystem(guildId, key, "Forgot a panel — its message was deleted in Discord", $"#{channel.Name}");
                    continue;
                }

                await message.ModifyAsync(m =>
                {
                    m.Embeds = new[] { embed };
                    m.Components = comps;
                });
                updated++;
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogWarning(ex, "Failed to refresh panel {Key} message {MessageId}", key, p.MessageId);
            }
        }

        return (updated, store.GetPosted(guildId, key).Count);
    }

    /// <summary>
    /// Ищет панели, опубликованные раньше (до запоминания): последние сообщения бота в текстовых каналах
    /// с кнопкой панели. Возвращает число новых найденных по каждой панели.
    /// </summary>
    /// <summary>Custom ids of all buttons and menus of a message.</summary>
    public static HashSet<string> CustomIds(IMessage message) => CustomIds(message.Components);

    public static HashSet<string> CustomIds(IEnumerable<IMessageComponent> components) => components
        .OfType<ActionRowComponent>()
        .SelectMany(r => r.Components)
        .Select(c => c switch { ButtonComponent b => b.CustomId, SelectMenuComponent s => s.CustomId, _ => null })
        .OfType<string>()
        .ToHashSet();

    /// <summary>A bot message with a module panel's buttons that isn't remembered — remember it (LFG: with a guessed project).</summary>
    public void Consider(ulong guildId, ulong channelId, ulong messageId, HashSet<string> ids, Dictionary<string, int> found)
    {
        foreach (var def in PanelDefs.All)
            if (ids.Contains(def.MarkerCustomId) && store.Track(guildId, def.Key, channelId, messageId))
            {
                found[def.Key] = found.GetValueOrDefault(def.Key) + 1;
                if (def.Key == "lfg" && GuessLfgProject(guildId, channelId) is { } project)
                    store.SetPanelProject(guildId, "lfg", messageId, project);
            }
    }

    public async Task<Dictionary<string, int>> DiscoverAsync(SocketGuild guild, int messagesPerChannel = 50)
    {
        var found = PanelDefs.All.ToDictionary(d => d.Key, _ => 0);
        var me = guild.CurrentUser;

        foreach (var channel in guild.TextChannels.Where(c => c is not SocketThreadChannel))
        {
            var perms = me.GetPermissions(channel);
            if (!perms.ViewChannel || !perms.ReadMessageHistory) continue;

            try
            {
                var messages = await channel.GetMessagesAsync(messagesPerChannel).FlattenAsync();
                foreach (var message in messages.OfType<IUserMessage>().Where(m => m.Author.Id == client.CurrentUser.Id))
                {
                    Consider(guild.Id, channel.Id, message.Id, CustomIds(message), found);
                }
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogDebug(ex, "Can't read history of #{Channel}", channel.Name);
            }
        }

        return found;
    }
}
