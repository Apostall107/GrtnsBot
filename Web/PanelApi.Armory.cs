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

    private static void MapMemberArmory(RouteGroupBuilder g)
    {
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

            var old = characters.GetInfo(guildId, me, exact, scope.Value)?.Armory;
            characters.SetArmory(guildId, me, exact, new ArmoryLink { CharacterId = id, ServerId = body.ServerId }, scope.Value);
            var result = await sync.SyncAsync(guildId, me, exact, scope.Value, ct);
            if (result == ArmorySyncResult.Updated) return Results.Ok();
            characters.SetArmory(guildId, me, exact, old, scope.Value); // не вышло — как было
            return Error(result == ArmorySyncResult.NotFound
                ? "This character wasn't found in the official armory."
                : "The official armory isn't answering right now — try again later.");
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
            return await sync.SyncAsync(guildId, me, exact, scope.Value, ct) switch
            {
                ArmorySyncResult.Updated => Results.Ok(),
                ArmorySyncResult.NotFound => Error("The character wasn't found in the official armory (renamed or deleted?). Link it again."),
                _ => Error("The official armory isn't answering right now — try again later.")
            };
        });
    }
}

public sealed record ArmoryLinkBody(string? CharacterId, int ServerId);
