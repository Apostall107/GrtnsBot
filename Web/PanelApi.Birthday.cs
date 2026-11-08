using System.Text;
using System.Text.Json;
using Discord.WebSocket;
using LfgBot.Modules;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Дни рождения участников: настройки модуля (роль «Участник», роль дня рождения, канал поздравления),
/// список дат и их правка. В день рождения роль выдаётся автоматически и снимается на следующий день.
/// </summary>
public static partial class PanelApi
{
    public sealed record BirthdaySettingsBody(bool Enabled, string? MemberRoleId, string? BirthdayRoleId, string? AnnounceChannelId);
    public sealed record BirthdayPersonBody(string? UserId, int Day, int Month, int? Year);

    private static void MapBirthdays(RouteGroupBuilder g)
    {
        g.MapGet("/", async (ulong guildId, DiscordSocketClient client, [FromServices] BirthdayStore store,
            [FromServices] BirthdayService service) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var data = store.Get(guildId);
            await WarmMembersAsync(client, guild, data.People.Keys);
            var inDays = service.Upcoming(guildId, int.MaxValue).ToDictionary(x => x.UserId, x => x.InDays);
            var people = data.People
                .Select(kv => new
                {
                    member = FindMember(client, kv.Key),
                    day = kv.Value.Day,
                    month = kv.Value.Month,
                    year = kv.Value.Year,
                    inDays = inDays.TryGetValue(kv.Key, out var d) ? d : 999
                })
                .OrderBy(x => x.inDays)
                .ToList();
            return Results.Ok(new
            {
                enabled = data.Enabled,
                memberRoleId = data.MemberRoleId?.ToString(),
                birthdayRoleId = data.BirthdayRoleId?.ToString(),
                announceChannelId = data.AnnounceChannelId?.ToString(),
                count = people.Count,
                people
            });
        });

        g.MapPut("/settings", async (ulong guildId, BirthdaySettingsBody b, DiscordSocketClient client,
            [FromServices] BirthdayStore store, [FromServices] BirthdayService service) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var birthdayRole = ParseId(b.BirthdayRoleId);
            if (b.Enabled && birthdayRole is null) return Error("Choose the role to give on the birthday.");
            if (birthdayRole is { } rid && guild.GetRole(rid) is null) return Error("The birthday role no longer exists.");
            store.UpdateSettings(guildId, s =>
            {
                s.Enabled = b.Enabled;
                s.MemberRoleId = ParseId(b.MemberRoleId);
                s.BirthdayRoleId = birthdayRole;
                s.AnnounceChannelId = ParseId(b.AnnounceChannelId);
            });
            var r = await service.ReconcileAsync(guildId);
            return Results.Ok(new { granted = r.Granted, removed = r.Removed });
        });

        // Участники для выбора: только с ролью «Участник» из настроек (если задана), поиск по имени; есть ли уже дата
        g.MapGet("/candidates", async (ulong guildId, string? q, bool? withoutDate, DiscordSocketClient client, [FromServices] BirthdayStore store) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (!guild.HasAllMembers) await guild.DownloadUsersAsync();
            var data = store.Get(guildId);
            var roleId = data.MemberRoleId;
            var query = (q ?? "").Trim();
            bool Has(string? t) => t is not null && t.Contains(query, StringComparison.OrdinalIgnoreCase);
            var pool = guild.Users.Where(u => !u.IsBot && (roleId is not { } rid || u.Roles.Any(r => r.Id == rid))).ToList();
            var list = pool
                .Where(u => query.Length == 0 || Has(u.DisplayName) || Has(u.GlobalName) || Has(u.Username))
                .Where(u => withoutDate != true || !data.People.ContainsKey(u.Id))
                .OrderBy(u => data.People.ContainsKey(u.Id)).ThenBy(u => u.DisplayName)
                .Take(60)
                .Select(u => new
                {
                    member = Member(guild, u.Id),
                    birthday = data.People.TryGetValue(u.Id, out var b) ? $"{b.Day:00}.{b.Month:00}{(b.Year is { } y ? $".{y}" : "")}" : null
                });
            return Results.Ok(new
            {
                role = roleId is { } r0 ? guild.GetRole(r0)?.Name : null,
                total = pool.Count,
                withDate = pool.Count(u => data.People.ContainsKey(u.Id)),
                members = list
            });
        });

        g.MapPost("/people", async (ulong guildId, BirthdayPersonBody b, [FromServices] BirthdayStore store,
            [FromServices] BirthdayService service) =>
        {
            if (ParseId(b.UserId) is not { } userId) return Error("Enter a valid Discord user ID.");
            var bday = new Birthday { Day = b.Day, Month = b.Month, Year = b.Year };
            if (!bday.IsValid) return Error("Enter a valid date (day 1–31, month 1–12).");
            if (!store.Set(guildId, userId, bday)) return Error("The birthday list is full.");
            await service.ReconcileAsync(guildId);
            return Results.Ok(new { ok = true });
        });

        g.MapDelete("/people/{userId}", async (ulong guildId, string userId, [FromServices] BirthdayStore store,
            [FromServices] BirthdayService service) =>
        {
            if (ParseId(userId) is not { } id) return Error("Bad user ID.");
            store.Remove(guildId, id);
            await service.ReconcileAsync(guildId);
            return Results.Ok(new { ok = true });
        });

        // Выгрузка текущего списка в CSV (он же шаблон для импорта)
        g.MapGet("/export", async (ulong guildId, DiscordSocketClient client, [FromServices] BirthdayStore store) =>
        {
            var data = store.Get(guildId);
            if (client.GetGuild(guildId) is { } guild) await WarmMembersAsync(client, guild, data.People.Keys);
            static string Q(object? v) => v is null ? "" : $"\"{v.ToString()!.Replace("\"", "\"\"")}\"";
            string Name(ulong uid) => client.Guilds.Select(x => x.GetUser(uid)).FirstOrDefault(u => u is not null)?.Username ?? "";
            var sb = new StringBuilder("﻿user_id,day,month,year,name\r\n");
            foreach (var (userId, b) in data.People)
                sb.AppendJoin(',', Q(userId), b.Day, b.Month, b.Year?.ToString() ?? "", Q(Name(userId))).Append("\r\n");
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8",
                $"birthdays-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        });

        // Массовый импорт одним файлом: CSV (user_id,day,month,year | user_id,date) или JSON [{userId,day,month,year}|{userId,date}].
        g.MapPost("/import", async (ulong guildId, HttpRequest request, DiscordSocketClient client, [FromServices] BirthdayStore store,
            [FromServices] BirthdayService service) =>
        {
            using var reader = new StreamReader(request.Body);
            var text = (await reader.ReadToEndAsync()).TrimStart('﻿').Trim();
            if (text.Length == 0) return Error("The file is empty.");

            List<(ulong UserId, Birthday Bday)> rows;
            try { rows = text.StartsWith('[') || text.StartsWith('{') ? ParseBirthdayJson(text) : ParseBirthdayCsv(text); }
            catch (JsonException) { return Error("This file isn't valid JSON."); }

            // Список строк «discord-ник / игровой ник - ДД.ММ» — люди находятся на сервере по имени
            var notFound = new List<string>();
            if (rows.Count == 0 && client.GetGuild(guildId) is { } g0)
            {
                if (!g0.HasAllMembers) await g0.DownloadUsersAsync();
                rows = ParseBirthdayNames(text, g0, notFound);
            }
            if (rows.Count == 0)
                return notFound.Count > 0
                    ? Results.Ok(new { total = 0, added = 0, updated = 0, full = 0, notFound })
                    : Error("No birthdays found. Use lines like “username / nickname - 05.02”, or CSV columns user_id, day, month, year.");

            int added = 0, updated = 0, full = 0;
            foreach (var (userId, bday) in rows)
            {
                var existed = store.Of(guildId, userId) is not null;
                if (!store.Set(guildId, userId, bday)) { full++; continue; }
                if (existed) updated++; else added++;
            }
            await service.ReconcileAsync(guildId);
            return Results.Ok(new { total = rows.Count, added, updated, full, notFound });
        });
    }

    private static readonly System.Text.RegularExpressions.Regex NameLineRe = new(
        @"^(?<left>.+?)[\s\-–—:]*(?<d>\d{1,2})[./](?<m>\d{1,2})(?:[./](?<y>\d{2,4}))?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Строки «discord-ник / игровой ник - ДД.ММ[.ГГГГ]»: человек ищется по имени пользователя Discord (первое слово),
    /// если не нашёлся — по нику на сервере / отображаемому имени (часть после «/»). Не найденные — в notFound.
    /// </summary>
    private static List<(ulong, Birthday)> ParseBirthdayNames(string text, SocketGuild guild, List<string> notFound)
    {
        var result = new List<(ulong, Birthday)>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = NameLineRe.Match(line);
            if (!m.Success) continue; // заголовки и прочий текст без даты
            int? year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : null;
            if (year is < 100) year += year > 30 ? 1900 : 2000;
            var bday = new Birthday { Day = int.Parse(m.Groups["d"].Value), Month = int.Parse(m.Groups["m"].Value), Year = year };
            if (!bday.IsValid) { notFound.Add($"{line} — bad date"); continue; }

            var left = m.Groups["left"].Value.Trim().TrimEnd('-', '–', '—', ' ');
            // «user * заметка / ник»: делитель — « / » с пробелами (в нике бывает «/» без пробелов, в заметке — «She/her»)
            string userPart, nick;
            if (left.Contains(" / "))
            {
                userPart = left[..left.IndexOf(" / ", StringComparison.Ordinal)].Trim();
                nick = left[(left.LastIndexOf(" / ", StringComparison.Ordinal) + 3)..].Trim();
            }
            else
            {
                var slash = left.IndexOf('/');
                userPart = (slash >= 0 ? left[..slash] : left).Trim();
                nick = slash >= 0 ? left[(slash + 1)..].Trim() : "";
            }
            var username = userPart.Split(new[] { ' ', '*', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

            var byUser = guild.Users.Where(u => !u.IsBot && u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).ToList();
            var found = byUser.Count == 1 ? byUser[0] : null;
            if (found is null && nick.Length > 0)
            {
                var byNick = guild.Users.Where(u => !u.IsBot && (
                    (u.Nickname?.Equals(nick, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (u.GlobalName?.Equals(nick, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    u.DisplayName.Equals(nick, StringComparison.OrdinalIgnoreCase))).ToList();
                if (byNick.Count == 1) found = byNick[0];
            }
            if (found is null) { notFound.Add(line); continue; }
            result.Add((found.Id, bday));
        }
        return result;
    }

    /// <summary>Разбирает JSON-массив объектов с userId/user_id/id и либо date, либо day/month/year.</summary>
    private static List<(ulong, Birthday)> ParseBirthdayJson(string text)
    {
        var result = new List<(ulong, Birthday)>();
        using var doc = JsonDocument.Parse(text);
        var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : default;
        foreach (var el in arr)
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            var id = Str(el, "userId") ?? Str(el, "user_id") ?? Str(el, "id");
            if (ParseId(id) is not { } userId) continue;
            Birthday? bday = null;
            if (Str(el, "date") is { } ds && BirthdayModule.TryParse(ds, out var parsed)) bday = parsed;
            else if (Int(el, "day") is { } d && Int(el, "month") is { } m)
                bday = new Birthday { Day = d, Month = m, Year = Int(el, "year") };
            if (bday is { IsValid: true }) result.Add((userId, bday));
        }
        return result;

        static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;
        static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) &&
            (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) || int.TryParse(v.ToString(), out i)) ? i : null;
    }

    /// <summary>Разбирает CSV: колонки user_id и либо date (ДД.ММ[.ГГГГ]), либо day/month/year. Заголовок необязателен.</summary>
    private static List<(ulong, Birthday)> ParseBirthdayCsv(string text)
    {
        var result = new List<(ulong, Birthday)>();
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var start = 0;
        string[]? header = null;
        if (lines.Length > 0 && lines[0].ToLowerInvariant() is var h0 && (h0.Contains("user") || h0.Contains("day") || h0.Contains("date")))
        {
            header = Cells(lines[0]).Select(c => c.ToLowerInvariant()).ToArray();
            start = 1;
        }
        int Col(string name, int fallback) => header is null ? fallback : Array.IndexOf(header, name);

        for (var i = start; i < lines.Length; i++)
        {
            var c = Cells(lines[i]);
            if (c.Length == 0) continue;
            var idCol = Col("user_id", 0);
            if (idCol < 0 || idCol >= c.Length || ParseId(c[idCol]) is not { } userId) continue;

            Birthday? bday = null;
            var dateCol = Col("date", -1);
            if (dateCol >= 0 && dateCol < c.Length && BirthdayModule.TryParse(c[dateCol], out var parsed)) bday = parsed;
            else
            {
                var dayCol = Col("day", 1); var monthCol = Col("month", 2); var yearCol = Col("year", 3);
                if (dayCol >= 0 && dayCol < c.Length && monthCol >= 0 && monthCol < c.Length &&
                    int.TryParse(c[dayCol].Trim(), out var day) && int.TryParse(c[monthCol].Trim(), out var month))
                {
                    int? year = yearCol >= 0 && yearCol < c.Length && int.TryParse(c[yearCol].Trim(), out var y) ? y : null;
                    bday = new Birthday { Day = day, Month = month, Year = year };
                }
            }
            if (bday is { IsValid: true }) result.Add((userId, bday));
        }
        return result;

        static string[] Cells(string line) => line.Split(line.Contains(';') && !line.Contains(',') ? ';' : ',')
            .Select(x => x.Trim().Trim('"')).ToArray();
    }
}
