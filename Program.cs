using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);

// Под systemd (Linux-сервер): логи в формате journald и корректная остановка службы.
// При обычном запуске (Windows, консоль) не делает ничего.
builder.Services.AddSystemd();

builder.Services.AddSingleton(new DiscordSocketConfig
{
    // GuildVoiceStates — временные комнаты.
    // GuildMembers — ЛС-оповещения LFG (привилегированный: включить "Server Members Intent" в Developer Portal → Bot)
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMembers,
    AlwaysDownloadUsers = true,
    LogLevel = LogSeverity.Info
});
builder.Services.AddSingleton<DiscordSocketClient>();
builder.Services.AddSingleton(sp => new InteractionService(
    sp.GetRequiredService<DiscordSocketClient>(),
    new InteractionServiceConfig { LogLevel = LogSeverity.Info, UseCompiledLambda = true }));

builder.Services.AddSingleton<LfgStore>();
builder.Services.AddSingleton<ActivityStore>();
builder.Services.AddSingleton<CharacterStore>();
builder.Services.AddSingleton<TimeService>();
builder.Services.AddSingleton<TicketStore>();
builder.Services.AddSingleton<TempVoiceStore>();
builder.Services.AddSingleton<TempRoomManager>();
builder.Services.AddSingleton<LfgNotifyStore>();
builder.Services.AddSingleton<LfgNotifier>();
builder.Services.AddSingleton<LfgSignupService>();
builder.Services.AddHostedService<BotService>();
builder.Services.AddHostedService<GroupCleanupService>();
builder.Services.AddHostedService<TempVoiceService>();

await builder.Build().RunAsync();
