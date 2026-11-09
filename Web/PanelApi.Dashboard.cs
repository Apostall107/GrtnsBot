using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Дашборд за период: участники (снимки по дням, вступления, активность), тикеты (создано / обработано, решения,
/// скорость, кто разбирает), LFG (сборы, заполнение, явка, активности, организаторы, когда играют).
/// Плюс те же цифры за предыдущий такой же период — для сравнения. days = 0 — за всё время.
/// </summary>
public static partial class PanelApi
{
    private static IEnumerable<RouteHandlerBuilder> MapDashboard(RouteGroupBuilder g)
    {
        yield return g.MapGet("/dashboard", (ulong guildId, int? days, DiscordSocketClient client,
            [FromServices] GuildStatsStore stats, [FromServices] TicketStore tickets, [FromServices] TicketArchiveStore archive,
            [FromServices] LfgHistoryStore history, [FromServices] MemberActivityStore activity, [FromServices] TimeService time) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var now = DateTimeOffset.UtcNow;
            var period = days is > 0 ? Math.Min(days.Value, 3650) : 0;
            var from = period > 0 ? now.AddDays(-period) : DateTimeOffset.MinValue;
            var prevFrom = period > 0 ? now.AddDays(-2 * period) : DateTimeOffset.MinValue;
            bool In(DateTimeOffset t) => t >= from && t <= now;
            bool InPrev(DateTimeOffset t) => period > 0 && t >= prevFrom && t < from;

            // Корзины: неделя (до полугода) или месяц, в часовом поясе сервера
            var monthly = period == 0 || period > 190;
            string Bucket(DateTimeOffset t)
            {
                var d = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, time.Zone).DateTime);
                if (monthly) return $"{d.Year:D4}-{d.Month:D2}";
                var monday = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
                return monday.ToString("yyyy-MM-dd");
            }

            // ---------- Участники ----------
            var snaps = stats.Last(guildId, GuildStatsStore.KeepDays)
                .Where(s => period == 0 || string.CompareOrdinal(s.Date, GuildStatsStore.Key(DateOnly.FromDateTime(from.UtcDateTime))) >= 0).ToList();
            var startSnap = snaps.FirstOrDefault();
            var users = guild.Users.Where(u => !u.IsBot).ToList();
            var joins = users.Where(u => u.JoinedAt is { } j && In(j)).ToList();
            var joinsPrev = users.Count(u => u.JoinedAt is { } j && InPrev(j));
            bool OnServer(ulong id) => guild.GetUser(id) is not null;
            var active7 = activity.CountActive(guildId, now.AddDays(-7), OnServer);
            var active30 = activity.CountActive(guildId, now.AddDays(-30), OnServer);

            // ---------- Тикеты ----------
            var all = archive.Query(guildId, null, null, null, 0, int.MaxValue);
            var open = tickets.GetTickets(guildId);
            var createdTimes = all.Select(e => e.OpenedAt).Concat(open.Select(t => t.CreatedAt)).ToList();
            var processed = all.Where(e => In(e.ClosedAt)).ToList();
            var decided = processed.Where(e => e.Outcome is "approved" or "denied").ToList();
            double? Median(IEnumerable<double> xs)
            {
                var a = xs.OrderBy(x => x).ToList();
                return a.Count == 0 ? null : Math.Round(a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2, 1);
            }
            var ticketWeeks = createdTimes.Where(In).GroupBy(Bucket).ToDictionary(x => x.Key, x => x.Count());
            var processedWeeks = processed.GroupBy(e => Bucket(e.ClosedAt)).ToDictionary(x => x.Key, x => x.Count());

            // ---------- LFG ----------
            var groups = history.Get(guildId, null).Where(e => In(e.ClosedAt)).ToList();
            var groupsPrev = period > 0 ? history.Get(guildId, prevFrom).Count(e => InPrev(e.ClosedAt)) : 0;
            var seats = groups.Sum(e => e.Members.Count);
            var size = groups.Sum(e => Math.Max(e.Size, e.Members.Count));
            var marked = groups.SelectMany(e => e.Members).Where(m => m.Attended is not null).ToList();
            var heat = new int[7, 24];
            foreach (var e in groups)
            {
                var local = TimeZoneInfo.ConvertTime(e.StartsAt ?? e.ClosedAt, time.Zone);
                heat[((int)local.DayOfWeek + 6) % 7, local.Hour]++;
            }
            var players = LfgStats.Players(groups);

            var first = new
            {
                snapshot = stats.Last(guildId, GuildStatsStore.KeepDays).FirstOrDefault()?.Date,
                ticket = createdTimes.Count == 0 ? (long?)null : createdTimes.Min().ToUnixTimeMilliseconds(),
                lfg = history.Get(guildId, null).Select(e => e.ClosedAt).DefaultIfEmpty().Min() is var lf && lf != default ? lf.ToUnixTimeMilliseconds() : (long?)null,
                activity = activity.TrackingSince.ToUnixTimeMilliseconds()
            };

            return Results.Ok(new
            {
                days = period,
                monthly,
                dataSince = first,
                members = new
                {
                    now = guild.MemberCount,
                    change = startSnap is null ? (int?)null : guild.MemberCount - startSnap.Members,
                    joined = joins.Count,
                    joinedPrev = period > 0 ? joinsPrev : (int?)null,
                    active7, active30,
                    series = snaps.Select(s => new { date = s.Date, members = s.Members, active7 = s.Active7 }),
                    joinsBy = joins.GroupBy(u => Bucket(u.JoinedAt!.Value)).OrderBy(x => x.Key).Select(x => new { key = x.Key, count = x.Count() })
                },
                tickets = new
                {
                    created = createdTimes.Count(In),
                    createdPrev = period > 0 ? createdTimes.Count(InPrev) : (int?)null,
                    processed = processed.Count,
                    processedPrev = period > 0 ? all.Count(e => InPrev(e.ClosedAt)) : (int?)null,
                    approved = processed.Count(e => e.Outcome == "approved"),
                    denied = processed.Count(e => e.Outcome == "denied"),
                    closed = processed.Count(e => e.Outcome is not "approved" and not "denied"),
                    openNow = open.Count,
                    medianHours = Median(decided.Select(e => (e.ClosedAt - e.OpenedAt).TotalHours)),
                    by = ticketWeeks.Keys.Union(processedWeeks.Keys).OrderBy(k => k).Select(k => new
                    {
                        key = k, created = ticketWeeks.GetValueOrDefault(k), processed = processedWeeks.GetValueOrDefault(k)
                    }),
                    projects = processed.GroupBy(e => e.Project).OrderByDescending(x => x.Count()).Take(8).Select(x => new
                    {
                        name = x.Key, total = x.Count(), approved = x.Count(e => e.Outcome == "approved"), denied = x.Count(e => e.Outcome == "denied")
                    }),
                    reviewers = decided.Where(e => e.ById is not null).GroupBy(e => e.ById!.Value).OrderByDescending(x => x.Count()).Take(6)
                        .Select(x => new { member = Member(guild, x.Key), count = x.Count(), medianHours = Median(x.Select(e => (e.ClosedAt - e.OpenedAt).TotalHours)) })
                },
                lfg = new
                {
                    groups = groups.Count,
                    groupsPrev = period > 0 ? groupsPrev : (int?)null,
                    players = groups.SelectMany(e => e.Members.Select(m => m.UserId)).Distinct().Count(),
                    fill = size == 0 ? (double?)null : Math.Round(100.0 * seats / size),
                    attendance = marked.Count == 0 ? (double?)null : Math.Round(100.0 * marked.Count(m => m.Attended == true) / marked.Count),
                    marked = marked.Count,
                    by = groups.GroupBy(e => Bucket(e.ClosedAt)).OrderBy(x => x.Key).Select(x => new
                    {
                        key = x.Key, groups = x.Count(), people = x.Sum(e => e.Members.Count)
                    }),
                    activities = groups.GroupBy(e => e.Activity).OrderByDescending(x => x.Count()).Take(8)
                        .Select(x => new { name = x.Key, groups = x.Count(), avgFilled = Math.Round(x.Average(e => e.Members.Count), 1) }),
                    organizers = players.Where(p => p.Organized > 0).OrderByDescending(p => p.Organized).Take(5)
                        .Select(p => new { member = Member(guild, p.UserId), count = p.Organized }),
                    heat = Enumerable.Range(0, 7).Select(d => Enumerable.Range(0, 24).Select(hh => heat[d, hh]).ToArray())
                }
            });
        });
    }
}
