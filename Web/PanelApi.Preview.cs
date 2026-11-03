using System.Security.Claims;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

        // Bot messages: все экраны группы текстов (шаги мастера, меню, окна) — с черновыми текстами и списком ключей каждого экрана
        texts.MapPost("/bot/screens", (ulong guildId, BotPreviewBody body, ClaimsPrincipal user, DiscordSocketClient client,
            [FromServices] LfgStore lfg, [FromServices] ActivityStore activities, [FromServices] LfgTemplateStore templates,
            [FromServices] LfgProfileService profile, [FromServices] TicketStore ticketStore, [FromServices] TimeService time) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var values = (body.Values ?? []).Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
            using var _ = Tx.Preview(values);
            var screens = new List<object>();
            void Add(string title, Func<object> render)
            {
                var keys = new HashSet<string>();
                try
                {
                    object result;
                    using (Tx.Record(keys)) result = render();
                    screens.Add(new { title, keys, preview = result });
                }
                catch { /* this screen can't be drawn for the server's data — skip it */ }
            }
            object Msg(Screen sc) => MessagePreview(guild, sc.Content, sc.Embed is null ? [] : [sc.Embed], sc.Components);
            object Embeds(MessageComponent? comps, params Embed[] embeds) => MessagePreview(guild, null, embeds, comps);

            var me = user.UserId() ?? 0;
            var g = SampleGroup(guild, user);
            var acts = activities.ForProject(guildId, g.ProjectKey);
            var role = acts.FirstOrDefault(a => guild.GetRole(a.RoleId) is not null) is { } act ? act.RoleId
                : guild.Roles.Where(r => !r.IsEveryone && !r.IsManaged).OrderByDescending(r => r.Position).FirstOrDefault()?.Id;
            LfgDraft Draft(bool full)
            {
                var d = new LfgDraft(guildId, 0, me)
                {
                    ActivityName = "Ludra Hard", PingRoleId = role,
                    Slots = new[] { SlotRole.Tank, SlotRole.Dps, SlotRole.Dps, SlotRole.Healer, SlotRole.Dps, SlotRole.Any }.Select(r => new Slot { Role = r }).ToList()
                };
                if (!full) return d;
                d.Selected = [1, 2];
                d.EventDate = time.Today.AddDays(1);
                d.EventHour = 20;
                if (role is { } r) d.RequiredRoleIds = [r];
                d.MinGs = 3000;
                d.Note = "Bring buff food";
                return d;
            }
            var sampleTemplates = templates.Get(guildId) is { Count: > 0 } real ? real
                : [new LfgTemplate { Name = "Ludra Hard 6", Slots = [SlotRole.Tank, SlotRole.Dps, SlotRole.Dps, SlotRole.Healer, SlotRole.Dps, SlotRole.Any] }];

            switch (body.Sample)
            {
                case "LFG — creating a group":
                    Add("Step 0 · your character", () => Msg(LfgRenderer.OwnerCharacterStep(Draft(false),
                        [new PickerCharacter("Kayleth", "main", null, 2903, 149159), new PickerCharacter("Apostall", null, null, 2450)])));
                    Add("Step 1", () => Msg(LfgRenderer.SizeStep(Draft(false))));
                    Add("Step 1 · other size", () => { var d = Draft(false); d.AllSizes = true; return Msg(LfgRenderer.SizeStep(d)); });
                    Add("Step 1 · templates", () => Msg(LfgRenderer.SizeStep(Draft(false), sampleTemplates)));
                    Add("Step 2", () => Msg(LfgRenderer.ActivityStep(Draft(false), acts)));
                    Add("Step 2 · no activities", () => Msg(LfgRenderer.ActivityStep(Draft(false), [])));
                    Add("Step 3", () => Msg(LfgRenderer.SlotStep(Draft(true))));
                    Add("Step 3 · no ping", () => { var d = Draft(true); d.PingRoleId = null; return Msg(LfgRenderer.SlotStep(d)); });
                    Add("Step 4", () =>
                    {
                        var d = Draft(true);
                        return Msg(LfgRenderer.TimeStep(d, time.Today, time.ToUtc(d.EventDate!.Value, d.EventHour!.Value, 0), time.ZoneName));
                    });
                    Add("Step 4 · no time", () => { var d = Draft(false); return Msg(LfgRenderer.TimeStep(d, time.Today, null, time.ZoneName)); });
                    Add("Custom activity window", () => ModalPreview(LfgRenderer.CustomActivityModal(guildId, "x")));
                    Add("Note window", () => ModalPreview(LfgRenderer.NoteModal(guildId, "x", null)));
                    Add("Time window", () => ModalPreview(LfgRenderer.TimeModal(guildId, "x")));
                    break;
                case "LFG — signing up":
                    Add("Character picker", () => Msg(LfgRenderer.CharacterPicker(g, SlotRole.Dps,
                        [new PickerCharacter("Kayleth", "main", null, 2903, 149159), new PickerCharacter("Apostall", null, null)])));
                    Add("Character window", () => ModalPreview(LfgRenderer.CharacterModal(guildId, "x")));
                    break;
                case "LFG — class, note & GS":
                    Add("After signing up", () => MessagePreview(guild,
                        Tx.Get(guildId, "lfg.sign.joined", ("activity", g.ActivityName), ("role", "⚔️ **DD**"), ("name", "Organizer"))
                        + Tx.Line(guildId, "lfg.sign.profileHint"), [], profile.Controls(g, me)));
                    Add("Note & GS window", () => profile.NoteModal(g, me) is { } m ? ModalPreview(m) : throw new InvalidOperationException());
                    break;
                case "LFG — organizer":
                    Add("Manage menu", () => Msg(LfgRenderer.Manage(g)));
                    Add("Group invites", () => Embeds(null, LfgRenderer.Invites(g, 0)));
                    Add("Dungeon invites", () => Embeds(null, LfgRenderer.Invites(g, 0, false)));
                    break;
                case "LFG — DMs & reminders":
                    Add("New group DM", () => Embeds(new ComponentBuilder()
                        .WithButton(Tx.Get(guildId, "lfg.dm.openGroup"), style: ButtonStyle.Link, url: "https://discord.com")
                        .WithButton(Tx.Get(guildId, "lfg.dm.turnOff"), "x", ButtonStyle.Secondary).Build(),
                        LfgRenderer.DmNotice(g, guild.Name)));
                    break;
                case "Tickets — creating":
                    var projects = ticketStore.GetProjects(guildId);
                    if (projects.Count > 0) Add("Project picker", () => MessagePreview(guild, null, [], TicketRenderer.ProjectPicker(guildId, projects)));
                    if (projects.FirstOrDefault(p => p.FormEnabled && p.Questions.Count > 0) is { } withForm)
                        Add($"Application form · {withForm.Name}", () => ModalPreview(TicketRenderer.FormModal(guildId, withForm)));
                    break;
                case "Tickets — approve, deny, close":
                    Add("Deny window", () => ModalPreview(TicketRenderer.DenyModal(guildId, "x")));
                    break;
            }
            return Results.Ok(new { screens });
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

    /// <summary>Окно (modal) → заголовок и поля для рисования на сайте.</summary>
    private static object ModalPreview(Modal modal)
    {
        var fields = new List<object>();
        void Walk(IMessageComponent c, string? label)
        {
            switch (c)
            {
                case ActionRowComponent row: foreach (var x in row.Components) Walk(x, label); break;
                case LabelComponent l: Walk(l.Component, l.Label); break;
                case TextInputComponent t:
                    fields.Add(new { label = t.Label ?? label, placeholder = t.Placeholder, value = t.Value, required = t.Required ?? true,
                        @long = t.Style == TextInputStyle.Paragraph });
                    break;
            }
        }
        foreach (var c in modal.Component.Components) Walk(c, null);
        return new { modal = new { title = modal.Title, fields } };
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

        var rows = comps?.Components.OfType<ActionRowComponent>().Select(row => row.Components.Select(c => c switch
        {
            ButtonComponent b => new PreviewPart("button", b.Label, b.Emote?.ToString(), b.Style.ToString(), b.Url, b.IsDisabled),
            SelectMenuComponent s => new PreviewPart("select", s.Placeholder, null, s.Type.ToString(), null, s.IsDisabled,
                s.Options?.Take(25).Select(o => new PreviewOption(o.Label, o.Description, o.Emote?.ToString(), o.IsDefault ?? false)).ToList()),
            _ => new PreviewPart("other", null, null, null, null, false)
        }).ToList()).ToList() ?? [];

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

/// <summary>A button or a menu of a previewed message.</summary>
public sealed record PreviewPart(string Type, string? Label, string? Emoji, string? Style, string? Url, bool Disabled,
    List<PreviewOption>? Options = null);

public sealed record PreviewOption(string Label, string? Description, string? Emoji, bool IsDefault);

public sealed record BotPreviewBody(string? Sample, Dictionary<string, string?>? Values);
