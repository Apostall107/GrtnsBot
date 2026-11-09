using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

public sealed class BotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceProvider services,
    IConfiguration config,
    ILogger<BotService> logger) : IHostedService
{
    private bool _commandsRegistered;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        client.Log += LogAsync;
        interactions.Log += LogAsync;
        client.Ready += OnReadyAsync;
        client.JoinedGuild += OnJoinedGuildAsync;
        client.InteractionCreated += OnInteractionAsync;
        interactions.InteractionExecuted += OnInteractionExecutedAsync;

        await interactions.AddModulesAsync(typeof(BotService).Assembly, services);

        var token = config["Discord:Token"];
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Discord:Token is not set (appsettings.json, user-secrets or env Discord__Token).");

        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await client.StopAsync();
        await client.LogoutAsync();
    }

    private async Task OnReadyAsync()
    {
        // Список участников серверов: без него сайт не знает имён и считает людей вышедшими. Ошибка — в лог (обычно выключен Server Members Intent)
        _ = Task.Run(DownloadMembersAsync);

        if (_commandsRegistered) return;
        _commandsRegistered = true;

        // Команды регистрируются на каждый сервер отдельно — появляются мгновенно
        foreach (var guild in client.Guilds)
            await RegisterToGuildAsync(guild);

        // Перерисовать опубликованные панели: встроенная кнопка «Website» появляется в старых панелях без ручного обновления
        _ = Task.Run(RefreshPanelsAsync);
        // Классы и их иконки в активных сборах — по текущим настройкам (например, после исправлений)
        _ = Task.Run(() => services.GetRequiredService<LfgSignupService>().ReapplyProfilesAsync());
    }

    private async Task DownloadMembersAsync()
    {
        foreach (var guild in client.Guilds)
        {
            try
            {
                if (!guild.HasAllMembers) await guild.DownloadUsersAsync();
                logger.LogInformation("Members of {Guild}: {Cached}/{Total} in cache", guild.Name, guild.Users.Count, guild.MemberCount);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Can't download members of {Guild} — turn on Server Members Intent (Developer Portal → Bot)", guild.Name);
            }
        }
    }

    private async Task RefreshPanelsAsync()
    {
        var panels = services.GetRequiredService<PanelService>();
        // Nothing is checked or forgotten before the servers are fully loaded in the cache
        await GuildCache.WaitAllAsync(client, TimeSpan.FromMinutes(2));

        // Panels whose record was lost (e.g. the bot couldn't see the server for a while) are found in the channels and linked again
        var relinker = services.GetRequiredService<PanelRelinker>();
        foreach (var guild in client.Guilds.Where(GuildCache.Loaded))
        {
            try { await relinker.RelinkAsync(guild); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to look for posted panels on {Guild}", guild.Name); }
        }

        foreach (var guild in client.Guilds.Where(GuildCache.Loaded))
        foreach (var def in PanelDefs.All)
        {
            try
            {
                var (updated, total) = await panels.RefreshAsync(guild.Id, def.Key);
                if (total > 0) logger.LogInformation("Panels {Key} on {Guild}: refreshed {Updated}/{Total}", def.Key, guild.Name, updated, total);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh {Key} panels on {Guild}", def.Key, guild.Name);
            }
        }

        // Панели ролей: новые кнопки (например, «My roles») появляются в уже опубликованных
        var community = services.GetRequiredService<CommunityService>();
        var store = services.GetRequiredService<CommunityStore>();
        foreach (var guild in client.Guilds.Where(GuildCache.Loaded))
        foreach (var panel in store.GetPanels(guild.Id).Where(p => p.MessageId is not null))
        {
            try
            {
                await community.RefreshAsync(panel);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh role panel {Panel}", panel.Id);
            }
        }
    }

    private Task OnJoinedGuildAsync(SocketGuild guild) => RegisterToGuildAsync(guild);

    private async Task RegisterToGuildAsync(SocketGuild guild)
    {
        try
        {
            await interactions.RegisterCommandsToGuildAsync(guild.Id);
            logger.LogInformation("Commands registered on {Guild} ({GuildId})", guild.Name, guild.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to register commands on {Guild} ({GuildId}). " +
                "Re-invite the bot with the applications.commands scope.", guild.Name, guild.Id);
        }
    }

    private async Task OnInteractionAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(client, interaction);
        await interactions.ExecuteCommandAsync(context, services);
    }

    private async Task OnInteractionExecutedAsync(ICommandInfo command, IInteractionContext context, IResult result)
    {
        if (result.IsSuccess) return;

        logger.LogWarning("Interaction {Command} failed: {Error} {Reason}",
            command?.Name, result.Error, result.ErrorReason);

        var gid = context.Interaction.GuildId ?? 0;
        var text = result.Error == InteractionCommandError.UnmetPrecondition
            ? Tx.Get(gid, "bot.accessDenied", ("reason", result.ErrorReason))
            : Tx.Get(gid, "bot.error");

        if (context.Interaction.HasResponded)
            await context.Interaction.FollowupAsync(text, ephemeral: true);
        else
            await context.Interaction.RespondAsync(text, ephemeral: true);
    }

    private Task LogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace
        };
        // Normal on stop/reconnect: Discord.Net cancels its connection task — not a problem, keep it out of the warnings
        if (message.Exception is TaskCanceledException or OperationCanceledException)
        {
            logger.LogDebug("[{Source}] Connection task canceled (stop or reconnect)", message.Source);
            return Task.CompletedTask;
        }
        logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
