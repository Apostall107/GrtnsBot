using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

public sealed record RoomRef(SocketVoiceChannel Channel, TempVoiceRoom Room);

/// <summary>
/// Действия владельца с временной комнатой. Общие для команд /room и кнопок панели.
/// Работает только с комнатами из списка бота на текущем сервере.
/// </summary>
public sealed class TempRoomManager(TempVoiceStore store, DiscordSocketClient client, BotOwner botOwner, ILogger<TempRoomManager> logger)
{
    /// <summary>
    /// Порядок голосовых каналов в категории: сначала создатели, под ними временные комнаты (старые выше),
    /// затем остальные голосовые каналы в прежнем порядке. Меняются только позиции, ничего больше.
    /// </summary>
    /// <param name="newChannelIds">Только что созданные каналы этой категории, которых ещё может не быть в кэше.</param>
    public async Task ReorderCategoryAsync(SocketGuild guild, ulong? categoryId, params ulong[] newChannelIds)
    {
        try
        {
            var current = guild.VoiceChannels
                .Where(c => c.CategoryId == categoryId)
                .Select(c => (c.Id, c.Position))
                .ToList();
            foreach (var id in newChannelIds.Where(id => current.All(c => c.Id != id)))
                current.Add((id, int.MaxValue));

            if (current.Count < 2) return;

            var ordered = current.OrderBy(c => c.Position).ThenBy(c => c.Id).Select(c => c.Id).ToList();
            var creators = ordered.Where(id => store.GetCreator(id) is not null);
            var rooms = ordered.Where(id => store.GetRoom(id) is not null).OrderBy(id => id);
            var others = ordered.Where(id => store.GetCreator(id) is null && store.GetRoom(id) is null);
            var desired = creators.Concat(rooms).Concat(others).ToList();

            var basePosition = current.Where(c => c.Position != int.MaxValue).Select(c => c.Position).DefaultIfEmpty(0).Min();
            var needed = desired
                .Select((id, i) => (id, position: basePosition + i))
                .Where(x => current.First(c => c.Id == x.id).Position != x.position)
                .ToList();
            if (needed.Count == 0) return;

            await guild.ReorderChannelsAsync(desired.Select((id, i) => new ReorderChannelProperties(id, basePosition + i)));
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not reorder voice channels in category {Category}", categoryId);
        }
    }

    /// <summary>Комната, в которой сейчас сидит пользователь. Возвращает текст ошибки или null.</summary>
    public string? TryResolve(SocketGuild guild, IUser user, bool ownerOnly, out RoomRef? room)
    {
        room = null;
        var channel = guild.GetUser(user.Id)?.VoiceChannel ?? (user as SocketGuildUser)?.VoiceChannel;
        var stored = channel is null ? null : store.GetRoom(channel.Id);
        if (channel is null || stored is null || stored.GuildId != guild.Id)
            return "Join your temporary room first.";

        if (ownerOnly && stored.OwnerId != user.Id && !botOwner.Is(user.Id))
            return $"Only the room owner <@{stored.OwnerId}> can do this. If they left, use **Claim**.";

        room = new RoomRef(channel, stored);
        return null;
    }

    public async Task<string> RenameAsync(RoomRef r, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) return "Name must be 1–100 characters.";
        await r.Channel.ModifyAsync(p => p.Name = name);
        return $"Renamed to **{Format.Sanitize(name)}**. (Discord allows 2 renames per 10 minutes.)";
    }

    public async Task<string> LimitAsync(RoomRef r, int limit)
    {
        if (limit is < 0 or > 99) return "Limit must be from 0 to 99 (0 = no limit).";
        await r.Channel.ModifyAsync(p => p.UserLimit = limit == 0 ? (int?)null : limit);
        return limit == 0 ? "User limit removed." : $"User limit set to **{limit}**.";
    }

    public async Task<string> LockAsync(RoomRef r)
    {
        var channel = r.Channel;
        var everyone = channel.Guild.EveryoneRole;
        var everyoneOverwrite = channel.GetPermissionOverwrite(everyone) ?? OverwritePermissions.InheritAll;
        await channel.AddPermissionOverwriteAsync(everyone, everyoneOverwrite.Modify(connect: PermValue.Deny));

        // Разрешения ролей перекрывают @everyone — закрываем и их (только в этой комнате)
        foreach (var ow in channel.PermissionOverwrites.Where(o => o.TargetType == PermissionTarget.Role && o.TargetId != everyone.Id))
        {
            if (channel.Guild.GetRole(ow.TargetId) is { } role)
                await channel.AddPermissionOverwriteAsync(role, ow.Permissions.Modify(connect: PermValue.Deny));
        }

        // Те, кто уже внутри, смогут перезайти
        foreach (var user in channel.ConnectedUsers.Where(u => u.Id != r.Room.OwnerId))
        {
            var current = channel.GetPermissionOverwrite(user) ?? OverwritePermissions.InheritAll;
            await channel.AddPermissionOverwriteAsync(user, current.Modify(connect: PermValue.Allow));
        }

        store.UpdateRoom(channel.Id, room => room.Locked = true);
        return "🔒 Room locked. Permit someone to let them in.";
    }

    public async Task<string> UnlockAsync(RoomRef r)
    {
        var channel = r.Channel;
        var creator = channel.Guild.GetVoiceChannel(r.Room.CreatorChannelId);

        foreach (var ow in channel.PermissionOverwrites.Where(o => o.TargetType == PermissionTarget.Role))
        {
            if (channel.Guild.GetRole(ow.TargetId) is not { } role) continue;
            // Возвращаем Connect таким, каким он был у канала-создателя
            var original = creator?.GetPermissionOverwrite(role)?.Connect ?? PermValue.Inherit;
            await channel.AddPermissionOverwriteAsync(role, ow.Permissions.Modify(connect: original));
        }

        store.UpdateRoom(channel.Id, room => room.Locked = false);
        return "🔓 Room unlocked.";
    }

    public async Task<string> PermitAsync(RoomRef r, ulong targetId)
    {
        var target = await GetMemberAsync(r.Channel.Guild, targetId);
        if (target is null) return "That member is not on this server.";

        var current = r.Channel.GetPermissionOverwrite(target) ?? OverwritePermissions.InheritAll;
        await r.Channel.AddPermissionOverwriteAsync(target, current.Modify(viewChannel: PermValue.Allow, connect: PermValue.Allow));
        return $"<@{targetId}> can now join your room.";
    }

    public async Task<string> KickAsync(RoomRef r, ulong targetId, ulong callerId)
    {
        if (targetId == callerId) return "You can't kick yourself.";
        if (targetId == client.CurrentUser.Id) return "You can't kick the bot.";

        var target = await GetMemberAsync(r.Channel.Guild, targetId);
        if (target is null) return "That member is not on this server.";

        var current = r.Channel.GetPermissionOverwrite(target) ?? OverwritePermissions.InheritAll;
        await r.Channel.AddPermissionOverwriteAsync(target, current.Modify(connect: PermValue.Deny));

        // Отключаем только если человек сидит именно в этой комнате
        if (r.Channel.ConnectedUsers.Any(u => u.Id == targetId))
            await target.ModifyAsync(p => p.ChannelId = new Optional<ulong?>(null));

        return $"<@{targetId}> was kicked from your room.";
    }

    public async Task<string> ClaimAsync(RoomRef r, IUser caller)
    {
        if (r.Room.OwnerId == caller.Id) return "You already own this room.";
        if (r.Channel.ConnectedUsers.Any(u => u.Id == r.Room.OwnerId))
            return $"The owner <@{r.Room.OwnerId}> is still here.";

        if (await client.Rest.GetUserAsync(r.Room.OwnerId) is { } oldOwner)
            await r.Channel.RemovePermissionOverwriteAsync(oldOwner);
        await r.Channel.AddPermissionOverwriteAsync(caller, TempVoiceService.OwnerPermissions);
        store.UpdateRoom(r.Channel.Id, room => room.OwnerId = caller.Id);
        return "👑 You are now the owner of this room.";
    }

    private async Task<IGuildUser?> GetMemberAsync(SocketGuild guild, ulong userId) =>
        guild.GetUser(userId) as IGuildUser ?? await client.Rest.GetGuildUserAsync(guild.Id, userId);

    // ---------- Панель управления ----------

    public static (Embed Embed, MessageComponent Components) Panel()
    {
        var embed = new EmbedBuilder()
            .WithTitle("🎛️ Room controls")
            .WithColor(Color.Teal)
            .WithDescription(
                "Use these while you are **in your temporary room**. Only the room owner can use them, except **Claim**.\n\n" +
                "✏️ **Rename** · 👥 **Limit** (0 = no limit) · 🔒 **Lock / Unlock** — when locked, only people inside " +
                "or permitted can join · 👑 **Claim** — take the room if the owner left\n" +
                "✅ **Permit** a member to join even when locked · 🚫 **Kick** a member and block them from rejoining")
            .Build();

        var permit = new SelectMenuBuilder()
            .WithCustomId("room:permit")
            .WithType(ComponentType.UserSelect)
            .WithPlaceholder("✅ Permit a member")
            .WithMinValues(1)
            .WithMaxValues(1);

        var kick = new SelectMenuBuilder()
            .WithCustomId("room:kick")
            .WithType(ComponentType.UserSelect)
            .WithPlaceholder("🚫 Kick a member")
            .WithMinValues(1)
            .WithMaxValues(1);

        var comps = new ComponentBuilder()
            .WithButton("Rename", "room:rename", ButtonStyle.Secondary, new Emoji("✏️"), row: 0)
            .WithButton("Limit", "room:limit", ButtonStyle.Secondary, new Emoji("👥"), row: 0)
            .WithButton("Lock / Unlock", "room:lock", ButtonStyle.Secondary, new Emoji("🔒"), row: 0)
            .WithButton("Claim", "room:claim", ButtonStyle.Primary, new Emoji("👑"), row: 0)
            .WithSelectMenu(permit, row: 1)
            .WithSelectMenu(kick, row: 2)
            .Build();

        return (embed, comps);
    }
}
