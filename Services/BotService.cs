using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
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
        if (_commandsRegistered) return;
        _commandsRegistered = true;

        // Команды регистрируются на каждый сервер отдельно — появляются мгновенно
        foreach (var guild in client.Guilds)
            await RegisterToGuildAsync(guild);
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

        var text = result.Error == InteractionCommandError.UnmetPrecondition
            ? $"Access denied: {result.ErrorReason}"
            : "Something went wrong. Please try again.";

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
        logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
