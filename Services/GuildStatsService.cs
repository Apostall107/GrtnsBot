using System.Text.Json;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Снимок сервера на начало дня (часовой пояс сервера): участники и открытые тикеты.</summary>
public sealed class GuildDaySnapshot
{
    public ulong GuildId { get; set; }
    /// <summary>yyyy-MM-dd по часовому поясу сервера.</summary>
    public string Date { get; set; } = "";
    public int Members { get; set; }
    public int OpenTickets { get; set; }
    /// <summary>Писали или были в войсе за 7 / 30 дней (null — снимок сделан до этого поля).</summary>
    public int? Active7 { get; set; }
    public int? Active30 { get; set; }
    public DateTimeOffset TakenAt { get; set; }
}

/// <summary>Ежедневные снимки (data/stats-history.json). Хранится ~13 месяцев на сервер.</summary>
public sealed class GuildStatsStore
{
    public const int KeepDays = 400;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<GuildDaySnapshot> _data;

    public GuildStatsStore(IConfiguration config)
    {
        _path = config["Storage:StatsHistoryPath"] ?? "data/stats-history.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<GuildDaySnapshot>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public bool Has(ulong guildId, DateOnly date)
    {
        var key = Key(date);
        lock (_lock) return _data.Any(s => s.GuildId == guildId && s.Date == key);
    }

    public void Add(GuildDaySnapshot snapshot)
    {
        lock (_lock)
        {
            _data.RemoveAll(s => s.GuildId == snapshot.GuildId && s.Date == snapshot.Date);
            _data.Add(snapshot);
            // Старое — прочь
            var guildDays = _data.Where(s => s.GuildId == snapshot.GuildId).OrderByDescending(s => s.Date).Skip(KeepDays).ToList();
            foreach (var old in guildDays) _data.Remove(old);
            Save();
        }
    }

    /// <summary>Снимок на дату или ближайший более ранний (не старше maxBack дней).</summary>
    public GuildDaySnapshot? OnOrBefore(ulong guildId, DateOnly date, int maxBack = 3)
    {
        var to = Key(date);
        var from = Key(date.AddDays(-maxBack));
        lock (_lock)
            return _data.Where(s => s.GuildId == guildId && string.CompareOrdinal(s.Date, to) <= 0 && string.CompareOrdinal(s.Date, from) >= 0)
                .OrderByDescending(s => s.Date).FirstOrDefault();
    }

    /// <summary>Последние дни (по возрастанию даты).</summary>
    public IReadOnlyList<GuildDaySnapshot> Last(ulong guildId, int days)
    {
        lock (_lock)
            return _data.Where(s => s.GuildId == guildId).OrderByDescending(s => s.Date).Take(days).OrderBy(s => s.Date).ToList();
    }

    public static string Key(DateOnly date) => date.ToString("yyyy-MM-dd");

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DataFiles.Save(_path, JsonSerializer.Serialize(_data.OrderBy(s => s.GuildId).ThenBy(s => s.Date), JsonOptions));
    }
}

/// <summary>Раз в 10 минут: если за сегодняшний день (часовой пояс сервера) снимка нет — делает его.</summary>
public sealed class GuildStatsService(
    DiscordSocketClient client,
    GuildStatsStore store,
    TicketStore tickets,
    MemberActivityStore activity,
    TimeService time,
    ILogger<GuildStatsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Только когда участники загружены, иначе число неверное
                    if (client.ConnectionState == Discord.ConnectionState.Connected)
                        foreach (var guild in client.Guilds.Where(g => g.IsConnected && g.HasAllMembers))
                            TakeIfMissing(guild);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Daily stats snapshot failed");
                }
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // остановка бота
        }
    }

    private void TakeIfMissing(SocketGuild guild)
    {
        var today = time.Today;
        if (store.Has(guild.Id, today)) return;
        store.Add(new GuildDaySnapshot
        {
            GuildId = guild.Id,
            Date = GuildStatsStore.Key(today),
            Members = guild.MemberCount,
            OpenTickets = tickets.GetTickets(guild.Id).Count,
            Active7 = activity.CountActive(guild.Id, DateTimeOffset.UtcNow.AddDays(-7), id => guild.GetUser(id) is not null),
            Active30 = activity.CountActive(guild.Id, DateTimeOffset.UtcNow.AddDays(-30), id => guild.GetUser(id) is not null),
            TakenAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Изменения для карточек: сейчас против снимка вчера (за сутки) и снимка 7 дней назад (за неделю).
    /// null — снимка ещё нет. history — участники за последние 30 дней (для мини-графика).
    /// </summary>
    public static object Trends(GuildStatsStore store, TimeService time, ulong guildId, int members, int openTickets)
    {
        var today = time.Today;
        var start = store.OnOrBefore(guildId, today.AddDays(-1), 0);
        var week = store.OnOrBefore(guildId, today.AddDays(-7));
        var history = store.Last(guildId, 30);
        return new
        {
            members = new
            {
                day = start is null ? (int?)null : members - start.Members,
                week = week is null ? (int?)null : members - week.Members,
                history = history.Select(s => new { date = s.Date, value = s.Members })
            },
            tickets = new
            {
                day = start is null ? (int?)null : openTickets - start.OpenTickets,
                week = week is null ? (int?)null : openTickets - week.OpenTickets,
                history = history.Select(s => new { date = s.Date, value = s.OpenTickets })
            }
        };
    }
}
