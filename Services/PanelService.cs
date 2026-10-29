using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Публикует панели модулей (LFG, Tickets, Room controls) со своими текстами и запоминает сообщения,
/// чтобы после смены текста в веб-админке обновить уже опубликованные панели.
/// </summary>
public sealed class PanelService(DiscordSocketClient client, PanelTextStore store, TimeService time, ILogger<PanelService> logger)
{
    public (Embed Embed, MessageComponent Components) Build(ulong guildId, string key)
    {
        var text = store.GetText(guildId, key);
        if (key == "lfg")
        {
            var screen = LfgRenderer.Panel(time.AutoDeleteHours, text);
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
                if (client.GetGuild(guildId)?.GetTextChannel(p.ChannelId) is not { } channel)
                {
                    store.Untrack(guildId, key, p.MessageId);
                    continue;
                }

                if (await channel.GetMessageAsync(p.MessageId) is not IUserMessage message || message.Author.Id != client.CurrentUser.Id)
                {
                    store.Untrack(guildId, key, p.MessageId);
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
                    var ids = message.Components
                        .OfType<ActionRowComponent>()
                        .SelectMany(r => r.Components)
                        .Select(c => (c as ButtonComponent)?.CustomId)
                        .OfType<string>()
                        .ToHashSet();

                    foreach (var def in PanelDefs.All)
                        if (ids.Contains(def.MarkerCustomId) && store.Track(guild.Id, def.Key, channel.Id, message.Id))
                            found[def.Key]++;
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
