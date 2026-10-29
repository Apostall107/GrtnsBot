using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Повторяющийся сбор: в выбранные дни недели в HH:mm (часовой пояс сервера).</summary>
public sealed class RecurringLfg
{
    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }
    /// <summary>Организатор сбора (может управлять составом и закрыть).</summary>
    public ulong OwnerId { get; set; }
    public string ActivityName { get; set; } = "";
    public ulong? PingRoleId { get; set; }
    public List<SlotRole> Slots { get; set; } = [];
    public List<DayOfWeek> Days { get; set; } = [];
    public int Hour { get; set; } = 20;
    public int Minute { get; set; }
    /// <summary>За сколько часов до начала публиковать пост.</summary>
    public int PostHoursBefore { get; set; } = 24;
    public bool NotifyByDm { get; set; } = true;
    /// <summary>Сразу посадить организатора в слот 1.</summary>
    public bool SeatOwner { get; set; } = true;
    public bool Enabled { get; set; } = true;

    /// <summary>Начало последнего опубликованного повторения — чтобы не публиковать дважды.</summary>
    public DateTimeOffset? LastStart { get; set; }
    public DateTimeOffset? LastPostedAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>Ближайшее начало строго позже <paramref name="after"/>.</summary>
    public DateTimeOffset? NextStart(TimeService time, DateTimeOffset after)
    {
        if (Days.Count == 0) return null;
        var today = time.Today;
        for (var i = -1; i <= 8; i++)
        {
            var date = today.AddDays(i);
            if (!Days.Contains(date.DayOfWeek)) continue;
            var start = time.ToUtc(date, Hour, Minute);
            if (start > after) return start;
        }
        return null;
    }
}

/// <summary>data/recurring-lfg.json</summary>
public sealed class RecurringLfgStore
{
    public const int MaxPerGuild = 50;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<RecurringLfg> _data;

    public RecurringLfgStore(IConfiguration config)
    {
        _path = config["Storage:RecurringLfgPath"] ?? "data/recurring-lfg.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<RecurringLfg>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public IReadOnlyList<RecurringLfg> GetAll()
    {
        lock (_lock) return _data.Select(Clone).ToList();
    }

    public IReadOnlyList<RecurringLfg> Get(ulong guildId)
    {
        lock (_lock) return _data.Where(r => r.GuildId == guildId).Select(Clone).ToList();
    }

    public RecurringLfg? Find(string id)
    {
        lock (_lock) return _data.Find(r => r.Id == id) is { } r ? Clone(r) : null;
    }

    public string? Add(RecurringLfg item)
    {
        lock (_lock)
        {
            if (_data.Count(r => r.GuildId == item.GuildId) >= MaxPerGuild) return $"Limit of {MaxPerGuild} schedules reached.";
            _data.Add(item);
            Save();
            return null;
        }
    }

    public RecurringLfg? Update(string id, Action<RecurringLfg> update)
    {
        lock (_lock)
        {
            var item = _data.Find(r => r.Id == id);
            if (item is null) return null;
            update(item);
            Save();
            return Clone(item);
        }
    }

    public RecurringLfg? Remove(string id)
    {
        lock (_lock)
        {
            var item = _data.Find(r => r.Id == id);
            if (item is null) return null;
            _data.Remove(item);
            Save();
            return item;
        }
    }

    private static RecurringLfg Clone(RecurringLfg r) =>
        JsonSerializer.Deserialize<RecurringLfg>(JsonSerializer.Serialize(r))!;

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}

/// <summary>Раз в минуту публикует повторяющиеся сборы, когда до начала осталось PostHoursBefore часов.</summary>
public sealed class RecurringLfgService(
    RecurringLfgStore store,
    LfgPublisher publisher,
    TimeService time,
    AuditLog audit,
    ILogger<RecurringLfgService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var item in store.GetAll().Where(r => r.Enabled))
                {
                    try
                    {
                        await RunAsync(item);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Recurring LFG {Id} failed", item.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // остановка бота
        }
    }

    private async Task RunAsync(RecurringLfg item)
    {
        var now = DateTimeOffset.UtcNow;
        var after = item.LastStart is { } last && last > now ? last : now;
        if (item.NextStart(time, after) is not { } start) return;
        if (now < start.AddHours(-Math.Max(0, item.PostHoursBefore))) return;

        try
        {
            var (group, _, _) = await publisher.PublishAsync(new LfgPublishRequest(
                item.GuildId, item.ChannelId, item.OwnerId, item.ActivityName, item.PingRoleId,
                item.Slots, start, item.NotifyByDm, item.SeatOwner));
            store.Update(item.Id, r =>
            {
                r.LastStart = start;
                r.LastPostedAt = DateTimeOffset.UtcNow;
                r.LastError = null;
            });
            audit.FromSystem(item.GuildId, "lfg", "Posted recurring LFG group", $"{group.ActivityName} · {start:u}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Discord.Net.HttpException)
        {
            // Не публикуем повторно каждую минуту: пропускаем это повторение и пишем причину
            store.Update(item.Id, r =>
            {
                r.LastStart = start;
                r.LastError = ex.Message;
            });
            audit.FromSystem(item.GuildId, "lfg", "Recurring LFG failed", $"{item.ActivityName}: {ex.Message}");
        }
    }
}
