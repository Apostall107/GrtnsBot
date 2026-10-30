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
            .WithColor(def.Color(text))
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
        var gid = d.GuildId;
        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:size:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.sizePlaceholder"));
        for (var i = MinSize; i <= MaxSize; i++)
            menu.AddOption(Tx.Get(gid, "lfg.wiz.sizeOption", ("n", i)), i.ToString());

        var builder = new ComponentBuilder();
        var row = 0;
        // Шаблоны: активность + слоты + требования одним выбором → сразу к выбору времени
        if (templates is { Count: > 0 })
        {
            var tpl = new SelectMenuBuilder()
                .WithCustomId($"lfg:tpl:{d.Id}")
                .WithPlaceholder(Tx.Get(gid, "lfg.wiz.tplPlaceholder"));
            foreach (var t in templates.Take(LfgTemplateStore.MaxInMenu))
            {
                var roles = string.Join("/", t.Slots.Select(r => r.Label()));
                var desc = Tx.Get(gid, "lfg.wiz.tplOption", ("count", t.Slots.Count), ("roles", roles));
                tpl.AddOption(Trim(t.Name), t.Key, Trim(desc), new Emoji("📋"));
            }
            builder.WithSelectMenu(tpl, row: row++);
        }

        builder.WithSelectMenu(menu, row: row++)
            .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Secondary, row: row);

        var text = templates is { Count: > 0 } ? Tx.Get(gid, "lfg.wiz.step1Templates") : Tx.Get(gid, "lfg.wiz.step1");
        return new Screen(text, null, builder.Build());
    }

    // ---------- Шаг 2: активность ----------
    public static Screen ActivityStep(LfgDraft d, IReadOnlyList<Activity> activities)
    {
        var gid = d.GuildId;
        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:act:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.actPlaceholder"));

        var actDesc = Tx.Get(gid, "lfg.wiz.actOption");
        foreach (var a in activities.Take(ActivityStore.MaxInMenu))
            menu.AddOption(a.Name, a.Key, actDesc.Length == 0 ? null : actDesc);

        menu.AddOption(Tx.Get(gid, "lfg.wiz.custom"), CustomActivityValue, Tx.Get(gid, "lfg.wiz.customOption"), new Emoji("✏️"));

        var comps = new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Secondary, row: 1)
            .Build();

        var hint = activities.Count == 0 ? Tx.Line(gid, "lfg.wiz.noActivities") : "";
        return new Screen(Tx.Get(gid, "lfg.wiz.step2", ("size", d.Slots.Count)) + hint, null, comps);
    }

    // ---------- Шаг 3: роли слотов ----------
    public static Screen SlotStep(LfgDraft d)
    {
        var gid = d.GuildId;
        var sb = new StringBuilder();
        sb.AppendText(d.PingRoleId is { } roleId ? Tx.Get(gid, "lfg.wiz.ping", ("role", $"<@&{roleId}>")) : Tx.Get(gid, "lfg.wiz.noPing"));
        sb.AppendLine();
        for (var i = 0; i < d.Slots.Count; i++)
        {
            var marker = d.Selected.Contains(i) ? "▸ " : "";
            var you = i == 0 ? $" — <@{d.OwnerId}> {Tx.Get(gid, "lfg.wiz.you")}" : "";
            sb.AppendLine($"{marker}{SlotHead(i, d.Slots[i])}{you}");
        }

        var embed = new EmbedBuilder()
            .WithTitle(d.ActivityName)
            .WithDescription(sb.ToString())
            .WithColor(Tx.Color(gid, "color.lfg.draft"))
            .Build();

        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:slots:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.slotsPlaceholder"))
            .WithMinValues(1)
            .WithMaxValues(d.Slots.Count);
        for (var i = 0; i < d.Slots.Count; i++)
        {
            var s = d.Slots[i];
            menu.AddOption(Tx.Get(gid, "lfg.wiz.slotOption", ("n", i + 1), ("role", s.Role.Label())), i.ToString(),
                emote: new Emoji(s.Role.Emoji()), isDefault: d.Selected.Contains(i));
        }

        var noSelection = d.Selected.Count == 0;
        var builder = new ComponentBuilder().WithSelectMenu(menu, row: 0);
        foreach (var role in SlotRoleExtensions.All)
            builder.WithButton(role.Label(), $"lfg:setrole:{d.Id}:{role}", ButtonStyle.Secondary,
                new Emoji(role.Emoji()), disabled: noSelection, row: 1);

        builder
            .WithButton(Tx.Get(gid, "lfg.wiz.next"), $"lfg:next:{d.Id}", ButtonStyle.Success, new Emoji("➡️"), row: 2)
            .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Danger, row: 2);

        return new Screen(Tx.Get(gid, "lfg.wiz.step3"), embed, builder.Build());
    }

    // ---------- Шаг 4: время ----------
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    public const int DateDaysAhead = 14;

    public static Screen TimeStep(LfgDraft d, DateOnly today, DateTimeOffset? startsAt, string zoneName, string? warning = null)
    {
        var gid = d.GuildId;
        var dateMenu = new SelectMenuBuilder()
            .WithCustomId($"lfg:date:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.datePlaceholder"));
        for (var i = 0; i < DateDaysAhead; i++)
        {
            var date = today.AddDays(i);
            var label = i switch { 0 => Tx.Get(gid, "lfg.wiz.today"), 1 => Tx.Get(gid, "lfg.wiz.tomorrow"), _ => date.ToString("dddd", En) };
            dateMenu.AddOption(label, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                date.ToString("ddd, dd.MM.yyyy", En), isDefault: d.EventDate == date);
        }

        var hourMenu = new SelectMenuBuilder()
            .WithCustomId($"lfg:hour:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.hourPlaceholder"));
        for (var h = 0; h < 24; h++)
            hourMenu.AddOption($"{h:00}:xx", h.ToString(), isDefault: d.EventHour == h);

        var minuteMenu = new SelectMenuBuilder()
            .WithCustomId($"lfg:min:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.minutePlaceholder"));
        for (var m = 0; m < 60; m += 5)
            minuteMenu.AddOption($":{m:00}", m.ToString(), isDefault: d.HasTime && d.EventMinute == m);

        var builder = new ComponentBuilder()
            .WithSelectMenu(dateMenu, row: 0)
            .WithSelectMenu(hourMenu, row: 1)
            .WithSelectMenu(minuteMenu, row: 2)
            .WithButton(Tx.Get(gid, "lfg.wiz.typeTime"), $"lfg:timemodal:{d.Id}", ButtonStyle.Secondary, new Emoji("✏️"), row: 3)
            .WithButton(Tx.Get(gid, "lfg.wiz.noTime"), $"lfg:notime:{d.Id}", ButtonStyle.Secondary, disabled: !d.HasTime, row: 3)
            .WithButton(Tx.Get(gid, "lfg.wiz.back"), $"lfg:back:{d.Id}", ButtonStyle.Secondary, new Emoji("⬅️"), row: 3)
            .WithButton(Tx.Get(gid, "lfg.wiz.publish"), $"lfg:publish:{d.Id}", ButtonStyle.Success, new Emoji("📣"), row: 3)
            .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Danger, row: 3);

        // ЛС-рассылка есть только у активностей с ролью
        if (d.PingRoleId is not null)
            builder.WithButton(Tx.Get(gid, d.NotifyByDm ? "lfg.wiz.dmOn" : "lfg.wiz.dmOff"), $"lfg:dm:{d.Id}",
                d.NotifyByDm ? ButtonStyle.Success : ButtonStyle.Secondary,
                new Emoji(d.NotifyByDm ? "🔔" : "🔕"), row: 4);
        // Голосовая комната сбора — только если включить
        builder.WithButton(Tx.Get(gid, d.CreateVoice ? "lfg.wiz.voiceOn" : "lfg.wiz.voiceOff"), $"lfg:dvoice:{d.Id}",
            d.CreateVoice ? ButtonStyle.Success : ButtonStyle.Secondary, new Emoji("🔊"), row: 4);

        var comps = builder.Build();

        var sb = new StringBuilder();
        sb.AppendText(Tx.Get(gid, "lfg.wiz.step4", ("zone", zoneName)));
        sb.AppendText(startsAt is { } st
            ? Tx.Get(gid, "lfg.wiz.selected", ("time", FormatTime(st)))
            : Tx.Get(gid, "lfg.wiz.selectedNone"));
        if (d.RequiredRoleIds.Count > 0 || d.MinGs is not null)
        {
            var req = new List<string>();
            if (d.RequiredRoleIds.Count > 0) req.Add(string.Join(" / ", d.RequiredRoleIds.Select(id => $"<@&{id}>")));
            if (d.MinGs is { } gs) req.Add($"GS {gs}+");
            sb.AppendText(Tx.Get(gid, "lfg.wiz.requirements", ("list", string.Join(" · ", req))));
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
        var gid = g.GuildId;
        var sb = new StringBuilder();
        sb.AppendText(Tx.Get(gid, "lfg.post.organizer", ("user", $"<@{g.OwnerId}>")));
        if (g.StartsAt is { } startsAt)
            sb.AppendText(Tx.Get(gid, "lfg.post.starts", ("time", FormatTime(startsAt))));
        sb.AppendText(Tx.Get(gid, "lfg.post.roster", ("filled", g.FilledCount), ("total", g.Slots.Count)));
        if (Requirements(g) is { } req) sb.AppendText(Tx.Get(gid, "lfg.post.requirements", ("list", req)));
        if (g.VoiceChannelId is { } voice) sb.AppendText(Tx.Get(gid, "lfg.post.voice", ("channel", $"<#{voice}>")));
        sb.AppendLine();
        // Заметки к персонажам в посте не показываются — их видит владелец при выборе персонажа
        sb.Append(Roster(g, withComments: false));

        var expires = Tx.Get(gid, "lfg.post.expires", ("time", $"<t:{g.ExpiresAt.ToUnixTimeSeconds()}:R>"));
        if (expires.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(expires);
        }

        var (status, color) = g.IsClosed ? (Tx.Get(gid, "lfg.post.closed"), Tx.Color(gid, "color.lfg.closed"))
            : g.IsFull ? (Tx.Get(gid, "lfg.post.full"), Tx.Color(gid, "color.lfg.full"))
            : (Tx.Get(gid, "lfg.post.recruiting"), Tx.Color(gid, "color.lfg.recruiting"));

        var embed = new EmbedBuilder()
            .WithTitle(g.ActivityName)
            .WithDescription(sb.ToString())
            .WithColor(color)
            .WithFooter(Tx.Get(gid, "lfg.post.footer", ("status", status), ("id", g.Id)))
            .Build();

        var builder = new ComponentBuilder();
        // Серая кнопка роли = мест нет, нажатие запишет в резерв
        foreach (var role in SlotRoleExtensions.All)
            builder.WithButton(role.Label(), $"lfg:join:{g.Id}:{role}",
                g.CanJoinAs(role) ? ButtonStyle.Primary : ButtonStyle.Secondary,
                new Emoji(role.Emoji()), disabled: g.IsClosed, row: 0);
        // Голосовая комната — только если её выбрали при создании: ссылка на неё
        if (g.VoiceChannelId is { } voiceId)
            builder.WithButton(PanelDefs.Label(g.GuildId, PanelDefs.LfgPost, "voice"), style: ButtonStyle.Link, url: $"https://discord.com/channels/{g.GuildId}/{voiceId}",
                emote: new Emoji("🔊"), row: 0);

        builder
            .WithButton(PanelDefs.Label(g.GuildId, PanelDefs.LfgPost, "leave"), $"lfg:leave:{g.Id}", ButtonStyle.Secondary, disabled: g.IsClosed, row: 1)
            .WithButton(PanelDefs.Label(g.GuildId, PanelDefs.LfgPost, "invites"), $"lfg:invites:{g.Id}", ButtonStyle.Secondary, new Emoji("📋"), row: 1)
            .WithButton(PanelDefs.Label(g.GuildId, PanelDefs.LfgPost, "dungeon"), $"lfg:dungeon:{g.Id}", ButtonStyle.Secondary, new Emoji("🏰"), row: 1)
            .WithButton(PanelDefs.Label(g.GuildId, PanelDefs.LfgPost, "manage"), $"lfg:manage:{g.Id}", ButtonStyle.Secondary, new Emoji("⚙️"), disabled: g.IsClosed, row: 1)
            .WithButton(PanelDefs.Label(g.GuildId, PanelDefs.LfgPost, "close"), $"lfg:close:{g.Id}", ButtonStyle.Danger, disabled: g.IsClosed, row: 1);

        return new Screen(null, embed, builder.Build());
    }

    // ---------- Выбор персонажа при записи ----------
    /// <param name="prefix">"lfg" — запись на сервере, "lfgdm" — запись из ЛС.</param>
    /// <param name="characters">Имя, заметка (видна только владельцу — здесь, при выборе) и иконка класса.</param>
    public static Screen CharacterPicker(LfgGroup g, SlotRole role, IReadOnlyList<(string Name, string? Note, string? Icon)> characters,
        string prefix = "lfg")
    {
        var gid = g.GuildId;
        var menu = new SelectMenuBuilder()
            .WithCustomId($"{prefix}:char:{g.Id}:{role}")
            .WithPlaceholder(Tx.Get(gid, "lfg.sign.charPlaceholder"));

        foreach (var c in characters.Take(CharacterStore.MaxPerUser))
        {
            var option = new SelectMenuOptionBuilder().WithLabel(Trim(c.Name)).WithValue(c.Name);
            if (!string.IsNullOrWhiteSpace(c.Note)) option.WithDescription(Trim(c.Note));
            if (LfgProjectStore.ToEmote(c.Icon) is { } emote) option.WithEmote(emote);
            menu.AddOption(option);
        }

        menu.AddOption(Tx.Get(gid, "lfg.sign.newChar"), NewCharacterValue, Tx.Get(gid, "lfg.sign.newCharOption"), new Emoji("➕"));

        return new Screen(
            Tx.Get(gid, "lfg.sign.which", ("activity", Format.Sanitize(g.ActivityName)), ("role", $"{role.Emoji()} **{role.Label()}**")),
            null,
            new ComponentBuilder().WithSelectMenu(menu).Build());
    }

    // ---------- Меню организатора ----------
    public static Screen Manage(LfgGroup g, string? note = null)
    {
        var gid = g.GuildId;
        var noName = Tx.Get(gid, "lfg.manage.noName");
        var sb = new StringBuilder();
        if (note is not null) sb.AppendLine(note);
        sb.AppendText(Tx.Get(gid, "lfg.manage.head", ("activity", Format.Sanitize(g.ActivityName)),
            ("free", g.Slots.Count(s => s.IsFree)), ("reserve", g.Reserve.Count)));
        if (Tx.Get(gid, "lfg.manage.hint") is { Length: > 0 } hint) sb.AppendLine(hint);

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
                .WithPlaceholder(Tx.Get(gid, "lfg.manage.toReserve"));
            foreach (var (slot, index) in members)
            {
                var role = slot.TakenAs ?? slot.Role;
                toReserve.AddOption(Trim($"{index + 1}. {slot.CharacterName ?? noName}"), slot.UserId!.Value.ToString(),
                    Trim(Tx.Get(gid, "lfg.manage.slotOption", ("n", index + 1), ("role", role.Label()))), new Emoji(role.Emoji()));
            }
            builder.WithSelectMenu(toReserve, row++);
        }

        if (g.Reserve.Count > 0)
        {
            var promote = new SelectMenuBuilder()
                .WithCustomId($"lfg:mpromote:{g.Id}")
                .WithPlaceholder(Tx.Get(gid, "lfg.manage.promote"));
            var remove = new SelectMenuBuilder()
                .WithCustomId($"lfg:mremove:{g.Id}")
                .WithPlaceholder(Tx.Get(gid, "lfg.manage.remove"));

            foreach (var (entry, index) in g.Reserve.Select((e, i) => (e, i)))
            {
                var label = Trim($"#{index + 1} {entry.CharacterName ?? noName}");
                var description = Trim(Tx.Get(gid, "lfg.manage.wants", ("role", entry.Role.Label())));
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
                .WithPlaceholder(Tx.Get(gid, "lfg.manage.attendance"))
                .WithMinValues(0)
                .WithMaxValues(seated.Count);
            foreach (var s in seated)
                attend.AddOption(Trim(s.CharacterName ?? noName), s.UserId!.Value.ToString(),
                    Trim(Tx.Get(gid, s.Attended switch { true => "lfg.manage.came", false => "lfg.manage.noShow", _ => "lfg.manage.notMarked" })),
                    new Emoji((s.TakenAs ?? s.Role).Emoji()), isDefault: s.Attended == true);
            builder.WithSelectMenu(attend, row++);
            if (Tx.Get(gid, "lfg.manage.attendanceHint") is { Length: > 0 } ah) sb.AppendLine(ah);
        }

        builder.WithButton(Tx.Get(gid, "lfg.manage.changeTime"), $"lfg:resched:{g.Id}", ButtonStyle.Secondary, new Emoji("🕒"), row: Math.Min(row, 4));

        if (members.Count == 0 && g.Reserve.Count == 0)
            sb.AppendText(Tx.Get(gid, "lfg.manage.nobody"));

        return new Screen(sb.ToString(), null, builder.Build());
    }

    private static string Trim(string value) => value.Length <= 100 ? value : value[..100];

    // ---------- Окна (тексты из Bot texts; разбираются классами *Modal по id полей) ----------
    public static Modal CharacterModal(ulong gid, string customId) => new ModalBuilder()
        .WithTitle(Tx.Get(gid, "lfg.sign.charTitle"))
        .WithCustomId(customId)
        .AddTextInput(Tx.Get(gid, "lfg.sign.charLabel"), "name", TextInputStyle.Short,
            placeholder: Tx.Get(gid, "lfg.sign.charHint"), maxLength: CharacterStore.MaxNameLength, required: true)
        .Build();

    public static Modal CustomActivityModal(ulong gid, string customId) => new ModalBuilder()
        .WithTitle(Tx.Get(gid, "lfg.wiz.customTitle"))
        .WithCustomId(customId)
        .AddTextInput(Tx.Get(gid, "lfg.wiz.customLabel"), "name", TextInputStyle.Short,
            placeholder: Tx.Get(gid, "lfg.wiz.customHint"), maxLength: 100, required: true)
        .Build();

    public static Modal TimeModal(ulong gid, string customId) => new ModalBuilder()
        .WithTitle(Tx.Get(gid, "lfg.wiz.timeTitle"))
        .WithCustomId(customId)
        .AddTextInput(Tx.Get(gid, "lfg.wiz.timeLabel"), "when", TextInputStyle.Short,
            placeholder: Tx.Get(gid, "lfg.wiz.timeHint"), maxLength: 20, required: true)
        .Build();

    // ---------- Команды /invite для игры ----------
    /// <param name="withCommand">true — «/invite Name» для приглашения в группу; false — только ники (приглашение в данж).</param>
    public static Embed Invites(LfgGroup g, ulong viewerId, bool withCommand = true)
    {
        var gid = g.GuildId;
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

        if (count == 0) sb.AppendText(Tx.Get(gid, "lfg.invites.nobody"));
        if (withoutNick > 0 && Tx.Get(gid, "lfg.invites.noName", ("count", withoutNick)) is { Length: > 0 } nn) sb.AppendLine(nn);

        return new EmbedBuilder()
            .WithTitle(Tx.Get(gid, withCommand ? "lfg.invites.groupTitle" : "lfg.invites.dungeonTitle", ("activity", g.ActivityName)))
            .WithDescription(sb.ToString())
            .WithColor(Tx.Color(gid, "color.lfg.invites"))
            .WithFooter(Tx.Get(gid, withCommand ? "lfg.invites.groupFooter" : "lfg.invites.dungeonFooter"))
            .Build();
    }

    // ---------- ЛС-оповещение о новом сборе ----------
    public static Embed DmNotice(LfgGroup g, string guildName)
    {
        var gid = g.GuildId;
        var sb = new StringBuilder();
        sb.AppendText(Tx.Get(gid, "lfg.dm.started", ("user", $"<@{g.OwnerId}>"), ("server", Format.Sanitize(guildName))));
        if (g.StartsAt is { } startsAt)
            sb.AppendText(Tx.Get(gid, "lfg.post.starts", ("time", FormatTime(startsAt))));
        sb.AppendText(Tx.Get(gid, "lfg.post.roster", ("filled", g.FilledCount), ("total", g.Slots.Count)));

        var open = g.Slots.Where(s => s.IsFree)
            .GroupBy(s => s.Role)
            .Select(gr => $"{gr.Key.Emoji()} {gr.Key.Label()} ×{gr.Count()}")
            .ToList();
        sb.AppendText(open.Count > 0
            ? Tx.Get(gid, "lfg.dm.openSlots", ("list", string.Join(", ", open)))
            : Tx.Get(gid, "lfg.dm.full"));

        return new EmbedBuilder()
            .WithTitle(Tx.Get(gid, "lfg.dm.title", ("activity", g.ActivityName)))
            .WithDescription(sb.ToString())
            .WithColor(Tx.Color(gid, "color.lfg.dm"))
            .WithFooter(Tx.Get(gid, "lfg.dm.footer"))
            .Build();
    }

    private static string SlotHead(int i, Slot s) => $"`{i + 1,2}.` {s.Role.Emoji()} **{s.Role.Label()}**";

    private static string Roster(LfgGroup g, bool withComments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < g.Slots.Count; i++)
        {
            sb.AppendLine(SlotLine(g.GuildId, i, g.Slots[i]));
            if (withComments && g.Slots[i].UserId is not null) AppendComment(sb, g.Slots[i].Comment);
        }

        if (g.Reserve.Count > 0)
        {
            sb.AppendLine();
            sb.AppendText(Tx.Get(g.GuildId, "lfg.post.reserve", ("count", g.Reserve.Count)));
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

    private static string SlotLine(ulong gid, int i, Slot s)
    {
        if (s.UserId is not { } userId) return $"{SlotHead(i, s)} — {Tx.Get(gid, "lfg.post.open")}";

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
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
