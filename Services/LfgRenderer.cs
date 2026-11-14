using System.Globalization;
using System.Text;
using Discord;
using LfgBot.Models;

namespace LfgBot.Services;

public sealed record Screen(string? Content, Embed? Embed, MessageComponent Components);

public static class LfgRenderer
{
    public const int MinSize = 2;
    /// <summary>Максимум участников сбора (больше 25 — компактный пост, выбор слотов страницами).</summary>
    public const int MaxSize = 100;
    /// <summary>Сколько пунктов в выпадающем списке Discord (лимит API).</summary>
    public const int MenuMax = 25;
    public const string CustomActivityValue = "__custom";
    public const string AllActivitiesValue = "__all";
    public const string NewCharacterValue = "__new";
    /// <summary>Версия вида поста сбора: увеличить при изменении поста/кнопок — активные посты перерисуются при старте бота.</summary>
    public const int PostVersion = 4;

    // ---------- Панель в канале ----------
    /// <summary>Тексты по умолчанию — <see cref="PanelDefs.Lfg"/>, свои — из веб-админки (PanelTextStore).</summary>
    public static Screen Panel(int autoDeleteHours, PanelText? text = null, int reminderMinutes = 10)
    {
        var def = PanelDefs.Lfg;
        var embed = new EmbedBuilder()
            .WithSiteTitle(def.Title(text))
            .WithColor(def.Color(text))
            .WithDescription(def.Description(text)
                .Replace("{hours}", autoDeleteHours.ToString())
                .Replace("{reminder}", reminderMinutes.ToString()))
            .WithSiteField()
            .Build();

        var comps = new ComponentBuilder()
            .WithButton(def.Label(text, "start"), "lfg:start", ButtonStyle.Success, new Emoji("📣"))
            .WithButton(def.Label(text, "dmon"), "lfg:dmpref:on", ButtonStyle.Secondary, new Emoji("🔔"))
            .WithButton(def.Label(text, "dmoff"), "lfg:dmpref:off", ButtonStyle.Secondary, new Emoji("🔕"))
            .WithSiteButton()
            .Build();

        return new Screen(null, embed, comps);
    }

    /// <summary>Избранные размеры групп — кнопками; остальные — по «Other size».</summary>
    public static readonly int[] FavoriteSizes = [5, 10, 20];

    // ---------- Шаг 0: персонаж организатора ----------
    /// <summary>С каким персонажем идёт организатор: от его GS зависят активности, которые он увидит.</summary>
    public static Screen OwnerCharacterStep(LfgDraft d, IReadOnlyList<PickerCharacter> characters)
    {
        var gid = d.GuildId;
        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:ochar:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.ownerCharPlaceholder"));
        foreach (var c in characters.Take(CharacterStore.MaxPerUser))
        {
            var option = new SelectMenuOptionBuilder().WithLabel(Trim(c.Name + GsText(c.Gs, c.Cp))).WithValue(c.Name);
            if (!string.IsNullOrWhiteSpace(c.Note)) option.WithDescription(Trim("💬 " + c.Note));
            if (LfgProjectStore.ToEmote(c.Icon) is { } emote) option.WithEmote(emote);
            menu.AddOption(option);
        }
        var comps = new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Secondary, row: 1)
            .Build();
        return new Screen(Tx.Get(gid, "lfg.wiz.ownerChar"), null, comps);
    }

    // ---------- Шаг 1: размер ----------
    public static Screen SizeStep(LfgDraft d, IReadOnlyList<LfgTemplate>? templates = null)
    {
        var gid = d.GuildId;
        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:size:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.sizePlaceholder"));
        for (var i = MinSize; i <= MenuMax; i++)
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

        if (d.AllSizes)
            builder.WithSelectMenu(menu, row: row++)
                .WithButton(Tx.Get(gid, "lfg.wiz.enterSize"), $"lfg:sizenum:{d.Id}", ButtonStyle.Primary, new Emoji("✏️"), row: row)
                .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Secondary, row: row);
        else
        {
            // Избранные размеры — кнопками, полный выбор — «Other size…»
            foreach (var n in FavoriteSizes)
                builder.WithButton(Tx.Get(gid, "lfg.wiz.sizeOption", ("n", n)), $"lfg:sizeb:{d.Id}:{n}", ButtonStyle.Primary, new Emoji("👥"), row: row);
            builder.WithButton(Tx.Get(gid, "lfg.wiz.otherSize"), $"lfg:sizemore:{d.Id}", ButtonStyle.Secondary, row: row)
                .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Secondary, row: row);
        }

        var text = templates is { Count: > 0 } ? Tx.Get(gid, "lfg.wiz.step1Templates") : Tx.Get(gid, "lfg.wiz.step1");
        return new Screen(text, null, builder.Build());
    }

    // ---------- Шаг 2: активность ----------
    /// <param name="hidden">Сколько активностей скрыто: персонаж организатора не проходит по GS.</param>
    /// <param name="hiddenByRole">Сколько активностей скрыто: у организатора нет их роли (пункт «Все активности» их покажет).</param>
    public static Screen ActivityStep(LfgDraft d, IReadOnlyList<Activity> activities, int hidden = 0, int hiddenByRole = 0)
    {
        var gid = d.GuildId;
        var menu = new SelectMenuBuilder()
            .WithCustomId($"lfg:act:{d.Id}")
            .WithPlaceholder(Tx.Get(gid, "lfg.wiz.actPlaceholder"));

        var actDesc = Tx.Get(gid, "lfg.wiz.actOption");
        foreach (var a in activities.Take(ActivityStore.MaxInMenu))
            menu.AddOption(a.MinGs is { } min ? Trim($"{a.Name} · GS {min}+") : a.Name, a.Key, actDesc.Length == 0 ? null : actDesc);

        if (hiddenByRole > 0)
            menu.AddOption(Tx.Get(gid, "lfg.wiz.allActivities"), AllActivitiesValue,
                Trim(Tx.Get(gid, "lfg.wiz.allActivitiesOption", ("n", hiddenByRole))), new Emoji("📋"));
        menu.AddOption(Tx.Get(gid, "lfg.wiz.custom"), CustomActivityValue, Tx.Get(gid, "lfg.wiz.customOption"), new Emoji("✏️"));

        var comps = new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Secondary, row: 1)
            .Build();

        var hint = activities.Count == 0 && hidden == 0 ? Tx.Line(gid, "lfg.wiz.noActivities") : "";
        if (hidden > 0 && d.OwnerCharacter is { } owner)
            hint += Tx.Line(gid, "lfg.wiz.noActivityForGs", ("name", Format.Sanitize(owner)), ("gs", d.OwnerGs?.ToString() ?? "—"));

        return new Screen(Tx.Get(gid, "lfg.wiz.step2", ("size", d.Slots.Count)) + hint, null, comps);
    }

    // ---------- Шаг 3: роли слотов ----------
    public static Screen SlotStep(LfgDraft d)
    {
        var gid = d.GuildId;
        var pages = (d.Slots.Count + MenuMax - 1) / MenuMax;
        d.SlotPage = Math.Clamp(d.SlotPage, 0, Math.Max(0, pages - 1));
        var from = d.SlotPage * MenuMax;
        var to = Math.Min(d.Slots.Count, from + MenuMax);

        var sb = new StringBuilder();
        sb.AppendText(d.PingRoleId is { } roleId ? Tx.Get(gid, "lfg.wiz.ping", ("role", $"<@&{roleId}>")) : Tx.Get(gid, "lfg.wiz.noPing"));
        sb.AppendLine();
        // Большой сбор: итог по ролям + слоты текущей страницы
        if (pages > 1)
            sb.AppendLine("**" + string.Join(" · ", d.Slots.GroupBy(x => x.Role).Select(g => $"{g.Key.Emoji()} {g.Key.Label()} ×{g.Count()}")) + "**")
              .AppendLine($"-# {from + 1}–{to} / {d.Slots.Count}");
        for (var i = from; i < to; i++)
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
            .WithMaxValues(to - from);
        for (var i = from; i < to; i++)
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

        if (pages > 1)
            builder.WithButton("◀", $"lfg:spage:{d.Id}:{d.SlotPage - 1}", ButtonStyle.Secondary, disabled: d.SlotPage == 0, row: 2)
                .WithButton("▶", $"lfg:spage:{d.Id}:{d.SlotPage + 1}", ButtonStyle.Secondary, disabled: d.SlotPage >= pages - 1, row: 2);
        builder
            .WithButton(Tx.Get(gid, "lfg.wiz.next"), $"lfg:next:{d.Id}", ButtonStyle.Success, new Emoji("➡️"), row: 2)
            .WithButton(Tx.Get(gid, "lfg.wiz.cancel"), $"lfg:cancel:{d.Id}", ButtonStyle.Danger, row: 2);

        return new Screen(Tx.Get(gid, "lfg.wiz.step3"), embed, builder.Build());
    }

    // ---------- Шаг 4: время ----------
    // Инвариантная культура = английские названия дней (сборка без ICU — именованных культур нет)
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    public const int DateDaysAhead = 14;

    public static Screen TimeStep(LfgDraft d, DateOnly today, DateTimeOffset? startsAt, string zoneName, string? warning = null, bool dmAvailable = true)
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
        if (d.PingRoleId is not null && dmAvailable)
            builder.WithButton(Tx.Get(gid, d.NotifyByDm ? "lfg.wiz.dmOn" : "lfg.wiz.dmOff"), $"lfg:dm:{d.Id}",
                d.NotifyByDm ? ButtonStyle.Success : ButtonStyle.Secondary,
                new Emoji(d.NotifyByDm ? "🔔" : "🔕"), row: 4);
        // Голосовая комната сбора — только если включить
        builder.WithButton(Tx.Get(gid, d.CreateVoice ? "lfg.wiz.voiceOn" : "lfg.wiz.voiceOff"), $"lfg:dvoice:{d.Id}",
            d.CreateVoice ? ButtonStyle.Success : ButtonStyle.Secondary, new Emoji("🔊"), row: 4);
        // Заметка к сбору (необязательная)
        builder.WithButton(Tx.Get(gid, "lfg.wiz.noteButton"), $"lfg:dnote:{d.Id}",
            string.IsNullOrWhiteSpace(d.Note) ? ButtonStyle.Secondary : ButtonStyle.Success, new Emoji("📝"), row: 4);

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
        if (!string.IsNullOrWhiteSpace(d.Note)) sb.AppendText(Tx.Get(gid, "lfg.wiz.note", ("note", Format.Sanitize(d.Note))));
        if (warning is not null) sb.AppendLine($"⚠️ {warning}");

        return new Screen(sb.ToString(), null, comps);
    }

    public static string FormatTime(DateTimeOffset t)
    {
        var unix = t.ToUnixTimeSeconds();
        return $"<t:{unix}:F> (<t:{unix}:R>)";
    }

    /// <summary>Текст-пинг над постом (роль + активность + дата). null — у сбора нет роли для пинга.</summary>
    public static string? PingContent(LfgGroup g)
    {
        if (g.PingRoleId is not { } roleId) return null;
        var when = g.StartsAt is { } t ? $" — <t:{t.ToUnixTimeSeconds()}:f>" : "";
        return Tx.Get(g.GuildId, "lfg.post.ping", ("role", $"<@&{roleId}>"), ("activity", g.ActivityName), ("when", when));
    }

    // ---------- Опубликованный сбор ----------
    public static Screen Group(LfgGroup g)
    {
        var gid = g.GuildId;
        var sb = new StringBuilder();
        // Сбор без организатора (повторяющийся, публикует бот) — строки организатора нет
        if (g.OwnerId != 0) sb.AppendText(Tx.Get(gid, "lfg.post.organizer", ("user", $"<@{g.OwnerId}>")));
        if (g.StartsAt is { } startsAt)
            sb.AppendText(Tx.Get(gid, "lfg.post.starts", ("time", FormatTime(startsAt))));
        sb.AppendText(Tx.Get(gid, "lfg.post.roster", ("filled", g.FilledCount), ("total", g.Slots.Count)));
        if (Requirements(g) is { } req) sb.AppendText(Tx.Get(gid, "lfg.post.requirements", ("list", req)));
        if (g.VoiceChannelId is { } voice) sb.AppendText(Tx.Get(gid, "lfg.post.voice", ("channel", $"<#{voice}>")));
        if (!string.IsNullOrWhiteSpace(g.Note)) sb.AppendText(Tx.Get(gid, "lfg.post.note", ("note", Format.Sanitize(g.Note))));
        sb.AppendLine();
        var expires = Tx.Get(gid, "lfg.post.expires", ("time", $"<t:{g.ExpiresAt.ToUnixTimeSeconds()}:R>"));
        // Заметки к персонажам в посте не показываются — их видит владелец при выборе персонажа.
        // Большой сбор: состав сжимается, чтобы уложиться в лимит Discord (4096 символов текста)
        sb.Append(Roster(g, withComments: false, budget: 3900 - sb.Length - expires.Length));

        if (expires.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(expires);
        }

        var (status, color) = g.IsClosed ? (Tx.Get(gid, "lfg.post.closed"), Tx.Color(gid, "color.lfg.closed"))
            : g.IsFull ? (Tx.Get(gid, "lfg.post.full"), Tx.Color(gid, "color.lfg.full"))
            : (Tx.Get(gid, "lfg.post.recruiting"), Tx.Color(gid, "color.lfg.recruiting"));

        var embed = new EmbedBuilder()
            .WithSiteTitle(g.ActivityName, SiteLink.GroupUrl(g.GuildId, g.Id))
            .WithDescription(sb.ToString())
            .WithColor(color)
            .WithFooter(Tx.Get(gid, "lfg.post.footer", ("status", status), ("id", g.Id)))
            .Build();

        var builder = new ComponentBuilder();
        // Серая кнопка роли = мест нет, нажатие запишет в резерв
        foreach (var role in SlotRoleExtensions.JoinRoles)
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
    public static Screen CharacterPicker(LfgGroup g, SlotRole role, IReadOnlyList<PickerCharacter> characters,
        string prefix = "lfg")
    {
        var gid = g.GuildId;
        var menu = new SelectMenuBuilder()
            .WithCustomId($"{prefix}:char:{g.Id}:{role}")
            .WithPlaceholder(Tx.Get(gid, "lfg.sign.charPlaceholder"));

        foreach (var c in characters.Take(CharacterStore.MaxPerUser))
        {
            // Ник · GS · CP в строке, заметка — строкой ниже
            var option = new SelectMenuOptionBuilder().WithLabel(Trim(c.Name + GsText(c.Gs, c.Cp))).WithValue(c.Name);
            if (!string.IsNullOrWhiteSpace(c.Note)) option.WithDescription(Trim("💬 " + c.Note));
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
        // После напоминания: кто ещё не нажал «Готов»
        if (g.ReminderMessageId is not null)
        {
            var notReady = g.Slots.Where(s => s.UserId is not null && !s.Ready).Select(s => $"<@{s.UserId}>").ToList();
            var list = string.Join(" ", notReady.Take(40)) + (notReady.Count > 40 ? $" +{notReady.Count - 40}" : "");
            if (notReady.Count > 0 && Tx.Get(gid, "lfg.manage.notReady", ("list", list)) is { Length: > 0 } nr)
                sb.AppendLine(nr);
        }

        var builder = new ComponentBuilder();
        var row = 0;

        var members = g.Slots
            .Select((slot, index) => (slot, index))
            .Where(x => x.slot.UserId is { } id && id != g.OwnerId)
            .Take(MenuMax)
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

            foreach (var (entry, index) in g.Reserve.Select((e, i) => (e, i)).Take(MenuMax))
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
        var seated = g.Slots.Where(s => s.UserId is not null).Take(MenuMax).ToList();
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
        builder.WithButton(Tx.Get(gid, "lfg.manage.noteButton"), $"lfg:mnote:{g.Id}", ButtonStyle.Secondary, new Emoji("📝"), row: Math.Min(row, 4));
        if (g.Slots.Any(s => s.IsFree))
            builder.WithButton(Tx.Get(gid, "lfg.manage.slotRoles"), $"lfg:mslots:{g.Id}", ButtonStyle.Secondary, new Emoji("🔁"), row: Math.Min(row, 4));

        if (members.Count == 0 && g.Reserve.Count == 0)
            sb.AppendText(Tx.Get(gid, "lfg.manage.nobody"));

        return new Screen(sb.ToString(), null, builder.Build());
    }

    /// <summary>Организатор: сменить роль свободного слота — один список «Слот N: старая → новая».</summary>
    public static Screen SlotRoles(LfgGroup g, string? note = null)
    {
        var gid = g.GuildId;
        var sb = new StringBuilder();
        if (note is not null) sb.AppendLine(note);
        var free = g.Slots.Select((s, i) => (s, i)).Where(x => x.s.IsFree).ToList();
        if (free.Count == 0) return new Screen(sb.Append(Tx.Get(gid, "lfg.manage.noFreeSlots")).ToString(), null, new ComponentBuilder().Build());
        if (Tx.Get(gid, "lfg.manage.slotRoleHint") is { Length: > 0 } hint) sb.AppendLine(hint);
        var menu = new SelectMenuBuilder().WithCustomId($"lfg:mslotset:{g.Id}").WithPlaceholder(Tx.Get(gid, "lfg.manage.slotRolePick"));
        // all roles for every free slot, at most 25 options (Discord's limit) — about 8 free slots
        foreach (var (slot, index) in free)
        foreach (var role in SlotRoleExtensions.All.Where(r => r != slot.Role))
        {
            if (menu.Options.Count >= 25) break;
            menu.AddOption(Trim(Tx.Get(gid, "lfg.manage.slotRoleOption", ("n", index + 1), ("from", slot.Role.Label()), ("to", role.Label()))),
                $"{index}:{role}", emote: new Emoji(role.Emoji()));
        }
        return new Screen(sb.ToString(), null, new ComponentBuilder().WithSelectMenu(menu).Build());
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

    public static Modal NoteModal(ulong gid, string customId, string? current) => new ModalBuilder()
        .WithTitle(Tx.Get(gid, "lfg.wiz.noteTitle"))
        .WithCustomId(customId)
        .AddTextInput(Tx.Get(gid, "lfg.wiz.noteLabel"), "note", TextInputStyle.Paragraph,
            placeholder: Tx.Get(gid, "lfg.wiz.noteHint"), maxLength: LfgNote.MaxLength, required: false, value: current)
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
        var home = HomeServer(g, viewerId);

        var lines = new List<(string Who, string Target)>();
        foreach (var s in g.Slots)
        {
            if (s.UserId is not { } userId || userId == viewerId) continue;
            if (s.CharacterName is not { } name)
            {
                withoutNick++;
                continue;
            }

            var role = s.TakenAs is { } t && t != SlotRole.Any ? t : s.Role;
            // Игрок с другого сервера: Имя[LUD] (формат — текст lfg.invites.otherServer)
            var target = ServerMark(s.Server, home).Length > 0
                ? Tx.Get(gid, "lfg.invites.otherServer", ("name", name), ("server", s.Server!))
                : name;
            lines.Add(($"{role.Emoji()} <@{userId}>", withCommand ? $"/invite {target}" : target));
        }
        count = lines.Count;
        if (count <= 12)
            foreach (var (who, target) in lines)
                sb.AppendLine(who).AppendLine($"```\n{target}\n```");
        else
        {
            // Большой сбор: все команды одним блоком (копировать построчно), лимит текста Discord — 4096
            var block = new StringBuilder();
            foreach (var (_, target) in lines)
            {
                if (block.Length + target.Length > 3800) break;
                block.AppendLine(target);
            }
            sb.AppendLine($"```\n{block}```");
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
        sb.AppendText(g.OwnerId == 0
            ? Tx.Get(gid, "lfg.dm.startedAuto", ("server", Format.Sanitize(guildName)))
            : Tx.Get(gid, "lfg.dm.started", ("user", $"<@{g.OwnerId}>"), ("server", Format.Sanitize(guildName))));
        if (g.StartsAt is { } startsAt)
            sb.AppendText(Tx.Get(gid, "lfg.post.starts", ("time", FormatTime(startsAt))));
        sb.AppendText(Tx.Get(gid, "lfg.post.roster", ("filled", g.FilledCount), ("total", g.Slots.Count)));

        var open = g.Slots.Where(s => s.IsOpen)
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

    private static string SlotHead(int i, Slot s) => $"`{i + 1,2}.` {s.Role.Emoji()} **{s.Role.Label()}**{SlotRequirementText(s)}";

    /// <summary>Своё требование слота: 🔒 роль — нужна именно она; 🔓 — слот без требования роли.</summary>
    private static string SlotRequirementText(Slot s) => s.Classes.Count > 0 ? $" 🔒 {string.Join(" / ", s.Classes.Select(id => $"<@&{id}>"))}" : s.Requirement switch
    {
        SlotRequirement.Inherit => "",
        SlotRequirement.Open => " 🔓",
        var id => $" 🔒 <@&{id}>"
    };

    /// <summary>«Свой» сервер сбора: сервер организатора, иначе сервер проекта по умолчанию.</summary>
    public static string? HomeServer(LfgGroup g, ulong? viewerId = null) =>
        (viewerId is { } v ? g.Slots.Find(x => x.UserId == v)?.Server : null) ?? g.Slots.Find(x => x.UserId == g.OwnerId)?.Server ?? g.HomeServer;

    /// <summary>Сервер игрока, если он не «свой»: [LUD]. Свой или неизвестен — пусто.</summary>
    private static string ServerMark(string? server, string? home) =>
        server is { Length: > 0 } && home is { Length: > 0 } && !server.Equals(home, StringComparison.OrdinalIgnoreCase) ? $" `[{server}]`" : "";

    private static string Roster(LfgGroup g, bool withComments, int budget = int.MaxValue)
    {
        var full = FullRoster(g, withComments);
        if (full.Length <= budget) return full;
        var compact = CompactRoster(g, mentionsOnly: false);
        if (compact.Length <= budget) return compact;
        var tiny = CompactRoster(g, mentionsOnly: true);
        if (tiny.Length <= budget) return tiny;
        var more = $"\n… {Tx.Get(g.GuildId, "lfg.post.moreOnSite")}";
        var cut = Math.Clamp(budget - more.Length, 0, tiny.Length);
        var line = cut > 0 ? tiny.LastIndexOf('\n', cut - 1) : -1;
        return tiny[..(line > 0 ? line : cut)] + more;
    }

    /// <summary>
    /// Сжатый состав большого сбора: занятые слоты по ролям одной строкой (ник или только упоминание),
    /// свободные — числом по ролям, резерв — списком.
    /// </summary>
    private static string CompactRoster(LfgGroup g, bool mentionsOnly)
    {
        var sb = new StringBuilder();
        string Who(ulong id, string? name, string? icon) =>
            mentionsOnly || name is null ? $"<@{id}>" : $"<@{id}>{ClassIcon(icon)} **{Format.Sanitize(name)}**";
        foreach (var role in g.Slots.Where(x => x.UserId is not null).GroupBy(x => x.TakenAs is { } t && t != SlotRole.Any ? t : x.Role))
            sb.AppendLine($"{role.Key.Emoji()} **{role.Key.Label()}** ({role.Count()}): " +
                          string.Join(mentionsOnly ? " " : " · ", role.Select(x => Who(x.UserId!.Value, x.CharacterName, x.ClassIcon) +
                              (x.Attended switch { true => " ✅", false => " ❌", _ => x.Ready ? " 🟢" : "" }))));
        var open = g.Slots.Where(x => x.IsOpen).GroupBy(x => x.Role).ToList();
        if (open.Count > 0)
            sb.AppendLine($"{Tx.Get(g.GuildId, "lfg.post.open")}: " + string.Join(" · ", open.Select(o => $"{o.Key.Emoji()} {o.Key.Label()} ×{o.Count()}")));
        var reserved = g.Slots.Count(x => x.UserId is null && x.Locked);
        if (reserved > 0) sb.AppendLine($"{Tx.Get(g.GuildId, "lfg.post.reserved")} ×{reserved}");
        if (g.Reserve.Count > 0)
        {
            sb.AppendLine();
            sb.AppendText(Tx.Get(g.GuildId, "lfg.post.reserve", ("count", g.Reserve.Count)));
            sb.AppendLine(string.Join(mentionsOnly ? " " : " · ", g.Reserve.Select(r => $"{r.Role.Emoji()} " + Who(r.UserId, r.CharacterName, r.ClassIcon))));
        }
        return sb.ToString();
    }

    private static string FullRoster(LfgGroup g, bool withComments)
    {
        var sb = new StringBuilder();
        var home = HomeServer(g);
        for (var i = 0; i < g.Slots.Count; i++)
        {
            sb.AppendLine(SlotLine(g.GuildId, i, g.Slots[i], home));
            if (withComments && g.Slots[i].UserId is not null) AppendComment(sb, g.Slots[i].Comment);
        }

        if (g.Reserve.Count > 0)
        {
            sb.AppendLine();
            sb.AppendText(Tx.Get(g.GuildId, "lfg.post.reserve", ("count", g.Reserve.Count)));
            foreach (var r in g.Reserve)
            {
                sb.AppendLine($"{r.Role.Emoji()} <@{r.UserId}>{ClassIcon(r.ClassIcon)} **{Format.Sanitize(r.CharacterName ?? "-")}**{ServerMark(r.Server, home)}{GsText(r.Gs, r.Cp)}");
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

    private static string SlotLine(ulong gid, int i, Slot s, string? home)
    {
        if (s.UserId is not { } userId) return $"{SlotHead(i, s)} — {Tx.Get(gid, s.Locked ? "lfg.post.reserved" : "lfg.post.open")}";

        var takenAs = s.Role == SlotRole.Any && s.TakenAs is { } t && t != SlotRole.Any
            ? $" ({t.Emoji()} {t.Label()})"
            : "";
        var character = s.CharacterName is { } name ? $" **{Format.Sanitize(name)}**{ServerMark(s.Server, home)}{GsText(s.Gs, s.Cp)}" : "";
        // Явка (после старта) важнее отметки готовности (🟢 — нажал «Готов» в напоминании)
        var attended = s.Attended switch { true => " ✅", false => " ❌", _ => s.Ready ? " 🟢" : "" };
        return $"{SlotHead(i, s)} — <@{userId}>{ClassIcon(s.ClassIcon)}{character}{takenAs}{attended}";
    }

    private static string GsText(int? gs, int? cp = null) => (gs is { } v ? $" · GS {v}" : "") + (cp is { } c ? $" · CP {Compact(c)}" : "");

    /// <summary>149159 → 149.2K (как на официальном сайте).</summary>
    public static string Compact(int v) => v >= 1_000_000
        ? (v / 1_000_000d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "M"
        : v >= 10_000 ? (v / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K" : v.ToString();

    /// <summary>Требования сбора текстом: роли (любая из) и минимальный GS.</summary>
    public static string? Requirements(LfgGroup g)
    {
        var parts = new List<string>();
        // Общие роли — только если есть слоты без своего требования
        if (g.RequiredRoleIds.Count > 0 && g.Slots.Any(s => s.Requirement == SlotRequirement.Inherit && s.Classes.Count == 0))
            parts.Add(string.Join(" / ", g.RequiredRoleIds.Select(id => $"<@&{id}>")));
        if (g.MinGs is { } min) parts.Add($"GS {min}+");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
