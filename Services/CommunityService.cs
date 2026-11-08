using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Два модуля:
/// Welcome — приветствие новичков (канал / ЛС, текст и/или embed, кнопки-ссылки) и автороль;
/// Role panels — самовыдача ролей: кнопки, пункты выпадающего списка и реакции, каждая связка выдаёт 1–5 ролей.
/// </summary>
public sealed class CommunityService(
    DiscordSocketClient client,
    CommunityStore store,
    PanelTextStore panels,
    AuditLog audit,
    AnnouncementService announce,
    ILogger<CommunityService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        client.UserJoined += OnUserJoined;
        client.ReactionAdded += OnReactionAdded;
        client.ReactionRemoved += OnReactionRemoved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        client.UserJoined -= OnUserJoined;
        client.ReactionAdded -= OnReactionAdded;
        client.ReactionRemoved -= OnReactionRemoved;
        return Task.CompletedTask;
    }

    // ================= Welcome =================

    private Task OnUserJoined(SocketGuildUser user)
    {
        _ = Task.Run(async () =>
        {
            try { await WelcomeAsync(user, store.GetWelcome(user.Guild.Id)); }
            catch (Exception ex) { logger.LogWarning(ex, "Welcome failed for {User}", user.Id); }
        });
        return Task.CompletedTask;
    }

    private async Task WelcomeAsync(SocketGuildUser user, WelcomeSettings w)
    {
        var guild = user.Guild;

        if (w.AutoRoleMode == WelcomeDefaults.AutoRoleJoin && w.AutoRoleIds.Count > 0 && !(user.IsBot && w.SkipBots))
        {
            if (w.AutoRoleDelaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(w.AutoRoleDelaySeconds));
            var member = guild.GetUser(user.Id);
            var roles = w.AutoRoleIds.Select(guild.GetRole).Where(r => CanManage(guild, r)).ToList();
            if (member is not null && roles.Count > 0)
            {
                try
                {
                    await member.AddRolesAsync(roles);
                    audit.FromSystem(guild.Id, "welcome", "Auto role on join", $"{user.DisplayName}: {Names(roles)}");
                }
                catch (Discord.Net.HttpException ex)
                {
                    logger.LogWarning(ex, "Auto role failed in {Guild}", guild.Id);
                    audit.FromSystem(guild.Id, "welcome", "Auto role failed", $"{user.DisplayName}: {ex.Message}");
                }
            }
        }

        if (user.IsBot) return;
        var buttons = WelcomeButtons(guild, w);

        if (w.Channel.Enabled && w.ChannelId is { } channelId && guild.GetTextChannel(channelId) is { } channel)
        {
            var (content, embed) = BuildWelcome(w.Channel, user);
            try
            {
                await channel.SendMessageAsync(content, embed: embed, components: buttons,
                    allowedMentions: new AllowedMentions { UserIds = [user.Id] });
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogWarning(ex, "Welcome message failed in {Channel}", channelId);
            }
        }

        if (w.Dm.Enabled)
        {
            var (content, embed) = BuildWelcome(w.Dm, user);
            try
            {
                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(content, embed: embed, components: buttons, allowedMentions: AllowedMentions.None);
            }
            catch (Discord.Net.HttpException)
            {
                // ЛС закрыты — не страшно
            }
        }
    }

    /// <summary>Тест: оба приветствия приходят в ЛС тому, кто нажал (как будто он только что зашёл).</summary>
    public async Task<string?> SendTestAsync(SocketGuildUser user, WelcomeSettings? draft = null)
    {
        var w = draft ?? store.GetWelcome(user.Guild.Id);
        if (!w.Channel.Enabled && !w.Dm.Enabled) return "Turn on the channel message or the DM first.";
        try
        {
            var dm = await user.CreateDMChannelAsync();
            var buttons = WelcomeButtons(user.Guild, w);
            foreach (var (label, msg) in new[] { ("Channel message", w.Channel), ("DM", w.Dm) }.Where(x => x.Item2.Enabled))
            {
                var (content, embed) = BuildWelcome(msg, user);
                await dm.SendMessageAsync($"-# Test · {label}\n{content}".Trim(), embed: embed, components: buttons, allowedMentions: AllowedMentions.None);
            }
            return null;
        }
        catch (Discord.Net.HttpException)
        {
            return "I can't DM you — allow direct messages from server members.";
        }
    }

    public (string? Content, Embed? Embed) BuildWelcome(WelcomeMessage m, SocketGuildUser user)
    {
        var content = string.IsNullOrWhiteSpace(m.Content) ? null : Cut(Render(m.Content, user), 2000);
        if (!m.UseEmbed) return (content ?? "👋", null);

        var e = new EmbedBuilder().WithColor(Tx.ParseColor(m.Color) ?? Tx.Color(user.Guild.Id, "color.welcome"));
        if (!string.IsNullOrWhiteSpace(m.Title)) e.WithTitle(Cut(Render(m.Title, user), 256));
        if (!string.IsNullOrWhiteSpace(m.Description)) e.WithDescription(Cut(Render(m.Description, user), 4000));
        if (m.ShowAvatar) e.WithThumbnailUrl(user.GetDisplayAvatarUrl(size: 256));
        if (IsUrl(m.ImageUrl)) e.WithImageUrl(m.ImageUrl);
        if (!string.IsNullOrWhiteSpace(m.Footer)) e.WithFooter(Cut(Render(m.Footer, user), 2048));
        if (e.Title is null && e.Description is null && e.ImageUrl is null) e.WithDescription(Render("👋 {user}", user));
        return (content, e.Build());
    }

    /// <summary>Подстановки: {user} {name} {username} {server} {members} {tickets}.</summary>
    public string Render(string template, SocketGuildUser user)
    {
        var guild = user.Guild;
        return (template ?? "")
            .Replace("{user}", user.Mention)
            .Replace("{name}", Format.Sanitize(user.DisplayName))
            .Replace("{username}", Format.Sanitize(user.Username))
            .Replace("{server}", Format.Sanitize(guild.Name))
            .Replace("{members}", guild.MemberCount.ToString())
            .Replace("{tickets}", TicketPanelUrl(guild.Id) ?? "");
    }

    /// <summary>Ссылка на первую опубликованную панель тикетов (или null).</summary>
    public string? TicketPanelUrl(ulong guildId) =>
        panels.GetPosted(guildId, "tickets").FirstOrDefault() is { } p
            ? $"https://discord.com/channels/{guildId}/{p.ChannelId}/{p.MessageId}"
            : null;

    private MessageComponent? WelcomeButtons(SocketGuild guild, WelcomeSettings w)
    {
        var b = new ComponentBuilder();
        var any = false;
        if (w.ApplyButton && TicketPanelUrl(guild.Id) is { } url)
        {
            b.WithButton(Cut(string.IsNullOrWhiteSpace(w.ApplyLabel) ? Tx.Get(guild.Id, "welcome.applyButton") : w.ApplyLabel!, 80),
                style: ButtonStyle.Link, url: url, emote: new Emoji("📝"));
            any = true;
        }
        foreach (var link in w.Links.Where(l => IsUrl(l.Url) && !string.IsNullOrWhiteSpace(l.Label)).Take(WelcomeDefaults.MaxLinks))
        {
            b.WithButton(Cut(link.Label, 80), style: ButtonStyle.Link, url: link.Url, emote: LfgProjectStore.ToEmote(link.Emoji));
            any = true;
        }
        return any ? b.Build() : null;
    }

    // ================= Role panels =================

    /// <summary>Роль можно выдавать: не @everyone, не управляемая, ниже высшей роли бота, у бота есть Manage Roles.</summary>
    public static bool CanManage(SocketGuild guild, SocketRole? role) =>
        role is not null && !role.IsEveryone && !role.IsManaged && guild.CurrentUser.Hierarchy > role.Position &&
        guild.CurrentUser.GuildPermissions.ManageRoles;

    private string RoleName(ulong guildId, ulong roleId) => client.GetGuild(guildId)?.GetRole(roleId)?.Name ?? "Role";

    /// <summary>Подпись связки: своя или имя первой роли.</summary>
    public string BindingLabel(RolePanel p, RoleBinding b) =>
        !string.IsNullOrWhiteSpace(b.Label) ? b.Label! : b.RoleIds.Count > 0 ? RoleName(p.GuildId, b.RoleIds[0]) : "Role";

    /// <summary>Рендер с подстановкой :имя: → эмодзи сервера/бота в заголовке и тексте (как в постах).</summary>
    private async Task<(Embed Embed, MessageComponent? Components)> RenderResolvedAsync(RolePanel p)
    {
        var copy = JsonClone(p);
        if (!string.IsNullOrWhiteSpace(copy.Text)) copy.Text = await announce.ResolveEmotesAsync(p.GuildId, copy.Text!);
        if (!string.IsNullOrWhiteSpace(copy.Title)) copy.Title = await announce.ResolveEmotesAsync(p.GuildId, copy.Title!);
        return Render(copy);
    }

    private static RolePanel JsonClone(RolePanel p) =>
        System.Text.Json.JsonSerializer.Deserialize<RolePanel>(System.Text.Json.JsonSerializer.Serialize(p))!;

    public (Embed Embed, MessageComponent? Components) Render(RolePanel p)
    {
        var gid = p.GuildId;
        var embed = new EmbedBuilder().WithColor(Tx.ParseColor(p.Color) ?? Tx.Color(gid, "color.roles.panel"));
        if (!string.IsNullOrWhiteSpace(p.Title)) embed.WithTitle(Cut(p.Title!, 256));
        if (IsUrl(p.ImageUrl)) embed.WithImageUrl(p.ImageUrl);
        if (IsUrl(p.ThumbnailUrl)) embed.WithThumbnailUrl(p.ThumbnailUrl);
        if (!string.IsNullOrWhiteSpace(p.Footer)) embed.WithFooter(Cut(p.Footer!, 2048));

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Text)) parts.Add(p.Text!.Trim());
        // Список связок: эмодзи — роли — описание. Для реакций без него непонятно, что ставить
        if (p.ShowList && p.Bindings.Count > 0)
            parts.Add(string.Join("\n", p.Bindings.Select(b =>
            {
                var line = $"{(string.IsNullOrWhiteSpace(b.Emoji) ? "•" : b.Emoji)} {string.Join(" ", b.RoleIds.Select(id => $"<@&{id}>"))}";
                return string.IsNullOrWhiteSpace(b.Description) ? line : $"{line} — {b.Description}";
            })));
        if (p.ShowHint && Tx.Get(gid, p.Mode switch
            {
                RolePanelMode.Unique => "roles.hintUnique",
                RolePanelMode.Verify => "roles.hintVerify",
                RolePanelMode.Remove => "roles.hintRemove",
                _ => "roles.hintNormal"
            }) is { Length: > 0 } hint)
            parts.Add($"-# {hint}");
        if (parts.Count > 0) embed.WithDescription(Cut(string.Join("\n\n", parts), 4000));
        if (embed.Title is null && embed.Description is null) embed.WithDescription("​");

        // Компоненты: выпадающий список (1 ряд) + кнопки по 5 в ряд, всего до 5 рядов
        var builder = new ComponentBuilder();
        var row = 0;
        var options = p.Options.Take(25).ToList();
        if (options.Count > 0)
        {
            var max = p.Mode == RolePanelMode.Unique ? 1 : p.MaxPick is > 0 ? Math.Min(p.MaxPick, options.Count) : options.Count;
            var menu = new SelectMenuBuilder()
                .WithCustomId($"rp:s:{p.Id}")
                .WithPlaceholder(Cut(string.IsNullOrWhiteSpace(p.Placeholder) ? Tx.Get(gid, "roles.placeholder") : p.Placeholder!, 150))
                .WithMinValues(0)
                .WithMaxValues(max);
            foreach (var o in options)
            {
                var opt = new SelectMenuOptionBuilder().WithLabel(Cut(BindingLabel(p, o), 100)).WithValue(o.Id);
                if (!string.IsNullOrWhiteSpace(o.Description)) opt.WithDescription(Cut(o.Description!, 100));
                if (LfgProjectStore.ToEmote(o.Emoji) is { } e) opt.WithEmote(e);
                menu.AddOption(opt);
            }
            builder.WithSelectMenu(menu, row++);
        }
        foreach (var (b, i) in p.Buttons.Take((5 - row) * 5).Select((b, i) => (b, i)))
            builder.WithButton(Cut(BindingLabel(p, b), 80), $"rp:b:{p.Id}:{b.Id}", b.Color switch
            {
                RoleButtonColor.Blurple => ButtonStyle.Primary,
                RoleButtonColor.Green => ButtonStyle.Success,
                RoleButtonColor.Red => ButtonStyle.Danger,
                _ => ButtonStyle.Secondary
            }, LfgProjectStore.ToEmote(b.Emoji), row: row + i / 5);
        // «Мои роли»: личный вид — что у меня уже есть (кнопки в канале у всех одинаковые)
        var shown = p.Buttons.Take((5 - row) * 5).Count();
        var myRow = row + shown / 5;
        if (shown + options.Count > 0 && myRow < 5)
            builder.WithButton(Cut(Tx.Get(gid, "roles.myButton"), 80), $"rp:my:{p.Id}", ButtonStyle.Secondary, new Emoji("👤"), row: myRow);

        var comps = builder.Build();
        return (embed.Build(), comps.Components.Count > 0 ? comps : null);
    }

    /// <summary>
    /// Личный вид панели (эфемерно, видит только нажавший): какие роли панели у него есть (✅ / ➖)
    /// и кнопки-переключатели — зелёная, если роль уже взята. Кнопки в канале у всех одинаковые, поэтому состояние — здесь.
    /// </summary>
    public async Task<(string Text, MessageComponent Components)> PersonalViewAsync(RolePanel panel, ulong userId, string? result)
    {
        var gid = panel.GuildId;
        // Свежие роли (кэш после выдачи/снятия обновляется не сразу)
        IReadOnlyCollection<ulong> mine;
        try
        {
            mine = (await client.Rest.GetGuildUserAsync(gid, userId))?.RoleIds ?? [];
        }
        catch (Discord.Net.HttpException)
        {
            mine = client.GetGuild(gid)?.GetUser(userId)?.Roles.Select(r => r.Id).ToList() ?? [];
        }
        bool Has(RoleBinding b) => b.RoleIds.Count > 0 && b.RoleIds.All(mine.Contains);

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(result)) sb.AppendLine(result).AppendLine();
        sb.AppendLine(Tx.Get(gid, "roles.yourRoles"));
        foreach (var b in panel.Bindings.Take(25))
            sb.AppendLine($"{(Has(b) ? "✅" : "➖")} {string.Join(" ", b.RoleIds.Select(id => $"<@&{id}>"))}");

        var builder = new ComponentBuilder();
        foreach (var (b, i) in panel.Bindings.Take(25).Select((b, i) => (b, i)))
            builder.WithButton(Cut((Has(b) ? "✓ " : "") + BindingLabel(panel, b), 80), $"rp:mb:{panel.Id}:{b.Id}",
                Has(b) ? ButtonStyle.Success : ButtonStyle.Secondary, LfgProjectStore.ToEmote(b.Emoji), row: i / 5);
        return (Cut(sb.ToString().TrimEnd(), 2000), builder.Build());
    }

    /// <summary>Публикует панель в канал (старое сообщение панели удаляется).</summary>
    public async Task<string?> PostAsync(RolePanel panel, ulong channelId)
    {
        if (client.GetGuild(panel.GuildId)?.GetTextChannel(channelId) is not { } channel) return "Channel not found.";
        if (panel.Bindings.Count == 0) return "Add at least one button, reaction or list item first.";

        var (embed, comps) = await RenderResolvedAsync(panel);
        IUserMessage message;
        try
        {
            message = await channel.SendMessageAsync(embed: embed, components: comps);
        }
        catch (Discord.Net.HttpException)
        {
            return "I can't post there. Give the bot View Channel, Send Messages and Embed Links in that channel.";
        }

        var oldChannel = panel.ChannelId;
        var oldMessage = panel.MessageId;
        store.SetPosted(panel.Id, channel.Id, message.Id);
        await SyncReactionsAsync(message, panel);
        if (oldChannel is { } oc && oldMessage is { } om && om != message.Id) await DeleteMessageAsync(panel.GuildId, oc, om);
        return null;
    }

    /// <summary>Перерисовывает опубликованную панель. Сообщение удалено — панель считается неопубликованной.</summary>
    public async Task<string?> RefreshAsync(RolePanel panel)
    {
        if (panel.ChannelId is not { } channelId || panel.MessageId is not { } messageId) return null;
        if (client.GetGuild(panel.GuildId)?.GetTextChannel(channelId) is not { } channel ||
            await channel.GetMessageAsync(messageId) is not IUserMessage message)
        {
            store.SetPosted(panel.Id, null, null);
            return "The posted message was deleted — post the panel again.";
        }

        var (embed, comps) = await RenderResolvedAsync(panel);
        try
        {
            await message.ModifyAsync(m =>
            {
                m.Embeds = new[] { embed };
                m.Components = comps ?? new ComponentBuilder().Build();
            });
            await SyncReactionsAsync(message, panel);
            return null;
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Failed to refresh role panel {Panel}", panel.Id);
            return "I couldn't update the posted message.";
        }
    }

    public Task DeletePostedAsync(RolePanel panel) =>
        panel.ChannelId is { } c && panel.MessageId is { } m ? DeleteMessageAsync(panel.GuildId, c, m) : Task.CompletedTask;

    private async Task DeleteMessageAsync(ulong guildId, ulong channelId, ulong messageId)
    {
        try
        {
            if (client.GetGuild(guildId)?.GetTextChannel(channelId) is { } ch) await ch.DeleteMessageAsync(messageId);
        }
        catch (Discord.Net.HttpException)
        {
            // уже удалено
        }
    }

    /// <summary>Реакции бота под панелью: нужные — поставить, лишние (от удалённых связок) — убрать.</summary>
    private async Task SyncReactionsAsync(IUserMessage message, RolePanel panel)
    {
        var wanted = panel.Reactions.Select(b => LfgProjectStore.ToEmote(b.Emoji)).Where(e => e is not null).Cast<IEmote>().ToList();
        foreach (var (emote, meta) in message.Reactions)
            if (meta.IsMe && !wanted.Any(w => SameEmote(w, emote)))
                try { await message.RemoveReactionAsync(emote, client.CurrentUser.Id); } catch (Discord.Net.HttpException) { }
        foreach (var emote in wanted)
        {
            if (message.Reactions.Any(r => r.Value.IsMe && SameEmote(emote, r.Key))) continue;
            try { await message.AddReactionAsync(emote); }
            catch (Discord.Net.HttpException ex) { logger.LogWarning(ex, "Can't add reaction {Emoji}", emote.Name); }
        }
    }

    /// <summary>Нет одной из ролей-допусков панели — текст отказа.</summary>
    private string? Gate(RolePanel panel, SocketGuildUser member)
    {
        // Удалённые с сервера роли-допуски не учитываются (иначе панель закрыта для всех)
        var required = panel.RequiredRoleIds.Where(id => member.Guild.GetRole(id) is not null).ToList();
        if (required.Count == 0 || member.Roles.Any(r => required.Contains(r.Id))) return null;
        return Tx.Get(panel.GuildId, "roles.needRole", ("roles", string.Join(" / ", required.Select(id => $"<@&{id}>"))));
    }

    /// <summary>
    /// Связка нажата (кнопка или реакция). add: true — только выдать (реакция поставлена),
    /// false — только снять (реакция убрана), null — переключить (кнопка). Возвращает текст ответа.
    /// </summary>
    public async Task<string> PickAsync(RolePanel panel, SocketGuildUser member, string bindingId, bool? add = null)
    {
        var gid = panel.GuildId;
        var guild = member.Guild;
        if (panel.Bindings.Find(b => b.Id == bindingId) is not { } binding) return Tx.Get(gid, "roles.gone");
        if (Gate(panel, member) is { } denied) return denied;

        var roles = binding.RoleIds.Select(guild.GetRole).Where(r => r is not null).ToList();
        if (roles.Count == 0) return Tx.Get(gid, "roles.gone");
        if (roles.FirstOrDefault(r => !CanManage(guild, r)) is { } blocked) return Tx.Get(gid, "roles.cantManage", ("role", blocked!.Mention));

        var hasAll = roles.All(r => member.Roles.Any(x => x.Id == r!.Id));
        var give = panel.Mode switch
        {
            RolePanelMode.Verify => true,
            RolePanelMode.Remove => false,
            _ => add ?? !hasAll
        };
        // Реакцию убрали в режиме «только выдать» / поставили в режиме «только снять» — ничего не делать
        if (add is { } a && a != give) return Tx.Get(gid, "roles.nothingChanged");

        try
        {
            if (give)
            {
                var missing = roles.Where(r => member.Roles.All(x => x.Id != r!.Id)).ToList();
                // Unique: роли других связок панели снимаются
                var others = panel.Mode == RolePanelMode.Unique
                    ? panel.Bindings.Where(b => b.Id != binding.Id).SelectMany(b => b.RoleIds).Except(binding.RoleIds)
                        .Where(id => member.Roles.Any(r => r.Id == id)).Select(guild.GetRole).Where(r => CanManage(guild, r)).ToList()
                    : [];
                if (missing.Count == 0 && others.Count == 0) return Tx.Get(gid, "roles.already", ("role", Mentions(roles)));
                if (others.Count > 0) await member.RemoveRolesAsync(others);
                if (missing.Count > 0) await member.AddRolesAsync(missing);
                audit.FromDiscord(gid, member, "roles", "Took roles from a panel", $"{panel.Title}: +{Names(missing)}{(others.Count > 0 ? $" −{Names(others)}" : "")}");
                return Tx.Get(gid, "roles.added", ("role", Mentions(roles)));
            }

            var present = roles.Where(r => member.Roles.Any(x => x.Id == r!.Id)).ToList();
            if (present.Count == 0) return Tx.Get(gid, "roles.notHave", ("role", Mentions(roles)));
            await member.RemoveRolesAsync(present);
            audit.FromDiscord(gid, member, "roles", "Removed roles via a panel", $"{panel.Title}: −{Names(present)}");
            return Tx.Get(gid, "roles.removed", ("role", Mentions(present)));
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Role panel {Panel}: failed to change roles of binding {Binding}", panel.Id, bindingId);
            return Tx.Get(gid, "roles.cantManage", ("role", Mentions(roles)));
        }
    }

    /// <summary>Выпадающий список: роли выбранных пунктов — выдать, остальных пунктов — снять (по режиму панели).</summary>
    public async Task<string> SelectAsync(RolePanel panel, SocketGuildUser member, IReadOnlyCollection<string> selectedIds)
    {
        var gid = panel.GuildId;
        var guild = member.Guild;
        if (Gate(panel, member) is { } denied) return denied;

        var options = panel.Options.ToList();
        var chosen = options.Where(o => selectedIds.Contains(o.Id)).ToList();
        var wantIds = chosen.SelectMany(o => o.RoleIds).ToHashSet();
        var allIds = options.SelectMany(o => o.RoleIds).ToHashSet();
        var mine = member.Roles.Select(r => r.Id).ToHashSet();

        var toAdd = panel.Mode == RolePanelMode.Remove ? [] : wantIds.Where(id => !mine.Contains(id)).Select(guild.GetRole).ToList();
        var toRemove = panel.Mode switch
        {
            RolePanelMode.Verify => [],
            // «Только снять»: выбранные пункты — снять их роли
            RolePanelMode.Remove => wantIds.Where(mine.Contains).Select(guild.GetRole).ToList(),
            _ => allIds.Except(wantIds).Where(mine.Contains).Select(guild.GetRole).ToList()
        };
        var blocked = toAdd.Concat(toRemove).Where(r => r is not null && !CanManage(guild, r)).ToList();
        toAdd = toAdd.Where(r => CanManage(guild, r)).ToList();
        toRemove = toRemove.Where(r => CanManage(guild, r)).ToList();

        try
        {
            if (toRemove.Count > 0) await member.RemoveRolesAsync(toRemove);
            if (toAdd.Count > 0) await member.AddRolesAsync(toAdd);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Role panel {Panel}: failed to set roles", panel.Id);
            return Tx.Get(gid, "roles.cantManage", ("role", Mentions(toAdd.Concat(toRemove))));
        }

        if (toAdd.Count + toRemove.Count > 0)
            audit.FromDiscord(gid, member, "roles", "Changed roles via a panel", $"{panel.Title}: +{Names(toAdd)} −{Names(toRemove)}");

        var lines = new List<string>();
        if (toAdd.Count > 0) lines.Add(Tx.Get(gid, "roles.added", ("role", Mentions(toAdd))));
        if (toRemove.Count > 0) lines.Add(Tx.Get(gid, "roles.removed", ("role", Mentions(toRemove))));
        if (blocked.Count > 0) lines.Add(Tx.Get(gid, "roles.cantManage", ("role", Mentions(blocked))));
        return lines.Count > 0 ? string.Join("\n", lines) : Tx.Get(gid, "roles.nothingChanged");
    }

    // ---------- Реакции ----------

    private Task OnReactionAdded(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) =>
        HandleReactionAsync(message.Id, reaction, true);

    private Task OnReactionRemoved(Cacheable<IUserMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel, SocketReaction reaction) =>
        HandleReactionAsync(message.Id, reaction, false);

    private Task HandleReactionAsync(ulong messageId, SocketReaction reaction, bool added)
    {
        if (reaction.UserId == client.CurrentUser?.Id) return Task.CompletedTask;
        if (store.FindPanelByMessage(messageId) is not { } panel) return Task.CompletedTask;
        var binding = panel.Reactions.FirstOrDefault(b => LfgProjectStore.ToEmote(b.Emoji) is { } e && SameEmote(e, reaction.Emote));
        if (binding is null)
        {
            logger.LogInformation("Reaction {Emoji} on panel {Panel} matches no binding", reaction.Emote, panel.Title);
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var guild = client.GetGuild(panel.GuildId);
                if (guild is null) return;
                // Участника может не быть в кэше — догрузить
                var member = guild.GetUser(reaction.UserId)
                    ?? (reaction.User.IsSpecified ? reaction.User.Value as SocketGuildUser : null);
                if (member is null)
                {
                    await guild.DownloadUsersAsync();
                    member = guild.GetUser(reaction.UserId);
                }
                if (member is null || member.IsBot) return;

                var result = await PickAsync(panel, member, binding.Id, added);
                var gid = panel.GuildId;
                var failed = result == Tx.Get(gid, "roles.gone") || Gate(panel, member) is not null
                    || binding.RoleIds.Select(guild.GetRole).Any(r => !CanManage(guild, r));
                logger.LogInformation("Reaction role {Emoji} by {User} on panel {Panel}: {Result}", reaction.Emote.Name, member.Username, panel.Title, result);

                // Отказ (нет роли-допуска, бот не может выдать роль): снять реакцию и написать причину в ЛС.
                // В ЛС упоминания ролей сервера показываются как «Неизвестная роль» — заменяем их на названия.
                if (added && failed)
                {
                    if (reaction.Channel is ITextChannel ch && await ch.GetMessageAsync(messageId) is IUserMessage m)
                        try { await m.RemoveReactionAsync(reaction.Emote, reaction.UserId); } catch (Discord.Net.HttpException) { }
                    var text = System.Text.RegularExpressions.Regex.Replace(result, @"<@&(\d+)>",
                        x => guild.GetRole(ulong.Parse(x.Groups[1].Value)) is { } r ? $"**@{r.Name}**" : "**@deleted role**");
                    try { await member.SendMessageAsync($"**{guild.Name}** — {panel.Title}: {text}"); } catch (Discord.Net.HttpException) { }
                }

                // Unique: убрать остальные реакции этого человека на панели (нужно Manage Messages)
                if (added && panel.Mode == RolePanelMode.Unique && reaction.Channel is ITextChannel tc &&
                    await tc.GetMessageAsync(messageId) is IUserMessage msg)
                {
                    foreach (var other in panel.Reactions.Where(b => b.Id != binding.Id))
                        if (LfgProjectStore.ToEmote(other.Emoji) is { } e)
                            try { await msg.RemoveReactionAsync(e, reaction.UserId); } catch (Discord.Net.HttpException) { }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reaction role failed on {Message}", messageId);
            }
        });
        return Task.CompletedTask;
    }

    private static bool SameEmote(IEmote a, IEmote b) => a switch
    {
        Emote x when b is Emote y => x.Id == y.Id,
        Emoji x => Bare(x.Name) == Bare(b.Name),
        _ => false
    };

    // ---------- Мелочи ----------

    /// <summary>Юникод-эмодзи без селектора вариантов (Discord присылает реакции то с U+FE0F, то без).</summary>
    private static string Bare(string? name) => (name ?? "").Replace("\uFE0F", "");

    private static string Mentions(IEnumerable<SocketRole?> roles) => string.Join(", ", roles.Where(r => r is not null).Select(r => r!.Mention));
    private static string Names(IEnumerable<SocketRole?> roles) => string.Join(", ", roles.Where(r => r is not null).Select(r => "@" + r!.Name));
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    public static bool IsUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http");
}
