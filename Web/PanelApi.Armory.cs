using System.Security.Claims;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>Привязка персонажей к официальной оружейной AION 2 (aion2.plaync.com): поиск, привязка, обновление.</summary>
public static partial class PanelApi
{
    /// <summary>Данные оружейной для сайта (null — не привязан).</summary>
    private static object? ArmoryDto(ArmoryLink? a) => a is null ? null : new
    {
        characterId = a.CharacterId,
        serverId = a.ServerId,
        server = a.ServerName,
        name = a.Name,
        className = a.ClassName,
        level = a.Level,
        combatPower = a.CombatPower,
        itemLevel = a.ItemLevel,
        legion = a.Legion,
        synced = a.SyncedAt?.ToUnixTimeMilliseconds(),
        error = a.Error,
        url = $"https://aion2.plaync.com/en-us/characters/{a.ServerId}/{Uri.EscapeDataString(a.CharacterId)}?region=eu"
    };

    private static object? BulkDto(ArmoryBulkStatus? s) => s is null ? null : new
    {
        running = s.Running, total = s.Total, done = s.Done, linked = s.Linked, updated = s.Updated, notFound = s.NotFound,
        ambiguous = s.Ambiguous, failed = s.Failed, started = s.Started.ToUnixTimeMilliseconds(), finished = s.Finished?.ToUnixTimeMilliseconds(),
        error = s.Error
    };

    /// <summary>Серверы EU по фракциям — для выбора на сайте.</summary>
    private static async Task<object> ArmoryServersDto(OfficialArmory armory, CancellationToken ct) =>
        (await armory.ServersAsync(ct)).Select(x => new { race = x.Race, id = x.Id, name = x.Name });

    private static void MapMemberArmory(RouteGroupBuilder g)
    {
        g.MapGet("/armory/servers", async ([FromServices] OfficialArmory armory, CancellationToken ct) =>
            Results.Ok(await ArmoryServersDto(armory, ct)));

        // Поиск персонажа по имени (сервер делает запрос сам — браузеру официальный API недоступен)
        g.MapGet("/armory/search", async (ulong guildId, string? name, int? server, ClaimsPrincipal user,
            [FromServices] OfficialArmory armory, [FromServices] ArmorySyncService sync, CancellationToken ct) =>
        {
            var q = (name ?? "").Trim();
            if (q.Length is < 2 or > 32) return Error("Enter 2–32 characters of the name.");
            if (sync.Paused) return Error("The official armory isn't answering right now — try again later.");
            if (!sync.TrySearchSlot(user.UserId()!.Value)) return Error("Too fast — wait a few seconds.");
            try
            {
                var hits = await armory.SearchAsync(q, server is > 0 ? server : null, ct);
                return Results.Ok(hits.Select(x => new
                {
                    characterId = x.CharacterId, name = x.Name, serverId = x.ServerId, server = x.ServerName,
                    level = x.Level, className = x.ClassName, race = x.Race
                }));
            }
            catch (ArmoryUnavailableException)
            {
                sync.MarkUnavailable();
                return Error("The official armory isn't answering right now — try again later.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return Error("Couldn't reach the official armory — try again.");
            }
        });

        // Привязать персонажа: проверяем карточку и сразу берём данные
        g.MapPost("/characters/{name}/armory", async (ulong guildId, string name, string? project, ArmoryLinkBody body, ClaimsPrincipal user,
            DiscordSocketClient client, [FromServices] CharacterStore characters, [FromServices] LfgProjectStore projects,
            [FromServices] ArmorySyncService sync, CancellationToken ct) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var me = user.UserId()!.Value;
            var exact = characters.Get(guildId, me, scope.Value).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return Error("Character not found.");
            var id = (body.CharacterId ?? "").Trim();
            if (id.Length is 0 or > 200 || body.ServerId is not (> 0 and < 100000)) return Error("Pick a character from the search.");

            var (result, current) = await sync.LinkNamedAsync(guildId, me, exact, scope.Value, id, body.ServerId, ct);
            if (result == ArmorySyncResult.Updated)
            {
                var info = characters.GetInfo(guildId, me, current, scope.Value);
                return Results.Ok(new
                {
                    gs = info?.Gs, combatPower = info?.Armory?.CombatPower, itemLevel = info?.Armory?.ItemLevel,
                    renamed = current != exact ? current : null
                });
            }
            return Error(result == ArmorySyncResult.NotFound
                ? "This character wasn't found in the official armory."
                : "The official armory isn't answering right now — try again later.");
        });

        // Альты: список ников → найти на сервере привязанного персонажа (или выбранном) и создать с привязкой
        g.MapPost("/characters/armory-alts", async (ulong guildId, string? project, ArmoryAltsBody body, ClaimsPrincipal user,
            DiscordSocketClient client, [FromServices] CharacterStore characters, [FromServices] LfgProjectStore projects,
            [FromServices] ArmorySyncService sync, CancellationToken ct) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var me = user.UserId()!.Value;
            var names = (body.Names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0) return Error("Enter the names of your other characters.");
            if (names.Count > 10) return Error("Up to 10 names at a time.");
            if (body.ServerId is not (> 0 and < 100000)) return Error("Pick the server.");
            var free = CharacterStore.MaxPerUser - characters.Get(guildId, me, scope.Value).Count;
            if (free <= 0) return Error($"You already have {CharacterStore.MaxPerUser} characters.");
            if (sync.Paused) return Error("The official armory isn't answering right now — try again later.");
            if (!sync.TrySearchSlot(me)) return Error("Too fast — wait a few seconds.");
            var r = await sync.AddAltsAsync(guildId, me, scope.Value, names.Take(free), body.ServerId, ct);
            return Results.Ok(new { added = r.Added, notFound = r.NotFound, existing = r.Existing, unavailable = r.Unavailable });
        });

        // Обновить всех своих привязанных персонажей проекта
        g.MapPost("/characters/armory-refresh-all", async (ulong guildId, string? project, ClaimsPrincipal user, DiscordSocketClient client,
            [FromServices] LfgProjectStore projects, [FromServices] ArmorySyncService sync, CancellationToken ct) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            if (sync.Paused) return Error("The official armory isn't answering right now — try again later.");
            var me = user.UserId()!.Value;
            if (!sync.TrySearchSlot(me)) return Error("Too fast — wait a few seconds.");
            var r = await sync.RefreshAllAsync(guildId, me, scope.Value, ct);
            return Results.Ok(new { updated = r.Updated, skipped = r.Skipped, renamed = r.Renamed, notFound = r.NotFound, unavailable = r.Unavailable });
        });

        g.MapDelete("/characters/{name}/armory", (ulong guildId, string name, string? project, ClaimsPrincipal user, DiscordSocketClient client,
            [FromServices] CharacterStore characters, [FromServices] LfgProjectStore projects) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var me = user.UserId()!.Value;
            var exact = characters.Get(guildId, me, scope.Value).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return Error("Character not found.");
            characters.SetArmory(guildId, me, exact, null, scope.Value);
            return Results.Ok();
        });

        // Обновить сейчас (не чаще раза в 15 минут)
        g.MapPost("/characters/{name}/armory/refresh", async (ulong guildId, string name, string? project, ClaimsPrincipal user,
            DiscordSocketClient client, [FromServices] CharacterStore characters, [FromServices] LfgProjectStore projects,
            [FromServices] ArmorySyncService sync, CancellationToken ct) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var me = user.UserId()!.Value;
            var exact = characters.Get(guildId, me, scope.Value).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return Error("Character not found.");
            if (characters.GetInfo(guildId, me, exact, scope.Value)?.Armory is not { } link) return Error("Link the character first.");
            if (link.SyncedAt is { } at && DateTimeOffset.UtcNow - at < ArmorySyncService.ManualCooldown)
                return Error($"Updated {(int)(DateTimeOffset.UtcNow - at).TotalMinutes} min ago — you can refresh once per {ArmorySyncService.ManualCooldown.TotalMinutes:0} minutes.");
            var (synced, current) = await sync.SyncNamedAsync(guildId, me, exact, scope.Value, ct);
            return synced switch
            {
                ArmorySyncResult.Updated => Results.Ok(new { renamed = current != exact ? current : null }),
                ArmorySyncResult.NotFound => Error("The character wasn't found in the official armory (renamed or deleted?). Link it again."),
                _ => Error("The official armory isn't answering right now — try again later.")
            };
        });
    }
}

public sealed record ArmoryAltsBody(List<string>? Names, int ServerId);
public sealed record ArmoryLinkBody(string? CharacterId, int ServerId);
