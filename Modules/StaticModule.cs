using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>Статики (КП) в Discord: панель, создание, заявки, управление организатора, пинг состава.</summary>
[RequireContext(ContextType.Guild)]
public sealed class StaticModule(StaticStore store, StaticService statics, LfgProjectStore projects, PanelService panels,
    PanelTextStore panelStore, AdminStore admins)
    : InteractionModuleBase<SocketInteractionContext>
{
    private SocketMessageComponent Component => (SocketMessageComponent)Context.Interaction;

    [SlashCommand("statics-panel", "Post the Statics (constant parties) panel in this channel")]
    [RequireBotAdmin]
    public async Task PanelCommand(
        [Summary("project", "Game / project of the statics created from this panel (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        var key = string.IsNullOrWhiteSpace(project) ? null : project;
        if (key is not null && projects.Find(Context.Guild.Id, key) is null)
        {
            await RespondAsync("Project not found.", ephemeral: true);
            return;
        }
        if (Context.Channel is not IMessageChannel channel)
        {
            await RespondAsync(T("lfg.noAccess"), ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        try
        {
            var message = await panels.PostAsync(channel, Context.Guild.Id, "statics");
            if (key is not null) panelStore.SetPanelProject(Context.Guild.Id, "statics", message.Id, key);
            await FollowupAsync(key is null ? "Panel posted." : $"Panel posted for **{projects.Find(Context.Guild.Id, key)!.Name}**.", ephemeral: true);
        }
        catch (Discord.Net.HttpException)
        {
            await FollowupAsync(T("lfg.noAccess"), ephemeral: true);
        }
    }

    // ================= Создание =================

    [ComponentInteraction("st:start")]
    public async Task OnStart()
    {
        store.PruneDrafts();
        // Проект: панель, на которой нажали, иначе панель статиков в этом канале, иначе проект по умолчанию
        var posted = panelStore.FindPosted(Context.Guild.Id, "statics", Component.Message.Id)
                     ?? panelStore.GetPosted(Context.Guild.Id, "statics").FirstOrDefault(p => p.ChannelId == ChannelId && p.ProjectKey is not null);
        var project = projects.Effective(Context.Guild.Id, posted?.ProjectKey);
        if (!LfgProjectStore.CanUse(Context.Guild.GetUser(Context.User.Id), project))
        {
            await RespondAsync(T("lfg.wiz.projectRole", ("project", project!.Name), ("roles", string.Join(T("lfg.or"), project.RoleIds.Select(id => $"<@&{id}>")))),
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        if (store.OwnedBy(Context.Guild.Id, Context.User.Id) >= StaticStore.MaxOwnedPerUser)
        {
            await RespondAsync(T("static.create.limit", ("max", StaticStore.MaxOwnedPerUser)), ephemeral: true);
            return;
        }
        var draft = new StaticDraft(Context.Guild.Id, ChannelId, Context.User.Id, project?.Key);
        store.Drafts[draft.Id] = draft;
        var (content, comps) = CreateScreen(draft);
        await RespondAsync(content, components: comps, ephemeral: true);
    }

    private (string Content, MessageComponent Components) CreateScreen(StaticDraft d)
    {
        var builder = new ComponentBuilder();
        var mine = statics.CharactersOf(d.GuildId, d.OwnerId, d.ProjectKey);
        var row = 0;
        if (mine.Count > 0)
        {
            var menu = new SelectMenuBuilder().WithCustomId($"st:ochar:{d.Id}").WithPlaceholder(T("static.create.charPlaceholder"));
            foreach (var c in mine.Take(25))
                menu.AddOption(c.Name, c.Name, PickDesc(c), isDefault: c.Name == d.Character);
            builder.WithSelectMenu(menu, row: row++);
        }
        foreach (var n in StaticService.FavoriteSizes)
            builder.WithButton(T("static.create.size", ("n", n)), $"st:size:{d.Id}:{n}", ButtonStyle.Primary, new Emoji("👥"), row: row);
        builder.WithButton(T("static.create.otherSize"), $"st:sizeother:{d.Id}", ButtonStyle.Secondary, row: row);
        var project = projects.GetProjects(d.GuildId).Count > 1 ? projects.Effective(d.GuildId, d.ProjectKey)?.Name : null;
        return ((project is null ? "" : $"🎮 **{project}**\n") + T("static.create.step"), builder.Build());
    }

    [ComponentInteraction("st:ochar:*")]
    public async Task OnOwnerCharacter(string draftId, string[] values)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        d.Character = values.FirstOrDefault();
        var (content, comps) = CreateScreen(d);
        await Component.UpdateAsync(m => { m.Content = content; m.Components = comps; });
    }

    [ComponentInteraction("st:size:*:*")]
    public async Task OnSize(string draftId, string size)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        if (!int.TryParse(size, out var n)) return;
        await RespondWithModalAsync(FormModal($"st:create:{d.Id}:{n}", T("static.create.modalTitle"), null, null, null));
    }

    [ComponentInteraction("st:sizeother:*")]
    public async Task OnOtherSize(string draftId)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        await RespondWithModalAsync(FormModal($"st:createx:{d.Id}", T("static.create.modalTitle"), null, null, ""));
    }

    [ModalInteraction("st:create:*:*")]
    public Task OnCreate(string draftId, string size, StaticNameModal form) =>
        CreateAsync(draftId, int.TryParse(size, out var n) ? n : 5, form.Name, form.Description);

    [ModalInteraction("st:createx:*")]
    public Task OnCreateCustom(string draftId, StaticFormModal form) =>
        CreateAsync(draftId, int.TryParse((form.Size ?? "").Trim(), out var n) ? n : 5, form.Name, form.Description);

    private async Task CreateAsync(string draftId, int size, string? name, string? description)
    {
        if (await GetDraftAsync(draftId) is not { } d) return;
        await DeferAsync(ephemeral: true);
        var (s, error) = await statics.CreateAsync(d.GuildId, d.ChannelId, Context.User, d.ProjectKey, name, description, size, d.Character, "discord");
        if (s is null)
        {
            await FollowupAsync(error ?? T("bot.error"), ephemeral: true);
            return;
        }
        store.Drafts.TryRemove(d.Id, out _);
        await FollowupAsync(T("static.create.done", ("name", s.Name), ("link", StaticService.JumpUrl(s))), ephemeral: true);
    }

    /// <summary>Окно названия и описания; size != null — с полем размера (пусто — «Other size»).</summary>
    private Modal FormModal(string customId, string title, string? name, string? description, string? size)
    {
        var mb = new ModalBuilder().WithTitle(title).WithCustomId(customId)
            .AddTextInput(T("static.create.nameLabel"), "name", TextInputStyle.Short, T("static.create.namePlaceholder"),
                minLength: 1, maxLength: StaticGroup.MaxName, required: true, value: name);
        if (size is not null)
            mb.AddTextInput(T("static.create.sizeLabel"), "size", TextInputStyle.Short, "8", minLength: 1, maxLength: 3, required: true,
                value: size.Length > 0 ? size : null);
        mb.AddTextInput(T("static.create.descLabel"), "desc", TextInputStyle.Paragraph, T("static.create.descPlaceholder"),
            maxLength: StaticGroup.MaxDescription, required: false, value: description);
        return mb.Build();
    }

    // ================= Мои статики =================

    [ComponentInteraction("st:mine")]
    public async Task OnMine()
    {
        var me = Context.User.Id;
        var list = store.ForGuild(Context.Guild.Id)
            .Where(s => s.FindMember(me) is not null || s.FindApplicant(me) is not null).ToList();
        if (list.Count == 0)
        {
            await RespondAsync(T("static.mine.none"), ephemeral: true);
            return;
        }
        var text = new StringBuilder(T("static.mine.title"));
        foreach (var s in list.Take(25))
        {
            var role = s.OwnerId == me ? T("static.mine.owner") : s.FindMember(me) is not null ? T("static.mine.member") : T("static.mine.applied");
            var title = s.MessageId != 0 ? $"[{s.Name}]({StaticService.JumpUrl(s)})" : $"**{s.Name}**";
            text.Append($"\n• {title} — {role} · {s.Members.Count}/{s.Size}");
        }
        await RespondAsync(text.ToString(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    // ================= Заявка =================

    [ComponentInteraction("st:apply:*")]
    public async Task OnApply(string id)
    {
        if (await GetStaticAsync(id) is not { } s) return;
        var me = Context.User.Id;
        var project = projects.Effective(s.GuildId, s.ProjectKey);
        if (!LfgProjectStore.CanUse(Context.Guild.GetUser(me), project))
        {
            await RespondAsync(T("lfg.wiz.projectRole", ("project", project!.Name), ("roles", string.Join(T("lfg.or"), project.RoleIds.Select(r => $"<@&{r}>")))),
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        var early = !s.Recruiting ? T("static.apply.closed")
            : s.FindMember(me) is not null ? T("static.apply.member")
            : s.FindApplicant(me) is not null ? T("static.apply.already") : null;
        if (early is not null)
        {
            await RespondAsync(early, ephemeral: true);
            return;
        }

        var mine = statics.CharactersOf(s.GuildId, me, s.ProjectKey);
        if (mine.Count == 0)
        {
            await DeferAsync(ephemeral: true);
            var error = await statics.ApplyAsync(s, Context.User, null, null);
            var (content, comps) = SentScreen(s, error);
            await FollowupAsync(content, components: comps, ephemeral: true);
            return;
        }
        var menu = new SelectMenuBuilder().WithCustomId($"st:applychar:{s.Id}").WithPlaceholder(T("static.apply.charPlaceholder"));
        foreach (var c in mine.Take(25)) menu.AddOption(c.Name, c.Name, PickDesc(c));
        var builder = new ComponentBuilder().WithSelectMenu(menu, row: 0)
            .WithButton(T("static.apply.noChar"), $"st:applynone:{s.Id}", ButtonStyle.Secondary, row: 1);
        await RespondAsync(T("static.apply.pick", ("name", s.Name)), components: builder.Build(), ephemeral: true);
    }

    [ComponentInteraction("st:applychar:*")]
    public Task OnApplyCharacter(string id, string[] values) => ApplyFromPickerAsync(id, values.FirstOrDefault());

    [ComponentInteraction("st:applynone:*")]
    public Task OnApplyWithout(string id) => ApplyFromPickerAsync(id, null);

    private async Task ApplyFromPickerAsync(string id, string? character)
    {
        if (await GetStaticAsync(id) is not { } s) return;
        await DeferAsync();
        var error = await statics.ApplyAsync(s, Context.User, character, null);
        var (content, comps) = SentScreen(s, error);
        await ModifyOriginalResponseAsync(m => { m.Content = content; m.Components = comps; });
    }

    private (string Content, MessageComponent Components) SentScreen(StaticGroup s, string? error) => error is not null
        ? (error, new ComponentBuilder().Build())
        : (T("static.apply.sent", ("name", s.Name)),
            new ComponentBuilder().WithButton(T("static.apply.addMessage"), $"st:msg:{s.Id}", ButtonStyle.Secondary, new Emoji("✏️")).Build());

    [ComponentInteraction("st:msg:*")]
    public async Task OnMessage(string id)
    {
        if (await GetStaticAsync(id) is not { } s) return;
        var current = s.FindApplicant(Context.User.Id)?.Message;
        await RespondWithModalAsync(new ModalBuilder().WithTitle(T("static.apply.messageTitle")).WithCustomId($"st:msgset:{s.Id}")
            .AddTextInput(T("static.apply.messageLabel"), "message", TextInputStyle.Paragraph, T("static.apply.messagePlaceholder"),
                maxLength: StaticGroup.MaxMessage, required: false, value: current)
            .Build());
    }

    [ModalInteraction("st:msgset:*")]
    public async Task OnMessageSet(string id, StaticMessageModal form)
    {
        if (await GetStaticAsync(id) is not { } s) return;
        await DeferAsync(ephemeral: true);
        var error = await statics.SetMessageAsync(s, Context.User.Id, form.Message);
        await FollowupAsync(error ?? T("static.apply.messageSaved"), ephemeral: true);
    }

    [ComponentInteraction("st:leave:*")]
    public async Task OnLeave(string id)
    {
        if (await GetStaticAsync(id) is not { } s) return;
        await DeferAsync(ephemeral: true);
        var (error, done) = await statics.LeaveAsync(s, Context.User.Id);
        await FollowupAsync(error ?? done ?? T("static.manage.saved"), ephemeral: true);
    }

    // ================= Управление (организатор) =================

    [ComponentInteraction("st:manage:*")]
    public async Task OnManage(string id)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        var (content, comps) = ManageScreen(s, null);
        await RespondAsync(content, components: comps, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private (string Content, MessageComponent Components) ManageScreen(StaticGroup s, string? note)
    {
        var text = new StringBuilder();
        if (note is not null) text.Append(note).Append("\n\n");
        text.Append(T("static.manage.title", ("name", s.Name), ("count", s.Members.Count), ("size", s.Size))).Append('\n');
        if (s.Applicants.Count == 0) text.Append(T("static.manage.noApplicants"));
        else
        {
            text.Append(T("static.manage.applicants", ("count", s.Applicants.Count)));
            foreach (var a in s.Applicants)
            {
                var line = $"\n• <@{a.UserId}>{statics.CharacterLine(s, a.UserId, a.CharacterName, a.ClassIcon, a.Gs, a.Cp)}" +
                           (a.Message is null ? "" : $"\n  > {a.Message.Replace("\n", " ")}");
                if (text.Length + line.Length > 1900) { text.Append("\n…"); break; }
                text.Append(line);
            }
        }

        var builder = new ComponentBuilder();
        var row = 0;
        if (s.Applicants.Count > 0)
        {
            builder.WithSelectMenu(PeopleMenu(s, $"st:mok:{s.Id}", T("static.manage.approvePlaceholder"),
                s.Applicants.Select(a => (a.UserId, a.CharacterName, a.Gs, a.Cp))), row: row++);
            builder.WithSelectMenu(PeopleMenu(s, $"st:mno:{s.Id}", T("static.manage.declinePlaceholder"),
                s.Applicants.Select(a => (a.UserId, a.CharacterName, a.Gs, a.Cp))), row: row++);
        }
        var others = s.Members.Where(m => m.UserId != s.OwnerId).ToList();
        if (others.Count > 0)
            builder.WithSelectMenu(PeopleMenu(s, $"st:mkick:{s.Id}", T("static.manage.removePlaceholder"),
                others.Select(m => (m.UserId, m.CharacterName, m.Gs, m.Cp))), row: row++);
        // Все персонажи человека в проекте (заявки, затем состав)
        var people = s.Applicants.Select(a => (a.UserId, a.CharacterName, a.Gs, a.Cp))
            .Concat(s.Members.Select(m => (m.UserId, m.CharacterName, m.Gs, m.Cp)))
            .DistinctBy(p => p.UserId).ToList();
        builder.WithSelectMenu(PeopleMenu(s, $"st:mchars:{s.Id}", T("static.manage.charsPlaceholder"), people), row: row++);
        builder.WithButton(T(s.Recruiting ? "static.manage.close" : "static.manage.open"), $"st:mrec:{s.Id}", ButtonStyle.Secondary,
                new Emoji(s.Recruiting ? "🔒" : "📝"), row: row)
            .WithButton(T("static.manage.edit"), $"st:medit:{s.Id}", ButtonStyle.Secondary, new Emoji("✏️"), row: row);
        builder.WithButton(T("static.manage.delete"), $"st:mdel:{s.Id}", ButtonStyle.Danger, new Emoji("🗑️"), row: row);
        return (text.ToString(), builder.Build());
    }

    private SelectMenuBuilder PeopleMenu(StaticGroup s, string customId, string placeholder, IEnumerable<(ulong UserId, string? Character, int? Gs, int? Cp)> people)
    {
        var menu = new SelectMenuBuilder().WithCustomId(customId).WithPlaceholder(placeholder);
        foreach (var (userId, character, gs, cp) in people.Take(25))
        {
            var name = Context.Guild.GetUser(userId)?.DisplayName ?? userId.ToString();
            var c = statics.Shown(s, userId, character, null, gs, cp);
            var desc = c is null ? null : c.Name + (c.Server is { } srv ? $" [{srv}]" : "") + StaticService.GsCp(c.Gs, c.Cp);
            menu.AddOption(name.Length > 100 ? name[..100] : name, userId.ToString(), desc is { Length: > 100 } ? desc[..100] : desc);
        }
        return menu;
    }

    [ComponentInteraction("st:mok:*")]
    public Task OnApprove(string id, string[] values) => ManageActionAsync(id, values, async (s, user) =>
        await statics.ApproveAsync(s, user, Context.User, "discord") ?? T("static.manage.accepted", ("user", $"<@{user}>")));

    [ComponentInteraction("st:mno:*")]
    public Task OnDecline(string id, string[] values) => ManageActionAsync(id, values, async (s, user) =>
        await statics.DeclineAsync(s, user, Context.User, "discord") ?? T("static.manage.declined", ("user", $"<@{user}>")));

    [ComponentInteraction("st:mkick:*")]
    public Task OnRemove(string id, string[] values) => ManageActionAsync(id, values, async (s, user) =>
        await statics.RemoveMemberAsync(s, user, Context.User, "discord") ?? T("static.manage.removed", ("user", $"<@{user}>")));

    /// <summary>Все персонажи человека в проекте статика — отдельным сообщением (меню управления остаётся).</summary>
    [ComponentInteraction("st:mchars:*")]
    public async Task OnCharacters(string id, string[] values)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        if (!ulong.TryParse(values.FirstOrDefault(), out var user)) return;
        var list = statics.CharactersOf(s.GuildId, user, s.ProjectKey);
        var project = statics.ProjectName(s) ?? "—";
        if (list.Count == 0)
        {
            await RespondAsync(T("static.manage.noChars", ("user", $"<@{user}>"), ("project", project)), ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        var text = new StringBuilder(T("static.manage.chars", ("user", $"<@{user}>"), ("project", project), ("count", list.Count)));
        foreach (var c in list)
        {
            var line = "\n•" + StaticService.CharacterText(c.Name, c.Icon, c.Gs, c.Cp, c.Server).TrimStart(' ', '—');
            if (text.Length + line.Length > 1900) { text.Append("\n…"); break; }
            text.Append(line);
        }
        await RespondAsync(text.ToString(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private async Task ManageActionAsync(string id, string[] values, Func<StaticGroup, ulong, Task<string>> action)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        if (!ulong.TryParse(values.FirstOrDefault(), out var user)) return;
        await DeferAsync();
        var note = await action(s, user);
        var (content, comps) = ManageScreen(s, note);
        await ModifyOriginalResponseAsync(m => { m.Content = content; m.Components = comps; m.AllowedMentions = AllowedMentions.None; });
    }

    [ComponentInteraction("st:mrec:*")]
    public async Task OnRecruiting(string id)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        await DeferAsync();
        var error = await statics.SetRecruitingAsync(s, !s.Recruiting, Context.User);
        var (content, comps) = ManageScreen(s, error);
        await ModifyOriginalResponseAsync(m => { m.Content = content; m.Components = comps; m.AllowedMentions = AllowedMentions.None; });
    }

    [ComponentInteraction("st:medit:*")]
    public async Task OnEdit(string id)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        await RespondWithModalAsync(FormModal($"st:meditset:{s.Id}", T("static.create.editTitle"), s.Name, s.Description, s.Size.ToString()));
    }

    [ModalInteraction("st:meditset:*")]
    public async Task OnEditSet(string id, StaticFormModal form)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        await DeferAsync(ephemeral: true);
        var size = int.TryParse((form.Size ?? "").Trim(), out var n) ? n : s.Size;
        var error = await statics.EditAsync(s, form.Name, form.Description, size, Context.User);
        await FollowupAsync(error ?? T("static.manage.saved"), ephemeral: true);
    }

    [ComponentInteraction("st:mdel:*")]
    public async Task OnDelete(string id)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        var comps = new ComponentBuilder()
            .WithButton(T("static.manage.deleteYes"), $"st:mdelok:{s.Id}", ButtonStyle.Danger, new Emoji("🗑️"))
            .WithButton(T("static.manage.deleteNo"), $"st:mdelno:{s.Id}", ButtonStyle.Secondary)
            .Build();
        await Component.UpdateAsync(m => { m.Content = T("static.manage.deleteConfirm", ("name", s.Name)); m.Components = comps; });
    }

    [ComponentInteraction("st:mdelok:*")]
    public async Task OnDeleteConfirm(string id)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        await DeferAsync();
        await statics.DeleteAsync(s, Context.User, "discord");
        await ModifyOriginalResponseAsync(m => { m.Content = T("static.manage.deleted", ("name", s.Name)); m.Components = new ComponentBuilder().Build(); });
    }

    [ComponentInteraction("st:mdelno:*")]
    public async Task OnDeleteCancel(string id)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        var (content, comps) = ManageScreen(s, null);
        await Component.UpdateAsync(m => { m.Content = content; m.Components = comps; m.AllowedMentions = AllowedMentions.None; });
    }

    // ================= Пинг состава =================

    [ComponentInteraction("st:ping:*")]
    public async Task OnPing(string id)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        await RespondWithModalAsync(new ModalBuilder().WithTitle(T("static.ping.title")).WithCustomId($"st:pingset:{s.Id}")
            .AddTextInput(T("static.ping.label"), "message", TextInputStyle.Paragraph, T("static.ping.placeholder"), maxLength: 1000, required: false)
            .Build());
    }

    [ModalInteraction("st:pingset:*")]
    public async Task OnPingSet(string id, StaticMessageModal form)
    {
        if (await GetManagedAsync(id) is not { } s) return;
        await DeferAsync(ephemeral: true);
        var (error, count) = await statics.PingAsync(s, Context.User, form.Message, "discord");
        await FollowupAsync(error ?? T("static.ping.sent", ("count", count)), ephemeral: true);
    }

    // ================= Помощники =================

    /// <summary>Подпись персонажа в меню: «GS 3053 · CP 1.2M».</summary>
    private static string? PickDesc(StaticService.LiveCharacter c) =>
        ((c.Server is { } srv ? $"[{srv}]" : "") + StaticService.GsCp(c.Gs, c.Cp)).TrimStart(' ', '·') is { Length: > 0 } t ? t[..Math.Min(t.Length, 100)] : null;


    private string T(string key, params (string Name, object? Value)[] args) => Tx.Get(Context.Guild?.Id ?? 0, key, args);

    private ulong ChannelId => Context.Interaction.ChannelId ?? throw new InvalidOperationException("Interaction has no channel.");

    private async Task<StaticDraft?> GetDraftAsync(string draftId)
    {
        if (store.Drafts.TryGetValue(draftId, out var d) && d.OwnerId == Context.User.Id) return d;
        await RespondAsync(T("static.expired"), ephemeral: true);
        return null;
    }

    private async Task<StaticGroup?> GetStaticAsync(string id)
    {
        if (store.Get(Context.Guild.Id, id) is { } s) return s;
        await RespondAsync(T("static.gone"), ephemeral: true);
        return null;
    }

    /// <summary>Статик, которым может управлять нажавший (организатор, владелец бота или админ бота на сервере).</summary>
    private async Task<StaticGroup?> GetManagedAsync(string id)
    {
        if (await GetStaticAsync(id) is not { } s) return null;
        var isAdmin = Context.User is IGuildUser gu && admins.IsAdmin(gu);
        if (StaticService.CanManage(s, Context.User.Id, isAdmin)) return s;
        await RespondAsync(T("static.manage.only"), ephemeral: true);
        return null;
    }
}

/// <summary>Решение организатора прямо из ЛС-уведомления о заявке (без RequireContext: кнопки в личке).</summary>
public sealed class StaticDmModule(StaticStore store, StaticService statics) : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("stdm:ok:*:*")]
    public Task OnAccept(string id, string userId) => DecideAsync(id, userId, true);

    [ComponentInteraction("stdm:no:*:*")]
    public Task OnDecline(string id, string userId) => DecideAsync(id, userId, false);

    private async Task DecideAsync(string id, string userId, bool accept)
    {
        if (!store.Statics.TryGetValue(id, out var s))
        {
            await RespondAsync(Tx.Get(0, "static.gone"), ephemeral: true);
            return;
        }
        if (!StaticService.CanManage(s, Context.User.Id) || !ulong.TryParse(userId, out var user))
        {
            await RespondAsync(Tx.Get(s.GuildId, "static.manage.only"), ephemeral: true);
            return;
        }
        await DeferAsync();
        var error = accept
            ? await statics.ApproveAsync(s, user, Context.User, "discord")
            : await statics.DeclineAsync(s, user, Context.User, "discord");
        var result = error ?? Tx.Get(s.GuildId, accept ? "static.manage.accepted" : "static.manage.declined", ("user", $"<@{user}>"));
        var original = ((SocketMessageComponent)Context.Interaction).Message.Content;
        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = $"{original}\n\n{result}";
            // Статик заполнен, а заявка ещё ждёт — кнопки остаются, можно решить позже
            if (s.FindApplicant(user) is null) m.Components = new ComponentBuilder().Build();
            m.AllowedMentions = AllowedMentions.None;
        });
    }
}

/// <summary>Окно статика: название, описание, размер (поля строятся вручную, значения читаются по id).</summary>
public sealed class StaticFormModal : IModal
{
    public string Title => "Static";

    [ModalTextInput("name")]
    public string? Name { get; set; }

    [ModalTextInput("desc")]
    public string? Description { get; set; }

    [ModalTextInput("size")]
    public string? Size { get; set; }
}

/// <summary>Окно статика без размера (размер выбран кнопкой).</summary>
public sealed class StaticNameModal : IModal
{
    public string Title => "Static";

    [ModalTextInput("name")]
    public string? Name { get; set; }

    [ModalTextInput("desc")]
    public string? Description { get; set; }
}

/// <summary>Окно с одним текстом (сообщение к заявке, пинг).</summary>
public sealed class StaticMessageModal : IModal
{
    public string Title => "Message";

    [ModalTextInput("message")]
    public string? Message { get; set; }
}
