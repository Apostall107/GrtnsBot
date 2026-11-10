using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Discord.Net reconnects by itself, but now and then the connection gets stuck: the site works, the bot is "offline"
/// and nothing reacts. If the bot hasn't been connected for 15 minutes in a row it exits — systemd (Restart=always)
/// starts it again in 10 s. Only under systemd (on a PC nobody would restart it). A check once a minute — no load.
/// </summary>
public sealed class ConnectionWatchdog(DiscordSocketClient client, ILogger<ConnectionWatchdog> logger) : BackgroundService
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (Environment.GetEnvironmentVariable("INVOCATION_ID") is null) return;   // not started by systemd
        var since = DateTime.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                if (client.ConnectionState == ConnectionState.Connected) { since = DateTime.UtcNow; continue; }
                if (DateTime.UtcNow - since < Limit) continue;
                logger.LogCritical("Not connected to Discord for {Minutes} min ({State}) — restarting the bot", Limit.TotalMinutes, client.ConnectionState);
                DataFiles.FlushAll();
                Environment.Exit(3);
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }
}
