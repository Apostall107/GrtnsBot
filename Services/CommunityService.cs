using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Приветствие новичков (канал / ЛС), автороль при входе и панели самовыдачи ролей
/// (кнопки, выпадающий список или реакции).
/// </summary>
public sealed class CommunityService(
    DiscordSocketClient client,
    CommunityStore store,
    PanelTextStore panels,
    AuditLog audit,
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

    // ================= Приветствие и автороль =================

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

        // Автороль (по умолчанию выключена)
        if (w.AutoRoleMode == WelcomeDefaults.AutoRoleJoin && w.AutoRoleIds.Count > 0 && !(user.IsBot && w.SkipBots))
        {
            var roles = w.AutoRoleIds.Select(guild.GetRole).Where(r => r is not null && CanManage(guild, r)).ToList();
            if (roles.Count > 0)
            {
                try
                {
                    await user.AddRolesAsync(roles);
                    audit.FromSystem(guild.Id, "roles", "Auto role on join",
                        $"{user.DisplayName}: {string.Join(", ", roles.Select(r => "@" + r!.Name))}");
                }
                catch (Discord.Net.HttpException ex)
                {
                    logger.LogWarning(ex, "Auto role failed in {Guild}", guild.Id);
                    audit.FromSystem(guild.Id, "roles", "Auto role failed", $"{user.DisplayName}: {ex.Message}");
                }
            }
        }

        if (user.IsBot) return;
        var button = ApplyButton(guild, w);

        if (w.ChannelId is { } channelId && guild.GetTextChannel(channelId) is { } channel)
        {
            try
            {
                await channel.SendMessageAsync(Render(w.ChannelText, user), components: button,
                    allowedMentions: new AllowedMentions { UserIds = [user.Id] });
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogWarning(ex, "Welcome message failed in {Channel}", channelId);
            }
        }

        if (w.SendDm)
        {
            try
            {
                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(Render(w.DmText, user), components: button, allowedMentions: AllowedMentions.None);
            }
            catch (Discord.Net.HttpException)
            {
                // ЛС закрыты — не страшно
            }
        }
    }

    /// <summary>Тест приветствия: отправляет его в ЛС этому человеку (как будто он только что зашёл).</summary>
    public async Task<string?> SendTestAsync(SocketGuildUser user)
    {
        var w = store.GetWelcome(user.Guild.Id);
        try
        {
            var dm = await user.CreateDMChannelAsync();
            var parts = new List<string>();
            if (w.ChannelId is not null) parts.Add($"**Channel message:**\n{Render(w.ChannelText, user)}");
            if (w.SendDm) parts.Add($"**DM:**\n{Render(w.DmText, user)}");
            if (parts.Count == 0) return "Turn on the channel message or the DM first.";
            await dm.SendMessageAsync(string.Join("\n\n", parts), components: ApplyButton(user.Guild, w), allowedMentions: AllowedMentions.None);
            return null;
        }
        catch (Discord.Net.HttpException)
        {
            return "I can't DM you — allow direct messages from server members.";
        }
    }

    /// <summary>Подстановки: {user} {name} {server} {members} {tickets}.</summary>
    public string Render(string template, SocketGuildUser user)
    {
        var guild = user.Guild;
        var text = (template ?? "")
            .Replace("{user}", user.Mention)
            .Replace("{name}", Format.Sanitize(user.DisplayName))
            .Replace("{server}", Format.Sanitize(guild.Name))
            .Replace("{members}", guild.MemberCount.ToString())
            .Replace("{tickets}", TicketPanelUrl(guild.Id) ?? "");
        return text.Length <= 2000 ? text : text[..2000];
    }

    /// <summary>Ссылка на первую опубликованную панель тикетов (или null).</summary>
    public string? TicketPanelUrl(ulong guildId) =>
        panels.GetPosted(guildId, "tickets").FirstOrDefault() is { } p
            ? $"https://discord.com/channels/{guildId}/{p.ChannelId}/{p.MessageId}"
            : null;

    private MessageComponent? ApplyButton(SocketGuild guild, WelcomeSettings w) =>
        w.ApplyButton && TicketPanelUrl(guild.Id) is { } url
            ? new ComponentBuilder().WithButton(Tx.Get(guild.Id, "welcome.applyButton"), style: ButtonStyle.Link, url: url, emote: new Emoji("📝")).Build()
            : null;

    // ================= Панели ролей =================

    /// <summary>Роль можно выдавать: не @everyone, не управляемая, ниже высшей роли бота.</summary>
    public static bool CanManage(SocketGuild guild, SocketRole? role) =>
        role is not null && !role.IsEveryone && !role.IsManaged && guild.CurrentUser.Hierarchy > role.Position &&
        guild.CurrentUser.GuildPermissions.ManageRoles;

    public (Embed Embed, MessageComponent? Components) Render(RolePanel p)
    {
        var gid = p.GuildId;
        var embed = new EmbedBuilder()
            .WithColor(Tx.ParseColor(p.Color) ?? Tx.Color(gid, "color.roles.panel"));
        if (!string.IsNullOrWhiteSpace(p.Title)) embed.WithTitle(p.Title);

        var text = p.Text?.Trim() ?? "";
        // Для реакций — список «эмодзи — роль», иначе непонятно, что ставить
        if (p.Style == RolePanelStyle.Reactions)
        {
            var lines = p.Options.Select(o => $"{o.Emoji} — {(string.IsNullOrWhiteSpace(o.Label) ? $"<@&{o.RoleId}>" : $"{Format.Sanitize(o.Label)} (<@&{o.RoleId}>)")}");
            text = string.Join("\n", new[] { text }.Concat(lines).Where(x => x.Length > 0));
        }
        var hint = Tx.Get(gid, p.Mode switch
        {
            RolePanelMode.Unique => "roles.hintUnique",
            RolePanelMode.Verify => "roles.hintVerify",
            _ => "roles.hintNormal"
        });
        if (hint.Length > 0) text += (text.Length > 0 ? "\n\n" : "") + $"-# {hint}";
        if (text.Length > 0) embed.WithDescription(text.Length <= 4000 ? text : text[..4000]);

        MessageComponent? comps = null;
        if (p.Style == RolePanelStyle.Buttons)
        {
            var b = new ComponentBuilder();
            foreach (var (o, i) in p.Options.Take(RolePanel.MaxOptions).Select((o, i) => (o, i)))
                b.WithButton(Trim(Label(o), 80), $"rp:b:{p.Id}:{o.RoleId}", ButtonStyle.Secondary,
                    LfgProjectStore.ToEmote(o.Emoji), row: i / 5);
            comps = b.Build();
        }
        else if (p.Style == RolePanelStyle.Dropdown && p.Options.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId($"rp:s:{p.Id}")
                .WithPlaceholder(Trim(string.IsNullOrWhiteSpace(p.Placeholder) ? Tx.Get(gid, "roles.placeholder") : p.Placeholder!, 150))
                .WithMinValues(p.Mode == RolePanelMode.Verify ? 1 : 0)
                .WithMaxValues(p.Mode == RolePanelMode.Unique ? 1 : Math.Min(p.Options.Count, 25));
            foreach (var o in p.Options.Take(25))
            {
                var opt = new SelectMenuOptionBuilder().WithLabel(Trim(Label(o), 100)).WithValue(o.RoleId.ToString());
                if (!string.IsNullOrWhiteSpace(o.Description)) opt.WithDescription(Trim(o.Description!, 100));
                if (LfgProjectStore.ToEmote(o.Emoji) is { } e) opt.WithEmote(e);
                menu.AddOption(opt);
            }
            comps = new ComponentBuilder().WithSelectMenu(menu).Build();
        }
        return (embed.Build(), comps);

        string Label(RolePanelOption o) =>
            !string.IsNullOrWhiteSpace(o.Label) ? o.Label! : client.GetGuild(gid)?.GetRole(o.RoleId)?.Name ?? "Role";
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Публикует панель в канал (старое сообщение панели удаляется).</summary>
    public async Task<string?> PostAsync(RolePanel panel, ulong channelId)
    {
        if (client.GetGuild(panel.GuildId)?.GetTextChannel(channelId) is not { } channel) return "Channel not found.";
        if (panel.Options.Count == 0) return "Add at least one role first.";

        var (embed, comps) = Render(panel);
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
        if (panel.Style == RolePanelStyle.Reactions) await AddReactionsAsync(message, panel);
        if (oldChannel is { } oc && oldMessage is { } om) await DeleteMessageAsync(panel.GuildId, oc, om);
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

        var (embed, comps) = Render(panel);
        try
        {
            await message.ModifyAsync(m =>
            {
                m.Embeds = new[] { embed };
                m.Components = comps ?? new ComponentBuilder().Build();
            });
            if (panel.Style == RolePanelStyle.Reactions) await AddReactionsAsync(message, panel);
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

    private async Task AddReactionsAsync(IUserMessage message, RolePanel panel)
    {
        foreach (var o in panel.Options)
        {
            if (LfgProjectStore.ToEmote(o.Emoji) is not { } emote) continue;
            try { await message.AddReactionAsync(emote); }
            catch (Discord.Net.HttpException ex) { logger.LogWarning(ex, "Can't add reaction {Emoji}", o.Emoji); }
        }
    }

    /// <summary>
    /// Нажатие на роль (кнопка или реакция): выдать / снять с учётом режима панели.
    /// add: true — только выдать (реакция поставлена), false — только снять (реакция убрана), null — переключить (кнопка).
    /// Возвращает текст для ответа.
    /// </summary>
    public async Task<string> PickAsync(RolePanel panel, SocketGuildUser member, ulong roleId, bool? add = null)
    {
        var gid = panel.GuildId;
        var guild = member.Guild;
        if (panel.Options.All(o => o.RoleId != roleId) || guild.GetRole(roleId) is not { } role) return Tx.Get(gid, "roles.gone");
        if (!CanManage(guild, role)) return Tx.Get(gid, "roles.cantManage", ("role", role.Mention));

        var has = member.Roles.Any(r => r.Id == roleId);
        var give = add ?? !has;
        try
        {
            if (give)
            {
                if (has) return Tx.Get(gid, "roles.already", ("role", role.Mention));
                // Unique: другие роли этой панели снимаются
                if (panel.Mode == RolePanelMode.Unique)
                {
                    var others = panel.Options.Select(o => o.RoleId).Where(id => id != roleId && member.Roles.Any(r => r.Id == id))
                        .Select(guild.GetRole).Where(r => CanManage(guild, r)).ToList();
                    if (others.Count > 0) await member.RemoveRolesAsync(others);
                }
                await member.AddRoleAsync(role);
                audit.FromDiscord(gid, member, "roles", "Took a role from a panel", $"{panel.Title}: @{role.Name}");
                return Tx.Get(gid, "roles.added", ("role", role.Mention));
            }

            if (!has) return Tx.Get(gid, "roles.notHave", ("role", role.Mention));
            if (panel.Mode == RolePanelMode.Verify) return Tx.Get(gid, "roles.verifyKeep", ("role", role.Mention));
            await member.RemoveRoleAsync(role);
            audit.FromDiscord(gid, member, "roles", "Removed a role via a panel", $"{panel.Title}: @{role.Name}");
            return Tx.Get(gid, "roles.removed", ("role", role.Mention));
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Role panel {Panel}: failed to change {Role}", panel.Id, roleId);
            return Tx.Get(gid, "roles.cantManage", ("role", role.Mention));
        }
    }

    /// <summary>Выпадающий список: выбранные роли панели — выдать, невыбранные — снять (в Verify только выдать).</summary>
    public async Task<string> SelectAsync(RolePanel panel, SocketGuildUser member, IReadOnlyCollection<ulong> selected)
    {
        var gid = panel.GuildId;
        var guild = member.Guild;
        var panelRoles = panel.Options.Select(o => o.RoleId).ToHashSet();
        var want = selected.Where(panelRoles.Contains).ToHashSet();
        var current = member.Roles.Select(r => r.Id).Where(panelRoles.Contains).ToHashSet();

        var toAdd = want.Except(current).Select(guild.GetRole).Where(r => CanManage(guild, r)).ToList();
        var toRemove = panel.Mode == RolePanelMode.Verify ? []
            : current.Except(want).Select(guild.GetRole).Where(r => CanManage(guild, r)).ToList();
        var blocked = want.Except(current).Select(guild.GetRole).Where(r => r is not null && !CanManage(guild, r)).ToList();

        try
        {
            if (toRemove.Count > 0) await member.RemoveRolesAsync(toRemove);
            if (toAdd.Count > 0) await member.AddRolesAsync(toAdd);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Role panel {Panel}: failed to set roles", panel.Id);
            return Tx.Get(gid, "roles.cantManage", ("role", string.Join(", ", toAdd.Concat(toRemove).Select(r => r!.Mention))));
        }

        if (toAdd.Count + toRemove.Count > 0)
            audit.FromDiscord(gid, member, "roles", "Changed roles via a panel",
                $"{panel.Title}: +{string.Join(", ", toAdd.Select(r => "@" + r!.Name))} −{string.Join(", ", toRemove.Select(r => "@" + r!.Name))}");

        var lines = new List<string>();
        if (toAdd.Count > 0) lines.Add(Tx.Get(gid, "roles.added", ("role", string.Join(", ", toAdd.Select(r => r!.Mention)))));
        if (toRemove.Count > 0) lines.Add(Tx.Get(gid, "roles.removed", ("role", string.Join(", ", toRemove.Select(r => r!.Mention)))));
        if (blocked.Count > 0) lines.Add(Tx.Get(gid, "roles.cantManage", ("role", string.Join(", ", blocked.Select(r => r!.Mention)))));
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
        if (store.FindPanelByMessage(messageId) is not { Style: RolePanelStyle.Reactions } panel) return Task.CompletedTask;

        _ = Task.Run(async () =>
        {
            try
            {
                        if (client.GetGuild(panel.GuildId)?.GetUser(reaction.UserId) is not { IsBot: false } member) return;
                var option = panel.Options.Find(o => SameEmote(o.Emoji, reaction.Emote));
                if (option is null) return;
                await PickAsync(panel, member, option.RoleId, added);

                // Unique: убрать остальные реакции этого человека на панели (нужно Manage Messages)
                if (added && panel.Mode == RolePanelMode.Unique && reaction.Channel is ITextChannel tc &&
                    await tc.GetMessageAsync(messageId) is IUserMessage msg)
                {
                    foreach (var other in panel.Options.Where(o => o != option))
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

    private static bool SameEmote(string? configured, IEmote emote)
    {
        if (LfgProjectStore.ToEmote(configured) is not { } e) return false;
        return e switch
        {
            Emote custom when emote is Emote other => custom.Id == other.Id,
            Emoji uni => uni.Name == emote.Name,
            _ => false
        };
    }
}
