using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Зашёл в канал-создатель → бот создаёт личную комнату и переносит туда.
/// Комната опустела → бот её удаляет.
/// </summary>
public sealed class TempVoiceService(
    DiscordSocketClient client,
    TempVoiceStore store,
    TempRoomManager rooms,
    ILogger<TempVoiceService> logger) : IHostedService
{
    // Все операции с голосовыми каналами по очереди — без гонок при массовых заходах
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateUpdated;
        client.ChannelDestroyed += OnChannelDestroyed;
        client.Ready += OnReady;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated -= OnVoiceStateUpdated;
        client.ChannelDestroyed -= OnChannelDestroyed;
        client.Ready -= OnReady;
        return Task.CompletedTask;
    }

    // Обработчики не блокируют gateway: работа уходит в фон
    private Task OnVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (before.VoiceChannel?.Id == after.VoiceChannel?.Id) return Task.CompletedTask; // мьют/стрим и т.п.
        _ = Task.Run(() => RunAsync(() => HandleMoveAsync(user, before.VoiceChannel, after.VoiceChannel)));
        return Task.CompletedTask;
    }

    private Task OnChannelDestroyed(SocketChannel channel)
    {
        store.RemoveRoom(channel.Id);
        store.RemoveCreator(channel.Id);
        return Task.CompletedTask;
    }

    private Task OnReady()
    {
        _ = Task.Run(() => RunAsync(CleanupAsync));
        return Task.CompletedTask;
    }

    private async Task RunAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Temp voice operation failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleMoveAsync(SocketUser user, SocketVoiceChannel? left, SocketVoiceChannel? joined)
    {
        if (joined is not null && user is SocketGuildUser member && !member.IsBot &&
            store.GetCreator(joined.Id) is { } creator && creator.GuildId == joined.Guild.Id &&
            store.GetRoom(joined.Id) is null)
            await CreateRoomAsync(member, joined, creator);

        if (left is not null)
            await DeleteIfEmptyAsync(left);
    }

    private async Task CreateRoomAsync(SocketGuildUser user, SocketVoiceChannel creatorChannel, TempVoiceCreator creator)
    {
        var guild = creatorChannel.Guild;

        // Уже есть своя комната — просто переносим туда
        if (store.FindRoomByOwner(guild.Id, user.Id) is { } own && guild.GetVoiceChannel(own.ChannelId) is { } existing)
        {
            await MoveAsync(user, existing.Id);
            return;
        }

        var botId = client.CurrentUser.Id;
        var overwrites = creatorChannel.PermissionOverwrites
            .Where(o => !(o.TargetType == PermissionTarget.User && (o.TargetId == user.Id || o.TargetId == botId)))
            .ToList();
        overwrites.Add(new Overwrite(user.Id, PermissionTarget.User, OwnerPermissions));
        overwrites.Add(new Overwrite(botId, PermissionTarget.User, BotPermissions));

        var name = BuildRoomName(creator.NameTemplate, creatorChannel.Name, user.DisplayName);

        var room = await guild.CreateVoiceChannelAsync(name, p =>
        {
            p.CategoryId = creatorChannel.CategoryId;
            p.PermissionOverwrites = new Optional<IEnumerable<Overwrite>>(overwrites);
            p.Bitrate = creatorChannel.Bitrate;
            if (creator.UserLimit is { } limit) p.UserLimit = limit;
        });

        store.AddRoom(new TempVoiceRoom
        {
            GuildId = guild.Id,
            ChannelId = room.Id,
            OwnerId = user.Id,
            CreatorChannelId = creatorChannel.Id
        });
        logger.LogInformation("Temp room {Room} created for {User}", name, user.Username);

        // Создатели вверху категории, комнаты сразу под ними
        await rooms.ReorderCategoryAsync(guild, creatorChannel.CategoryId, room.Id);

        // Панель управления — в текстовый чат самой комнаты
        if (room is IMessageChannel roomChat)
        {
            try
            {
                var (embed, comps) = TempRoomManager.Panel();
                await roomChat.SendMessageAsync($"<@{user.Id}>, this is your room.", embed: embed, components: comps,
                    allowedMentions: new AllowedMentions { UserIds = [user.Id] });
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogWarning(ex, "Could not post the control panel in room {Room}", name);
            }
        }

        try
        {
            await MoveAsync(user, room.Id);
        }
        catch (Discord.Net.HttpException)
        {
            // пользователь успел выйти из голоса — комната никому не нужна
            await room.DeleteAsync();
            store.RemoveRoom(room.Id);
        }
    }

    /// <summary>Удаляет только комнату, которую создал сам бот (есть в списке комнат), на её сервере и не создатель.</summary>
    private async Task DeleteIfEmptyAsync(SocketVoiceChannel channel)
    {
        if (store.GetRoom(channel.Id) is not { } room) return;
        if (room.GuildId != channel.Guild.Id) return;
        if (store.GetCreator(channel.Id) is not null) return;
        if (channel.ConnectedUsers.Count > 0) return;

        try
        {
            await channel.DeleteAsync();
        }
        catch (Discord.Net.HttpException)
        {
            // уже удалён вручную
        }
        store.RemoveRoom(channel.Id);
    }

    /// <summary>После перезапуска: удаляем пустые комнаты и забываем удалённые вручную.</summary>
    private async Task CleanupAsync()
    {
        foreach (var room in store.GetAllRooms())
        {
            var channel = client.GetGuild(room.GuildId)?.GetVoiceChannel(room.ChannelId);
            if (channel is null)
            {
                if (client.GetGuild(room.GuildId) is not null) store.RemoveRoom(room.ChannelId);
                continue;
            }
            await DeleteIfEmptyAsync(channel);
        }

        // Наводим порядок во всех категориях, где есть создатели
        foreach (var guild in client.Guilds)
        foreach (var categoryId in store.GetCreators(guild.Id)
                     .Select(c => guild.GetVoiceChannel(c.ChannelId)?.CategoryId)
                     .Distinct())
            await rooms.ReorderCategoryAsync(guild, categoryId);
    }

    /// <summary>
    /// Имя комнаты. Без шаблона: из названия создателя убирается «Create room» и обрамление
    /// (➕, -, |, пробелы), к остатку добавляется ник: «➕ Create room Aion 2» → «Aion 2 • Ник».
    /// Если от названия ничего не осталось — «Ник's room».
    /// </summary>
    public static string BuildRoomName(string? template, string creatorName, string userName)
    {
        string name;
        if (string.IsNullOrWhiteSpace(template))
        {
            var prefix = Regex.Replace(creatorName, @"create\s*room", " ", RegexOptions.IgnoreCase);
            prefix = Regex.Replace(TrimDecorations(prefix), @"\s{2,}", " ");
            name = prefix.Length > 0 ? $"{prefix} • {userName}" : $"{userName}'s room";
        }
        else
        {
            name = template.Replace("{user}", userName);
        }

        return name.Length <= 100 ? name : name[..100];
    }

    /// <summary>Убирает по краям всё, что не буква и не цифра.</summary>
    private static string TrimDecorations(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && !char.IsLetterOrDigit(value[start])) start++;
        while (end > start && !char.IsLetterOrDigit(value[end - 1])) end--;
        return value[start..end];
    }

    private static Task MoveAsync(IGuildUser user, ulong channelId) =>
        user.ModifyAsync(p => p.ChannelId = new Optional<ulong?>(channelId));

    /// <summary>Владелец может переименовать комнату, менять лимит и перемещать людей прямо в Discord.</summary>
    public static readonly OverwritePermissions OwnerPermissions = OverwritePermissions.InheritAll.Modify(
        viewChannel: PermValue.Allow,
        connect: PermValue.Allow,
        speak: PermValue.Allow,
        manageChannel: PermValue.Allow,
        moveMembers: PermValue.Allow);

    private static readonly OverwritePermissions BotPermissions = OverwritePermissions.InheritAll.Modify(
        viewChannel: PermValue.Allow,
        sendMessages: PermValue.Allow,
        embedLinks: PermValue.Allow,
        connect: PermValue.Allow,
        manageChannel: PermValue.Allow,
        moveMembers: PermValue.Allow);
}
