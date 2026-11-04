using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Раз в час сверяет роль дня рождения: у кого сегодня ДР и есть роль «Участник» — выдаёт указанную роль,
/// у остальных с этой ролью — снимает (то есть на следующий день роль уходит сама). Необязательно — поздравление в канал.
/// Дата «сегодня» берётся в часовом поясе сервера (Settings → часовой пояс).
/// </summary>
public sealed class BirthdayService(DiscordSocketClient client, BirthdayStore store, TimeService time,
    ILogger<BirthdayService> logger) : BackgroundService
{
    public sealed record Reconciled(int Granted, int Removed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Небольшая пауза, чтобы клиент успел подключиться и прогреть участников
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                foreach (var guildId in store.Guilds())
                {
                    try { await ReconcileAsync(guildId); }
                    catch (Exception e) { logger.LogWarning(e, "Birthday reconcile for guild {Guild} failed", guildId); }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // остановка
        }
    }

    /// <summary>Сверить роль дня рождения на сервере прямо сейчас. Возвращает, сколько выдано и снято.</summary>
    public async Task<Reconciled> ReconcileAsync(ulong guildId)
    {
        var g = store.Get(guildId);
        if (!g.Enabled || g.BirthdayRoleId is not { } roleId) return new Reconciled(0, 0);
        if (client.GetGuild(guildId) is not { } guild || guild.GetRole(roleId) is not { } role) return new Reconciled(0, 0);

        var today = time.LocalNow.Date;

        // Кто сегодня именинник: дата совпадает, есть роль «Участник» (если задана) и человек на сервере
        var celebrants = new HashSet<ulong>();
        foreach (var (userId, b) in g.People)
        {
            if (!b.IsOn(today)) continue;
            if (guild.GetUser(userId) is not { } member) continue;
            if (g.MemberRoleId is { } memberRole && member.Roles.All(r => r.Id != memberRole)) continue;
            celebrants.Add(userId);
        }

        var granted = 0;
        foreach (var userId in celebrants)
        {
            var member = guild.GetUser(userId)!;
            if (member.Roles.Any(r => r.Id == roleId)) continue;
            try
            {
                await member.AddRoleAsync(role);
                granted++;
                if (g.AnnounceChannelId is { } chId && guild.GetTextChannel(chId) is { } channel)
                {
                    try { await channel.SendMessageAsync(Tx.Get(guildId, "birthday.announce", ("user", $"<@{userId}>")),
                        allowedMentions: new AllowedMentions { UserIds = new List<ulong> { userId } }); }
                    catch (Discord.Net.HttpException) { /* нет прав писать в канал */ }
                }
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogWarning(ex, "Birthday: can't give role to {User} (check the bot's role is above the birthday role)", userId);
            }
        }

        // Снять роль у всех, у кого она есть, но сегодня не их день (включая вчерашних именинников)
        var removed = 0;
        foreach (var member in role.Members.ToList())
        {
            if (celebrants.Contains(member.Id)) continue;
            try { await member.RemoveRoleAsync(role); removed++; }
            catch (Discord.Net.HttpException ex) { logger.LogWarning(ex, "Birthday: can't remove role from {User}", member.Id); }
        }

        if (granted > 0 || removed > 0)
            logger.LogInformation("Birthdays on {Guild}: +{Granted} / -{Removed}", guild.Name, granted, removed);
        return new Reconciled(granted, removed);
    }

    /// <summary>Ближайшие дни рождения (по локальной дате сервера), для панели. Для каждого — через сколько дней.</summary>
    public IReadOnlyList<(ulong UserId, Birthday Birthday, int InDays)> Upcoming(ulong guildId, int take = 50)
    {
        var g = store.Get(guildId);
        var today = time.LocalNow.Date;
        return g.People
            .Select(kv => (kv.Key, kv.Value, InDays: DaysUntil(today, kv.Value)))
            .OrderBy(x => x.InDays)
            .Take(take)
            .ToList();
    }

    private static int DaysUntil(DateTime today, Birthday b)
    {
        var (m, d) = (b.Month, b.Day);
        if (m == 2 && d == 29) d = 28; // ближайшее сравнение без учёта високосности
        for (var i = 0; i < 366; i++)
        {
            var date = today.AddDays(i);
            if (date.Month == m && date.Day == d) return i;
        }
        return 999;
    }
}
