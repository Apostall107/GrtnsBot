using System.Security.Claims;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Предпросмотр сообщений бота на сайте: бот собирает настоящее сообщение (embed + кнопки) с черновыми текстами
/// из формы — ничего не сохраняется и не отправляется. Сайт рисует его как сообщение Discord.
/// </summary>
public static partial class PanelApi
{
    private static readonly Regex MentionRe = new(@"<(@[!&]?|#)(\d+)>", RegexOptions.Compiled);

    private static void MapPreviews(RouteGroupBuilder texts, RouteGroupBuilder tickets)
    {
        // Панели (LFG, Tickets, Room controls) и подписи кнопок постов LFG / тикетов — из формы Panel texts
        texts.MapPost("/{key}/preview", (ulong guildId, string key, PanelTextBody body, ClaimsPrincipal user, DiscordSocketClient client,
            PanelService panels, LfgStore lfg, TicketStore ticketStore) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (PanelDefs.Find(key) is not { } def) return Error("Unknown panel.");
            var (text, error) = DraftPanelText(def, body);
            if (error is not null) return Error(error);

            using var _ = PanelTextStore.Preview(key, text);
            return Results.Ok(key switch
            {
                "lfgpost" => SampleGroupPreview(guild, user, lfg),
                "ticketmsg" => SampleTicketPreview(guild, user, ticketStore, null),
                _ => PanelPreview(guild, panels, key)
            });
        });

        // Тексты бота (Bot messages): посты групп и сообщения тикетов — с черновыми текстами группы
        texts.MapPost("/bot/preview", (ulong guildId, BotPreviewBody body, ClaimsPrincipal user, DiscordSocketClient client,
            LfgStore lfg, TicketStore ticketStore) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var values = (body.Values ?? []).Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
            using var _ = Tx.Preview(values);
            return body.Sample switch
            {
                "lfgpost" => Results.Ok(SampleGroupPreview(guild, user, lfg)),
                "ticketmsg" => Results.Ok(SampleTicketPreview(guild, user, ticketStore, null)),
                _ => Error("No preview for this group.")
            };
        });

        // Сообщение в тикете с черновым текстом проекта (Tickets → проект)
        tickets.MapPost("/projects/{key}/preview", (ulong guildId, string key, ProjectUpdateBody body, ClaimsPrincipal user,
            DiscordSocketClient client, TicketStore ticketStore) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var project = ticketStore.FindProject(guildId, key) ?? new TicketProject { Key = key, Name = "Project" };
            var draft = new TicketProject
            {
                Key = project.Key,
                Name = string.IsNullOrWhiteSpace(body.Name) ? project.Name : body.Name.Trim(),
                StaffRoleIds = body.StaffRoleIds is null ? project.StaffRoleIds : ParseIds(body.StaffRoleIds),
                MemberRoleIds = body.MemberRoleIds is null ? project.MemberRoleIds : ParseIds(body.MemberRoleIds),
                CustomText = body.CustomText is null ? project.CustomText : body.CustomText,
                FormEnabled = body.FormEnabled ?? project.FormEnabled,
                Questions = body.Questions is null ? project.Questions
                    : body.Questions.Where(q => !string.IsNullOrWhiteSpace(q.Label)).Select(q => new TicketQuestion { Label = q.Label!.Trim() }).ToList()
            };
            return Results.Ok(SampleTicketPreview(guild, user, ticketStore, draft));
        });
    }

    /// <summary>Черновой текст панели из формы (те же правила, что при сохранении).</summary>
    private static (PanelText? Text, string? Error) DraftPanelText(PanelDef def, PanelTextBody body)
    {
        var labelsOnly = PanelDefs.Messages.Contains(def);
        var title = labelsOnly ? null
            : body.Title is not null && (string.IsNullOrWhiteSpace(body.Title) || body.Title.Trim() == PanelDef.NoTitle) ? PanelDef.NoTitle
            : Clean(body.Title, def.DefaultTitle);
        var description = labelsOnly ? null : Clean(body.Description, def.DefaultDescription);
        if (title is { Length: > PanelDef.MaxTitle }) return (null, $"The title is limited to {PanelDef.MaxTitle} characters.");
        if (description is { Length: > PanelDef.MaxDescription }) return (null, $"The text is limited to {PanelDef.MaxDescription} characters.");

        var labels = new Dictionary<string, string>();
        foreach (var l in def.Labels)
        {
            var value = body.Labels is not null && body.Labels.TryGetValue(l.Id, out var v) ? Clean(v, l.Default) : null;
            if (value is null) continue;
            if (value.Length > l.MaxLength) return (null, $"{l.Name}: up to {l.MaxLength} characters.");
            labels[l.Id] = value;
        }

        string? color = null;
        if (!labelsOnly && !string.IsNullOrWhiteSpace(body.Color))
        {
            if (Tx.ParseColor(body.Color) is not { } parsed) return (null, "Color: use the #rrggbb format.");
            color = parsed.RawValue == def.DefaultColor ? null : $"#{parsed.RawValue:x6}";
        }

        return (title is null && description is null && labels.Count == 0 && color is null
            ? null
            : new PanelText { Title = title, Description = description, Color = color, Labels = labels }, null);
    }

    private static object PanelPreview(SocketGuild guild, PanelService panels, string key)
    {
        var (embed, comps) = panels.Build(guild.Id, key);
        return MessagePreview(guild, null, [embed], comps);
    }

    /// <summary>Пример поста группы: настоящие участники сервера, часть слотов занята, есть резерв и требования.</summary>
    private static object SampleGroupPreview(SocketGuild guild, ClaimsPrincipal user, LfgStore lfg)
    {
        // Активная группа сервера — самый честный пример; нет ни одной — собираем свою
        var real = lfg.Groups.Values.Where(g => g.GuildId == guild.Id && !g.IsClosed).OrderBy(g => g.StartsAt ?? DateTimeOffset.MaxValue).FirstOrDefault();
        var group = real ?? SampleGroup(guild, user);
        var screen = LfgRenderer.Group(group);
        return MessagePreview(guild, null, screen.Embed is null ? [] : [screen.Embed], screen.Components,
            real is null ? "Sample group" : $"Active group: {real.ActivityName}");
    }

    private static LfgGroup SampleGroup(SocketGuild guild, ClaimsPrincipal user)
    {
        var people = guild.Users.Where(u => !u.IsBot).Take(4).Select(u => (u.Id, u.DisplayName)).ToList();
        var me = user.UserId() ?? people.FirstOrDefault().Id;
        var roles = new[] { SlotRole.Tank, SlotRole.Dps, SlotRole.Dps, SlotRole.Healer, SlotRole.Dps, SlotRole.Any };
        var group = new LfgGroup
        {
            GuildId = guild.Id,
            OwnerId = me,
            ActivityName = "Ludra Hard",
            StartsAt = DateTimeOffset.UtcNow.AddHours(2),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(5),
            MinGs = 3000,
            Note = "Bring buff food, voice chat required",
            Slots = roles.Select(r => new Slot { Role = r }).ToList()
        };
        for (var i = 0; i < Math.Min(3, people.Count); i++)
        {
            group.Slots[i].UserId = i == 0 ? me : people[i].Id;
            group.Slots[i].TakenAs = group.Slots[i].Role;
            group.Slots[i].CharacterName = i == 0 ? "Organizer" : people[i].DisplayName;
            group.Slots[i].Gs = 3100 + i * 20;
        }
        if (people.Count > 3)
            group.Reserve.Add(new ReserveEntry { UserId = people[3].Id, Role = SlotRole.Dps, CharacterName = people[3].DisplayName });
        return group;
    }

    /// <summary>Пример сообщения в тикете: заявитель — тот, кто смотрит; анкета из проекта (если есть).</summary>
    private static object SampleTicketPreview(SocketGuild guild, ClaimsPrincipal user, TicketStore store, TicketProject? draft)
    {
        var project = draft ?? store.GetProjects(guild.Id).FirstOrDefault() ?? new TicketProject { Name = "Aion 2" };
        var ticket = new Ticket
        {
            GuildId = guild.Id,
            ProjectKey = project.Key,
            UserId = user.UserId() ?? 0,
            VoiceChannelId = guild.VoiceChannels.FirstOrDefault()?.Id ?? 0,
            Answers = project.FormEnabled
                ? project.Questions.Take(5).Select(q => new TicketAnswer { Question = q.Label, Answer = "Answer of the applicant…" }).ToList()
                : []
        };
        var (embeds, comps) = TicketRenderer.TicketMessage(ticket, project);
        return MessagePreview(guild, null, embeds, comps, $"Ticket · {project.Name}");
    }

    /// <summary>Сообщение → данные для рисования на сайте; упоминания и каналы — уже с именами.</summary>
    private static object MessagePreview(SocketGuild guild, string? content, IEnumerable<Embed> embeds, MessageComponent? comps, string? note = null)
    {
        var mentions = new Dictionary<string, string>();
        void Collect(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in MentionRe.Matches(text))
            {
                var kind = m.Groups[1].Value;
                if (!ulong.TryParse(m.Groups[2].Value, out var id)) continue;
                mentions[m.Value] = kind switch
                {
                    "@&" => "@" + (guild.GetRole(id)?.Name ?? "role"),
                    "#" => "#" + (guild.GetChannel(id)?.Name ?? "channel"),
                    _ => "@" + (guild.GetUser(id)?.DisplayName ?? "member")
                };
            }
        }

        var list = embeds.ToList();
        Collect(content);
        foreach (var e in list)
        {
            Collect(e.Title);
            Collect(e.Description);
            Collect(e.Footer?.Text);
            foreach (var f in e.Fields) { Collect(f.Name); Collect(f.Value); }
        }

        var rows = comps?.Components.OfType<ActionRowComponent>().Select(row => row.Components.Select(c => (object)(c switch
        {
            ButtonComponent b => new
            {
                type = "button",
                label = b.Label,
                emoji = b.Emote?.ToString(),
                style = b.Style.ToString(),
                url = b.Url,
                disabled = b.IsDisabled
            },
            SelectMenuComponent s => new
            {
                type = "select",
                label = s.Placeholder,
                emoji = (string?)null,
                style = s.Type.ToString(),
                url = (string?)null,
                disabled = s.IsDisabled
            },
            _ => new { type = "other", label = (string?)null, emoji = (string?)null, style = "", url = (string?)null, disabled = false }
        })).ToList()).ToList() ?? [];

        return new
        {
            note,
            content,
            mentions,
            embeds = list.Select(e => new
            {
                title = e.Title,
                description = e.Description,
                color = e.Color is { } c ? $"#{c.RawValue:x6}" : null,
                image = e.Image?.Url,
                thumbnail = e.Thumbnail?.Url,
                footer = e.Footer?.Text,
                fields = e.Fields.Select(f => new { name = f.Name, value = f.Value, inline = f.Inline }),
                timestamp = e.Timestamp?.ToUnixTimeMilliseconds()
            }),
            rows
        };
    }
}

public sealed record BotPreviewBody(string? Sample, Dictionary<string, string?>? Values);
