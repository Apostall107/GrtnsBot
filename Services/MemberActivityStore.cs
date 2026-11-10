using System.Collections.Concurrent;
using System.Text.Json;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Последняя активность участника на сервере: сообщение в канале и голосовой канал.</summary>
public sealed class MemberActivity
{
    public DateTimeOffset? LastMessage { get; set; }
    public DateTimeOffset? LastVoice { get; set; }

    public DateTimeOffset? Last => LastMessage > LastVoice || LastVoice is null ? LastMessage : LastVoice;
}

/// <summary>
/// data/member-activity.json — когда участник последний раз писал и был в войсе (по серверам).
/// Пишется с задержкой: изменения копятся в памяти и сохраняются раз в несколько минут.
/// </summary>
public sealed class MemberActivityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _path;
    private readonly ConcurrentDictionary<string, MemberActivity> _data;
    private readonly object _saveLock = new();
    private volatile bool _dirty;

    /// <summary>С какого момента ведётся учёт (до этого активность неизвестна).</summary>
    public DateTimeOffset TrackingSince { get; }

    public MemberActivityStore(IConfiguration config)
    {
        _path = config["Storage:MemberActivityPath"] ?? "data/member-activity.json";
        var file = DataFiles.Load<ActivityFile>(_path);
        _data = new ConcurrentDictionary<string, MemberActivity>(file?.Members ?? new());
        TrackingSince = file?.Since ?? DateTimeOffset.UtcNow;
        if (file is null) _dirty = true;
    }

    private static string Key(ulong guildId, ulong userId) => $"{guildId}:{userId}";

    public void Message(ulong guildId, ulong userId)
    {
        _data.AddOrUpdate(Key(guildId, userId), _ => new MemberActivity { LastMessage = DateTimeOffset.UtcNow },
            (_, a) => { a.LastMessage = DateTimeOffset.UtcNow; return a; });
        _dirty = true;
    }

    public void Voice(ulong guildId, ulong userId)
    {
        _data.AddOrUpdate(Key(guildId, userId), _ => new MemberActivity { LastVoice = DateTimeOffset.UtcNow },
            (_, a) => { a.LastVoice = DateTimeOffset.UtcNow; return a; });
        _dirty = true;
    }

    /// <summary>Сколько участников сервера писали или были в войсе после since.</summary>
    public int CountActive(ulong guildId, DateTimeOffset since, Func<ulong, bool>? isMember = null)
    {
        var prefix = $"{guildId}:";
        var n = 0;
        foreach (var (key, a) in _data)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || a.Last is not { } last || last < since) continue;
            if (isMember is not null && (!ulong.TryParse(key.AsSpan(prefix.Length), out var uid) || !isMember(uid))) continue;
            n++;
        }
        return n;
    }

    public MemberActivity? Get(ulong guildId, ulong userId) => _data.TryGetValue(Key(guildId, userId), out var a) ? a : null;

    public void SaveIfDirty()
    {
        if (!_dirty) return;
        lock (_saveLock)
        {
            _dirty = false;
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            DataFiles.Save(_path, JsonSerializer.Serialize(new ActivityFile { Since = TrackingSince, Members = _data.ToDictionary(x => x.Key, x => x.Value) }, JsonOptions));
        }
    }

    private sealed class ActivityFile
    {
        public DateTimeOffset Since { get; set; }
        public Dictionary<string, MemberActivity> Members { get; set; } = new();
    }
}

/// <summary>Отмечает сообщения и голосовые каналы участников; кто сидит в войсе — отмечается каждые 5 минут.</summary>
public sealed class MemberActivityTracker(DiscordSocketClient client, MemberActivityStore store, ILogger<MemberActivityTracker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.MessageReceived += message =>
        {
            if (message.Author is SocketGuildUser { IsBot: false } user) store.Message(user.Guild.Id, user.Id);
            return Task.CompletedTask;
        };
        client.UserVoiceStateUpdated += (user, before, after) =>
        {
            if (user is SocketGuildUser { IsBot: false } member && (before.VoiceChannel is not null || after.VoiceChannel is not null))
                store.Voice(member.Guild.Id, member.Id);
            return Task.CompletedTask;
        };

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (var guild in client.Guilds)
                    foreach (var channel in guild.VoiceChannels)
                    foreach (var member in channel.ConnectedUsers.Where(u => !u.IsBot))
                        store.Voice(guild.Id, member.Id);
                    store.SaveIfDirty();
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Member activity save failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            store.SaveIfDirty();
        }
    }
}
