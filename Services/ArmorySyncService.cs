using System.Collections.Concurrent;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Итог синхронизации одного персонажа.</summary>
public enum ArmorySyncResult { Updated, NotFound, Unavailable, NotLinked, Ambiguous }

/// <summary>Состояние массовой привязки/обновления на сервере.</summary>
public sealed record ArmoryBulkStatus(bool Running, int Total, int Done, int Linked, int Updated, int NotFound, int Ambiguous, int Failed,
    DateTimeOffset Started, DateTimeOffset? Finished, string? Error);

/// <summary>
/// Официальная оружейная: каждый привязывает и обновляет своих персонажей кнопкой на сайте; админ может запустить
/// массовую привязку/обновление (по одному запросу в несколько секунд). Автоматически ничего не обновляется.
/// GS берётся оттуда (LFG → Group options). Оружейная отказывает (403/429/503) — пауза, данные остаются прежними.
/// </summary>
public sealed class ArmorySyncService(OfficialArmory armory, CharacterStore characters, GuildPrefsStore prefs, LfgProjectStore projects,
    LfgProfileService profiles, DiscordSocketClient client, ILogger<ArmorySyncService> logger)
{
    public static readonly TimeSpan ManualCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Pause = TimeSpan.FromMinutes(30);

    private DateTimeOffset _pausedUntil;
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _searches = new();
    private readonly ConcurrentDictionary<ulong, ArmoryBulkStatus> _bulk = new();

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

    /// <summary>Настройки оружейной для проекта персонажа (scope null — проект по умолчанию).</summary>
    public ArmoryProjectPrefs ProjectPrefs(ulong guildId, string? scopeOrKey)
    {
        var project = projects.Effective(guildId, scopeOrKey);
        return prefs.ArmoryFor(guildId, project?.Key, project?.Name);
    }

    /// <summary>Сервер для поиска персонажа: его собственный, иначе по умолчанию для проекта.</summary>
    public int? SearchServer(ulong guildId, string? scope, CharacterInfo? info) =>
        info?.Armory?.ServerId ?? info?.ServerId ?? ProjectPrefs(guildId, scope).ServerId;

    public ArmoryBulkStatus? BulkStatus(ulong guildId) => _bulk.TryGetValue(guildId, out var s) ? s : null;

    /// <summary>
    /// Массово: привязанных — обновить, непривязанных — найти по точному имени на их сервере (или сервере по умолчанию)
    /// и привязать, если такой персонаж ровно один. false — уже идёт.
    /// </summary>
    public bool StartBulk(ulong guildId, string? projectKey, bool linkNew)
    {
        if (BulkStatus(guildId) is { Running: true }) return false;
        var guild = client.GetGuild(guildId);
        var def = projects.DefaultKey(guildId);
        var list = characters.All(guildId)
            .Where(c => guild?.GetUser(c.UserId) is not null)
            .Where(c => projectKey is null || (c.Scope ?? def) == projectKey)
            .Where(c => ProjectPrefs(guildId, c.Scope).Enabled)
            .Where(c => linkNew || c.Info?.Armory is not null)
            .ToList();
        var status = new ArmoryBulkStatus(true, list.Count, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow, null, null);
        _bulk[guildId] = status;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var c in list)
                {
                    if (Paused)
                    {
                        status = status with { Error = "The official armory refused requests — try again in 30 minutes." };
                        break;
                    }
                    var result = c.Info?.Armory is not null
                        ? await SyncAsync(guildId, c.UserId, c.Name, c.Scope)
                        : await AutoLinkAsync(guildId, c.UserId, c.Name, c.Scope, c.Info);
                    status = result switch
                    {
                        ArmorySyncResult.Updated when c.Info?.Armory is null => status with { Linked = status.Linked + 1 },
                        ArmorySyncResult.Updated => status with { Updated = status.Updated + 1 },
                        ArmorySyncResult.NotFound => status with { NotFound = status.NotFound + 1 },
                        ArmorySyncResult.Ambiguous => status with { Ambiguous = status.Ambiguous + 1 },
                        _ => status with { Failed = status.Failed + 1 }
                    };
                    status = status with { Done = status.Done + 1 };
                    _bulk[guildId] = status;
                    await Task.Delay(Step);
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Armory bulk sync failed");
                status = status with { Error = e.Message };
            }
            _bulk[guildId] = status with { Running = false, Finished = DateTimeOffset.UtcNow };
            logger.LogInformation("Armory bulk on {Guild}: {Done}/{Total}, linked {Linked}, updated {Updated}", guildId, status.Done, status.Total,
                status.Linked, status.Updated);
        });
        return true;
    }

    /// <summary>Найти персонажа по точному имени на его сервере и привязать (если найден ровно один).</summary>
    public async Task<ArmorySyncResult> AutoLinkAsync(ulong guildId, ulong userId, string name, string? scope, CharacterInfo? info)
    {
        if (Paused) return ArmorySyncResult.Unavailable;
        var server = SearchServer(guildId, scope, info);
        IReadOnlyList<ArmoryHit> hits;
        try
        {
            hits = await armory.SearchAsync(name, server);
        }
        catch (ArmoryUnavailableException)
        {
            MarkUnavailable();
            return ArmorySyncResult.Unavailable;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return ArmorySyncResult.Unavailable;
        }
        var race = info?.Race;
        var exact = hits.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Where(h => race is null || server is not null || RaceId(h.Race) == race)
            .ToList();
        if (exact.Count == 0) return ArmorySyncResult.NotFound;
        if (exact.Count > 1) return ArmorySyncResult.Ambiguous;
        await Task.Delay(Step);
        return await LinkAsync(guildId, userId, name, scope, exact[0].CharacterId, exact[0].ServerId);
    }

    /// <summary>
    /// Альты: каждый ник ищется на сервере (точное совпадение); найденный — создаётся персонаж и сразу привязывается
    /// (GS, класс, сервер из оружейной). Возвращает добавленных, ненайденных и уже существующих.
    /// </summary>
    public async Task<(List<string> Added, List<string> NotFound, List<string> Existing, bool Unavailable)> AddAltsAsync(
        ulong guildId, ulong userId, string? scope, IEnumerable<string> names, int serverId, CancellationToken ct = default)
    {
        var added = new List<string>();
        var notFound = new List<string>();
        var existing = new List<string>();
        var mine = characters.Get(guildId, userId, scope);
        foreach (var raw in names)
        {
            var name = raw.Replace("`", "").Trim();
            if (name.Length is < 2 or > CharacterStore.MaxNameLength) continue;
            if (mine.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) || added.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                existing.Add(name);
                continue;
            }
            if (Paused) return (added, notFound, existing, true);
            IReadOnlyList<ArmoryHit> hits;
            try
            {
                hits = await armory.SearchAsync(name, serverId, ct);
            }
            catch (ArmoryUnavailableException)
            {
                MarkUnavailable();
                return (added, notFound, existing, true);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return (added, notFound, existing, true);
            }
            var hit = hits.Where(h => h.ServerId == serverId && h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count != 1)
            {
                notFound.Add(name);
                continue;
            }
            // Ник — как в игре (регистр)
            characters.Add(guildId, userId, hit[0].Name, scope);
            var (result, linkedName) = await LinkNamedAsync(guildId, userId, hit[0].Name, scope, hit[0].CharacterId, hit[0].ServerId, ct);
            if (result == ArmorySyncResult.Unavailable) return (added, notFound, existing, true);
            added.Add(linkedName);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return (added, notFound, existing, false);
    }

    /// <summary>Привязать к найденному персонажу и сразу взять данные. Не вышло — привязка остаётся прежней.</summary>
    public async Task<ArmorySyncResult> LinkAsync(ulong guildId, ulong userId, string name, string? scope, string characterId, int serverId,
        CancellationToken ct = default) => (await LinkNamedAsync(guildId, userId, name, scope, characterId, serverId, ct)).Result;

    /// <summary>Привязать; Name — ник после привязки (заменяется игровым, если отличается).</summary>
    public async Task<(ArmorySyncResult Result, string Name)> LinkNamedAsync(ulong guildId, ulong userId, string name, string? scope,
        string characterId, int serverId, CancellationToken ct = default)
    {
        var old = characters.GetInfo(guildId, userId, name, scope)?.Armory;
        characters.SetArmory(guildId, userId, name, new ArmoryLink { CharacterId = characterId, ServerId = serverId }, scope);
        var r = await SyncNamedAsync(guildId, userId, name, scope, ct);
        if (r.Result != ArmorySyncResult.Updated) characters.SetArmory(guildId, userId, name, old, scope);
        return r;
    }

    public static int? RaceId(string? race) =>
        race is null ? null : race.StartsWith("Ely", StringComparison.OrdinalIgnoreCase) ? 1 : race.StartsWith("Asmo", StringComparison.OrdinalIgnoreCase) ? 2 : null;

    /// <summary>Фракция по ID сервера (1xxx — Elyos, 2xxx — Asmodians).</summary>
    public static int? RaceOfServer(int serverId) => (serverId / 1000) is 1 or 2 ? serverId / 1000 : null;

    /// <summary>Обновить персонажа из оружейной сейчас.</summary>
    public async Task<ArmorySyncResult> SyncAsync(ulong guildId, ulong userId, string name, string? scope, CancellationToken ct = default) =>
        (await SyncNamedAsync(guildId, userId, name, scope, ct)).Result;

    /// <summary>Обновить из оружейной; Name — ник после обновления (мог смениться на игровой).</summary>
    public async Task<(ArmorySyncResult Result, string Name)> SyncNamedAsync(ulong guildId, ulong userId, string name, string? scope,
        CancellationToken ct = default)
    {
        if (characters.GetInfo(guildId, userId, name, scope)?.Armory is not { } link) return (ArmorySyncResult.NotLinked, name);
        if (Paused) return (ArmorySyncResult.Unavailable, name);
        ArmoryProfile? p;
        try
        {
            p = await armory.GetAsync(link.CharacterId, link.ServerId, ct);
        }
        catch (ArmoryUnavailableException e)
        {
            MarkUnavailable();
            logger.LogWarning("Armory refused ({Status}), pausing for {Minutes} min", e.Status, Pause.TotalMinutes);
            return (ArmorySyncResult.Unavailable, name);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                     or System.Text.Json.JsonException)
        {
            logger.LogDebug(e, "Armory request failed");
            return (ArmorySyncResult.Unavailable, name);
        }

        if (p is null)
        {
            characters.SetArmory(guildId, userId, name, link with { SyncedAt = DateTimeOffset.UtcNow, Error = "not found" }, scope);
            return (ArmorySyncResult.NotFound, name);
        }

        characters.SetArmory(guildId, userId, name, link with
        {
            Name = p.Name, ClassName = p.ClassName, Level = p.Level, CombatPower = p.CombatPower, ItemLevel = p.ItemLevel,
            Legion = p.Legion, ServerName = p.ServerName ?? link.ServerName, SyncedAt = DateTimeOffset.UtcNow, Error = null
        }, scope);
        characters.SetRealm(guildId, userId, name, RaceOfServer(link.ServerId) ?? RaceId(p.Race), link.ServerId, scope);

        // Ник как в игре: неточный регистр/опечатка при добавлении или персонажа переименовали в игре
        if (p.Name.Length > 0 && !string.Equals(p.Name, name, StringComparison.Ordinal) &&
            characters.Rename(guildId, userId, name, p.Name, scope) is null)
        {
            logger.LogInformation("Armory: character {Old} renamed to {New} (user {User})", name, p.Name, userId);
            await profiles.RenameInGroupsAsync(guildId, userId, name, p.Name, scope);
            name = p.Name;
        }

        await ApplyAsync(guildId, userId, name, scope, p);
        return (ArmorySyncResult.Updated, name);
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
            var match = profiles.ClassRoles(guildId, userId, projectKey).FirstOrDefault(r => SameClass(r.Name, className));
            if (match is not null && info?.ClassRoleId != match.Id)
            {
                characters.SetInfo(guildId, userId, name, true, match.Id, false, null, scope);
                changed = true;
            }
        }

        if (changed) await profiles.RefreshCharacterAsync(guildId, userId, name, scope);
    }

    // Названия классов AION 2 на русском/украинском — роли сервера могут называться и так
    private static readonly Dictionary<string, string[]> ClassAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gladiator"] = ["гладиатор", "гладіатор", "glad"],
        ["Templar"] = ["страж", "темплар", "храмовник", "templ"],
        ["Assassin"] = ["убийца", "убивця", "ассасин", "асасин", "sin"],
        ["Ranger"] = ["стрелок", "стрілець", "рейнджер", "лучник"],
        ["Sorcerer"] = ["волшебник", "чарівник", "маг", "сорк", "sorc"],
        ["Spiritmaster"] = ["заклинатель", "заклинач", "спиритмастер", "призыватель", "sm"],
        ["Elementalist"] = ["заклинатель", "заклинач", "элементалист", "елементаліст"],
        ["Cleric"] = ["целитель", "цілитель", "клерик", "клірик", "хил", "лекарь"],
        ["Chanter"] = ["чародей", "чародій", "чантер", "чант"],
    };

    private static string Letters(string s) => new(s.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Роль сервера — этот класс: название содержит английское имя класса или его русское/украинское название.</summary>
    public static bool SameClass(string roleName, string className)
    {
        var role = Letters(roleName);
        if (role.Length == 0) return false;
        if (role.Contains(Letters(className))) return true;
        if (!ClassAliases.TryGetValue(className, out var aliases)) return false;
        // Короткие сокращения (sin, sm) — только целиком, чтобы не ловить случайные совпадения
        return aliases.Any(a => a.Length <= 4 ? role == a : role.Contains(a));
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
