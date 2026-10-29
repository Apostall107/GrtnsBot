using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Общая логика записи и обновления поста сбора: используется на сервере, из ЛС,
/// в меню организатора и для напоминаний.
/// </summary>
public sealed class LfgSignupService(DiscordSocketClient client, LfgStore store, CharacterStore characters, ILogger<LfgSignupService> logger)
{
    /// <summary>Запись в слот или в резерв + сохранение + обновление поста.</summary>
    public async Task<string> JoinAsync(LfgGroup group, ulong userId, SlotRole role, string characterName)
    {
        await group.Gate.WaitAsync();
        try
        {
            var error = group.TryJoin(userId, role, characterName, out var reserved);
            if (error is not null) return error;
            ApplyProfile(group, userId);

            store.SaveGroups();
            await RefreshPostAsync(group);

            var name = Format.Sanitize(characterName);
            var activity = Format.Sanitize(group.ActivityName);
            if (!reserved)
                return $"✅ Signed up for **{activity}**: {role.Emoji()} **{role.Label()}** — **{name}**";

            var position = group.Reserve.FindIndex(r => r.UserId == userId) + 1;
            return $"🕓 No free slot for {role.Emoji()} **{role.Label()}** — you are on the **reserve** list " +
                   $"(#{position}) for **{activity}** as **{name}**. The organizer can move you into the group; " +
                   "you'll get a DM if that happens.";
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
        var info = characters.GetInfo(group.GuildId, userId, name);

        var guild = client.GetGuild(group.GuildId);
        var role = info?.ClassRoleId is { } roleId ? guild?.GetRole(roleId) : null;
        if (role is not null && guild?.GetUser(userId) is { } member && member.Roles.All(r => r.Id != role.Id)) role = null;

        var icon = IconOf(role);
        if (slot is not null)
        {
            slot.ClassRoleId = role?.Id;
            slot.ClassIcon = icon;
            slot.Comment = info?.Comment;
        }
        else if (entry is not null)
        {
            entry.ClassRoleId = role?.Id;
            entry.ClassIcon = icon;
            entry.Comment = info?.Comment;
        }
    }

    /// <summary>Юникод-эмодзи роли (иконка-эмодзи из настроек роли), если есть.</summary>
    public static string? IconOf(IRole? role) =>
        role?.Emoji is { } e && !string.IsNullOrWhiteSpace(e.Name) ? e.Name : null;

    /// <summary>Перерисовать пост сбора. Вызывать под group.Gate.</summary>
    public async Task RefreshPostAsync(LfgGroup group)
    {
        if (await GetChannelAsync(group.ChannelId) is not { } channel) return;
        try
        {
            var s = LfgRenderer.Group(group);
            await channel.ModifyMessageAsync(group.MessageId, m =>
            {
                m.Embeds = new[] { s.Embed! };
                m.Components = s.Components;
            });
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

            var role = slot.TakenAs ?? slot.Role;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"<@{group.OwnerId}> moved you from the reserve into the group.");
            sb.AppendLine($"Role: {role.Emoji()} **{role.Label()}** — **{Format.Sanitize(slot.CharacterName ?? "-")}**");
            if (group.StartsAt is { } startsAt)
                sb.AppendLine($"🕒 Starts: {LfgRenderer.FormatTime(startsAt)}");

            var embed = new EmbedBuilder()
                .WithTitle($"✅ You're in: {group.ActivityName}")
                .WithDescription(sb.ToString())
                .WithColor(Color.Green)
                .Build();
            var buttons = new ComponentBuilder()
                .WithButton("Open the group", style: ButtonStyle.Link, url: JumpUrl(group))
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
                    $"⏰ **{Format.Sanitize(group.ActivityName)}** starts <t:{startsAt.ToUnixTimeSeconds()}:R>! {mentions}\n" +
                    "-# Organizer: press 📋 Invites under the post for the /invite commands.",
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

    /// <summary>Удалить пост сбора и напоминание (только сообщения самого бота).</summary>
    public async Task DeleteMessagesAsync(LfgGroup group)
    {
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
