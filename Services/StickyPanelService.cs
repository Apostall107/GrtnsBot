using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Sticky-панели (галочка в Panels &amp; posts / Role panels): когда в канале появились новые сообщения, панель
/// публикуется заново внизу, старая удаляется. Чтобы не спамить, перепубликация — после 15 секунд тишины в канале
/// (но не позже чем через минуту после первого нового сообщения).
/// </summary>
public sealed class StickyPanelService(DiscordSocketClient client, PanelTextStore texts, PanelService panels, CommunityStore community,
    CommunityService roles, ILogger<StickyPanelService> logger) : BackgroundService
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    private sealed class Pending
    {
        public DateTimeOffset FirstAt { get; } = DateTimeOffset.UtcNow;
        public CancellationTokenSource Cts { get; set; } = new();
    }

    private readonly ConcurrentDictionary<ulong, Pending> _pending = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.MessageReceived += message =>
        {
            if (message.Channel is SocketTextChannel channel && HasSticky(channel)) Schedule(channel);
            return Task.CompletedTask;
        };
        return Task.CompletedTask;
    }

    private bool HasSticky(SocketTextChannel channel) =>
        texts.StickyIn(channel.Guild.Id, channel.Id).Count > 0 ||
        community.GetPanels(channel.Guild.Id).Any(p => p.Sticky && p.ChannelId == channel.Id && p.MessageId is not null);

    /// <summary>Отложить перепубликацию: каждое новое сообщение сдвигает её, но не дальше MaxWait от первого.</summary>
    private void Schedule(SocketTextChannel channel)
    {
        var pending = _pending.GetOrAdd(channel.Id, _ => new Pending());
        var oldCts = pending.Cts;
        var cts = new CancellationTokenSource();
        pending.Cts = cts;
        oldCts.Cancel();
        oldCts.Dispose();
        var left = MaxWait - (DateTimeOffset.UtcNow - pending.FirstAt);
        var delay = left < Quiet ? (left < TimeSpan.Zero ? TimeSpan.Zero : left) : Quiet;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _pending.TryRemove(channel.Id, out _);
            await RepostAsync(channel);
        });
    }

    private async Task RepostAsync(SocketTextChannel channel)
    {
        await _gate.WaitAsync();
        try
        {
            var guildId = channel.Guild.Id;
            var modulePanels = texts.StickyIn(guildId, channel.Id);
            var rolePanels = community.GetPanels(guildId).Where(p => p.Sticky && p.ChannelId == channel.Id && p.MessageId is not null).ToList();
            var stickyIds = modulePanels.Select(x => x.MessageId).Concat(rolePanels.Select(p => p.MessageId!.Value)).ToHashSet();
            if (stickyIds.Count == 0) return;

            // Уже внизу (последние сообщения канала — sticky-панели) — ничего не делать
            var last = (await channel.GetMessagesAsync(stickyIds.Count).FlattenAsync()).ToList();
            if (last.Count > 0 && last.All(m => stickyIds.Contains(m.Id))) return;

            foreach (var (key, oldId) in modulePanels)
            {
                try
                {
                    var (embed, comps) = panels.Build(guildId, key);
                    var message = await channel.SendMessageAsync(embed: embed, components: comps);
                    texts.ReplaceMessage(guildId, key, oldId, message.Id);
                    try { await channel.DeleteMessageAsync(oldId); } catch (Discord.Net.HttpException) { /* уже удалено */ }
                }
                catch (Discord.Net.HttpException ex)
                {
                    logger.LogWarning(ex, "Sticky panel {Key} in #{Channel}: repost failed", key, channel.Name);
                }
            }

            foreach (var panel in rolePanels)
                if (await roles.PostAsync(panel, channel.Id) is { } error)
                    logger.LogWarning("Sticky role panel {Panel} in #{Channel}: {Error}", panel.Id, channel.Name, error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sticky panels in #{Channel} failed", channel.Name);
        }
        finally
        {
            _gate.Release();
        }
    }
}
