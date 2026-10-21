using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Раз в минуту: напоминание за 10 минут до старта и удаление просроченных сборов (ExpiresAt).
/// </summary>
public sealed class GroupCleanupService(
    LfgStore store,
    LfgSignupService signup,
    ILogger<GroupCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan ReminderBefore = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTimeOffset.UtcNow;

                foreach (var group in store.Groups.Values
                             .Where(g => !g.Reminded && g.StartsAt is { } st && now >= st - ReminderBefore && now < st)
                             .ToList())
                {
                    try
                    {
                        await signup.SendReminderAsync(group);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to send reminder for group {GroupId}", group.Id);
                    }
                }

                foreach (var group in store.Groups.Values.Where(g => g.ExpiresAt <= now).ToList())
                {
                    try
                    {
                        await ExpireAsync(group);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete expired group {GroupId}", group.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // остановка бота
        }
    }

    private async Task ExpireAsync(LfgGroup group)
    {
        await group.Gate.WaitAsync();
        try
        {
            if (!store.Groups.TryRemove(group.Id, out _)) return; // уже закрыт вручную
            group.IsClosed = true;
            store.SaveGroups();
            await signup.DeleteMessagesAsync(group);
            logger.LogInformation("Expired group {GroupId} ({Activity}) deleted", group.Id, group.ActivityName);
        }
        finally
        {
            group.Gate.Release();
        }
    }
}
