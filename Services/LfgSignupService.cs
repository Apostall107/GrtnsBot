using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Общая логика записи и обновления поста сбора: используется на сервере, из ЛС,
/// в меню организатора и для напоминаний.
/// </summary>
public sealed class LfgSignupService(DiscordSocketClient client, LfgStore store, CharacterStore characters, LfgProjectStore projects,
    ILogger<LfgSignupService> logger)
{
    /// <summary>Запись в слот или в резерв + сохранение + обновление поста.</summary>
    public async Task<string> JoinAsync(LfgGroup group, ulong userId, SlotRole role, string characterName)
    {
        if (ProjectBlock(group, userId) is { } blocked) return blocked;
        await group.Gate.WaitAsync();
        try
        {
            // Требования сбора: GS не хватает — только резерв; роль проверяется по слоту (у слота может быть своя).
            // Организатор может перевести из резерва в группу в любом случае.
            var gsUnmet = GsUnmet(group, userId, characterName);
            var fits = SlotFilter(group, userId);
            var error = group.TryJoin(userId, role, characterName, out var reserved, forceReserve: gsUnmet is not null, fits: fits);
            if (error is not null) return error;
            ApplyProfile(group, userId);

            store.SaveGroups();
            await RefreshPostAsync(group);

            var name = Format.Sanitize(characterName);
            var activity = Format.Sanitize(group.ActivityName);
            var gid = group.GuildId;
            var roleText = $"{role.Emoji()} **{role.Label()}**";
            if (!reserved)
                return Tx.Get(gid, "lfg.sign.joined", ("activity", activity), ("role", roleText), ("name", name));

            var position = group.Reserve.FindIndex(r => r.UserId == userId) + 1;
            var unmet = gsUnmet ?? RoleUnmet(group, role, fits);
            if (unmet is not null && group.Slots.All(s => s.UserId != userId))
                return Tx.Get(gid, "lfg.sign.reserveRequirements", ("requirements", unmet), ("position", position),
                    ("activity", activity), ("name", name));
            return Tx.Get(gid, "lfg.sign.reserveFull", ("role", roleText), ("position", position), ("activity", activity), ("name", name));
        }
        finally
        {
            group.Gate.Release();
        }
    }

    /// <summary>
    /// Подставляет в слот/резерв класс-роль и заметку выбранного персонажа (из /character или меню после записи).
    /// Роль показывается, только если она всё ещё есть у игрока. Вызывать под group.Gate.
    /// </summary>
    public void ApplyProfile(LfgGroup group, ulong userId)
    {
        var slot = group.Slots.Find(s => s.UserId == userId);
        var entry = slot is null ? group.FindReserve(userId) : null;
        var name = slot?.CharacterName ?? entry?.CharacterName;
        var info = characters.GetInfo(group.GuildId, userId, name, Scope(group));

        var guild = client.GetGuild(group.GuildId);
        var role = info?.ClassRoleId is { } roleId ? guild?.GetRole(roleId) : null;
        if (role is not null && guild?.GetUser(userId) is { } member && member.Roles.All(r => r.Id != role.Id)) role = null;

        var icon = projects.ClassIcon(group.GuildId, role, group.ProjectKey);
        if (slot is not null)
        {
            slot.ClassRoleId = role?.Id;
            slot.ClassIcon = icon;
            slot.Comment = info?.Comment;
            slot.Gs = info?.Gs;
        }
        else if (entry is not null)
        {
            entry.ClassRoleId = role?.Id;
            entry.ClassIcon = icon;
            entry.Comment = info?.Comment;
            entry.Gs = info?.Gs;
        }
    }

    /// <summary>Не хватает GS персонажа (текст требования) или null.</summary>
    public string? GsUnmet(LfgGroup group, ulong userId, string? characterName)
    {
        if (userId == group.OwnerId || group.MinGs is not { } min) return null;
        return (characters.GetInfo(group.GuildId, userId, characterName, Scope(group))?.Gs ?? 0) < min
            ? Tx.Get(group.GuildId, "lfg.sign.gsRequirement", ("gs", min))
            : null;
    }

    /// <summary>
    /// Под какие слоты игрок подходит по роли: у слота своя требуемая роль или общие роли сбора (любая из).
    /// null — под все (требований роли нет или это организатор).
    /// </summary>
    public Func<Slot, bool>? SlotFilter(LfgGroup group, ulong userId)
    {
        if (userId == group.OwnerId || (group.RequiredRoleIds.Count == 0 && group.Slots.All(s => s.Requirement == SlotRequirement.Inherit)))
            return null;
        var member = client.GetGuild(group.GuildId)?.GetUser(userId);
        var mine = member?.Roles.Select(r => r.Id).ToHashSet() ?? new HashSet<ulong>();
        return s =>
        {
            var need = group.RequiredFor(s);
            return need.Count == 0 || need.Any(mine.Contains);
        };
    }

    /// <summary>
    /// Игрок попал в резерв, хотя свободный слот его роли есть — значит, не подошёл по требуемой роли слота.
    /// Текст: роли этих слотов (любая из), иначе null (просто нет мест).
    /// </summary>
    private static string? RoleUnmet(LfgGroup group, SlotRole wanted, Func<Slot, bool>? fits)
    {
        if (fits is null || group.FindFreeSlot(wanted) < 0) return null;
        var roles = group.Slots.Where(s => s.IsFree && !fits(s) && (wanted == SlotRole.Any || s.Role == wanted || s.Role == SlotRole.Any))
            .SelectMany(s => group.RequiredFor(s)).Distinct().ToList();
        return roles.Count == 0 ? null : string.Join(" / ", roles.Select(id => $"<@&{id}>"));
    }

    /// <summary>Перенос времени сбора: новое время, автоудаление, напоминание, пост и ЛС всем записавшимся.</summary>
    public async Task<string?> RescheduleAsync(LfgGroup group, DateTimeOffset? startsAt, TimeService time, IUser by)
    {
        List<ulong> members;
        await group.Gate.WaitAsync();
        try
        {
            if (group.IsClosed) return Tx.Get(group.GuildId, "lfg.err.groupClosed");
            group.StartsAt = startsAt;
            group.ExpiresAt = time.ExpiresAt(startsAt);
            group.Reminded = !time.ReminderNeeded(startsAt);
            store.SaveGroups();
            await RefreshPostAsync(group);
            members = group.Slots.Where(s => s.UserId is not null).Select(s => s.UserId!.Value)
                .Concat(group.Reserve.Select(r => r.UserId)).Where(id => id != by.Id).Distinct().ToList();
        }
        finally
        {
            group.Gate.Release();
        }

        var gid = group.GuildId;
        var when = startsAt is { } t ? LfgRenderer.FormatTime(t) : Tx.Get(gid, "lfg.noTime");
        var embed = new EmbedBuilder()
            .WithTitle(Tx.Get(gid, "lfg.dm.timeChangedTitle", ("activity", group.ActivityName)))
            .WithDescription(Tx.Get(gid, "lfg.dm.timeChanged", ("user", $"<@{by.Id}>"), ("time", when)))
            .WithColor(Tx.Color(gid, "color.lfg.timeChanged"))
            .Build();
        var buttons = new ComponentBuilder().WithButton(Tx.Get(gid, "lfg.dm.openGroup"), style: ButtonStyle.Link, url: JumpUrl(group)).Build();
        _ = Task.Run(async () =>
        {
            foreach (var id in members)
            {
                try
                {
                    var user = client.GetUser(id) as IUser ?? await client.Rest.GetUserAsync(id);
                    if (user is not null) await user.SendMessageAsync(embed: embed, components: buttons);
                    await Task.Delay(1000);
                }
                catch (Discord.Net.HttpException)
                {
                    // закрытые ЛС
                }
            }
        });
        return null;
    }

    /// <summary>Ключ хранения персонажей проекта этого сбора.</summary>
    public string? Scope(LfgGroup group) => projects.Scope(group.GuildId, group.ProjectKey);

    /// <summary>Нет роли проекта сбора — записаться (и выбрать персонажа этого проекта) нельзя. null — можно.</summary>
    public string? ProjectBlock(LfgGroup group, ulong userId)
    {
        var project = projects.Effective(group.GuildId, group.ProjectKey);
        if (LfgProjectStore.CanUse(client.GetGuild(group.GuildId)?.GetUser(userId), project)) return null;
        return Tx.Get(group.GuildId, "lfg.sign.projectRole", ("project", project!.Name),
            ("roles", string.Join(Tx.Get(group.GuildId, "lfg.or"), project.RoleIds.Select(id => $"<@&{id}>"))));
    }

    /// <summary>Юникод-эмодзи роли (иконка-эмодзи из настроек роли), если есть.</summary>
    public static string? IconOf(IRole? role) =>
        role?.Emoji is { } e && !string.IsNullOrWhiteSpace(e.Name) ? e.Name : null;

    /// <summary>Перерисовать пост сбора (и права голоса в комнате сбора). Вызывать под group.Gate.</summary>
    public async Task RefreshPostAsync(LfgGroup group)
    {
        await SyncVoiceAsync(group);
        if (await GetChannelAsync(group.ChannelId) is not { } channel) return;
        try
        {
            var s = LfgRenderer.Group(group);
            await channel.ModifyMessageAsync(group.MessageId, m =>
            {
                m.Embeds = new[] { s.Embed! };
                m.Components = s.Components;
            });
            // Копии поста в других каналах
            foreach (var mirror in group.Mirrors.ToList())
            {
                try
                {
                    if (await GetChannelAsync(mirror.ChannelId) is { } mc)
                        await mc.ModifyMessageAsync(mirror.MessageId, m =>
                        {
                            m.Embeds = new[] { s.Embed! };
                            m.Components = s.Components;
                        });
                }
                catch (Discord.Net.HttpException)
                {
                    group.Mirrors.Remove(mirror); // копию удалили вручную
                }
            }
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not update LFG post {GroupId}", group.Id);
        }
    }

    /// <summary>ЛС человеку, которого организатор перевёл из резерва в группу.</summary>
    public async Task NotifyPromotedAsync(LfgGroup group, ulong userId, Slot slot)
    {
        try
        {
            var user = client.GetUser(userId) as IUser ?? await client.Rest.GetUserAsync(userId);
            if (user is null) return;

            var gid = group.GuildId;
            var role = slot.TakenAs ?? slot.Role;
            var sb = new System.Text.StringBuilder();
            sb.AppendText(Tx.Get(gid, "lfg.dm.promoted", ("user", $"<@{group.OwnerId}>"),
                ("role", $"{role.Emoji()} **{role.Label()}**"), ("name", Format.Sanitize(slot.CharacterName ?? "-"))));
            if (group.StartsAt is { } startsAt)
                sb.AppendText(Tx.Get(gid, "lfg.post.starts", ("time", LfgRenderer.FormatTime(startsAt))));

            var embed = new EmbedBuilder()
                .WithTitle(Tx.Get(gid, "lfg.dm.promotedTitle", ("activity", group.ActivityName)))
                .WithDescription(sb.ToString())
                .WithColor(Tx.Color(gid, "color.lfg.promoted"))
                .Build();
            var buttons = new ComponentBuilder()
                .WithButton(Tx.Get(gid, "lfg.dm.openGroup"), style: ButtonStyle.Link, url: JumpUrl(group))
                .Build();

            await user.SendMessageAsync(embed: embed, components: buttons);
        }
        catch (Discord.Net.HttpException)
        {
            // закрытые ЛС — пропускаем
        }
    }

    /// <summary>Напоминание за 10 минут: ответ на пост сбора с упоминанием участников.</summary>
    public async Task SendReminderAsync(LfgGroup group)
    {
        await group.Gate.WaitAsync();
        try
        {
            if (group.Reminded || group.IsClosed) return;
            group.Reminded = true;

            var members = group.Slots.Where(s => s.UserId is not null).Select(s => s.UserId!.Value).ToList();
            if (members.Count > 0 && group.StartsAt is { } startsAt &&
                await GetChannelAsync(group.ChannelId) is { } channel)
            {
                var mentions = string.Join(" ", members.Select(id => $"<@{id}>"));
                var message = await channel.SendMessageAsync(
                    Tx.Get(group.GuildId, "lfg.reminder", ("activity", Format.Sanitize(group.ActivityName)),
                        ("time", $"<t:{startsAt.ToUnixTimeSeconds()}:R>"), ("members", mentions)),
                    allowedMentions: new AllowedMentions { UserIds = members },
                    messageReference: new MessageReference(group.MessageId, failIfNotExists: false));
                group.ReminderMessageId = message.Id;
            }

            store.SaveGroups();
        }
        finally
        {
            group.Gate.Release();
        }
    }

    /// <summary>
    /// Голосовая комната сбора: видят и заходят все (как у категории), говорить могут только участники группы.
    /// Создаёт участник группы или организатор. Удаляется вместе с постом.
    /// </summary>
    public async Task<string> CreateVoiceAsync(LfgGroup group, IUser by)
    {
        await group.Gate.WaitAsync();
        try
        {
            var gid = group.GuildId;
            if (group.IsClosed) return Tx.Get(gid, "lfg.err.groupClosed");
            var guild = client.GetGuild(group.GuildId);
            if (guild is null) return "Server not found.";
            if (group.VoiceChannelId is { } existing && guild.GetVoiceChannel(existing) is not null)
                return Tx.Get(gid, "lfg.voice.exists", ("channel", $"<#{existing}>"));
            if (by.Id != group.OwnerId && group.Slots.All(s => s.UserId != by.Id))
                return Tx.Get(gid, "lfg.voice.onlyMembers");

            var postChannel = guild.GetTextChannel(group.ChannelId);
            var name = $"🔊 {group.ActivityName}";
            if (name.Length > 100) name = name[..100];
            // Категория: заданная в админке (проекта или общая), иначе категория канала с постом
            var category = projects.EffectiveVoiceCategory(group.GuildId, group.ProjectKey) is { } chosen && guild.GetCategoryChannel(chosen) is not null
                ? chosen
                : postChannel?.CategoryId;
            var channel = await guild.CreateVoiceChannelAsync(name, p =>
            {
                if (category is { } cat) p.CategoryId = cat;
                p.PermissionOverwrites = new Optional<IEnumerable<Overwrite>>(VoiceOverwrites(guild, group));
            });
            group.VoiceChannelId = channel.Id;
            store.SaveGroups();
            await RefreshPostAsync(group);
            return Tx.Get(group.GuildId, "lfg.voice.created", ("channel", $"<#{channel.Id}>"));
        }
        catch (Discord.Net.HttpException)
        {
            return Tx.Get(group.GuildId, "lfg.voice.failed");
        }
        finally
        {
            group.Gate.Release();
        }
    }

    /// <summary>@everyone: видеть и заходить, без голоса; участники группы и организатор — с голосом.</summary>
    private List<Overwrite> VoiceOverwrites(SocketGuild guild, LfgGroup group)
    {
        var speaker = OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Allow, connect: PermValue.Allow,
            speak: PermValue.Allow, stream: PermValue.Allow, useVoiceActivation: PermValue.Allow);
        var list = new List<Overwrite>
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role,
                OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Allow, connect: PermValue.Allow,
                    speak: PermValue.Deny, stream: PermValue.Deny)),
            new(client.CurrentUser.Id, PermissionTarget.User, speaker.Modify(manageChannel: PermValue.Allow, moveMembers: PermValue.Allow))
        };
        foreach (var id in group.Slots.Where(s => s.UserId is not null).Select(s => s.UserId!.Value).Append(group.OwnerId).Distinct())
            list.Add(new Overwrite(id, PermissionTarget.User, speaker));
        return list;
    }

    /// <summary>Состав изменился — голос в комнате сбора только у текущих участников. Вызывать под group.Gate.</summary>
    private async Task SyncVoiceAsync(LfgGroup group)
    {
        if (group.VoiceChannelId is not { } id || client.GetGuild(group.GuildId) is not { } guild ||
            guild.GetVoiceChannel(id) is not { } channel) return;
        try
        {
            var wanted = group.Slots.Where(s => s.UserId is not null).Select(s => s.UserId!.Value).Append(group.OwnerId).ToHashSet();
            foreach (var ow in channel.PermissionOverwrites.Where(o => o.TargetType == PermissionTarget.User &&
                                                                     o.TargetId != client.CurrentUser.Id && !wanted.Contains(o.TargetId)))
                if (guild.GetUser(ow.TargetId) is { } gone) await channel.RemovePermissionOverwriteAsync(gone);
            foreach (var userId in wanted.Where(u => channel.PermissionOverwrites.All(o => o.TargetId != u)))
                if (guild.GetUser(userId) is { } member)
                    await channel.AddPermissionOverwriteAsync(member, OverwritePermissions.InheritAll.Modify(
                        viewChannel: PermValue.Allow, connect: PermValue.Allow, speak: PermValue.Allow,
                        stream: PermValue.Allow, useVoiceActivation: PermValue.Allow));
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not sync the voice room of group {GroupId}", group.Id);
        }
    }

    /// <summary>Удалить пост сбора и напоминание (только сообщения самого бота).</summary>
    public async Task DeleteMessagesAsync(LfgGroup group)
    {
        foreach (var mirror in group.Mirrors)
        {
            try
            {
                if (await GetChannelAsync(mirror.ChannelId) is { } mc) await mc.DeleteMessageAsync(mirror.MessageId);
            }
            catch (Discord.Net.HttpException) { /* уже удалена */ }
        }
        if (group.VoiceChannelId is { } voiceId && client.GetGuild(group.GuildId)?.GetVoiceChannel(voiceId) is { } voice)
        {
            try { await voice.DeleteAsync(); }
            catch (Discord.Net.HttpException) { /* уже удалена */ }
        }

        if (await GetChannelAsync(group.ChannelId) is not { } channel) return;
        foreach (var id in new[] { (ulong?)group.MessageId, group.ReminderMessageId })
        {
            if (id is not { } messageId || messageId == 0) continue;
            try { await channel.DeleteMessageAsync(messageId); }
            catch (Discord.Net.HttpException) { /* уже удалено вручную */ }
        }
    }

    public static string JumpUrl(LfgGroup g) => $"https://discord.com/channels/{g.GuildId}/{g.ChannelId}/{g.MessageId}";

    private async Task<IMessageChannel?> GetChannelAsync(ulong channelId)
    {
        if (client.GetChannel(channelId) is IMessageChannel cached) return cached;
        try
        {
            return await client.Rest.GetChannelAsync(channelId) as IMessageChannel;
        }
        catch (Discord.Net.HttpException)
        {
            return null;
        }
    }
}
