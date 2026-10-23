using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Пока пользователь заблокирован за проваленную капчу (и включён «Timeout on fail»), бот отключает его
/// от голосовых каналов при каждом заходе. Работает и для тех, кому Discord не даёт выдать таймаут
/// (администраторы, роль выше роли бота): отключение от голоса иерархией ролей не ограничено.
/// </summary>
public sealed class CaptchaVoiceGuard(DiscordSocketClient client, CaptchaService captcha, ILogger<CaptchaVoiceGuard> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated -= OnVoiceStateUpdated;
        return Task.CompletedTask;
    }

    private Task OnVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (after.VoiceChannel is null || before.VoiceChannel?.Id == after.VoiceChannel.Id) return Task.CompletedTask;
        if (user is not SocketGuildUser member || !captcha.IsVoiceRestricted(member.Id)) return Task.CompletedTask;

        _ = Task.Run(() => DisconnectAsync(member));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Наказание за проваленную капчу: таймаут Discord (максимум 28 дней) и отключение от голоса.
    /// Таймаут нельзя выдать администратору или роли выше бота — тогда остаётся только отключение от голоса,
    /// а повторные заходы режет этот же сервис.
    /// </summary>
    public async Task PunishAsync(SocketGuildUser member, TimeSpan duration)
    {
        if (duration > TimeSpan.FromDays(28)) duration = TimeSpan.FromDays(28);
        try
        {
            await member.SetTimeOutAsync(duration, new RequestOptions { AuditLogReason = "Failed bot captcha" });
            logger.LogInformation("Timed out {User} ({UserId}) for {Minutes} min for failing the captcha",
                member.Username, member.Id, (int)duration.TotalMinutes);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not time out {UserId} after a failed captcha " +
                                  "(administrator, role above the bot's, or no Timeout Members permission)", member.Id);
        }

        if (member.VoiceChannel is not null) await DisconnectAsync(member);
    }

    public async Task DisconnectAsync(SocketGuildUser member)
    {
        try
        {
            await member.ModifyAsync(p => p.ChannelId = new Optional<ulong?>(null),
                new RequestOptions { AuditLogReason = "Failed bot captcha" });
            logger.LogInformation("Disconnected {User} ({UserId}) from voice: blocked after a failed captcha",
                member.Username, member.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not disconnect {UserId} from voice (Move Members permission?)", member.Id);
        }
    }
}
