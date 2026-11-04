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
    /// Порядок голосовых каналов в категории: свои каналы остаются, как их расставил админ; временные комнаты —
    /// по настройке своего создателя: под ним, над ним, в начале или в конце категории (старые комнаты выше).
    /// Меняются только позиции, ничего больше.
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
            var fixedChannels = ordered.Where(id => store.GetRoom(id) is null).ToList();
            var rooms = ordered.Where(id => store.GetRoom(id) is not null).OrderBy(id => id).ToList();

            // Куда идёт комната: создатель в этой же категории — над/под ним, иначе в начало/конец
            var top = new List<ulong>();
            var bottom = new List<ulong>();
            var above = new Dictionary<ulong, List<ulong>>();
            var below = new Dictionary<ulong, List<ulong>>();
            foreach (var roomId in rooms)
            {
                var creatorId = store.GetRoom(roomId)!.CreatorChannelId;
                var placement = store.GetCreator(creatorId)?.Placement ?? TempRoomPlacement.Below;
                var nearCreator = fixedChannels.Contains(creatorId);
                switch (placement)
                {
                    case TempRoomPlacement.Above when nearCreator:
                        (above.TryGetValue(creatorId, out var a) ? a : above[creatorId] = []).Add(roomId);
                        break;
                    case TempRoomPlacement.Below when nearCreator:
                        (below.TryGetValue(creatorId, out var b) ? b : below[creatorId] = []).Add(roomId);
                        break;
                    case TempRoomPlacement.Top or TempRoomPlacement.Above:
                        top.Add(roomId);
                        break;
                    default:
                        bottom.Add(roomId);
                        break;
                }
            }

            var desired = new List<ulong>(top);
            foreach (var id in fixedChannels)
            {
                if (above.TryGetValue(id, out var up)) desired.AddRange(up);
                desired.Add(id);
                if (below.TryGetValue(id, out var down)) desired.AddRange(down);
            }
            desired.AddRange(bottom);

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
            return Tx.Get(guild.Id, "room.joinFirst");

        if (ownerOnly && stored.OwnerId != user.Id && !botOwner.Is(user.Id))
            return Tx.Get(guild.Id, "room.onlyOwner", ("owner", $"<@{stored.OwnerId}>"));

        room = new RoomRef(channel, stored);
        return null;
    }

    public async Task<string> RenameAsync(RoomRef r, string name)
    {
        name = name.Trim();
        var gid = r.Channel.Guild.Id;
        if (name.Length is 0 or > 100) return Tx.Get(gid, "room.badName");
        await r.Channel.ModifyAsync(p => p.Name = name);
        return Tx.Get(gid, "room.renamed", ("name", Format.Sanitize(name)));
    }

    public async Task<string> LimitAsync(RoomRef r, int limit)
    {
        var gid = r.Channel.Guild.Id;
        if (limit is < 0 or > 99) return Tx.Get(gid, "room.badLimit");
        await r.Channel.ModifyAsync(p => p.UserLimit = limit == 0 ? (int?)null : limit);
        return limit == 0 ? Tx.Get(gid, "room.limitRemoved") : Tx.Get(gid, "room.limitSet", ("limit", limit));
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
        return Tx.Get(channel.Guild.Id, "room.locked");
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
        return Tx.Get(channel.Guild.Id, "room.unlocked");
    }

    public async Task<string> PermitAsync(RoomRef r, ulong targetId)
    {
        var target = await GetMemberAsync(r.Channel.Guild, targetId);
        if (target is null) return Tx.Get(r.Channel.Guild.Id, "room.notMember");

        var current = r.Channel.GetPermissionOverwrite(target) ?? OverwritePermissions.InheritAll;
        await r.Channel.AddPermissionOverwriteAsync(target, current.Modify(viewChannel: PermValue.Allow, connect: PermValue.Allow));
        return Tx.Get(r.Channel.Guild.Id, "room.permitted", ("user", $"<@{targetId}>"));
    }

    public async Task<string> KickAsync(RoomRef r, ulong targetId, ulong callerId)
    {
        var gid = r.Channel.Guild.Id;
        if (targetId == callerId) return Tx.Get(gid, "room.kickSelf");
        if (targetId == client.CurrentUser.Id) return Tx.Get(gid, "room.kickBot");

        var target = await GetMemberAsync(r.Channel.Guild, targetId);
        if (target is null) return Tx.Get(gid, "room.notMember");

        var current = r.Channel.GetPermissionOverwrite(target) ?? OverwritePermissions.InheritAll;
        await r.Channel.AddPermissionOverwriteAsync(target, current.Modify(connect: PermValue.Deny));

        // Отключаем только если человек сидит именно в этой комнате
        if (r.Channel.ConnectedUsers.Any(u => u.Id == targetId))
            await target.ModifyAsync(p => p.ChannelId = new Optional<ulong?>(null));

        return Tx.Get(gid, "room.kicked", ("user", $"<@{targetId}>"));
    }

    public async Task<string> ClaimAsync(RoomRef r, IUser caller)
    {
        var gid = r.Channel.Guild.Id;
        if (r.Room.OwnerId == caller.Id) return Tx.Get(gid, "room.alreadyOwner");
        if (r.Channel.ConnectedUsers.Any(u => u.Id == r.Room.OwnerId))
            return Tx.Get(gid, "room.ownerHere", ("owner", $"<@{r.Room.OwnerId}>"));

        if (await client.Rest.GetUserAsync(r.Room.OwnerId) is { } oldOwner)
            await r.Channel.RemovePermissionOverwriteAsync(oldOwner);
        await r.Channel.AddPermissionOverwriteAsync(caller, TempVoiceService.OwnerPermissions);
        store.UpdateRoom(r.Channel.Id, room => room.OwnerId = caller.Id);
        return Tx.Get(gid, "room.claimed");
    }

    private async Task<IGuildUser?> GetMemberAsync(SocketGuild guild, ulong userId) =>
        guild.GetUser(userId) as IGuildUser ?? await client.Rest.GetGuildUserAsync(guild.Id, userId);

    // ---------- Панель управления ----------

    /// <summary>Тексты по умолчанию — <see cref="PanelDefs.Rooms"/>, свои — из веб-админки (PanelTextStore).</summary>
    public static (Embed Embed, MessageComponent Components) Panel(PanelText? text = null)
    {
        var def = PanelDefs.Rooms;
        var embed = new EmbedBuilder()
            .WithSiteTitle(def.Title(text))
            .WithColor(def.Color(text))
            .WithDescription(def.Description(text))
            .WithSiteField()
            .Build();

        var permit = new SelectMenuBuilder()
            .WithCustomId("room:permit")
            .WithType(ComponentType.UserSelect)
            .WithPlaceholder(def.Label(text, "permit"))
            .WithMinValues(1)
            .WithMaxValues(1);

        var kick = new SelectMenuBuilder()
            .WithCustomId("room:kick")
            .WithType(ComponentType.UserSelect)
            .WithPlaceholder(def.Label(text, "kick"))
            .WithMinValues(1)
            .WithMaxValues(1);

        var comps = new ComponentBuilder()
            .WithButton(def.Label(text, "rename"), "room:rename", ButtonStyle.Secondary, new Emoji("✏️"), row: 0)
            .WithButton(def.Label(text, "limit"), "room:limit", ButtonStyle.Secondary, new Emoji("👥"), row: 0)
            .WithButton(def.Label(text, "lock"), "room:lock", ButtonStyle.Secondary, new Emoji("🔒"), row: 0)
            .WithButton(def.Label(text, "claim"), "room:claim", ButtonStyle.Primary, new Emoji("👑"), row: 0)
            .WithSiteButton(row: 0)
            .WithSelectMenu(permit, row: 1)
            .WithSelectMenu(kick, row: 2)
            .Build();

        return (embed, comps);
    }
}
