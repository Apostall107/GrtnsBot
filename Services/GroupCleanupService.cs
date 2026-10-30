using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Раз в минуту: напоминание за N минут до старта (Settings) и удаление просроченных сборов (ExpiresAt).
/// </summary>
public sealed class GroupCleanupService(
    LfgStore store,
    LfgSignupService signup,
    TimeService time,
    AuditLog audit,
    ILogger<GroupCleanupService> logger) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTimeOffset.UtcNow;
                // За сколько напоминать — из настроек (веб-админка → Settings), 0 = не напоминать
                var reminderBefore = TimeSpan.FromMinutes(time.ReminderMinutes);

                foreach (var group in store.Groups.Values
                             .Where(g => time.ReminderMinutes > 0 && !g.Reminded && g.StartsAt is { } st && now >= st - reminderBefore && now < st)
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
            store.RecordClosed(group, "expired");
            store.SaveGroups();
            await signup.DeleteMessagesAsync(group);
            logger.LogInformation("Expired group {GroupId} ({Activity}) deleted", group.Id, group.ActivityName);
            audit.FromSystem(group.GuildId, "lfg", "LFG group expired", group.ActivityName);
        }
        finally
        {
            group.Gate.Release();
        }
    }
}
