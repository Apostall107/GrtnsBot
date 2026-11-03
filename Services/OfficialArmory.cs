using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Найденный в официальной оружейной персонаж.</summary>
public sealed record ArmoryHit(string CharacterId, string Name, int ServerId, string ServerName, int Level, string? ClassName, string? Race);

/// <summary>Данные персонажа из официальной оружейной.</summary>
public sealed record ArmoryProfile(string Name, int Level, string? ClassName, int? CombatPower, int? ItemLevel, string? Legion, string? ServerName,
    string? Race);

/// <summary>
/// Официальная оружейная AION 2 (aion2.plaync.com — страница Character Info): поиск по имени и карточка персонажа.
/// API не документирован: при ошибке вызывающий оставляет последние известные данные. Регион — Armory:Region (eu).
/// </summary>
public sealed class OfficialArmory(IHttpClientFactory http, IConfiguration config, ILogger<OfficialArmory> logger)
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private string Region => config["Armory:Region"] ?? "eu";

    private Dictionary<int, string>? _pcClasses;
    private DateTimeOffset _pcClassesAt;

    /// <summary>Ссылка на профиль на официальном сайте.</summary>
    public string ProfileUrl(int serverId, string characterId) =>
        $"https://aion2.plaync.com/en-us/characters/{serverId}/{Uri.EscapeDataString(characterId)}?region={Region}";

    private HttpClient Client()
    {
        var c = http.CreateClient("armory");
        c.Timeout = TimeSpan.FromSeconds(15);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GreatnessBot/1.0 (guild roster)");
        return c;
    }

    /// <summary>Поиск по имени (и серверу). Бросает <see cref="ArmoryUnavailableException"/>, если сервис отказывает.</summary>
    public async Task<IReadOnlyList<ArmoryHit>> SearchAsync(string name, int? serverId = null, CancellationToken ct = default)
    {
        var url = $"https://api-search.plaync.com/aion2global/search/v2/character?keyword={Uri.EscapeDataString(name)}" +
                  $"&serverId={serverId?.ToString() ?? ""}&page=1&size=30&region={Region}&localeInfo=en-US";
        using var doc = await GetJsonAsync(url, ct);
        var classes = await PcClassesAsync(ct);
        var list = new List<ArmoryHit>();
        if (doc?.RootElement.TryGetProperty("list", out var arr) == true && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
            {
                var id = WebUtility.UrlDecode(Str(x, "characterId") ?? "");
                if (id.Length == 0) continue;
                var pcId = Int(x, "pcId");
                list.Add(new ArmoryHit(id, Tags.Replace(Str(x, "name") ?? "", ""), Int(x, "serverId") ?? 0, Str(x, "serverName") ?? "",
                    Int(x, "level") ?? 0, pcId is { } p && classes.TryGetValue(p, out var cn) ? cn : null,
                    Int(x, "race") switch { 1 => "Elyos", 2 => "Asmodians", _ => null }));
            }
        return list;
    }

    /// <summary>Карточка персонажа. null — персонаж не найден (удалён/переименован).</summary>
    public async Task<ArmoryProfile?> GetAsync(string characterId, int serverId, CancellationToken ct = default)
    {
        var url = $"https://aion2.plaync.com/api/character/info?region={Region}&lang=en-US&characterId={Uri.EscapeDataString(characterId)}&serverId={serverId}";
        using var doc = await GetJsonAsync(url, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("profile", out var p)) return null;
        int? itemLevel = null;
        if (doc.RootElement.TryGetProperty("stat", out var stat) && stat.TryGetProperty("statList", out var stats) && stats.ValueKind == JsonValueKind.Array)
            foreach (var s in stats.EnumerateArray())
                if (Str(s, "type") == "ItemLevel") itemLevel = Int(s, "value");
        return new ArmoryProfile(Str(p, "characterName") ?? "", Int(p, "characterLevel") ?? 0, Str(p, "className"), Int(p, "combatPower"),
            itemLevel, Str(p, "regionName") is { Length: > 0 } legion ? legion : null, Str(p, "serverName"), Str(p, "raceName"));
    }

    private async Task<Dictionary<int, string>> PcClassesAsync(CancellationToken ct)
    {
        if (_pcClasses is { } cached && DateTimeOffset.UtcNow - _pcClassesAt < TimeSpan.FromDays(1)) return cached;
        var map = new Dictionary<int, string>();
        try
        {
            using var doc = await GetJsonAsync($"https://aion2.plaync.com/en-us/api/gameinfo/pcdata?lang=en-US&region={Region}", ct);
            if (doc?.RootElement.TryGetProperty("pcDataList", out var arr) == true)
                foreach (var x in arr.EnumerateArray())
                    if (Int(x, "id") is { } id && Str(x, "classText") is { } text) map[id] = text;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Armory class list failed");
        }
        _pcClasses = map;
        _pcClassesAt = DateTimeOffset.UtcNow;
        return map;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var res = await Client().GetAsync(url, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            throw new ArmoryUnavailableException((int)res.StatusCode);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => null
        } : null;
}

/// <summary>Оружейная временно отказывает (403/429/503) — подождать.</summary>
public sealed class ArmoryUnavailableException(int status) : Exception($"The official armory refused the request ({status}).")
{
    public int Status { get; } = status;
}
