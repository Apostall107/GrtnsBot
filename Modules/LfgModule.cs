using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;

namespace LfgBot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class LfgModule(PanelService panels, LfgPublisher publisher, AuditLog audit, LfgProfileService profile,
    LfgTemplateStore templates, LfgStore store, ActivityStore activities, CharacterStore characters, TimeService time,
    LfgNotifyStore notifyPrefs, LfgSignupService signup, BotOwner botOwner,
    LfgProjectStore projects, PanelTextStore panelStore, AdminStore admins)
    : InteractionModuleBase<SocketInteractionContext>
{
    private SocketMessageComponent Component => (SocketMessageComponent)Context.Interaction;

    // ================= Запуск =================

    [SlashCommand("lfg-dm", "Turn LFG DM notifications on or off for yourself")]
    public Task DmPreferenceCommand(
        [Summary("mode", "on or off"), Choice("on", "on"), Choice("off", "off")] string mode) =>
        SetDmPreferenceAsync(mode == "on");

    // Кнопки «DMs on / DMs off» на панели LFG
    [ComponentInteraction("lfg:dmpref:*")]
    public Task DmPreferenceButton(string mode) => SetDmPreferenceAsync(mode == "on");

    private async Task SetDmPreferenceAsync(bool enabled)
    {
        notifyPrefs.SetEnabled(Context.Guild.Id, Context.User.Id, enabled);
        await RespondAsync(T(enabled ? "lfg.dmPref.on" : "lfg.dmPref.off"), ephemeral: true);
    }

    [SlashCommand("lfg", "Start a group / raid signup")]
    public Task LfgCommand() => StartDraftAsync();

    [SlashCommand("lfg-panel", "Post the LFG panel with instructions in this channel")]
    [RequireBotAdmin]
    public async Task PanelCommand()
    {
        if (await GetChannelAsync(ChannelId) is not { } channel)
        {
            await RespondAsync(NoAccessText, ephemeral: true);
            return;
        }

        await panels.PostAsync(channel, Context.Guild.Id, "lfg");
        await RespondAsync("Panel posted.", ephemeral: true);
    }

    [ComponentInteraction("lfg:start")]
    public Task StartButton() => StartDraftAsync();

    private async Task StartDraftAsync()
    {
        store.PruneDrafts(TimeSpan.FromMinutes(30));

        // Проект сборов: панель, на которой нажали Create LFG (LFG → Projects), иначе панель этого канала, иначе по умолчанию
        var messageId = (Context.Interaction as SocketMessageComponent)?.Message.Id;
        var posted = messageId is { } mid ? panelStore.FindPosted(Context.Guild.Id, "lfg", mid) : null;
        var projectKey = posted?.ProjectKey ??
                         panelStore.GetPosted(Context.Guild.Id, "lfg").FirstOrDefault(p => p.ChannelId == ChannelId && p.ProjectKey is not null)?.ProjectKey;
        var project = projects.Effective(Context.Guild.Id, projectKey);
        if (!LfgProjectStore.CanUse(Context.Guild.GetUser(Context.User.Id), project))
        {
            await RespondAsync(T("lfg.wiz.projectRole", ("project", project!.Name), ("roles", string.Join(T("lfg.or"), project.RoleIds.Select(id => $"<@&{id}>")))),
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        var draft = new LfgDraft(Context.Guild.Id, ChannelId, Context.User.Id) { ProjectKey = project?.Key };
        store.Drafts[draft.Id] = draft;

        // Шаблоны этого проекта + общие (без проекта)
        var s = LfgRenderer.SizeStep(draft, templates.Get(Context.Guild.Id)
            .Where(t => t.ProjectKey is null || project is null || t.ProjectKey == project.Key).ToList());
        await RespondAsync(s.Content, embed: s.Embed, components: s.Components, ephemeral: true);
    }

    // ================= Шаги черновика =================

    [ComponentInteraction("lfg:size:*")]
    public async Task OnSize(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;

        var size = int.Parse(values[0]);
        d.Slots = Enumerable.Range(0, size).Select(_ => new Slot()).ToList();
        d.Selected.Clear();

        await ShowAsync(LfgRenderer.ActivityStep(d, activities.ForProject(d.GuildId, d.ProjectKey)));
    }

    [ComponentInteraction("lfg:tpl:*")]
    public async Task OnTemplate(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        if (templates.Find(d.GuildId, values[0]) is not { } t)
        {
            await RespondAsync(T("lfg.wiz.templateGone"), ephemeral: true);
            return;
        }

        var activity = t.ActivityKey is { } key ? activities.Find(d.GuildId, key) : null;
        d.ActivityName = activity?.Name ?? t.CustomActivity ?? t.Name;
        d.PingRoleId = activity?.RoleId;
        d.Slots = t.Slots.Select(r => new Slot { Role = r }).ToList();
        d.Selected.Clear();
        d.RequiredRoleIds = t.RequiredRoleIds.ToList();
        d.MinGs = t.MinGs;
        await ShowAsync(TimeScreen(d));
    }

    [ComponentInteraction("lfg:act:*")]
    public async Task OnActivity(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;

        if (values[0] == LfgRenderer.CustomActivityValue)
        {
            await RespondWithModalAsync(LfgRenderer.CustomActivityModal(d.GuildId, $"lfg:custom:{d.Id}"));
            return;
        }

        var activity = activities.Find(d.GuildId, values[0]);
        if (activity is null)
        {
            await RespondAsync(T("lfg.wiz.activityGone"), ephemeral: true);
            return;
        }

        d.ActivityName = activity.Name;
        d.PingRoleId = activity.RoleId;
        await ShowAsync(LfgRenderer.SlotStep(d));
    }

    [ModalInteraction("lfg:custom:*")]
    public async Task OnCustomActivity(string draftId, CustomActivityModal modal)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;

        d.ActivityName = modal.Name.Trim();
        d.PingRoleId = null;

        var s = LfgRenderer.SlotStep(d);
        await RespondAsync(s.Content, embed: s.Embed, components: s.Components, ephemeral: true);
    }

    [ComponentInteraction("lfg:slots:*")]
    public async Task OnSlotsSelected(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;

        d.Selected = values.Select(int.Parse).ToHashSet();
        await ShowAsync(LfgRenderer.SlotStep(d));
    }

    [ComponentInteraction("lfg:setrole:*:*")]
    public async Task OnSetRole(string draftId, string role)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;

        var slotRole = Enum.Parse<SlotRole>(role);
        foreach (var index in d.Selected)
            d.Slots[index].Role = slotRole;
        d.Selected.Clear();

        await ShowAsync(LfgRenderer.SlotStep(d));
    }

    [ComponentInteraction("lfg:cancel:*")]
    public async Task OnCancel(string draftId)
    {
        store.Drafts.TryRemove(draftId, out _);
        await ShowAsync(new Screen(T("lfg.wiz.cancelled"), null, new ComponentBuilder().Build()));
    }

    // ================= Шаг 4: время =================

    [ComponentInteraction("lfg:next:*")]
    public async Task OnNext(string draftId)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        await ShowAsync(TimeScreen(d));
    }

    [ComponentInteraction("lfg:back:*")]
    public async Task OnBack(string draftId)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        await ShowAsync(LfgRenderer.SlotStep(d));
    }

    [ComponentInteraction("lfg:date:*")]
    public async Task OnDate(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        d.EventDate = DateOnly.ParseExact(values[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        d.EventHour ??= time.DefaultHour();
        await ShowAsync(TimeScreen(d));
    }

    [ComponentInteraction("lfg:hour:*")]
    public async Task OnHour(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        d.EventHour = int.Parse(values[0]);
        d.EventDate ??= time.Today;
        await ShowAsync(TimeScreen(d));
    }

    [ComponentInteraction("lfg:min:*")]
    public async Task OnMinute(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        d.EventMinute = int.Parse(values[0]);
        d.EventDate ??= time.Today;
        d.EventHour ??= time.DefaultHour();
        await ShowAsync(TimeScreen(d));
    }

    [ComponentInteraction("lfg:dm:*")]
    public async Task OnToggleDm(string draftId)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        d.NotifyByDm = !d.NotifyByDm;
        await ShowAsync(TimeScreen(d));
    }

    [ComponentInteraction("lfg:dvoice:*")]
    public async Task OnToggleVoice(string draftId)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        d.CreateVoice = !d.CreateVoice;
        await ShowAsync(TimeScreen(d));
    }

    [ComponentInteraction("lfg:notime:*")]
    public async Task OnNoTime(string draftId)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        d.ClearTime();
        await ShowAsync(TimeScreen(d));
    }

    [ComponentInteraction("lfg:timemodal:*")]
    public async Task OnTimeModal(string draftId)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        await RespondWithModalAsync(LfgRenderer.TimeModal(d.GuildId, $"lfg:timeset:{d.Id}"));
    }

    [ModalInteraction("lfg:timeset:*")]
    public async Task OnTimeEntered(string draftId, TimeModal modal)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;

        string? warning = null;
        if (time.TryParse(modal.When, out var date, out var hour, out var minute))
        {
            d.EventDate = date;
            d.EventHour = hour;
            d.EventMinute = minute;
        }
        else
        {
            warning = T("lfg.wiz.badTime", ("text", Format.Sanitize(modal.When)));
        }

        var s = TimeScreen(d, warning);
        await RespondAsync(s.Content, embed: s.Embed, components: s.Components, ephemeral: true);
    }

    private DateTimeOffset? StartsAt(LfgDraft d) =>
        d.EventDate is { } date && d.EventHour is { } hour ? time.ToUtc(date, hour, d.EventMinute) : null;

    private Screen TimeScreen(LfgDraft d, string? warning = null)
    {
        var startsAt = StartsAt(d);
        if (warning is null && startsAt is { } st && st < DateTimeOffset.UtcNow)
            warning = T("lfg.wiz.pastTime");
        return LfgRenderer.TimeStep(d, time.Today, startsAt, time.ZoneName, warning);
    }

    [ComponentInteraction("lfg:publish:*")]
    public async Task OnPublish(string draftId)
    {
        if (await GetDraftAsync(draftId) is not { } draft) return;

        var startsAt = StartsAt(draft);
        if (startsAt is { } st && st < DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            await ShowAsync(TimeScreen(draft));
            return;
        }

        if (await GetChannelAsync(draft.ChannelId) is not { } channel)
        {
            await RespondAsync(T("lfg.noAccess"), ephemeral: true);
            return;
        }

        if (!store.Drafts.TryRemove(draftId, out var d))
        {
            await RespondAsync(T("lfg.wiz.expired"), ephemeral: true);
            return;
        }

        var (group, jumpUrl, dmsStarted) = await publisher.PublishAsync(new LfgPublishRequest(
            d.GuildId, d.ChannelId, d.OwnerId, d.ActivityName ?? "Activity", d.PingRoleId,
            d.Slots.Select(x => x.Role).ToList(), startsAt, d.NotifyByDm, true, d.RequiredRoleIds, d.MinGs,
            d.ProjectKey, null, d.CreateVoice));
        audit.FromDiscord(d.GuildId, Context.User, "lfg", "Created LFG group",
            $"{group.ActivityName} · {group.Slots.Count} slots · {(startsAt is { } at ? at.ToString("u") : "no time")}");

        var result = T("lfg.wiz.published", ("link", jumpUrl));
        if (dmsStarted) result += "\n" + T("lfg.wiz.dmsSending");
        if (group.VoiceChannelId is { } vc) result += "\n" + T("lfg.wiz.voiceCreated", ("channel", $"<#{vc}>"));

        // Админ бота может продублировать пост в другие каналы
        var comps = new ComponentBuilder();
        if (Context.User is IGuildUser gu && admins.IsAdmin(gu))
        {
            result += Tx.Line(Context.Guild.Id, "lfg.wiz.mirrorHint");
            comps.WithSelectMenu(new SelectMenuBuilder()
                .WithCustomId($"lfg:mirror:{group.Id}")
                .WithType(ComponentType.ChannelSelect)
                .WithChannelTypes(ChannelType.Text, ChannelType.News, ChannelType.Voice)
                .WithPlaceholder(T("lfg.wiz.mirrorPlaceholder"))
                .WithMinValues(1)
                .WithMaxValues(5));
        }
        await ShowAsync(new Screen(result, null, comps.Build()));
    }

    [ComponentInteraction("lfg:mirror:*")]
    public async Task OnMirror(string groupId, string[] values)
    {
        if (!store.Groups.TryGetValue(groupId, out var group) || Context.User is not IGuildUser gu || !admins.IsAdmin(gu))
        {
            await RespondAsync(T("lfg.gone"), ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var added = await publisher.AddMirrorsAsync(group, values.Select(ulong.Parse));
        audit.FromDiscord(group.GuildId, Context.User, "lfg", "Duplicated LFG post", $"{group.ActivityName} → {added} channel(s)");
        await FollowupAsync(added > 0 ? T("lfg.wiz.mirrored", ("count", added)) : T("lfg.wiz.mirrorNone"), ephemeral: true);
    }

    // ================= Опубликованный сбор =================

    [ComponentInteraction("lfg:join:*:*")]
    public async Task OnJoin(string groupId, string role)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync(T("lfg.gone"), ephemeral: true);
            return;
        }

        var slotRole = Enum.Parse<SlotRole>(role);
        string? error;
        await group.Gate.WaitAsync();
        try { error = group.CheckJoin(Context.User.Id, slotRole); }
        finally { group.Gate.Release(); }

        if (error is not null)
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        // Сбор проекта (игры): без роли проекта записаться и выбрать его персонажей нельзя
        if (signup.ProjectBlock(group, Context.User.Id) is { } blocked)
        {
            await RespondAsync(blocked, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        var saved = profile.PickerList(group, Context.User.Id);
        if (saved.Count == 0)
        {
            await RespondWithModalAsync(LfgRenderer.CharacterModal(group.GuildId, $"lfg:charnew:{groupId}:{role}"));
            return;
        }

        var picker = LfgRenderer.CharacterPicker(group, slotRole, saved);
        await RespondAsync(picker.Content, components: picker.Components, ephemeral: true);
    }

    [ComponentInteraction("lfg:char:*:*")]
    public async Task OnCharacterPicked(string groupId, string role, string[] values)
    {
        if (values[0] == LfgRenderer.NewCharacterValue)
        {
            await RespondWithModalAsync(LfgRenderer.CharacterModal(Context.Guild.Id, $"lfg:charnew:{groupId}:{role}"));
            return;
        }

        if (Blocked(groupId) is { } blocked)
        {
            await RespondAsync(blocked, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        characters.Add(Context.Guild.Id, Context.User.Id, values[0], ScopeOf(groupId)); // поднимает персонажа наверх списка
        var result = await JoinAsync(groupId, Enum.Parse<SlotRole>(role), values[0]);
        var controls = ProfileControls(groupId);
        await Component.UpdateAsync(m =>
        {
            m.Content = result + (controls is null ? "" : ProfileHint(Context.Guild.Id));
            m.Components = controls ?? new ComponentBuilder().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
    }

    /// <summary>Персонажи хранятся по проекту сбора.</summary>
    private string? ScopeOf(string groupId) => store.Groups.TryGetValue(groupId, out var g) ? signup.Scope(g) : null;

    private string? Blocked(string groupId) =>
        store.Groups.TryGetValue(groupId, out var g) ? signup.ProjectBlock(g, Context.User.Id) : null;

    [ModalInteraction("lfg:charnew:*:*")]
    public async Task OnNewCharacter(string groupId, string role, CharacterModal modal)
    {
        var name = modal.Name.Replace("`", "").Trim();
        if (name.Length == 0)
        {
            await RespondAsync(T("lfg.sign.emptyName"), ephemeral: true);
            return;
        }

        if (Blocked(groupId) is { } blocked)
        {
            await RespondAsync(blocked, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        characters.Add(Context.Guild.Id, Context.User.Id, name, ScopeOf(groupId));
        var result = await JoinAsync(groupId, Enum.Parse<SlotRole>(role), name);
        var controls = ProfileControls(groupId);
        await RespondAsync(result + (controls is null ? "" : ProfileHint(Context.Guild.Id)), components: controls, ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    /// <summary>Под ответом о записи: выбрать класс-роль и добавить заметку к персонажу.</summary>
    public static string ProfileHint(ulong guildId) => Tx.Line(guildId, "lfg.sign.profileHint");

    private MessageComponent? ProfileControls(string groupId) =>
        store.Groups.TryGetValue(groupId, out var group) ? profile.Controls(group, Context.User.Id) : null;

    [ComponentInteraction("lfg:invites:*")]
    public Task OnInvites(string groupId) => ShowInvitesAsync(groupId, withCommand: true);

    /// <summary>Приглашение в данж: только ники, без /invite.</summary>
    [ComponentInteraction("lfg:dungeon:*")]
    public Task OnDungeonInvites(string groupId) => ShowInvitesAsync(groupId, withCommand: false);

    private async Task ShowInvitesAsync(string groupId, bool withCommand)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync(T("lfg.gone"), ephemeral: true);
            return;
        }

        Embed embed;
        await group.Gate.WaitAsync();
        try { embed = LfgRenderer.Invites(group, Context.User.Id, withCommand); }
        finally { group.Gate.Release(); }

        await RespondAsync(embed: embed, ephemeral: true);
    }

    // ================= Меню организатора =================

    [ComponentInteraction("lfg:manage:*")]
    public async Task OnManage(string groupId)
    {
        if (await GetOwnedGroupAsync(groupId) is not { } group) return;

        Screen screen;
        await group.Gate.WaitAsync();
        try { screen = LfgRenderer.Manage(group); }
        finally { group.Gate.Release(); }

        await RespondAsync(screen.Content, components: screen.Components, ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    [ComponentInteraction("lfg:mreserve:*")]
    public Task OnManageMoveToReserve(string groupId, string[] values)
    {
        var userId = ulong.Parse(values[0]);
        return ManageAsync(groupId, g => g.MoveToReserve(userId), T("lfg.manage.movedToReserve", ("user", $"<@{userId}>")));
    }

    [ComponentInteraction("lfg:mremove:*")]
    public Task OnManageRemoveReserve(string groupId, string[] values)
    {
        var userId = ulong.Parse(values[0]);
        return ManageAsync(groupId, g => g.RemoveFromReserve(userId), T("lfg.manage.removedFromReserve", ("user", $"<@{userId}>")));
    }

    [ComponentInteraction("lfg:mpromote:*")]
    public async Task OnManagePromote(string groupId, string[] values)
    {
        var userId = ulong.Parse(values[0]);
        Slot? promoted = null;
        var group = await ManageAsync(groupId, g => g.Promote(userId, out promoted),
            T("lfg.manage.promoted", ("user", $"<@{userId}>")));

        if (group is not null && promoted is not null)
            await signup.NotifyPromotedAsync(group, userId, promoted);
    }

    /// <summary>Действие организатора: изменить состав, сохранить, обновить пост и меню.</summary>
    private async Task<LfgGroup?> ManageAsync(string groupId, Func<LfgGroup, string?> action, string okText)
    {
        if (await GetOwnedGroupAsync(groupId) is not { } group) return null;

        await DeferAsync();
        string? error;
        Screen screen;
        await group.Gate.WaitAsync();
        try
        {
            error = action(group);
            if (error is null)
            {
                store.SaveGroups();
                await signup.RefreshPostAsync(group);
            }
            screen = LfgRenderer.Manage(group, error is null ? okText : $"⚠️ {error}");
            if (error is null)
                audit.FromDiscord(group.GuildId, Context.User, "lfg", "Changed LFG roster",
                    $"{group.ActivityName}: {okText.Replace("<@", "@").Replace(">", "")}");
        }
        finally
        {
            group.Gate.Release();
        }

        await Component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = screen.Content;
            m.Components = screen.Components;
            m.AllowedMentions = AllowedMentions.None;
        });
        return error is null ? group : null;
    }

    /// <summary>Организатор сбора или владелец бота.</summary>
    private bool IsOrganizer(LfgGroup group) => group.OwnerId == Context.User.Id || botOwner.Is(Context.User.Id);

    private async Task<LfgGroup?> GetOwnedGroupAsync(string groupId)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync(T("lfg.gone"), ephemeral: true);
            return null;
        }

        if (!IsOrganizer(group))
        {
            await RespondAsync(T("lfg.manage.onlyOrganizer"), ephemeral: true);
            return null;
        }

        return group;
    }

    [ComponentInteraction("lfg:voice:*")]
    public async Task OnVoiceRoom(string groupId)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync(T("lfg.gone"), ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var voiceBefore = group.VoiceChannelId;
        var result = await signup.CreateVoiceAsync(group, Context.User);
        if (group.VoiceChannelId is not null && group.VoiceChannelId != voiceBefore) audit.FromDiscord(group.GuildId, Context.User, "lfg", "Created group voice room", group.ActivityName);
        await FollowupAsync(result, ephemeral: true);
    }

    [ComponentInteraction("lfg:attend:*")]
    public Task OnAttendance(string groupId, string[] values)
    {
        var came = values.Select(ulong.Parse).ToHashSet();
        return ManageAsync(groupId, g =>
        {
            foreach (var s in g.Slots.Where(s => s.UserId is not null)) s.Attended = came.Contains(s.UserId!.Value);
            return null;
        }, T("lfg.manage.attendanceSaved", ("count", came.Count)));
    }

    [ComponentInteraction("lfg:resched:*")]
    public async Task OnReschedule(string groupId)
    {
        if (await GetOwnedGroupAsync(groupId) is not { } group) return;
        var current = group.StartsAt is { } st ? TimeZoneInfo.ConvertTime(st, time.Zone).ToString("dd.MM HH:mm") : "";
        var modal = new ModalBuilder()
            .WithTitle(T("lfg.manage.timeTitle"))
            .WithCustomId($"lfg:reschedok:{group.Id}")
            .AddTextInput(T("lfg.manage.timeLabel"), "when", TextInputStyle.Short,
                placeholder: T("lfg.wiz.timeHint"), maxLength: 20, required: false, value: current)
            .Build();
        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("lfg:reschedok:*")]
    public async Task OnRescheduleSubmitted(string groupId, LfgRescheduleModal modal)
    {
        if (!store.Groups.TryGetValue(groupId, out var group) || !IsOrganizer(group))
        {
            await RespondAsync(T("lfg.gone"), ephemeral: true);
            return;
        }

        var text = ((SocketModal)Context.Interaction).Data.Components.FirstOrDefault(c => c.CustomId == "when")?.Value?.Trim() ?? "";
        DateTimeOffset? startsAt = null;
        if (text.Length > 0)
        {
            if (!time.TryParse(text, out var date, out var hour, out var minute))
            {
                await RespondAsync(T("lfg.manage.badTime"), ephemeral: true);
                return;
            }
            startsAt = time.ToUtc(date, hour, minute);
            if (startsAt < DateTimeOffset.UtcNow.AddMinutes(-1))
            {
                await RespondAsync(T("lfg.manage.pastTime"), ephemeral: true);
                return;
            }
        }

        await DeferAsync(ephemeral: true);
        var error = await signup.RescheduleAsync(group, startsAt, time, Context.User);
        if (error is null)
            audit.FromDiscord(group.GuildId, Context.User, "lfg", "Changed LFG time",
                $"{group.ActivityName} → {(startsAt is { } t ? t.ToString("u") : "no time")}");
        await FollowupAsync(error ?? T("lfg.manage.rescheduled", ("time", startsAt is { } s ? LfgRenderer.FormatTime(s) : T("lfg.noTime"))),
            ephemeral: true);
    }

    [ComponentInteraction("lfg:leave:*")]
    public Task OnLeave(string groupId) =>
        WithGroupAsync(groupId, g => g.Leave(Context.User.Id));

    // Закрыть может только организатор. Сначала подтверждение + список /invite, потом пост удаляется.
    [ComponentInteraction("lfg:close:*")]
    public async Task OnClose(string groupId)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync(T("lfg.gone"), ephemeral: true);
            return;
        }

        if (!IsOrganizer(group))
        {
            await RespondAsync(T("lfg.close.onlyOrganizer"), ephemeral: true);
            return;
        }

        Embed invites;
        await group.Gate.WaitAsync();
        try { invites = LfgRenderer.Invites(group, Context.User.Id); }
        finally { group.Gate.Release(); }

        var buttons = new ComponentBuilder()
            .WithButton(T("lfg.close.confirm"), $"lfg:closeok:{groupId}", ButtonStyle.Danger, new Emoji("🗑️"))
            .WithButton(T("lfg.close.keep"), $"lfg:closeno:{groupId}", ButtonStyle.Secondary)
            .Build();

        await RespondAsync(T("lfg.close.question"),
            embed: invites, components: buttons, ephemeral: true);
    }

    [ComponentInteraction("lfg:closeok:*")]
    public async Task OnCloseConfirmed(string groupId)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await ShowAsync(new Screen(T("lfg.gone"), null, new ComponentBuilder().Build()));
            return;
        }

        if (!IsOrganizer(group))
        {
            await RespondAsync(T("lfg.close.onlyOrganizer"), ephemeral: true);
            return;
        }

        await group.Gate.WaitAsync();
        try
        {
            group.IsClosed = true;
            store.RecordClosed(group, "closed");
            store.Groups.TryRemove(groupId, out _);
            store.SaveGroups();
            await signup.DeleteMessagesAsync(group); // пост и напоминание
        }
        finally
        {
            group.Gate.Release();
        }
        audit.FromDiscord(group.GuildId, Context.User, "lfg", "Closed LFG group", group.ActivityName);

        // Список инвайтов остаётся в эфемерном сообщении, убираем только кнопки
        await Component.UpdateAsync(m =>
        {
            m.Content = T("lfg.close.done");
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ComponentInteraction("lfg:closeno:*")]
    public Task OnCloseCancelled(string groupId) =>
        Component.UpdateAsync(m =>
        {
            m.Content = T("lfg.close.kept");
            m.Embeds = Array.Empty<Embed>();
            m.Components = new ComponentBuilder().Build();
        });

    // ================= Хелперы =================

    private async Task WithGroupAsync(string groupId, Func<LfgGroup, string?> mutate)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync(T("lfg.gone"), ephemeral: true);
            return;
        }

        await group.Gate.WaitAsync();
        try
        {
            var error = mutate(group);
            if (error is not null)
            {
                await RespondAsync(error, ephemeral: true);
                return;
            }

            store.SaveGroups();
            await ShowAsync(LfgRenderer.Group(group));
        }
        finally
        {
            group.Gate.Release();
        }
    }

    /// <summary>Записывает участника и обновляет сообщение сбора по его ID. Возвращает текст для пользователя.</summary>
    private async Task<string> JoinAsync(string groupId, SlotRole role, string characterName)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
            return T("lfg.gone");
        return await signup.JoinAsync(group, Context.User.Id, role, characterName);
    }

    private string NoAccessText => T("lfg.noAccess");

    private string T(string key, params (string Name, object? Value)[] args) => Tx.Get(Context.Guild?.Id ?? 0, key, args);

    /// <summary>ID канала берётся из самого взаимодействия: Context.Channel бывает null, если канала нет в кэше.</summary>
    private ulong ChannelId => Context.Interaction.ChannelId
        ?? throw new InvalidOperationException("Interaction has no channel.");

    /// <summary>Канал из кэша, иначе через REST. null — у бота нет доступа к каналу.</summary>
    private async Task<IMessageChannel?> GetChannelAsync(ulong channelId)
    {
        if (Context.Client.GetChannel(channelId) is IMessageChannel cached) return cached;
        try
        {
            return await Context.Client.Rest.GetChannelAsync(channelId) as IMessageChannel;
        }
        catch (Discord.Net.HttpException)
        {
            return null;
        }
    }

    private async Task<LfgDraft?> GetDraftAsync(string draftId)
    {
        if (store.Drafts.TryGetValue(draftId, out var draft)) return draft;
        await RespondAsync(T("lfg.wiz.expired"), ephemeral: true);
        return null;
    }

    /// <summary>Редактирует сообщение, на котором нажали компонент.</summary>
    private Task ShowAsync(Screen s) => Component.UpdateAsync(m =>
    {
        if (s.Content is not null) m.Content = s.Content;
        m.Embeds = s.Embed is null ? Array.Empty<Embed>() : new[] { s.Embed };
        m.Components = s.Components;
    });
}

public sealed class CustomActivityModal : IModal
{
    public string Title => "Custom activity";

    [InputLabel("Name")]
    [ModalTextInput("name", TextInputStyle.Short, placeholder: "e.g. World boss farm", maxLength: 100)]
    public string Name { get; set; } = "";
}

public sealed class TimeModal : IModal
{
    public string Title => "Start time";

    [InputLabel("Date and time (server time zone)")]
    [ModalTextInput("when", TextInputStyle.Short, placeholder: "21:30 or 15.10 21:30", maxLength: 20)]
    public string When { get; set; } = "";
}

public sealed class CharacterModal : IModal
{
    public string Title => "Character";

    [InputLabel("Character name")]
    [ModalTextInput("name", TextInputStyle.Short, placeholder: "In-game name", maxLength: CharacterStore.MaxNameLength)]
    public string Name { get; set; } = "";
}

/// <summary>Окно «Change time» (строится вручную, поле when читается из ответа). Нужен для регистрации обработчика.</summary>
public sealed class LfgRescheduleModal : IModal
{
    public string Title => "Change time";

    [ModalTextInput("when")]
    public string? When { get; set; }
}
