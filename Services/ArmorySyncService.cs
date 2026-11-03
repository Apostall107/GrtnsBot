using System.Collections.Concurrent;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Итог синхронизации одного персонажа.</summary>
public enum ArmorySyncResult { Updated, NotFound, Unavailable, NotLinked }

/// <summary>
/// Привязанные к официальной оружейной персонажи обновляются раз в сутки (по одному запросу в несколько секунд):
/// боевая мощь, уровень предметов, легион, класс. GS берётся оттуда (LFG → Group options → GS from the armory).
/// Оружейная отказывает (403/429/503) — пауза, данные остаются прежними.
/// </summary>
public sealed class ArmorySyncService(OfficialArmory armory, CharacterStore characters, GuildPrefsStore prefs, LfgProjectStore projects,
    LfgProfileService profiles, DiscordSocketClient client, ILogger<ArmorySyncService> logger) : BackgroundService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Pause = TimeSpan.FromMinutes(30);

    private DateTimeOffset _pausedUntil;
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _searches = new();

    /// <summary>Оружейная недавно отказала — не дёргать её.</summary>
    public bool Paused => DateTimeOffset.UtcNow < _pausedUntil;

    /// <summary>Не чаще одного поиска в 3 секунды на человека.</summary>
    public bool TrySearchSlot(ulong userId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_searches.TryGetValue(userId, out var last) && now - last < TimeSpan.FromSeconds(3)) return false;
        _searches[userId] = now;
        if (_searches.Count > 2000) _searches.Clear();
        return true;
    }

    public void MarkUnavailable() => _pausedUntil = DateTimeOffset.UtcNow + Pause;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            do
            {
                try
                {
                    await SyncDueAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Armory sync failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // остановка
        }
    }

    private async Task SyncDueAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var guild in client.Guilds)
        {
            var due = characters.All(guild.Id)
                .Where(c => c.Info?.Armory is { } a && (a.SyncedAt is not { } at || DateTimeOffset.UtcNow - at > MaxAge))
                .OrderBy(c => c.Info!.Armory!.SyncedAt ?? DateTimeOffset.MinValue)
                .ToList();
            foreach (var c in due)
            {
                if (Paused) return;
                await SyncAsync(guild.Id, c.UserId, c.Name, c.Scope, ct);
                count++;
                await Task.Delay(Step, ct);
            }
        }
        if (count > 0) logger.LogInformation("Armory sync: {Count} character(s)", count);
    }

    /// <summary>Обновить персонажа из оружейной сейчас.</summary>
    public async Task<ArmorySyncResult> SyncAsync(ulong guildId, ulong userId, string name, string? scope, CancellationToken ct = default)
    {
        if (characters.GetInfo(guildId, userId, name, scope)?.Armory is not { } link) return ArmorySyncResult.NotLinked;
        if (Paused) return ArmorySyncResult.Unavailable;
        ArmoryProfile? p;
        try
        {
            p = await armory.GetAsync(link.CharacterId, link.ServerId, ct);
        }
        catch (ArmoryUnavailableException e)
        {
            MarkUnavailable();
            logger.LogWarning("Armory refused ({Status}), pausing for {Minutes} min", e.Status, Pause.TotalMinutes);
            return ArmorySyncResult.Unavailable;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                     or System.Text.Json.JsonException)
        {
            logger.LogDebug(e, "Armory request failed");
            return ArmorySyncResult.Unavailable;
        }

        if (p is null)
        {
            characters.SetArmory(guildId, userId, name, link with { SyncedAt = DateTimeOffset.UtcNow, Error = "not found" }, scope);
            return ArmorySyncResult.NotFound;
        }

        characters.SetArmory(guildId, userId, name, link with
        {
            Name = p.Name, ClassName = p.ClassName, Level = p.Level, CombatPower = p.CombatPower, ItemLevel = p.ItemLevel,
            Legion = p.Legion, ServerName = p.ServerName ?? link.ServerName, SyncedAt = DateTimeOffset.UtcNow, Error = null
        }, scope);
        await ApplyAsync(guildId, userId, name, scope, p);
        return ArmorySyncResult.Updated;
    }

    /// <summary>GS и класс из оружейной в профиль персонажа; активные сборы обновляются, если что-то изменилось.</summary>
    private async Task ApplyAsync(ulong guildId, ulong userId, string name, string? scope, ArmoryProfile p)
    {
        var info = characters.GetInfo(guildId, userId, name, scope);
        var changed = false;

        if (GsFrom(guildId, p) is { } gs)
        {
            changed |= info?.Gs != gs;
            characters.SetGs(guildId, userId, name, gs, scope, confirm: true, fromArmory: true);
        }

        // Класс: роль-класс проекта с тем же названием (если класс ещё не выбран или не совпадает)
        if (p.ClassName is { Length: > 0 } className && client.GetGuild(guildId) is { } guild)
        {
            var projectKey = scope ?? projects.DefaultKey(guildId);
            var match = profiles.ClassRoles(guildId, userId, projectKey)
                .FirstOrDefault(r => r.Name.Contains(className, StringComparison.OrdinalIgnoreCase));
            if (match is not null && info?.ClassRoleId != match.Id)
            {
                characters.SetInfo(guildId, userId, name, true, match.Id, false, null, scope);
                changed = true;
            }
        }

        if (changed) await profiles.RefreshCharacterAsync(guildId, userId, name, scope);
    }

    /// <summary>Какое число из оружейной считать GS на сервере (null — GS вводят вручную).</summary>
    public int? GsFrom(ulong guildId, ArmoryProfile p) => prefs.Get(guildId).ArmoryGs switch
    {
        "combatPower" => p.CombatPower,
        "none" => null,
        _ => p.ItemLevel
    };

    /// <summary>GS этого персонажа берётся из оружейной (поле GS только для чтения).</summary>
    public bool GsLocked(ulong guildId, CharacterInfo? info) => characters.GsFromArmory(guildId, info);
}
