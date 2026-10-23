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
