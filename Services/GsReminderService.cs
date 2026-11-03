using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Раз в неделю (если включено в LFG → GS reminder): у кого GS персонажа не обновлялся N дней (или не задан) —
/// сообщение в выбранном канале с упоминаниями и/или ЛС (если ЛС «GS reminder» включены). Кнопка открывает окно ввода GS.
/// </summary>
public sealed class GsReminderService(DiscordSocketClient client, GuildPrefsStore prefs, CharacterStore characters,
    LfgProjectStore projects, UserDmStore userDm, ILogger<GsReminderService> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var guildId in prefs.Guilds())
                {
                    var p = prefs.Get(guildId);
                    if (!p.GsReminder || (p.GsReminderLastRun is { } last && DateTimeOffset.UtcNow - last < Every)) continue;
                    try
                    {
                        await RunAsync(guildId);
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning(e, "GS reminder for guild {Guild} failed", guildId);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // остановка
        }
    }

    /// <summary>Персонажи с устаревшим GS: (участник → персонажи).</summary>
    public Dictionary<ulong, List<RosterCharacter>> Stale(ulong guildId)
    {
        var days = Math.Clamp(prefs.Get(guildId).GsReminderDays, 1, 365);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var guild = client.GetGuild(guildId);
        return characters.All(guildId)
            .Where(c => guild?.GetUser(c.UserId) is not null)
            .Where(c => c.Info?.GsUpdatedAt is not { } at || at < cutoff || c.Info.Gs is null)
            .GroupBy(c => c.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>Разослать напоминание сейчас. Возвращает, скольким людям напомнили.</summary>
    public async Task<int> RunAsync(ulong guildId)
    {
        prefs.Update(guildId, x => x.GsReminderLastRun = DateTimeOffset.UtcNow);
        if (client.GetGuild(guildId) is not { } guild) return 0;
        var p = prefs.Get(guildId);
        var stale = Stale(guildId);
        if (stale.Count == 0) return 0;

        var button = new ComponentBuilder()
            .WithButton(Tx.Get(guildId, "gs.reminder.button"), $"gsr:open:{guildId}", ButtonStyle.Primary, new Emoji("📈"))
            .Build();

        if (p.GsReminderChannelId is { } channelId && guild.GetTextChannel(channelId) is { } channel)
        {
            // Упоминания — порциями, чтобы сообщение не превысило лимит
            foreach (var chunk in stale.Keys.Chunk(40))
            {
                var mentions = string.Join(" ", chunk.Select(id => $"<@{id}>"));
                await channel.SendMessageAsync(Tx.Get(guildId, "gs.reminder.channel", ("members", mentions), ("days", p.GsReminderDays)),
                    components: button, allowedMentions: new AllowedMentions { UserIds = chunk.ToList() });
            }
        }

        if (p.Dm.GsReminder)
        {
            foreach (var (userId, list) in stale.Where(x => !userDm.IsOff(guildId, x.Key, "gsReminder")))
            {
                try
                {
                    var user = guild.GetUser(userId);
                    if (user is null) continue;
                    var names = string.Join(", ", list.Select(c => $"**{Format.Sanitize(c.Name)}**"));
                    await user.SendMessageAsync(Tx.Get(guildId, "gs.reminder.dm", ("server", Format.Sanitize(guild.Name)), ("characters", names)),
                        components: button);
                    await Task.Delay(1000);
                }
                catch (Discord.Net.HttpException)
                {
                    // закрытые ЛС
                }
            }
        }

        logger.LogInformation("GS reminder on {Guild}: {Count} member(s)", guild.Name, stale.Count);
        return stale.Count;
    }

    /// <summary>Персонажи участника для выбора в окне GS: значение "scope|имя", подпись с проектом и текущим GS.</summary>
    public IReadOnlyList<(string Value, string Label, string? Description)> Choices(ulong guildId, ulong userId)
    {
        var names = projects.GetProjects(guildId).ToDictionary(x => x.Key, x => x.Name);
        var def = projects.DefaultKey(guildId);
        return characters.All(guildId).Where(c => c.UserId == userId)
            .OrderBy(c => c.Info?.GsUpdatedAt ?? DateTimeOffset.MinValue)
            .Take(25)
            .Select(c =>
            {
                var project = names.TryGetValue(c.Scope ?? def ?? "", out var n) ? n : null;
                var gs = c.Info?.Gs is { } v ? $"GS {v}" : "GS —";
                return ($"{c.Scope}|{c.Name}", c.Name, (string?)(project is null ? gs : $"{project} · {gs}"));
            }).ToList();
    }
}
