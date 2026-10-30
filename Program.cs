using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;
using LfgBot.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

// WebApplication вместо обычного Host: в том же процессе работает веб-админка (папка Web/)
var builder = WebApplication.CreateBuilder(args);
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

builder.Services.AddSingleton<BotSettingsStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<LfgHistoryStore>();
builder.Services.AddSingleton<LfgStore>();
builder.Services.AddSingleton<LfgTemplateStore>();
builder.Services.AddSingleton<ActivityStore>();
builder.Services.AddSingleton<CharacterStore>();
builder.Services.AddSingleton<TimeService>();
builder.Services.AddSingleton<TicketStore>();
builder.Services.AddSingleton<TempVoiceStore>();
builder.Services.AddSingleton<RoomPrefsStore>();
builder.Services.AddSingleton<TempRoomManager>();
builder.Services.AddSingleton<LfgNotifyStore>();
builder.Services.AddSingleton<LfgNotifier>();
builder.Services.AddSingleton<LfgSignupService>();
builder.Services.AddSingleton<LfgProfileService>();
builder.Services.AddSingleton<BotOwner>();
builder.Services.AddSingleton<AdminStore>();
builder.Services.AddSingleton<CaptchaStore>();
builder.Services.AddSingleton<CaptchaService>();
builder.Services.AddSingleton<CaptchaVoiceGuard>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CaptchaVoiceGuard>());
builder.Services.AddSingleton<TicketMessageService>();
builder.Services.AddSingleton<TicketArchiveStore>();
builder.Services.AddSingleton<TicketService>();
builder.Services.AddSingleton<PanelTextStore>();
builder.Services.AddSingleton<PanelService>();
builder.Services.AddSingleton<LfgPublisher>();
builder.Services.AddSingleton<RecurringLfgStore>();
builder.Services.AddHostedService<BotService>();
builder.Services.AddHostedService<GroupCleanupService>();
builder.Services.AddHostedService<RecurringLfgService>();
builder.Services.AddHostedService<TicketAutoCloseService>();
builder.Services.AddHostedService<TempVoiceService>();

// Веб-админка: вход через Discord, слушает только 127.0.0.1 (наружу — через Caddy с HTTPS)
builder.AddWebPanel();

var app = builder.Build();
app.UseWebPanel();

await app.RunAsync();
