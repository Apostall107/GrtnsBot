using System.Globalization;
using System.Text;
using Discord;
using LfgBot.Models;

namespace LfgBot.Services;

public sealed record Screen(string? Content, Embed? Embed, MessageComponent Components);

public static class LfgRenderer
{
    public const int MinSize = 2;
    public const int MaxSize = 25; // лимит опций в select menu
    public const string CustomActivityValue = "__custom";
    public const string NewCharacterValue = "__new";

    // ---------- Панель в канале ----------
    /// <summary>Тексты по умолчанию — <see cref="PanelDefs.Lfg"/>, свои — из веб-админки (PanelTextStore).</summary>
    public static Screen Panel(int autoDeleteHours, PanelText? text = null, int reminderMinutes = 10)
    {
        var def = PanelDefs.Lfg;
        var embed = new EmbedBuilder()
            .WithTitle(def.Title(text))
            .WithColor(Color.Blue)
            .WithDescription(def.Description(text)
                .Replace("{hours}", autoDeleteHours.ToString())
                .Replace("{reminder}", reminderMinutes.ToString()))
            .Build();

        var comps = new ComponentBuilder()
            .WithButton(def.Label(text, "start"), "lfg:start", ButtonStyle.Success, new Emoji("📣"))
            .WithButton(def.Label(text, "dmon"), "lfg:dmpref:on", ButtonStyle.Secondary, new Emoji("🔔"))
            .WithButton(def.Label(text, "dmoff"), "lfg:dmpref:off", ButtonStyle.Secondary, new Emoji("🔕"))
            .Build();

        return new Screen(null, embed, comps);
    }

    // ---------- Шаг 1: размер ----------
    public static Screen SizeStep(LfgDraft d, IReadOnlyList<LfgTemplate>? templates = null)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:size:{d.Id}")
            .WithPlaceholder("How many players?");
        for (var i = MinSize; i <= MaxSize; i++)
            menu.AddOption($"{i} players", i.ToString());

        var builder = new ComponentBuilder();
        var row = 0;
        // Шаблоны: активность + слоты + требования одним выбором → сразу к выбору времени
        if (templates is { Count: > 0 })
        {
            var tpl = new SelectMenuBuilder()
                .WithCustomId($"lfg:tpl:{d.Id}")
                .WithPlaceholder("📋 Use a template");
            foreach (var t in templates.Take(LfgTemplateStore.MaxPerGuild))
            {
                var roles = string.Join("/", t.Slots.Select(r => r.Label()));
                var desc = $"{t.Slots.Count} slots: {roles}";
                tpl.AddOption(Trim(t.Name), t.Key, Trim(desc), new Emoji("📋"));
            }
            builder.WithSelectMenu(tpl, row: row++);
        }

        builder.WithSelectMenu(menu, row: row++)
            .WithButton("Cancel", $"lfg:cancel:{d.Id}", ButtonStyle.Secondary, row: row);

        var text = templates is { Count: > 0 }
            ? "**Step 1/4** — pick a template (activity, slots and requirements in one go) or choose the group size."
            : "**Step 1/4** — choose the group size.";
        return new Screen(text, null, builder.Build());
    }

    // ---------- Шаг 2: активность ----------
    public static Screen ActivityStep(LfgDraft d, IReadOnlyList<Activity> activities)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:act:{d.Id}")
            .WithPlaceholder("Choose a dungeon / activity");

        foreach (var a in activities.Take(ActivityStore.MaxPerGuild))
            menu.AddOption(a.Name, a.Key, "Pings the linked role");

        menu.AddOption("Custom activity", CustomActivityValue, "Enter a name manually, no ping", new Emoji("✏️"));

        var comps = new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("Cancel", $"lfg:cancel:{d.Id}", ButtonStyle.Secondary, row: 1)
            .Build();

        var hint = activities.Count == 0
            ? "\n-# No activities yet — an admin can add them with `/activity add`."
            : "";
        return new Screen($"**Step 2/4** — group of {d.Slots.Count}. Choose the activity.{hint}", null, comps);
    }

    // ---------- Шаг 3: роли слотов ----------
    public static Screen SlotStep(LfgDraft d)
    {
        var sb = new StringBuilder();
        sb.AppendLine(d.PingRoleId is { } roleId ? $"Ping: <@&{roleId}>" : "Ping: *none (custom activity)*");
        sb.AppendLine();
        for (var i = 0; i < d.Slots.Count; i++)
        {
            var marker = d.Selected.Contains(i) ? "▸ " : "";
            var you = i == 0 ? $" — <@{d.OwnerId}> *(you)*" : "";
            sb.AppendLine($"{marker}{SlotHead(i, d.Slots[i])}{you}");
        }

        var embed = new EmbedBuilder()
            .WithTitle(d.ActivityName)
            .WithDescription(sb.ToString())
            .WithColor(Color.DarkBlue)
            .Build();

        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:slots:{d.Id}")
            .WithPlaceholder("Select slots, then click a role")
            .WithMinValues(1)
            .WithMaxValues(d.Slots.Count);
        for (var i = 0; i < d.Slots.Count; i++)
        {
            var s = d.Slots[i];
            menu.AddOption($"Slot {i + 1}: {s.Role.Label()}", i.ToString(),
                emote: new Emoji(s.Role.Emoji()), isDefault: d.Selected.Contains(i));
        }

        var noSelection = d.Selected.Count == 0;
        var builder = new ComponentBuilder().WithSelectMenu(menu, row: 0);
        foreach (var role in SlotRoleExtensions.All)
            builder.WithButton(role.Label(), $"lfg:setrole:{d.Id}:{role}", ButtonStyle.Secondary,
                new Emoji(role.Emoji()), disabled: noSelection, row: 1);

        builder
            .WithButton("Next", $"lfg:next:{d.Id}", ButtonStyle.Success, new Emoji("➡️"), row: 2)
            .WithButton("Cancel", $"lfg:cancel:{d.Id}", ButtonStyle.Danger, row: 2);

        return new Screen(
            "**Step 3/4** — select slots in the list and click a role. All slots are «All» by default. Slot 1 is yours.",
            embed, builder.Build());
    }

    // ---------- Шаг 4: время ----------
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    public const int DateDaysAhead = 14;

    public static Screen TimeStep(LfgDraft d, DateOnly today, DateTimeOffset? startsAt, string zoneName, string? warning = null)
    {
        var dateMenu = new SelectMenuBuilder()
            .WithCustomId($"lfg:date:{d.Id}")
            .WithPlaceholder("Date");
        for (var i = 0; i < DateDaysAhead; i++)
        {
            var date = today.AddDays(i);
            var label = i switch { 0 => "Today", 1 => "Tomorrow", _ => date.ToString("dddd", En) };
            dateMenu.AddOption(label, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date.ToString("ddd, dd.MM.yyyy", En), isDefault: d.EventDate == date);
        }

        var hourMenu = new SelectMenuBuilder()
            .WithCustomId($"lfg:hour:{d.Id}")
            .WithPlaceholder("Hour");
        for (var h = 0; h < 24; h++)
            hourMenu.AddOption($"{h:00}:xx", h.ToString(), isDefault: d.EventHour == h);

        var minuteMenu = new SelectMenuBuilder()
            .WithCustomId($"lfg:min:{d.Id}")
            .WithPlaceholder("Minutes");
        for (var m = 0; m < 60; m += 5)
            minuteMenu.AddOption($":{m:00}", m.ToString(), isDefault: d.HasTime && d.EventMinute == m);

        var builder = new ComponentBuilder()
            .WithSelectMenu(dateMenu, row: 0)
            .WithSelectMenu(hourMenu, row: 1)
            .WithSelectMenu(minuteMenu, row: 2)
            .WithButton("Type manually", $"lfg:timemodal:{d.Id}", ButtonStyle.Secondary, new Emoji("✏️"), row: 3)
            .WithButton("No time", $"lfg:notime:{d.Id}", ButtonStyle.Secondary, disabled: !d.HasTime, row: 3)
            .WithButton("Back", $"lfg:back:{d.Id}", ButtonStyle.Secondary, new Emoji("⬅️"), row: 3)
            .WithButton("Publish", $"lfg:publish:{d.Id}", ButtonStyle.Success, new Emoji("📣"), row: 3)
            .WithButton("Cancel", $"lfg:cancel:{d.Id}", ButtonStyle.Danger, row: 3);

        // ЛС-рассылка есть только у активностей с ролью
        if (d.PingRoleId is not null)
            builder.WithButton(d.NotifyByDm ? "DM role members: ON" : "DM role members: OFF", $"lfg:dm:{d.Id}",
                d.NotifyByDm ? ButtonStyle.Success : ButtonStyle.Secondary,
                new Emoji(d.NotifyByDm ? "🔔" : "🔕"), row: 4);
        // Голосовая комната сбора — только если включить
        builder.WithButton(d.CreateVoice ? "Voice room: ON" : "Voice room: OFF", $"lfg:dvoice:{d.Id}",
            d.CreateVoice ? ButtonStyle.Success : ButtonStyle.Secondary, new Emoji("🔊"), row: 4);

        var comps = builder.Build();

        var sb = new StringBuilder();
        sb.AppendLine($"**Step 4/4** — start time in the server time zone (`{zoneName}`).");
        sb.AppendLine("-# Everyone will see the time converted to their own time zone.");
        sb.AppendLine(startsAt is { } st
            ? $"Selected: {FormatTime(st)}"
            : "Selected: *no time*");
        if (d.RequiredRoleIds.Count > 0 || d.MinGs is not null)
        {
            var req = new List<string>();
            if (d.RequiredRoleIds.Count > 0) req.Add(string.Join(" / ", d.RequiredRoleIds.Select(id => $"<@&{id}>")));
            if (d.MinGs is { } gs) req.Add($"GS {gs}+");
            sb.AppendLine($"📋 Requirements: {string.Join(" · ", req)} — others go to the reserve");
        }
        if (warning is not null) sb.AppendLine($"⚠️ {warning}");

        return new Screen(sb.ToString(), null, comps);
    }

    public static string FormatTime(DateTimeOffset t)
    {
        var unix = t.ToUnixTimeSeconds();
        return $"<t:{unix}:F> (<t:{unix}:R>)";
    }

    // ---------- Опубликованный сбор ----------
    public static Screen Group(LfgGroup g)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Organizer: <@{g.OwnerId}>");
        if (g.StartsAt is { } startsAt)
            sb.AppendLine($"🕒 Starts: {FormatTime(startsAt)}");
        sb.AppendLine($"Roster: **{g.FilledCount}/{g.Slots.Count}**");
        if (Requirements(g) is { } req) sb.AppendLine($"📋 Requirements: {req}");
        sb.AppendLine();
        // Заметки к персонажам в посте не показываются — их видит владелец при выборе персонажа
        sb.Append(Roster(g, withComments: false));

        sb.AppendLine();
        sb.AppendLine($"-# Post will be removed <t:{g.ExpiresAt.ToUnixTimeSeconds()}:R>");

        var (status, color) = g.IsClosed ? ("🔒 Signups closed", Color.DarkGrey)
            : g.IsFull ? ("✅ Group is full", Color.Green)
            : ("📝 Recruiting", Color.Blue);

        var embed = new EmbedBuilder()
            .WithTitle(g.ActivityName)
            .WithDescription(sb.ToString())
            .WithColor(color)
            .WithFooter($"{status} • ID {g.Id}")
            .Build();

        var builder = new ComponentBuilder();
        // Серая кнопка роли = мест нет, нажатие запишет в резерв
        foreach (var role in SlotRoleExtensions.All)
            builder.WithButton(role.Label(), $"lfg:join:{g.Id}:{role}",
                g.CanJoinAs(role) ? ButtonStyle.Primary : ButtonStyle.Secondary,
                new Emoji(role.Emoji()), disabled: g.IsClosed, row: 0);
        // Голосовая комната — только если её выбрали при создании: ссылка на неё
        if (g.VoiceChannelId is { } voiceId)
            builder.WithButton("Voice room", style: ButtonStyle.Link, url: $"https://discord.com/channels/{g.GuildId}/{voiceId}",
                emote: new Emoji("🔊"), row: 0);

        builder
            .WithButton("Leave", $"lfg:leave:{g.Id}", ButtonStyle.Secondary, disabled: g.IsClosed, row: 1)
            .WithButton("Invite to group", $"lfg:invites:{g.Id}", ButtonStyle.Secondary, new Emoji("📋"), row: 1)
            .WithButton("Invite to dungeon", $"lfg:dungeon:{g.Id}", ButtonStyle.Secondary, new Emoji("🏰"), row: 1)
            .WithButton("Manage", $"lfg:manage:{g.Id}", ButtonStyle.Secondary, new Emoji("⚙️"), disabled: g.IsClosed, row: 1)
            .WithButton("Close", $"lfg:close:{g.Id}", ButtonStyle.Danger, disabled: g.IsClosed, row: 1);

        return new Screen(null, embed, builder.Build());
    }

    // ---------- Выбор персонажа при записи ----------
    /// <param name="prefix">"lfg" — запись на сервере, "lfgdm" — запись из ЛС.</param>
    /// <param name="characters">Имя, заметка (видна только владельцу — здесь, при выборе) и иконка класса.</param>
    public static Screen CharacterPicker(LfgGroup g, SlotRole role, IReadOnlyList<(string Name, string? Note, string? Icon)> characters,
        string prefix = "lfg")
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"{prefix}:char:{g.Id}:{role}")
            .WithPlaceholder("Choose a character");

        foreach (var c in characters.Take(CharacterStore.MaxPerUser))
        {
            var option = new SelectMenuOptionBuilder().WithLabel(Trim(c.Name)).WithValue(c.Name);
            if (!string.IsNullOrWhiteSpace(c.Note)) option.WithDescription(Trim(c.Note));
            if (LfgProjectStore.ToEmote(c.Icon) is { } emote) option.WithEmote(emote);
            menu.AddOption(option);
        }

        menu.AddOption("New character", NewCharacterValue, "Enter a name manually", new Emoji("➕"));

        return new Screen(
            $"Signing up for **{Format.Sanitize(g.ActivityName)}** as {role.Emoji()} **{role.Label()}**. Which character?",
            null,
            new ComponentBuilder().WithSelectMenu(menu).Build());
    }

    // ---------- Меню организатора ----------
    public static Screen Manage(LfgGroup g, string? note = null)
    {
        var sb = new StringBuilder();
        if (note is not null) sb.AppendLine(note);
        sb.AppendLine($"**Manage {Format.Sanitize(g.ActivityName)}** — free slots: **{g.Slots.Count(s => s.IsFree)}**, " +
                      $"reserve: **{g.Reserve.Count}**.");
        sb.AppendLine("-# Move someone to the reserve to free a slot, then move a member from the reserve into it. " +
                      "Members moved into the group get a DM.");

        var builder = new ComponentBuilder();
        var row = 0;

        var members = g.Slots
            .Select((slot, index) => (slot, index))
            .Where(x => x.slot.UserId is { } id && id != g.OwnerId)
            .ToList();
        if (members.Count > 0)
        {
            var toReserve = new SelectMenuBuilder()
                .WithCustomId($"lfg:mreserve:{g.Id}")
                .WithPlaceholder("🔽 Move from the group to the reserve");
            foreach (var (slot, index) in members)
            {
                var role = slot.TakenAs ?? slot.Role;
                toReserve.AddOption(Trim($"{index + 1}. {slot.CharacterName ?? "no name"}"), slot.UserId!.Value.ToString(),
                    $"Slot {index + 1} · {role.Label()}", new Emoji(role.Emoji()));
            }
            builder.WithSelectMenu(toReserve, row++);
        }

        if (g.Reserve.Count > 0)
        {
            var promote = new SelectMenuBuilder()
                .WithCustomId($"lfg:mpromote:{g.Id}")
                .WithPlaceholder("⬆️ Move from the reserve into the group");
            var remove = new SelectMenuBuilder()
                .WithCustomId($"lfg:mremove:{g.Id}")
                .WithPlaceholder("✖️ Remove from the reserve");

            foreach (var (entry, index) in g.Reserve.Select((e, i) => (e, i)))
            {
                var label = Trim($"#{index + 1} {entry.CharacterName ?? "no name"}");
                var description = $"Wants {entry.Role.Label()}";
                promote.AddOption(label, entry.UserId.ToString(), description, new Emoji(entry.Role.Emoji()));
                remove.AddOption(label, entry.UserId.ToString(), description, new Emoji(entry.Role.Emoji()));
            }

            builder.WithSelectMenu(promote, row++);
            builder.WithSelectMenu(remove, row++);
        }

        // Явка: после старта (или в сборе без времени) — кто пришёл; остальные отмечаются как не пришедшие
        var seated = g.Slots.Where(s => s.UserId is not null).ToList();
        if (seated.Count > 0 && (g.StartsAt is null || g.StartsAt <= DateTimeOffset.UtcNow))
        {
            var attend = new SelectMenuBuilder()
                .WithCustomId($"lfg:attend:{g.Id}")
                .WithPlaceholder("✅ Attendance: who came?")
                .WithMinValues(0)
                .WithMaxValues(seated.Count);
            foreach (var s in seated)
                attend.AddOption(Trim(s.CharacterName ?? "no name"), s.UserId!.Value.ToString(),
                    s.Attended switch { true => "came", false => "didn't come", _ => "not marked" },
                    new Emoji((s.TakenAs ?? s.Role).Emoji()), isDefault: s.Attended == true);
            builder.WithSelectMenu(attend, row++);
            sb.AppendLine("-# Attendance: select everyone who came — the others are marked as no-show.");
        }

        builder.WithButton("Change time", $"lfg:resched:{g.Id}", ButtonStyle.Secondary, new Emoji("🕒"), row: Math.Min(row, 4));

        if (members.Count == 0 && g.Reserve.Count == 0)
            sb.AppendLine("Nobody to manage yet.");

        return new Screen(sb.ToString(), null, builder.Build());
    }

    private static string Trim(string value) => value.Length <= 100 ? value : value[..100];

    // ---------- Команды /invite для игры ----------
    /// <param name="withCommand">true — «/invite Name» для приглашения в группу; false — только ники (приглашение в данж).</param>
    public static Embed Invites(LfgGroup g, ulong viewerId, bool withCommand = true)
    {
        var sb = new StringBuilder();
        var count = 0;
        var withoutNick = 0;

        foreach (var s in g.Slots)
        {
            if (s.UserId is not { } userId || userId == viewerId) continue;
            if (s.CharacterName is not { } name)
            {
                withoutNick++;
                continue;
            }

            count++;
            var role = s.TakenAs is { } t && t != SlotRole.Any ? t : s.Role;
            sb.AppendLine($"{role.Emoji()} <@{userId}>");
            sb.AppendLine(withCommand ? $"```\n/invite {name}\n```" : $"```\n{name}\n```");
        }

        if (count == 0) sb.AppendLine("Nobody to invite yet.");
        if (withoutNick > 0) sb.AppendLine($"-# Without a character name: {withoutNick}");

        return new EmbedBuilder()
            .WithTitle(withCommand ? $"📋 Group invites — {g.ActivityName}" : $"🏰 Dungeon invites — {g.ActivityName}")
            .WithDescription(sb.ToString())
            .WithColor(Color.Gold)
            .WithFooter(withCommand ? "Copy a line and paste it into the game chat" : "Copy a name into the dungeon invite field")
            .Build();
    }

    // ---------- ЛС-оповещение о новом сборе ----------
    public static Embed DmNotice(LfgGroup g, string guildName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<@{g.OwnerId}> started a group on **{Format.Sanitize(guildName)}**.");
        if (g.StartsAt is { } startsAt)
            sb.AppendLine($"🕒 Starts: {FormatTime(startsAt)}");
        sb.AppendLine($"Roster: **{g.FilledCount}/{g.Slots.Count}**");

        var open = g.Slots.Where(s => s.IsFree)
            .GroupBy(s => s.Role)
            .Select(gr => $"{gr.Key.Emoji()} {gr.Key.Label()} ×{gr.Count()}")
            .ToList();
        sb.AppendLine(open.Count > 0
            ? $"Open slots: {string.Join(", ", open)}"
            : "The group is full — you can still join the reserve.");

        return new EmbedBuilder()
            .WithTitle($"📣 LFG: {g.ActivityName}")
            .WithDescription(sb.ToString())
            .WithColor(Color.Blue)
            .WithFooter("Sign up right here with the role buttons. Don't want these DMs? Press \"DMs off\" on the LFG panel.")
            .Build();
    }

    private static string SlotHead(int i, Slot s) => $"`{i + 1,2}.` {s.Role.Emoji()} **{s.Role.Label()}**";

    private static string Roster(LfgGroup g, bool withComments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < g.Slots.Count; i++)
        {
            sb.AppendLine(SlotLine(i, g.Slots[i]));
            if (withComments && g.Slots[i].UserId is not null) AppendComment(sb, g.Slots[i].Comment);
        }

        if (g.Reserve.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Reserve ({g.Reserve.Count}):**");
            foreach (var r in g.Reserve)
            {
                sb.AppendLine($"{r.Role.Emoji()} <@{r.UserId}>{ClassIcon(r.ClassIcon)} **{Format.Sanitize(r.CharacterName ?? "-")}**{GsText(r.Gs)}");
                if (withComments) AppendComment(sb, r.Comment);
            }
        }
        return sb.ToString();
    }

    /// <summary>Иконка класса после ника — только иконка (эмодзи из LFG → Classes или эмодзи роли), без упоминания роли.</summary>
    private static string ClassIcon(string? icon) => string.IsNullOrWhiteSpace(icon) ? "" : $" {icon}";

    private static void AppendComment(StringBuilder sb, string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return;
        sb.AppendLine($"-# 💬 {Format.Sanitize(comment.Replace("\n", " "))}");
    }

    private static string SlotLine(int i, Slot s)
    {
        if (s.UserId is not { } userId) return $"{SlotHead(i, s)} — *open*";

        var takenAs = s.Role == SlotRole.Any && s.TakenAs is { } t && t != SlotRole.Any
            ? $" ({t.Emoji()} {t.Label()})"
            : "";
        var character = s.CharacterName is { } name ? $" **{Format.Sanitize(name)}**{GsText(s.Gs)}" : "";
        var attended = s.Attended switch { true => " ✅", false => " ❌", _ => "" };
        return $"{SlotHead(i, s)} — <@{userId}>{ClassIcon(s.ClassIcon)}{character}{takenAs}{attended}";
    }

    private static string GsText(int? gs) => gs is { } v ? $" · GS {v}" : "";

    /// <summary>Требования сбора текстом: роли (любая из) и минимальный GS.</summary>
    public static string? Requirements(LfgGroup g)
    {
        var parts = new List<string>();
        if (g.RequiredRoleIds.Count > 0) parts.Add(string.Join(" / ", g.RequiredRoleIds.Select(id => $"<@&{id}>")));
        if (g.MinGs is { } min) parts.Add($"GS {min}+");
        return parts.Count == 0 ? null : string.Join(" · ", parts) + " — others go to the reserve";
    }
}
