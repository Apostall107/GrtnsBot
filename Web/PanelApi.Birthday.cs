using Discord.WebSocket;
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
    }
}
