using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>
/// /admin — эфемерная админ-панель с вкладками LFG / Tickets / Temp Voice / Bot admins.
/// Всё, что раньше делалось админскими слэш-командами, плюс просмотр и принудительное закрытие
/// сборов, тикетов и временных комнат. Доступ: Administrator, Manage Server или роль-админ бота.
/// </summary>
[RequireContext(ContextType.Guild)]
[RequireBotAdmin]
public sealed class AdminModule(
    AdminStore admins,
    ActivityStore activities,
    LfgStore lfg,
    LfgSignupService signup,
    TimeService time,
    TicketStore tickets,
    TicketMessageService ticketMessages,
    TempVoiceStore voice,
    TempRoomManager rooms,
    PanelService panels,
    AuditLog audit) : InteractionModuleBase<SocketInteractionContext>
{
    private const int MaxOptions = 25; // лимит пунктов в выпадающем списке
    private const string DefaultCreatorName = "➕ Create room";

    private const string NoAccessText =
        "⚠️ I can't access this channel. Give the bot **View Channel**, **Send Messages** and **Embed Links** here.";

    private ulong GuildId => Context.Guild.Id;

    // ================= Вход и вкладки =================

    [SlashCommand("admin", "Open the bot admin panel (bot admins only)")]
    public async Task AdminCommand()
    {
        var s = Home(null);
        await RespondAsync(s.Content, embed: s.Embed, components: s.Components, ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    [ComponentInteraction("adm:tab:*")]
    public Task OnTab(string tab) => ShowAsync(tab switch
    {
        "lfg" => LfgTab(null),
        "tickets" => TicketsTab(null),
        "voice" => VoiceTab(null),
        "admins" => AdminsTab(null),
        _ => Home(null)
    });

    private Screen Home(string? notice)
    {
        var embed = new EmbedBuilder()
            .WithTitle("🛠️ Admin panel")
            .WithColor(Color.DarkGrey)
            .WithDescription("Choose a module. This panel is visible only to you.")
            .AddField("📣 LFG", $"{activities.Get(GuildId).Count} activities\n{Groups().Count} active groups", true)
            .AddField("🎫 Tickets", $"{tickets.GetProjects(GuildId).Count} projects\n{tickets.GetTickets(GuildId).Count} open tickets", true)
            .AddField("🔊 Temp Voice", $"{voice.GetCreators(GuildId).Count} 'join to create'\n{Rooms().Count} active rooms", true)
            .Build();

        var comps = new ComponentBuilder()
            .WithButton("LFG", "adm:tab:lfg", ButtonStyle.Primary, new Emoji("📣"))
            .WithButton("Tickets", "adm:tab:tickets", ButtonStyle.Primary, new Emoji("🎫"))
            .WithButton("Temp Voice", "adm:tab:voice", ButtonStyle.Primary, new Emoji("🔊"))
            .WithButton("Bot admins", "adm:tab:admins", ButtonStyle.Secondary, new Emoji("🛡️"))
            .Build();

        return new Screen(notice, embed, comps);
    }

    // ================= LFG =================

    private Screen LfgTab(string? notice)
    {
        var list = activities.Get(GuildId);
        var sb = new StringBuilder("**Activities** — members with the role are pinged and get a DM\n");
        if (list.Count == 0) sb.AppendLine("*none yet*");
        foreach (var a in list) sb.AppendLine($"• **{a.Name}** → <@&{a.RoleId}>");
        sb.AppendLine();
        sb.AppendLine($"**Active groups:** {Groups().Count}");
        sb.AppendLine($"-# Posts are deleted {time.AutoDeleteHours} h after creation or after the start time.");

        var embed = new EmbedBuilder().WithTitle("📣 LFG").WithColor(Color.Blue).WithDescription(Clip(sb.ToString())).Build();
        var comps = new ComponentBuilder()
            .WithButton("Post LFG panel here", "adm:lfg:panel", ButtonStyle.Success, new Emoji("📌"), row: 0)
            .WithButton("Add activity", "adm:act:add", ButtonStyle.Primary, new Emoji("➕"),
                disabled: list.Count >= ActivityStore.MaxPerGuild, row: 0)
            .WithButton("Remove activity", "adm:act:rm", ButtonStyle.Secondary, new Emoji("➖"), disabled: list.Count == 0, row: 0)
            .WithButton("Active groups", "adm:lfg:groups", ButtonStyle.Secondary, new Emoji("📋"), row: 0)
            .WithButton("Back", "adm:tab:home", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();

        return new Screen(notice, embed, comps);
    }

    [ComponentInteraction("adm:lfg:panel")]
    public async Task OnLfgPanel()
    {
        if (await GetChannelAsync() is not { } channel)
        {
            await ShowAsync(LfgTab(NoAccessText));
            return;
        }

        await panels.PostAsync(channel, GuildId, "lfg");
        await ShowAsync(LfgTab("✅ LFG panel posted in this channel."));
    }

    [ComponentInteraction("adm:act:add")]
    public Task OnActivityAdd()
    {
        if (activities.Get(GuildId).Count >= ActivityStore.MaxPerGuild)
            return ShowAsync(LfgTab($"⚠️ Limit of {ActivityStore.MaxPerGuild} activities reached."));

        var embed = new EmbedBuilder()
            .WithTitle("➕ Add activity")
            .WithColor(Color.Blue)
            .WithDescription("1. Pick the role to ping when a group for this activity is posted.\n2. Enter the activity name.")
            .Build();
        var comps = new ComponentBuilder()
            .WithSelectMenu(RoleSelect("adm:act:addrole", "Role to ping", 1, 1), row: 0)
            .WithButton("Back", "adm:tab:lfg", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();
        return ShowAsync(new Screen(null, embed, comps));
    }

    [ComponentInteraction("adm:act:addrole")]
    public Task OnActivityRole(string[] values) =>
        RespondWithModalAsync<AdminActivityModal>($"adm:act:addname:{values[0]}");

    [ModalInteraction("adm:act:addname:*")]
    public Task OnActivityName(string roleId, AdminActivityModal modal)
    {
        var name = (modal.Name ?? "").Trim();
        if (name.Length == 0) return ShowAsync(LfgTab("⚠️ The name can't be empty."));
        if (!ulong.TryParse(roleId, out var role)) return ShowAsync(LfgTab("⚠️ Unknown role."));

        var error = activities.Add(GuildId, name, role);
        return ShowAsync(LfgTab(error is null ? $"✅ Added **{name}** → <@&{role}>" : $"⚠️ {error}"));
    }

    [ComponentInteraction("adm:act:rm")]
    public Task OnActivityRemove()
    {
        var list = activities.Get(GuildId);
        if (list.Count == 0) return ShowAsync(LfgTab("⚠️ There are no activities to remove."));

        var menu = new SelectMenuBuilder().WithCustomId("adm:act:rmsel").WithPlaceholder("Choose an activity to remove");
        foreach (var a in list.Take(MaxOptions))
            menu.AddOption(Label(a.Name), a.Key, Label($"Pings @{RoleName(a.RoleId)}"));

        var embed = new EmbedBuilder().WithTitle("➖ Remove activity").WithColor(Color.Blue)
            .WithDescription("Groups that are already posted are not affected.").Build();
        var comps = new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("Back", "adm:tab:lfg", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();
        return ShowAsync(new Screen(null, embed, comps));
    }

    [ComponentInteraction("adm:act:rmsel")]
    public Task OnActivityRemoveSelected(string[] values)
    {
        var removed = activities.Remove(GuildId, values[0]);
        return ShowAsync(LfgTab(removed is null ? "⚠️ Activity not found." : $"✅ Removed **{removed.Name}**."));
    }

    // ---------- Активные сборы ----------

    private List<LfgGroup> Groups() => lfg.Groups.Values
        .Where(g => g.GuildId == GuildId && !g.IsClosed)
        .OrderBy(g => g.StartsAt ?? g.ExpiresAt)
        .ToList();

    [ComponentInteraction("adm:lfg:groups")]
    public Task OnGroups() => ShowAsync(GroupsScreen(null));

    private Screen GroupsScreen(string? notice)
    {
        var groups = Groups();
        var sb = new StringBuilder();
        if (groups.Count == 0) sb.AppendLine("*No active groups.*");
        foreach (var g in groups.Take(MaxOptions))
        {
            var reserve = g.Reserve.Count > 0 ? $" (+{g.Reserve.Count} reserve)" : "";
            sb.AppendLine($"• **{g.ActivityName}** — <@{g.OwnerId}> — {g.FilledCount}/{g.Slots.Count}{reserve} — " +
                          $"{When(g.StartsAt)} — [post]({LfgSignupService.JumpUrl(g)})");
        }
        if (groups.Count > MaxOptions) sb.AppendLine($"-# …and {groups.Count - MaxOptions} more");

        var embed = new EmbedBuilder().WithTitle("📋 Active groups").WithColor(Color.Blue)
            .WithDescription(Clip(sb.ToString())).Build();

        var comps = new ComponentBuilder();
        if (groups.Count > 0)
        {
            var menu = new SelectMenuBuilder().WithCustomId("adm:lfg:pick").WithPlaceholder("Choose a group to manage");
            foreach (var g in groups.Take(MaxOptions))
                menu.AddOption(Label($"{g.ActivityName} · {g.FilledCount}/{g.Slots.Count}"), g.Id,
                    Label($"by {MemberName(g.OwnerId)} · {(g.StartsAt is { } st ? $"{TimeZoneInfo.ConvertTime(st, time.Zone):dd.MM HH:mm}" : "no time")}"));
            comps.WithSelectMenu(menu, row: 0);
        }
        comps.WithButton("Refresh", "adm:lfg:groups", ButtonStyle.Secondary, new Emoji("🔄"), row: 1)
             .WithButton("Back", "adm:tab:lfg", ButtonStyle.Secondary, new Emoji("◀️"), row: 1);

        return new Screen(notice, embed, comps.Build());
    }

    [ComponentInteraction("adm:lfg:pick")]
    public Task OnGroupPick(string[] values) => ShowAsync(GroupScreen(values[0]));

    private Screen GroupScreen(string groupId)
    {
        if (!lfg.Groups.TryGetValue(groupId, out var g) || g.GuildId != GuildId)
            return GroupsScreen("⚠️ This group is no longer active.");

        var sb = new StringBuilder();
        sb.AppendLine($"Organizer: <@{g.OwnerId}>");
        sb.AppendLine($"Start: {When(g.StartsAt)}");
        sb.AppendLine($"Auto-delete: <t:{g.ExpiresAt.ToUnixTimeSeconds()}:R>");
        sb.AppendLine($"Post: [jump to the post]({LfgSignupService.JumpUrl(g)})");
        sb.AppendLine();
        sb.AppendLine($"**Members {g.FilledCount}/{g.Slots.Count}**");
        for (var i = 0; i < g.Slots.Count; i++)
        {
            var s = g.Slots[i];
            sb.AppendLine(s.UserId is { } u
                ? $"{i + 1}. {(s.TakenAs ?? s.Role).Emoji()} <@{u}> — {Format.Sanitize(s.CharacterName ?? "?")}"
                : $"{i + 1}. {s.Role.Emoji()} *free*");
        }
        if (g.Reserve.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Reserve {g.Reserve.Count}**");
            foreach (var r in g.Reserve)
                sb.AppendLine($"• {r.Role.Emoji()} <@{r.UserId}> — {Format.Sanitize(r.CharacterName ?? "?")}");
        }

        var embed = new EmbedBuilder().WithTitle($"📣 {g.ActivityName}").WithColor(Color.Blue)
            .WithDescription(Clip(sb.ToString())).Build();
        var comps = new ComponentBuilder()
            .WithButton("Force close & delete post", $"adm:lfg:close:{g.Id}", ButtonStyle.Danger, new Emoji("🗑️"))
            .WithButton("Back", "adm:lfg:groups", ButtonStyle.Secondary, new Emoji("◀️"))
            .Build();
        return new Screen(null, embed, comps);
    }

    [ComponentInteraction("adm:lfg:close:*")]
    public async Task OnGroupClose(string groupId)
    {
        if (!lfg.Groups.TryGetValue(groupId, out var group) || group.GuildId != GuildId)
        {
            await ShowAsync(GroupsScreen("⚠️ This group is no longer active."));
            return;
        }

        await DeferAsync();
        await group.Gate.WaitAsync();
        try
        {
            if (lfg.Groups.TryRemove(groupId, out _))
            {
                group.IsClosed = true;
                lfg.SaveGroups();
                await signup.DeleteMessagesAsync(group); // пост и напоминание
            }
        }
        finally
        {
            group.Gate.Release();
        }

        audit.FromDiscord(GuildId, Context.User, "lfg", "Force-closed LFG group (/admin)", group.ActivityName);
        await ModifyAsync(GroupsScreen($"✅ **{group.ActivityName}** by <@{group.OwnerId}> closed, the post was deleted."));
    }

    // ================= Tickets =================

    private Screen TicketsTab(string? notice)
    {
        var projects = tickets.GetProjects(GuildId);
        var open = tickets.GetTickets(GuildId);

        var sb = new StringBuilder();
        if (projects.Count == 0) sb.AppendLine("*No projects yet. Click **Add project**.*");
        foreach (var p in projects)
        {
            sb.AppendLine($"**{p.Name}** — {open.Count(t => t.ProjectKey == p.Key)} open");
            sb.AppendLine($"• Leadership: {Roles(p.StaffRoleIds)}");
            sb.AppendLine($"• Roles on approve: {Roles(p.MemberRoleIds)}");
            sb.AppendLine($"• Category: {Category(p.CategoryId)}");
        }

        var embed = new EmbedBuilder().WithTitle("🎫 Tickets").WithColor(Color.Orange)
            .WithDescription(Clip(sb.ToString())).Build();

        var comps = new ComponentBuilder()
            .WithButton("Post ticket panel here", "adm:tk:panel", ButtonStyle.Success, new Emoji("📌"), row: 0)
            .WithButton("Add project", "adm:tp:add", ButtonStyle.Primary, new Emoji("➕"),
                disabled: projects.Count >= TicketStore.MaxProjects, row: 0)
            .WithButton($"Open tickets ({open.Count})", "adm:tk:list", ButtonStyle.Secondary, new Emoji("📋"), row: 0);

        if (projects.Count > 0)
        {
            var menu = new SelectMenuBuilder().WithCustomId("adm:tp:pick").WithPlaceholder("Manage a project…");
            foreach (var p in projects.Take(MaxOptions))
                menu.AddOption(Label(p.Name), p.Key, Label($"{open.Count(t => t.ProjectKey == p.Key)} open tickets"));
            comps.WithSelectMenu(menu, row: 1);
        }
        comps.WithButton("Back", "adm:tab:home", ButtonStyle.Secondary, new Emoji("◀️"), row: 2);

        return new Screen(notice, embed, comps.Build());
    }

    [ComponentInteraction("adm:tk:panel")]
    public async Task OnTicketPanel()
    {
        if (await GetChannelAsync() is not { } channel)
        {
            await ShowAsync(TicketsTab(NoAccessText));
            return;
        }

        await panels.PostAsync(channel, GuildId, "tickets");
        await ShowAsync(TicketsTab("✅ Ticket panel posted in this channel."));
    }

    [ComponentInteraction("adm:tp:add")]
    public Task OnProjectAdd()
    {
        if (tickets.GetProjects(GuildId).Count >= TicketStore.MaxProjects)
            return ShowAsync(TicketsTab($"⚠️ Limit of {TicketStore.MaxProjects} projects reached."));

        var embed = new EmbedBuilder()
            .WithTitle("➕ Add project")
            .WithColor(Color.Orange)
            .WithDescription("1. Pick the role given to the applicant on **Approve**.\n2. Enter the project name (e.g. WWM, Aion 2).\n" +
                             "Then link leadership roles and choose the category on the project screen.")
            .Build();
        var comps = new ComponentBuilder()
            .WithSelectMenu(RoleSelect("adm:tp:addrole", "Role given on Approve", 1, 1), row: 0)
            .WithButton("Back", "adm:tab:tickets", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();
        return ShowAsync(new Screen(null, embed, comps));
    }

    [ComponentInteraction("adm:tp:addrole")]
    public Task OnProjectRole(string[] values) =>
        RespondWithModalAsync<AdminProjectModal>($"adm:tp:addname:{values[0]}");

    [ModalInteraction("adm:tp:addname:*")]
    public Task OnProjectName(string roleId, AdminProjectModal modal)
    {
        var name = (modal.Name ?? "").Trim();
        if (name.Length == 0) return ShowAsync(TicketsTab("⚠️ The name can't be empty."));
        if (!ulong.TryParse(roleId, out var role)) return ShowAsync(TicketsTab("⚠️ Unknown role."));

        var error = tickets.AddProject(GuildId, name, role);
        if (error is not null) return ShowAsync(TicketsTab($"⚠️ {error}"));

        var created = tickets.GetProjects(GuildId).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return ShowAsync(created is null
            ? TicketsTab($"✅ Project **{name}** added.")
            : ProjectScreen(created.Key, $"✅ Project **{name}** added. Now link the leadership roles below."));
    }

    [ComponentInteraction("adm:tp:pick")]
    public Task OnProjectPick(string[] values) => ShowAsync(ProjectScreen(values[0], null));

    [ComponentInteraction("adm:tp:view:*")]
    public Task OnProjectView(string key) => ShowAsync(ProjectScreen(key, null));

    private Screen ProjectScreen(string key, string? notice)
    {
        var p = tickets.FindProject(GuildId, key);
        if (p is null) return TicketsTab("⚠️ This project no longer exists.");

        var open = tickets.GetTickets(GuildId).Count(t => t.ProjectKey == p.Key);
        var embed = new EmbedBuilder()
            .WithTitle($"🎫 {p.Name}")
            .WithColor(Color.Orange)
            .WithDescription(
                $"**Leadership** (see tickets, can Approve): {Roles(p.StaffRoleIds)}\n" +
                $"**Roles on approve:** {Roles(p.MemberRoleIds)}\n" +
                $"**Category:** {Category(p.CategoryId)}\n" +
                $"**Open tickets:** {open}\n\n" +
                "**Ticket text** — shown in every ticket under the system info, above the buttons:\n" +
                (string.IsNullOrWhiteSpace(p.CustomText) ? "*not set*" : Quote(p.CustomText)) + "\n\n" +
                "-# Change the lists with the menus below — the selection replaces the current list.")
            .Build();

        var category = new SelectMenuBuilder()
            .WithCustomId($"adm:tp:cat:{p.Key}")
            .WithType(ComponentType.ChannelSelect)
            .WithChannelTypes(ChannelType.Category)
            .WithPlaceholder($"Ticket category (empty = \"{TicketRenderer.DefaultCategoryName}\")")
            .WithMinValues(0)
            .WithMaxValues(1);
        if (p.CategoryId is { } c && Context.Guild.GetCategoryChannel(c) is not null)
            category.WithDefaultValues(new SelectMenuDefaultValue(c, SelectDefaultValueType.Channel));

        var comps = new ComponentBuilder()
            .WithSelectMenu(RoleSelect($"adm:tp:staff:{p.Key}", "Leadership roles", 0, 10, p.StaffRoleIds), row: 0)
            .WithSelectMenu(RoleSelect($"adm:tp:member:{p.Key}", "Roles given on Approve", 0, 10, p.MemberRoleIds), row: 1)
            .WithSelectMenu(category, row: 2)
            .WithButton("Ticket text", $"adm:tp:text:{p.Key}", ButtonStyle.Primary, new Emoji("📝"), row: 3)
            .WithButton("Remove project", $"adm:tp:rm:{p.Key}", ButtonStyle.Danger, new Emoji("🗑️"), row: 3)
            .WithButton("Back", "adm:tab:tickets", ButtonStyle.Secondary, new Emoji("◀️"), row: 3)
            .Build();

        return new Screen(notice, embed, comps);
    }

    [ComponentInteraction("adm:tp:staff:*")]
    public Task OnProjectStaff(string key, string[] values)
    {
        var ids = ParseIds(values);
        var p = tickets.UpdateProject(GuildId, key, x => x.StaffRoleIds = ids);
        return ShowAsync(p is null
            ? TicketsTab("⚠️ This project no longer exists.")
            : ProjectScreen(key, $"✅ Leadership of **{p.Name}**: {Roles(ids)}"));
    }

    [ComponentInteraction("adm:tp:member:*")]
    public Task OnProjectMember(string key, string[] values)
    {
        var ids = ParseIds(values);
        var p = tickets.UpdateProject(GuildId, key, x => x.MemberRoleIds = ids);
        if (p is null) return ShowAsync(TicketsTab("⚠️ This project no longer exists."));
        return ShowAsync(ProjectScreen(key, ids.Count == 0
            ? $"⚠️ **{p.Name}** has no roles to give — Approve won't work until you add one."
            : $"✅ Roles on **{p.Name}** approve: {Roles(ids)}"));
    }

    [ComponentInteraction("adm:tp:cat:*")]
    public Task OnProjectCategory(string key, string[] values)
    {
        ulong? categoryId = ParseIds(values).Cast<ulong?>().FirstOrDefault();
        var p = tickets.UpdateProject(GuildId, key, x => x.CategoryId = categoryId);
        return ShowAsync(p is null
            ? TicketsTab("⚠️ This project no longer exists.")
            : ProjectScreen(key, $"✅ **{p.Name}** tickets will be created in {Category(categoryId)}."));
    }

    // ---------- Свой текст в сообщении тикета ----------

    [ComponentInteraction("adm:tp:text:*")]
    public Task OnProjectText(string key)
    {
        var p = tickets.FindProject(GuildId, key);
        if (p is null) return ShowAsync(TicketsTab("⚠️ This project no longer exists."));

        // Окно собираем вручную, чтобы подставить текущий текст
        var modal = new ModalBuilder()
            .WithTitle($"Ticket text — {p.Name}".Length <= 45 ? $"Ticket text — {p.Name}" : "Ticket text")
            .WithCustomId($"adm:tp:textset:{p.Key}")
            .AddTextInput("Text (empty = remove the block)", "text", TextInputStyle.Paragraph,
                placeholder: "Markdown works. {applicant} = applicant mention, {project} = project name",
                maxLength: TicketRenderer.MaxCustomTextLength, required: false, value: p.CustomText)
            .Build();
        return RespondWithModalAsync(modal);
    }

    [ModalInteraction("adm:tp:textset:*")]
    public async Task OnProjectTextSubmitted(string key, AdminTicketTextModal modal)
    {
        var text = string.IsNullOrWhiteSpace(modal.Text) ? null : modal.Text.Trim();
        var p = tickets.UpdateProject(GuildId, key, x => x.CustomText = text);
        if (p is null)
        {
            await ShowAsync(TicketsTab("⚠️ This project no longer exists."));
            return;
        }

        var openCount = tickets.GetTickets(GuildId).Count(t => t.ProjectKey == key);
        var saved = text is null ? $"✅ Ticket text of **{p.Name}** removed." : $"✅ Ticket text of **{p.Name}** saved.";
        await ShowAsync(ProjectScreen(key, openCount > 0 ? $"{saved} Updating {openCount} open tickets…" : saved));
        if (openCount == 0) return;

        var (updated, total) = await ticketMessages.RefreshProjectAsync(GuildId, key);
        await FollowupAsync(updated == total
                ? $"✅ Updated {updated} open {(total == 1 ? "ticket" : "tickets")}."
                : $"⚠️ Updated {updated} of {total} open tickets — the rest have no ticket message (deleted?) or I can't access the channel.",
            ephemeral: true);
    }

    [ComponentInteraction("adm:tp:rm:*")]
    public Task OnProjectRemove(string key)
    {
        var p = tickets.FindProject(GuildId, key);
        if (p is null) return ShowAsync(TicketsTab("⚠️ This project no longer exists."));

        var embed = new EmbedBuilder().WithTitle($"🗑️ Remove {p.Name}?").WithColor(Color.Red)
            .WithDescription("New tickets for this project can't be created. Open tickets stay until they are closed.").Build();
        var comps = new ComponentBuilder()
            .WithButton("Remove project", $"adm:tp:rmok:{p.Key}", ButtonStyle.Danger, new Emoji("🗑️"))
            .WithButton("Cancel", $"adm:tp:view:{p.Key}", ButtonStyle.Secondary)
            .Build();
        return ShowAsync(new Screen(null, embed, comps));
    }

    [ComponentInteraction("adm:tp:rmok:*")]
    public Task OnProjectRemoveConfirmed(string key)
    {
        var removed = tickets.RemoveProject(GuildId, key);
        return ShowAsync(TicketsTab(removed is null ? "⚠️ Project not found." : $"✅ Project **{removed.Name}** removed."));
    }

    // ---------- Открытые тикеты ----------

    [ComponentInteraction("adm:tk:list")]
    public Task OnTickets() => ShowAsync(TicketsListScreen(null));

    private Screen TicketsListScreen(string? notice)
    {
        var open = tickets.GetTickets(GuildId).OrderBy(t => t.CreatedAt).ToList();
        var sb = new StringBuilder();
        if (open.Count == 0) sb.AppendLine("*No open tickets.*");
        foreach (var t in open.Take(MaxOptions))
            sb.AppendLine($"• **{ProjectName(t.ProjectKey)}** — <@{t.UserId}> — <#{t.TextChannelId}> — " +
                          $"{(t.ApprovedBy is not null ? "✅ approved" : "⏳ pending")} — <t:{t.CreatedAt.ToUnixTimeSeconds()}:R>");
        if (open.Count > MaxOptions) sb.AppendLine($"-# …and {open.Count - MaxOptions} more");

        var embed = new EmbedBuilder().WithTitle("📋 Open tickets").WithColor(Color.Orange)
            .WithDescription(Clip(sb.ToString())).Build();

        var comps = new ComponentBuilder();
        if (open.Count > 0)
        {
            var menu = new SelectMenuBuilder().WithCustomId("adm:tk:pick").WithPlaceholder("Choose a ticket to manage");
            foreach (var t in open.Take(MaxOptions))
                menu.AddOption(Label($"{ProjectName(t.ProjectKey)} · {MemberName(t.UserId)}"), t.Id,
                    Label(t.ApprovedBy is not null ? "Approved" : "Pending"));
            comps.WithSelectMenu(menu, row: 0);
        }
        comps.WithButton("Refresh", "adm:tk:list", ButtonStyle.Secondary, new Emoji("🔄"), row: 1)
             .WithButton("Back", "adm:tab:tickets", ButtonStyle.Secondary, new Emoji("◀️"), row: 1);

        return new Screen(notice, embed, comps.Build());
    }

    [ComponentInteraction("adm:tk:pick")]
    public Task OnTicketPick(string[] values)
    {
        var t = tickets.FindTicket(values[0]);
        if (t is null || t.GuildId != GuildId) return ShowAsync(TicketsListScreen("⚠️ This ticket is already closed."));

        var embed = new EmbedBuilder()
            .WithTitle($"🎫 {ProjectName(t.ProjectKey)} — {MemberName(t.UserId)}")
            .WithColor(Color.Orange)
            .WithDescription(
                $"Applicant: <@{t.UserId}>\n" +
                $"Text: <#{t.TextChannelId}>\nVoice: <#{t.VoiceChannelId}>\n" +
                $"Opened: <t:{t.CreatedAt.ToUnixTimeSeconds()}:R>\n" +
                (t.ApprovedBy is { } by ? $"✅ Approved by <@{by}>" : "⏳ Waiting for approval"))
            .Build();
        var comps = new ComponentBuilder()
            .WithButton("Force close (delete channels)", $"adm:tk:close:{t.Id}", ButtonStyle.Danger, new Emoji("🗑️"))
            .WithButton("Back", "adm:tk:list", ButtonStyle.Secondary, new Emoji("◀️"))
            .Build();
        return ShowAsync(new Screen(null, embed, comps));
    }

    [ComponentInteraction("adm:tk:close:*")]
    public async Task OnTicketClose(string ticketId)
    {
        var t = tickets.FindTicket(ticketId);
        if (t is null || t.GuildId != GuildId)
        {
            await ShowAsync(TicketsListScreen("⚠️ This ticket is already closed."));
            return;
        }

        tickets.RemoveTicket(ticketId);
        audit.FromDiscord(GuildId, Context.User, "tickets", "Force-closed ticket (/admin)", $"{ProjectName(t.ProjectKey)} · {MemberName(t.UserId)}");
        // Сначала обновляем панель: она может находиться в самом канале тикета
        await ShowAsync(TicketsListScreen($"✅ Ticket of <@{t.UserId}> ({ProjectName(t.ProjectKey)}) closed."));
        await DeleteGuildChannelAsync(t.VoiceChannelId);
        await DeleteGuildChannelAsync(t.TextChannelId);
    }

    // ================= Temp Voice =================

    private List<TempVoiceRoom> Rooms() => voice.GetRooms(GuildId)
        .Where(r => Context.Guild.GetVoiceChannel(r.ChannelId) is not null)
        .ToList();

    private Screen VoiceTab(string? notice)
    {
        var creators = voice.GetCreators(GuildId);
        var sb = new StringBuilder("**'Join to create' channels**\n");
        if (creators.Count == 0) sb.AppendLine("*none yet*");
        foreach (var c in creators)
        {
            var template = string.IsNullOrWhiteSpace(c.NameTemplate) ? "auto name" : $"`{c.NameTemplate}`";
            sb.AppendLine($"• <#{c.ChannelId}> → {template}, limit: {(c.UserLimit is { } l ? l.ToString() : "none")}");
        }
        sb.AppendLine();
        sb.AppendLine($"**Active rooms:** {Rooms().Count}");
        sb.AppendLine("-# Rooms copy the permissions of their 'join to create' channel.");

        var embed = new EmbedBuilder().WithTitle("🔊 Temp Voice").WithColor(Color.Teal)
            .WithDescription(Clip(sb.ToString())).Build();
        var comps = new ComponentBuilder()
            .WithButton("Post room panel here", "adm:tv:panel", ButtonStyle.Success, new Emoji("📌"), row: 0)
            .WithButton("Create channel", "adm:tv:create", ButtonStyle.Primary, new Emoji("➕"), row: 0)
            .WithButton("Use existing", "adm:tv:add", ButtonStyle.Primary, new Emoji("🔁"), row: 0)
            .WithButton("Remove", "adm:tv:rm", ButtonStyle.Secondary, new Emoji("➖"), disabled: creators.Count == 0, row: 0)
            .WithButton("Active rooms", "adm:tv:rooms", ButtonStyle.Secondary, new Emoji("📋"), row: 0)
            .WithButton("Back", "adm:tab:home", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();

        return new Screen(notice, embed, comps);
    }

    [ComponentInteraction("adm:tv:panel")]
    public async Task OnRoomPanel()
    {
        if (await GetChannelAsync() is not { } channel)
        {
            await ShowAsync(VoiceTab(NoAccessText));
            return;
        }

        await panels.PostAsync(channel, GuildId, "rooms");
        await ShowAsync(VoiceTab("✅ Room control panel posted in this channel."));
    }

    // ---------- Создать новый канал-создатель ----------

    [ComponentInteraction("adm:tv:create")]
    public Task OnCreatorCreate()
    {
        var embed = new EmbedBuilder()
            .WithTitle("➕ Create 'join to create' channel")
            .WithColor(Color.Teal)
            .WithDescription("Pick the category for the channel and its rooms (or **No category**), then set the name and limit.")
            .Build();
        var category = new SelectMenuBuilder()
            .WithCustomId("adm:tv:createcat")
            .WithType(ComponentType.ChannelSelect)
            .WithChannelTypes(ChannelType.Category)
            .WithPlaceholder("Category")
            .WithMinValues(1)
            .WithMaxValues(1);
        var comps = new ComponentBuilder()
            .WithSelectMenu(category, row: 0)
            .WithButton("No category", "adm:tv:createnocat", ButtonStyle.Secondary, row: 1)
            .WithButton("Back", "adm:tab:voice", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();
        return ShowAsync(new Screen(null, embed, comps));
    }

    [ComponentInteraction("adm:tv:createcat")]
    public Task OnCreatorCategory(string[] values) =>
        RespondWithModalAsync<AdminCreatorModal>($"adm:tv:createset:{values[0]}");

    [ComponentInteraction("adm:tv:createnocat")]
    public Task OnCreatorNoCategory() =>
        RespondWithModalAsync<AdminCreatorModal>("adm:tv:createset:0");

    [ModalInteraction("adm:tv:createset:*")]
    public async Task OnCreatorCreateSubmitted(string categoryId, AdminCreatorModal modal)
    {
        if (!TryParseLimit(modal.Limit, out var limit))
        {
            await ShowAsync(VoiceTab("⚠️ The limit must be a number from 0 to 99."));
            return;
        }

        var name = string.IsNullOrWhiteSpace(modal.Name) ? DefaultCreatorName : modal.Name.Trim();
        ulong? category = ulong.TryParse(categoryId, out var c) && c != 0 ? c : null;

        IVoiceChannel channel;
        try
        {
            channel = await Context.Guild.CreateVoiceChannelAsync(name, p =>
            {
                if (category is { } id) p.CategoryId = id;
            });
        }
        catch (Discord.Net.HttpException)
        {
            await ShowAsync(VoiceTab("⚠️ I couldn't create the channel — I need **Manage Channels**."));
            return;
        }

        voice.SetCreator(new TempVoiceCreator
        {
            GuildId = GuildId,
            ChannelId = channel.Id,
            NameTemplate = string.IsNullOrWhiteSpace(modal.Template) ? null : modal.Template.Trim(),
            UserLimit = limit > 0 ? limit : null
        });

        await ShowAsync(VoiceTab($"✅ Created <#{channel.Id}>. Joining it creates a personal room. " +
                                 "Rooms copy this channel's permissions, so set who can see it right there."));
        await rooms.ReorderCategoryAsync(Context.Guild, category, channel.Id);
    }

    // ---------- Сделать существующий канал создателем ----------

    [ComponentInteraction("adm:tv:add")]
    public Task OnCreatorAdd()
    {
        var embed = new EmbedBuilder()
            .WithTitle("🔁 Use an existing voice channel")
            .WithColor(Color.Teal)
            .WithDescription("Everyone who joins it will be moved to a new room, so don't pick a channel people normally sit in.\n" +
                             "Picking a channel that is already 'join to create' updates its settings.")
            .Build();
        var channel = new SelectMenuBuilder()
            .WithCustomId("adm:tv:addch")
            .WithType(ComponentType.ChannelSelect)
            .WithChannelTypes(ChannelType.Voice)
            .WithPlaceholder("Voice channel")
            .WithMinValues(1)
            .WithMaxValues(1);
        var comps = new ComponentBuilder()
            .WithSelectMenu(channel, row: 0)
            .WithButton("Back", "adm:tab:voice", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();
        return ShowAsync(new Screen(null, embed, comps));
    }

    [ComponentInteraction("adm:tv:addch")]
    public Task OnCreatorAddChannel(string[] values)
    {
        if (!ulong.TryParse(values[0], out var channelId) || voice.GetRoom(channelId) is not null)
            return ShowAsync(VoiceTab("⚠️ That channel is a temporary room — pick a normal voice channel."));
        return RespondWithModalAsync<AdminCreatorSettingsModal>($"adm:tv:addset:{channelId}");
    }

    [ModalInteraction("adm:tv:addset:*")]
    public async Task OnCreatorAddSubmitted(string channelIdText, AdminCreatorSettingsModal modal)
    {
        if (!TryParseLimit(modal.Limit, out var limit))
        {
            await ShowAsync(VoiceTab("⚠️ The limit must be a number from 0 to 99."));
            return;
        }

        var channel = ulong.TryParse(channelIdText, out var id) ? Context.Guild.GetVoiceChannel(id) : null;
        if (channel is null || voice.GetRoom(channel.Id) is not null)
        {
            await ShowAsync(VoiceTab("⚠️ Voice channel not found."));
            return;
        }

        voice.SetCreator(new TempVoiceCreator
        {
            GuildId = GuildId,
            ChannelId = channel.Id,
            NameTemplate = string.IsNullOrWhiteSpace(modal.Template) ? null : modal.Template.Trim(),
            UserLimit = limit > 0 ? limit : null
        });

        await ShowAsync(VoiceTab($"✅ <#{channel.Id}> is now a 'join to create' channel."));
        await rooms.ReorderCategoryAsync(Context.Guild, channel.CategoryId, channel.Id);
    }

    // ---------- Убрать создателя ----------

    [ComponentInteraction("adm:tv:rm")]
    public Task OnCreatorRemove()
    {
        var creators = voice.GetCreators(GuildId);
        if (creators.Count == 0) return ShowAsync(VoiceTab("⚠️ There are no 'join to create' channels."));

        var menu = new SelectMenuBuilder().WithCustomId("adm:tv:rmsel").WithPlaceholder("Choose a channel");
        foreach (var c in creators.Take(MaxOptions))
            menu.AddOption(Label(Context.Guild.GetVoiceChannel(c.ChannelId)?.Name ?? c.ChannelId.ToString()), c.ChannelId.ToString());

        var embed = new EmbedBuilder().WithTitle("➖ Stop using a channel as 'join to create'").WithColor(Color.Teal)
            .WithDescription("The channel itself is not deleted. Existing rooms keep working until they are empty.").Build();
        var comps = new ComponentBuilder()
            .WithSelectMenu(menu, row: 0)
            .WithButton("Back", "adm:tab:voice", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();
        return ShowAsync(new Screen(null, embed, comps));
    }

    [ComponentInteraction("adm:tv:rmsel")]
    public Task OnCreatorRemoveSelected(string[] values)
    {
        var removed = ulong.TryParse(values[0], out var id) && voice.GetCreator(id)?.GuildId == GuildId && voice.RemoveCreator(id);
        return ShowAsync(VoiceTab(removed ? $"✅ <#{id}> is a normal channel again." : "⚠️ That channel is not a 'join to create' channel."));
    }

    // ---------- Активные комнаты ----------

    [ComponentInteraction("adm:tv:rooms")]
    public Task OnRooms() => ShowAsync(RoomsScreen(null));

    private Screen RoomsScreen(string? notice)
    {
        var list = Rooms();
        var sb = new StringBuilder();
        if (list.Count == 0) sb.AppendLine("*No active rooms.*");
        foreach (var r in list.Take(MaxOptions))
        {
            var channel = Context.Guild.GetVoiceChannel(r.ChannelId);
            sb.AppendLine($"• <#{r.ChannelId}> — owner <@{r.OwnerId}> — {channel?.ConnectedUsers.Count ?? 0} inside{(r.Locked ? " — 🔒" : "")}");
        }
        if (list.Count > MaxOptions) sb.AppendLine($"-# …and {list.Count - MaxOptions} more");

        var embed = new EmbedBuilder().WithTitle("📋 Active rooms").WithColor(Color.Teal)
            .WithDescription(Clip(sb.ToString())).Build();

        var comps = new ComponentBuilder();
        if (list.Count > 0)
        {
            var menu = new SelectMenuBuilder().WithCustomId("adm:tv:pick").WithPlaceholder("Choose a room to manage");
            foreach (var r in list.Take(MaxOptions))
                menu.AddOption(Label(Context.Guild.GetVoiceChannel(r.ChannelId)?.Name ?? r.ChannelId.ToString()),
                    r.ChannelId.ToString(), Label($"Owner: {MemberName(r.OwnerId)}"));
            comps.WithSelectMenu(menu, row: 0);
        }
        comps.WithButton("Refresh", "adm:tv:rooms", ButtonStyle.Secondary, new Emoji("🔄"), row: 1)
             .WithButton("Back", "adm:tab:voice", ButtonStyle.Secondary, new Emoji("◀️"), row: 1);

        return new Screen(notice, embed, comps.Build());
    }

    [ComponentInteraction("adm:tv:pick")]
    public Task OnRoomPick(string[] values) => ShowAsync(RoomScreen(values[0], null));

    private Screen RoomScreen(string channelIdText, string? notice)
    {
        if (FindRoom(channelIdText) is not { } r) return RoomsScreen("⚠️ This room no longer exists.");

        var members = r.Channel.ConnectedUsers.Select(u => $"<@{u.Id}>").ToList();
        var embed = new EmbedBuilder()
            .WithTitle($"🔊 {r.Channel.Name}")
            .WithColor(Color.Teal)
            .WithDescription(Clip(
                $"Channel: <#{r.Channel.Id}>\n" +
                $"Owner: <@{r.Room.OwnerId}>\n" +
                $"Created from: <#{r.Room.CreatorChannelId}>\n" +
                $"Locked: {(r.Room.Locked ? "yes 🔒" : "no")}\n" +
                $"Inside ({members.Count}): {(members.Count > 0 ? string.Join(", ", members) : "nobody")}"))
            .Build();

        var owner = new SelectMenuBuilder()
            .WithCustomId($"adm:tv:owner:{r.Channel.Id}")
            .WithType(ComponentType.UserSelect)
            .WithPlaceholder("👑 Transfer ownership to…")
            .WithMinValues(1)
            .WithMaxValues(1);
        var comps = new ComponentBuilder()
            .WithSelectMenu(owner, row: 0)
            .WithButton("Delete room", $"adm:tv:del:{r.Channel.Id}", ButtonStyle.Danger, new Emoji("🗑️"), row: 1)
            .WithButton("Back", "adm:tv:rooms", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();

        return new Screen(notice, embed, comps);
    }

    [ComponentInteraction("adm:tv:owner:*")]
    public async Task OnRoomOwner(string channelIdText, IUser[] users)
    {
        if (FindRoom(channelIdText) is not { } r)
        {
            await ShowAsync(RoomsScreen("⚠️ This room no longer exists."));
            return;
        }

        var target = users[0];
        if (target.IsBot)
        {
            await ShowAsync(RoomScreen(channelIdText, "⚠️ A bot can't own a room."));
            return;
        }
        if (target.Id == r.Room.OwnerId)
        {
            await ShowAsync(RoomScreen(channelIdText, $"⚠️ <@{target.Id}> already owns this room."));
            return;
        }

        await DeferAsync();
        try
        {
            IUser? oldOwner = (IUser?)Context.Guild.GetUser(r.Room.OwnerId) ?? await Context.Client.Rest.GetUserAsync(r.Room.OwnerId);
            if (oldOwner is not null) await r.Channel.RemovePermissionOverwriteAsync(oldOwner);
            await r.Channel.AddPermissionOverwriteAsync(target, TempVoiceService.OwnerPermissions);
            voice.UpdateRoom(r.Channel.Id, room => room.OwnerId = target.Id);
            await ModifyAsync(RoomScreen(channelIdText, $"👑 <@{target.Id}> is now the owner of <#{r.Channel.Id}>."));
        }
        catch (Discord.Net.HttpException)
        {
            await ModifyAsync(RoomScreen(channelIdText, "⚠️ I couldn't change the room permissions — I need **Manage Channels** there."));
        }
    }

    [ComponentInteraction("adm:tv:del:*")]
    public async Task OnRoomDelete(string channelIdText)
    {
        if (FindRoom(channelIdText) is not { } r)
        {
            await ShowAsync(RoomsScreen("⚠️ This room no longer exists."));
            return;
        }

        await DeferAsync();
        var name = r.Channel.Name;
        try
        {
            await r.Channel.DeleteAsync();
        }
        catch (Discord.Net.HttpException)
        {
            await ModifyAsync(RoomScreen(channelIdText, "⚠️ I couldn't delete the room — I need **Manage Channels** there."));
            return;
        }

        voice.RemoveRoom(r.Channel.Id);
        await ModifyAsync(RoomsScreen($"✅ Room **{Format.Sanitize(name)}** deleted."));
    }

    /// <summary>Временная комната бота на этом сервере, которая ещё существует.</summary>
    private RoomRef? FindRoom(string channelIdText)
    {
        if (!ulong.TryParse(channelIdText, out var id)) return null;
        var room = voice.GetRoom(id);
        if (room is null || room.GuildId != GuildId) return null;
        var channel = Context.Guild.GetVoiceChannel(id);
        return channel is null ? null : new RoomRef(channel, room);
    }

    // ================= Bot admins =================

    private bool CallerIsServerManager => Context.User is IGuildUser u && admins.CanManageAdmins(u);

    private Screen AdminsTab(string? notice)
    {
        var roles = admins.GetRoles(GuildId);
        var canEdit = CallerIsServerManager;
        var embed = new EmbedBuilder()
            .WithTitle("🛡️ Bot admins")
            .WithColor(Color.DarkGrey)
            .WithDescription(
                "Who can use `/admin`:\n" +
                "• members with **Administrator** or **Manage Server**\n" +
                $"• members with these roles: {Roles(roles)}\n\n" +
                (canEdit
                    ? "-# Choose the bot admin roles below — the selection replaces the current list."
                    : "-# Only members with Manage Server can change this list."))
            .Build();

        var comps = new ComponentBuilder()
            .WithSelectMenu(RoleSelect("adm:adm:roles", "Bot admin roles", 0, AdminStore.MaxRoles, roles).WithDisabled(!canEdit), row: 0)
            .WithButton("Back", "adm:tab:home", ButtonStyle.Secondary, new Emoji("◀️"), row: 1)
            .Build();

        return new Screen(notice, embed, comps);
    }

    [ComponentInteraction("adm:adm:roles")]
    public Task OnAdminRoles(string[] values)
    {
        if (!CallerIsServerManager)
            return ShowAsync(AdminsTab("⚠️ Only members with Manage Server can change bot admins."));

        var ids = ParseIds(values);
        admins.SetRoles(GuildId, ids);
        return ShowAsync(AdminsTab($"✅ Bot admin roles: {Roles(ids)}"));
    }

    // ================= Хелперы =================

    /// <summary>Перерисовывает эфемерное сообщение панели (после кнопки, списка или окна ввода).</summary>
    private Task ShowAsync(Screen s) => Context.Interaction switch
    {
        SocketMessageComponent c => c.UpdateAsync(m => Apply(m, s)),
        SocketModal modal => modal.UpdateAsync(m => Apply(m, s)),
        _ => RespondAsync(s.Content, embed: s.Embed, components: s.Components, ephemeral: true,
            allowedMentions: AllowedMentions.None)
    };

    /// <summary>То же после DeferAsync — для долгих действий.</summary>
    private Task ModifyAsync(Screen s) => Context.Interaction.ModifyOriginalResponseAsync(m => Apply(m, s));

    private static void Apply(MessageProperties m, Screen s)
    {
        m.Content = s.Content ?? "";
        m.Embeds = s.Embed is null ? Array.Empty<Embed>() : new[] { s.Embed };
        m.Components = s.Components;
        m.AllowedMentions = AllowedMentions.None;
    }

    private SelectMenuBuilder RoleSelect(string customId, string placeholder, int min, int max, IEnumerable<ulong>? selected = null)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(customId)
            .WithType(ComponentType.RoleSelect)
            .WithPlaceholder(placeholder)
            .WithMinValues(min)
            .WithMaxValues(max);

        var defaults = (selected ?? [])
            .Where(id => Context.Guild.GetRole(id) is not null)
            .Take(max)
            .Select(id => new SelectMenuDefaultValue(id, SelectDefaultValueType.Role))
            .ToArray();
        if (defaults.Length > 0) menu.WithDefaultValues(defaults);
        return menu;
    }

    private static List<ulong> ParseIds(IEnumerable<string> values) =>
        values.Select(v => ulong.TryParse(v, out var id) ? id : 0).Where(id => id != 0).Distinct().ToList();

    private static bool TryParseLimit(string? text, out int limit)
    {
        limit = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return int.TryParse(text.Trim(), out limit) && limit is >= 0 and <= 99;
    }

    private static string Roles(IReadOnlyCollection<ulong> ids) =>
        ids.Count > 0 ? string.Join(", ", ids.Select(id => $"<@&{id}>")) : "*not set*";

    private string Category(ulong? id) =>
        id is { } c ? $"<#{c}>" : $"*{TicketRenderer.DefaultCategoryName}* (default)";

    private string RoleName(ulong id) => Context.Guild.GetRole(id)?.Name ?? "deleted role";

    private string MemberName(ulong id) => Context.Guild.GetUser(id)?.DisplayName ?? id.ToString();

    private string ProjectName(string key) => tickets.FindProject(GuildId, key)?.Name ?? "Deleted project";

    private static string When(DateTimeOffset? at) => at is { } t ? $"<t:{t.ToUnixTimeSeconds()}:f>" : "no time";

    /// <summary>Свой текст цитатой для превью (обрезается, чтобы влезть в embed).</summary>
    private static string Quote(string text)
    {
        var clipped = text.Length <= 1500 ? text : text[..1500] + "…";
        return string.Join("\n", clipped.Split('\n').Select(line => "> " + line));
    }

    /// <summary>Подписи пунктов списка: 1–100 символов.</summary>
    private static string Label(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "—";
        return text.Length <= 100 ? text : text[..99] + "…";
    }

    /// <summary>Описание embed — максимум 4096 символов.</summary>
    private static string Clip(string text) => text.Length <= 4000 ? text : text[..4000] + "\n…";

    private async Task<IMessageChannel?> GetChannelAsync()
    {
        var channelId = Context.Interaction.ChannelId ?? 0;
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

    /// <summary>Удаляет канал этого сервера (используется только для каналов тикета).</summary>
    private async Task DeleteGuildChannelAsync(ulong channelId)
    {
        try
        {
            if (Context.Guild.GetChannel(channelId) is { } channel) await channel.DeleteAsync();
        }
        catch (Discord.Net.HttpException)
        {
            // уже удалён
        }
    }
}

public sealed class AdminActivityModal : IModal
{
    public string Title => "New activity";

    [InputLabel("Activity name")]
    [ModalTextInput("name", TextInputStyle.Short, placeholder: "e.g. Fire Temple", maxLength: 100)]
    public string Name { get; set; } = "";
}

public sealed class AdminProjectModal : IModal
{
    public string Title => "New ticket project";

    [InputLabel("Project name")]
    [ModalTextInput("name", TextInputStyle.Short, placeholder: "e.g. Aion 2", maxLength: 100)]
    public string Name { get; set; } = "";
}

public sealed class AdminTicketTextModal : IModal
{
    public string Title => "Ticket text";

    [InputLabel("Text (empty = remove the block)")]
    [RequiredInput(false)]
    [ModalTextInput("text", TextInputStyle.Paragraph, maxLength: TicketRenderer.MaxCustomTextLength)]
    public string? Text { get; set; }
}

public sealed class AdminCreatorModal : IModal
{
    public string Title => "'Join to create' channel";

    [InputLabel("Channel name")]
    [RequiredInput(false)]
    [ModalTextInput("name", TextInputStyle.Short, placeholder: "➕ Create room", maxLength: 100)]
    public string? Name { get; set; }

    [InputLabel("Room name ({user} = member name)")]
    [RequiredInput(false)]
    [ModalTextInput("template", TextInputStyle.Short, placeholder: "Empty = channel name + member name", maxLength: 100)]
    public string? Template { get; set; }

    [InputLabel("User limit (0 = no limit)")]
    [RequiredInput(false)]
    [ModalTextInput("limit", TextInputStyle.Short, placeholder: "0–99", maxLength: 2)]
    public string? Limit { get; set; }
}

public sealed class AdminCreatorSettingsModal : IModal
{
    public string Title => "Room settings";

    [InputLabel("Room name ({user} = member name)")]
    [RequiredInput(false)]
    [ModalTextInput("template", TextInputStyle.Short, placeholder: "Empty = channel name + member name", maxLength: 100)]
    public string? Template { get; set; }

    [InputLabel("User limit (0 = no limit)")]
    [RequiredInput(false)]
    [ModalTextInput("limit", TextInputStyle.Short, placeholder: "0–99", maxLength: 2)]
    public string? Limit { get; set; }
}
