using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;

namespace LfgBot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class LfgModule(PanelService panels, LfgPublisher publisher, AuditLog audit, LfgStore store, ActivityStore activities, CharacterStore characters, TimeService time,
    LfgNotifyStore notifyPrefs, LfgNotifier notifier, LfgSignupService signup, BotOwner botOwner)
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
        await RespondAsync(enabled
            ? "🔔 DMs on: you will get a DM when a group is started for your roles."
            : "🔕 DMs off: you will no longer get LFG DMs on this server.", ephemeral: true);
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

        var draft = new LfgDraft(Context.Guild.Id, ChannelId, Context.User.Id);
        store.Drafts[draft.Id] = draft;

        var s = LfgRenderer.SizeStep(draft);
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

        await ShowAsync(LfgRenderer.ActivityStep(d, activities.Get(d.GuildId)));
    }

    [ComponentInteraction("lfg:act:*")]
    public async Task OnActivity(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;

        if (values[0] == LfgRenderer.CustomActivityValue)
        {
            await RespondWithModalAsync<CustomActivityModal>($"lfg:custom:{d.Id}");
            return;
        }

        var activity = activities.Find(d.GuildId, values[0]);
        if (activity is null)
        {
            await RespondAsync("This activity was deleted. Pick another one.", ephemeral: true);
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
        await ShowAsync(new Screen("Signup cancelled.", null, new ComponentBuilder().Build()));
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
        await RespondWithModalAsync<TimeModal>($"lfg:timeset:{d.Id}");
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
            warning = $"Could not parse \"{Format.Sanitize(modal.When)}\". Use `21:30` or `15.10 21:30` (day.month).";
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
            warning = "This time is in the past. Pick another one.";
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
            await RespondAsync(NoAccessText, ephemeral: true);
            return;
        }

        if (!store.Drafts.TryRemove(draftId, out var d))
        {
            await RespondAsync("This draft has expired. Start again.", ephemeral: true);
            return;
        }

        var (group, jumpUrl, dmsStarted) = await publisher.PublishAsync(new LfgPublishRequest(
            d.GuildId, d.ChannelId, d.OwnerId, d.ActivityName ?? "Activity", d.PingRoleId,
            d.Slots.Select(x => x.Role).ToList(), startsAt, d.NotifyByDm));
        audit.FromDiscord(d.GuildId, Context.User, "lfg", "Created LFG group",
            $"{group.ActivityName} · {group.Slots.Count} slots · {(startsAt is { } at ? at.ToString("u") : "no time")}");

        var result = $"✅ Published: {jumpUrl}";
        if (dmsStarted) result += "\n🔔 Sending DMs to members with the activity role…";

        await ShowAsync(new Screen(result, null, new ComponentBuilder().Build()));
    }

    // ================= Опубликованный сбор =================

    [ComponentInteraction("lfg:join:*:*")]
    public async Task OnJoin(string groupId, string role)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync("This group is no longer active.", ephemeral: true);
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

        var saved = characters.Get(Context.Guild.Id, Context.User.Id);
        if (saved.Count == 0)
        {
            await RespondWithModalAsync<CharacterModal>($"lfg:charnew:{groupId}:{role}");
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
            await RespondWithModalAsync<CharacterModal>($"lfg:charnew:{groupId}:{role}");
            return;
        }

        characters.Add(Context.Guild.Id, Context.User.Id, values[0]); // поднимает персонажа наверх списка
        var result = await JoinAsync(groupId, Enum.Parse<SlotRole>(role), values[0]);
        await Component.UpdateAsync(m =>
        {
            m.Content = result;
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ModalInteraction("lfg:charnew:*:*")]
    public async Task OnNewCharacter(string groupId, string role, CharacterModal modal)
    {
        var name = modal.Name.Replace("`", "").Trim();
        if (name.Length == 0)
        {
            await RespondAsync("Character name cannot be empty.", ephemeral: true);
            return;
        }

        characters.Add(Context.Guild.Id, Context.User.Id, name);
        var result = await JoinAsync(groupId, Enum.Parse<SlotRole>(role), name);
        await RespondAsync(result, ephemeral: true);
    }

    [ComponentInteraction("lfg:invites:*")]
    public async Task OnInvites(string groupId)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync("This group is no longer active.", ephemeral: true);
            return;
        }

        Embed embed;
        await group.Gate.WaitAsync();
        try { embed = LfgRenderer.Invites(group, Context.User.Id); }
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
        return ManageAsync(groupId, g => g.MoveToReserve(userId), $"🔽 <@{userId}> was moved to the reserve.");
    }

    [ComponentInteraction("lfg:mremove:*")]
    public Task OnManageRemoveReserve(string groupId, string[] values)
    {
        var userId = ulong.Parse(values[0]);
        return ManageAsync(groupId, g => g.RemoveFromReserve(userId), $"✖️ <@{userId}> was removed from the reserve.");
    }

    [ComponentInteraction("lfg:mpromote:*")]
    public async Task OnManagePromote(string groupId, string[] values)
    {
        var userId = ulong.Parse(values[0]);
        Slot? promoted = null;
        var group = await ManageAsync(groupId, g => g.Promote(userId, out promoted),
            $"⬆️ <@{userId}> was moved from the reserve into the group and got a DM.");

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
            await RespondAsync("This group is no longer active.", ephemeral: true);
            return null;
        }

        if (!IsOrganizer(group))
        {
            await RespondAsync("Only the organizer can manage this group.", ephemeral: true);
            return null;
        }

        return group;
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
            await RespondAsync("This group is no longer active.", ephemeral: true);
            return;
        }

        if (!IsOrganizer(group))
        {
            await RespondAsync("Only the organizer can close this group.", ephemeral: true);
            return;
        }

        Embed invites;
        await group.Gate.WaitAsync();
        try { invites = LfgRenderer.Invites(group, Context.User.Id); }
        finally { group.Gate.Release(); }

        var buttons = new ComponentBuilder()
            .WithButton("Close & delete post", $"lfg:closeok:{groupId}", ButtonStyle.Danger, new Emoji("🗑️"))
            .WithButton("Keep", $"lfg:closeno:{groupId}", ButtonStyle.Secondary)
            .Build();

        await RespondAsync("Close this group? The post will be deleted. Copy the invites below first if you need them.",
            embed: invites, components: buttons, ephemeral: true);
    }

    [ComponentInteraction("lfg:closeok:*")]
    public async Task OnCloseConfirmed(string groupId)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await ShowAsync(new Screen("This group is no longer active.", null, new ComponentBuilder().Build()));
            return;
        }

        if (!IsOrganizer(group))
        {
            await RespondAsync("Only the organizer can close this group.", ephemeral: true);
            return;
        }

        await group.Gate.WaitAsync();
        try
        {
            group.IsClosed = true;
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
            m.Content = "✅ Group closed and the post was deleted.";
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ComponentInteraction("lfg:closeno:*")]
    public Task OnCloseCancelled(string groupId) =>
        Component.UpdateAsync(m =>
        {
            m.Content = "Group kept open.";
            m.Embeds = Array.Empty<Embed>();
            m.Components = new ComponentBuilder().Build();
        });

    // ================= Хелперы =================

    private async Task WithGroupAsync(string groupId, Func<LfgGroup, string?> mutate)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync("This group is no longer active.", ephemeral: true);
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
            return "This group is no longer active.";
        return await signup.JoinAsync(group, Context.User.Id, role, characterName);
    }

    private const string NoAccessText =
        "I can't access this channel. Give the bot **View Channel**, **Send Messages** and **Embed Links** here.";

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
        await RespondAsync("This draft has expired. Start again.", ephemeral: true);
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
