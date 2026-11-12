using System.Security.Claims;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>Участникам: календарь, готовность, личные ЛС, состав гильдии и топ активных (если админ открыл).</summary>
public static partial class PanelApi
{
    private static void MapMemberGuild(RouteGroupBuilder g)
    {
        // Календарь: только сборы проектов, к которым у человека есть доступ
        g.MapGet("/calendar", async (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, [FromServices] LfgStore store,
            [FromServices] RecurringLfgStore recurring, [FromServices] TimeService time, [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            await WarmMembersAsync(client, guild, CalendarOwners(guildId, store, recurring));
            var me = user.UserId()!.Value;
            var member = guild.GetUser(me);
            bool Allowed(string? key) => key is null || LfgProjectStore.CanUse(member, projects.Find(guildId, key));
            return Results.Ok(new { zone = time.ZoneName, events = CalendarEvents(guild, store, recurring, time, projects, Allowed, me) });
        });

        // «✅ Готов» с сайта (как кнопка в напоминании)
        g.MapPost("/groups/{groupId}/ready", async (ulong guildId, string groupId, ClaimsPrincipal user, [FromServices] LfgStore lfg,
            [FromServices] LfgSignupService signup) =>
        {
            if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != guildId) return Error("This group is no longer active.");
            var me = user.UserId()!.Value;
            await group.Gate.WaitAsync();
            try
            {
                if (group.Slots.Find(s => s.UserId == me) is not { } slot) return Error("You're not in this group.");
                if (!slot.Ready)
                {
                    slot.Ready = true;
                    // pressed ✅ Ready → counts as came (the organizer can still change it in Attendance)
                    slot.Attended ??= true;
                    lfg.SaveGroups();
                    await signup.RefreshPostAsync(group);
                }
            }
            finally
            {
                group.Gate.Release();
            }
            return Results.Ok();
        });

        // «GS актуален»: обновить дату без изменения значения
        g.MapPost("/characters/{name}/gs-confirm", (ulong guildId, string name, string? project, ClaimsPrincipal user, DiscordSocketClient client,
            [FromServices] CharacterStore characters, [FromServices] LfgProjectStore projects) =>
        {
            if (CharacterScope(guildId, project, user, client, projects) is not { } scope) return Error("You don't have the role of this project.");
            var me = user.UserId()!.Value;
            var exact = characters.Get(guildId, me, scope.Value).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is null) return Error("Character not found.");
            if (characters.GetInfo(guildId, me, exact, scope.Value)?.Gs is not { } gs) return Error("Set the GS first.");
            characters.SetGs(guildId, me, exact, gs, scope.Value, confirm: true);
            return Results.Ok();
        });

        // Мои ЛС: только те, что админ включил на сервере
        g.MapGet("/notify", (ulong guildId, ClaimsPrincipal user, [FromServices] UserDmStore userDm, [FromServices] LfgNotifyStore notify) =>
        {
            var me = user.UserId()!.Value;
            var list = new List<object>();
            if (userDm.GuildEnabled(guildId, "lfgNewGroup")) list.Add(new { key = "lfgNewGroup", enabled = notify.IsEnabled(guildId, me) });
            foreach (var key in UserDmStore.Keys.Where(k => userDm.GuildEnabled(guildId, k)))
                list.Add(new { key, enabled = !userDm.IsOff(guildId, me, key) });
            return Results.Ok(list);
        });

        g.MapPut("/notify", (ulong guildId, NotifyBody body, ClaimsPrincipal user, [FromServices] UserDmStore userDm, [FromServices] LfgNotifyStore notify) =>
        {
            var me = user.UserId()!.Value;
            if (body.Key == "lfgNewGroup") notify.SetEnabled(guildId, me, body.Enabled);
            else if (UserDmStore.Keys.Contains(body.Key)) userDm.Set(guildId, me, body.Key!, body.Enabled);
            else return Error("Unknown notification.");
            return Results.Ok();
        });

        // Гильдия для участников: состав своих проектов (видят все) и топ активных за 30 дней (без no-show, если включён)
        g.MapGet("/guild", async (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, [FromServices] GuildPrefsStore prefs,
            [FromServices] CharacterStore characters, [FromServices] LfgProjectStore projects, [FromServices] LfgHistoryStore history) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            await WarmMembersAsync(client, guild, characters.All(guildId).Select(c => c.UserId)
                .Concat(history.Get(guildId, DateTimeOffset.UtcNow.AddDays(-30)).SelectMany(e => e.Members.Select(m => m.UserId))));
            var p = prefs.Get(guildId);
            var member = guild.GetUser(user.UserId()!.Value);
            var def = projects.DefaultKey(guildId);
            var allowedProjects = projects.GetProjects(guildId).Where(x => LfgProjectStore.CanUse(member, x)).ToList();
            var allowedKeys = allowedProjects.Select(x => x.Key).ToHashSet();
            bool Allowed(string? key) => key is null || allowedKeys.Contains(key) || allowedProjects.Count == 0 && projects.GetProjects(guildId).Count == 0;

            var roster = characters.All(guildId)
                    .Where(c => OnServer(guild, c.UserId) && Allowed(c.Scope ?? def))
                    .Select(c =>
                    {
                        var key = c.Scope ?? def;
                        var classId = c.Info?.ClassRoleId;
                        return new
                        {
                            member = Member(guild, c.UserId),
                            character = c.Name,
                            project = key,
                            classRole = classId is { } id ? Role(guild, id) : null,
                            classIcon = classId is { } ci ? projects.ClassIcon(guildId, guild.GetRole(ci), key) : null,
                            slotRoles = classId is { } sid && projects.EffectiveClassSlotRoles(guildId, key).TryGetValue(sid, out var sr)
                                ? sr.Select(r => r.ToString()).ToList() : [],
                            gs = c.Info?.Gs,
                            armory = ArmoryDto(c.Info?.Armory),
                            server = c.Info?.Armory?.ServerName ?? OfficialArmory.ServerName(c.Info?.ServerId),
                            // Фракция по серверу: 1xxx — Elyos, 2xxx — Asmodians
                            race = ArmorySyncService.RaceOfServer(c.Info?.Armory?.ServerId ?? c.Info?.ServerId ?? 0)
                        };
                    })
                    .OrderBy(x => x.character, StringComparer.OrdinalIgnoreCase)
                    .ToList<object>();

            // Топ: по каждому проекту отдельно (ключ проекта; "" — все доступные вместе)
            var recent = history.Get(guildId, DateTimeOffset.UtcNow.AddDays(-30)).Where(e => Allowed(e.ProjectKey ?? def)).ToList();
            List<object> Top(string? projectKey) => recent
                    .Where(e => projectKey is null || (e.ProjectKey ?? def) == projectKey)
                    .SelectMany(e => e.Members.Where(m => m.Attended != false))
                    .GroupBy(m => m.UserId)
                    .Where(x => OnServer(guild, x.Key))
                    .Select(x => new
                    {
                        member = Member(guild, x.Key),
                        groups = x.Count(),
                        role = x.GroupBy(m => m.Role).OrderByDescending(r => r.Count()).First().Key
                    })
                    .OrderByDescending(x => x.groups).Take(10)
                    .ToList<object>();
            var top = p.MemberTop ? Top(null) : null;
            var topByProject = p.MemberTop ? allowedProjects.ToDictionary(x => x.Key, x => Top(x.Key)) : null;

            return Results.Ok(new
            {
                projects = allowedProjects.Select(x => new { key = x.Key, name = x.Name, isDefault = x.Key == def }),
                roster,
                top,
                topByProject
            });
        });
    }
}

public sealed record NotifyBody(string? Key, bool Enabled);
