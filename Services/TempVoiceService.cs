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
    RoomPrefsStore prefs,
    ILogger<TempVoiceService> logger) : IHostedService
{
    // Создание комнат — по очереди (без гонок при массовых заходах). Удаление идёт мимо очереди:
    // пустая комната не должна ждать, пока Discord разрешит переставить каналы.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Одна операция дольше этого не держит очередь (rate limit Discord и т.п.) — дальше она доделывается сама
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(40);
    // Страховка: раз в минуту удаляются пустые комнаты, даже если событие выхода потерялось (переподключение и т.п.)
    private static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(60);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, DateTime> _emptySince = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _orderedOnce;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated += OnVoiceStateUpdated;
        client.ChannelDestroyed += OnChannelDestroyed;
        client.Ready += OnReady;
        _ = Task.Run(SweepLoopAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated -= OnVoiceStateUpdated;
        client.ChannelDestroyed -= OnChannelDestroyed;
        client.Ready -= OnReady;
        _stop.Cancel();
        return Task.CompletedTask;
    }

    private async Task SweepLoopAsync()
    {
        using var timer = new PeriodicTimer(SweepEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try { await SweepAsync(); }
                catch (Exception ex) { logger.LogWarning(ex, "Temp voice sweep failed"); }
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    /// <summary>Пустые комнаты (с учётом задержки удаления их создателя) удаляются; комнаты с людьми — не трогаются.</summary>
    private async Task SweepAsync()
    {
        foreach (var room in store.GetAllRooms())
        {
            var guild = client.GetGuild(room.GuildId);
            if (!GuildCache.Loaded(guild)) continue;
            if (guild!.GetVoiceChannel(room.ChannelId) is not { } channel)
            {
                if (!SafetyValve.Allow(room.GuildId, "tempvoice-forget", 10, TimeSpan.FromHours(1), "forgetting temp rooms whose channels are gone")) continue;
                store.RemoveRoom(room.ChannelId);   // удалили вручную, пока бот не видел
                _emptySince.TryRemove(room.ChannelId, out _);
                continue;
            }
            if (channel.ConnectedUsers.Count > 0) { _emptySince.TryRemove(channel.Id, out _); continue; }
            var since = _emptySince.GetOrAdd(channel.Id, DateTime.UtcNow);
            var delay = store.GetCreator(room.CreatorChannelId)?.DeleteDelaySeconds ?? 0;
            // пустая хотя бы два прохода подряд (новую комнату, куда владельца ещё переносят, не трогаем) и не меньше задержки
            if (DateTime.UtcNow - since < TimeSpan.FromSeconds(Math.Max(delay, SweepEvery.TotalSeconds * .8))) continue;
            // voice states lost after a reconnect would make every room look empty — never wipe many at once
            if (!SafetyValve.Allow(room.GuildId, "tempvoice-sweep", 15, TimeSpan.FromMinutes(10), "deleting temp rooms that look empty")) continue;
            logger.LogInformation("Temp voice sweep: deleting empty room {Room}", channel.Name);
            await DeleteIfEmptyAsync(channel, delayed: true);
        }
    }

    // Обработчики не блокируют gateway: работа уходит в фон
    private Task OnVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (before.VoiceChannel?.Id == after.VoiceChannel?.Id) return Task.CompletedTask; // мьют/стрим и т.п.
        var (left, joined) = (before.VoiceChannel, after.VoiceChannel);
        if (joined is not null) _emptySince.TryRemove(joined.Id, out _);
        // Из своей комнаты в канал-создатель: сначала создание (перенос обратно в свою комнату), потом проверка старой — как раньше
        if (joined is not null && store.GetCreator(joined.Id) is not null)
        {
            _ = Task.Run(() => RunAsync(async () =>
            {
                await HandleJoinAsync(user, joined);
                if (left is not null) await DeleteIfEmptyAsync(left);
            }));
            return Task.CompletedTask;
        }
        // вышел — проверяем сразу, не дожидаясь очереди создания
        if (left is not null)
            _ = Task.Run(async () =>
            {
                try { await DeleteIfEmptyAsync(left); }
                catch (Exception ex) { logger.LogWarning(ex, "Temp voice: could not check room {Room}", left.Name); }
            });
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
        _ = Task.Run(async () =>
        {
            // как и всё остальное: ничего не забываем, пока сервер не загружен целиком
            await GuildCache.WaitAllAsync(client, TimeSpan.FromMinutes(2));
            await RunAsync(CleanupAsync);
        });
        return Task.CompletedTask;
    }

    private async Task RunAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            var task = action();
            if (await Task.WhenAny(task, Task.Delay(GateTimeout)) != task)
            {
                // не держим очередь: операция доделается в фоне, ошибка — в лог
                logger.LogWarning("Temp voice operation still running after {Seconds} s — not waiting for it", GateTimeout.TotalSeconds);
                _ = task.ContinueWith(t => logger.LogWarning(t.Exception, "Temp voice operation failed"), TaskContinuationOptions.OnlyOnFaulted);
                return;
            }
            await task;
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

    private async Task HandleJoinAsync(SocketUser user, SocketVoiceChannel? joined)
    {
        if (joined is not null && user is SocketGuildUser member && !member.IsBot &&
            store.GetCreator(joined.Id) is { } creator && creator.GuildId == joined.Guild.Id &&
            store.GetRoom(joined.Id) is null)
            await CreateRoomAsync(member, joined, creator);
    }

    private async Task CreateRoomAsync(SocketGuildUser user, SocketVoiceChannel creatorChannel, TempVoiceCreator creator)
    {
        var guild = creatorChannel.Guild;

        // Уже есть своя комната от ЭТОГО создателя — переносим туда (комнаты разных создателей независимы)
        if (store.FindRoomByOwner(guild.Id, user.Id, creatorChannel.Id) is { } own && guild.GetVoiceChannel(own.ChannelId) is { } existing)
        {
            try
            {
                _emptySince.TryRemove(existing.Id, out _);
                await MoveAsync(user, existing.Id);
                return;
            }
            catch (Discord.Net.HttpException)
            {
                // её как раз удаляют (была пустой) — делаем новую
                store.RemoveRoom(existing.Id);
            }
        }

        var botId = client.CurrentUser.Id;
        var overwrites = creatorChannel.PermissionOverwrites
            .Where(o => !(o.TargetType == PermissionTarget.User && (o.TargetId == user.Id || o.TargetId == botId)))
            .ToList();
        overwrites.Add(new Overwrite(user.Id, PermissionTarget.User, OwnerPermissions));
        overwrites.Add(new Overwrite(botId, PermissionTarget.User, BotPermissions));

        // Лимит комнат от этого создателя
        if (creator.MaxRooms > 0 && store.GetRooms(guild.Id).Count(r => r.CreatorChannelId == creatorChannel.Id) >= creator.MaxRooms)
        {
            logger.LogInformation("Temp voice: {Creator} reached its limit of {Max} rooms", creatorChannel.Name, creator.MaxRooms);
            return;
        }

        // Мои настройки комнаты (сайт → Home или /room defaults) важнее настроек канала-создателя — если создатель это разрешает
        var mine = creator.AllowMemberPrefs ? prefs.Get(guild.Id, user.Id) : null;
        var name = !string.IsNullOrWhiteSpace(mine?.Name)
            ? mine!.Name!.Replace("{user}", user.DisplayName)
            : BuildRoomName(creator.NameTemplate, creatorChannel.Name, user.DisplayName);
        if (name.Length > 100) name = name[..100];
        var userLimit = mine?.Limit is { } myLimit ? (myLimit > 0 ? myLimit : (int?)null) : creator.UserLimit;

        // Категория комнат: своя из настроек (если существует) или категория создателя
        var categoryId = creator.RoomCategoryId is { } rc && guild.GetCategoryChannel(rc) is not null ? rc : creatorChannel.CategoryId;
        var bitrate = creator.BitrateKbps is { } kbps ? Math.Clamp(kbps * 1000, 8000, guild.MaxBitrate) : creatorChannel.Bitrate;
        var room = await guild.CreateVoiceChannelAsync(name, p =>
        {
            p.CategoryId = categoryId;
            p.PermissionOverwrites = new Optional<IEnumerable<Overwrite>>(overwrites);
            p.Bitrate = bitrate;
            if (userLimit is { } limit) p.UserLimit = limit;
        });

        var stored = new TempVoiceRoom
        {
            GuildId = guild.Id,
            ChannelId = room.Id,
            OwnerId = user.Id,
            CreatorChannelId = creatorChannel.Id
        };
        store.AddRoom(stored);
        logger.LogInformation("Temp room {Room} created for {User}", name, user.Username);

        // Комната — над/под создателем или в начале/конце категории (Temp Voice → ⚙ Settings)
        await rooms.ReorderCategoryAsync(guild, categoryId, room.Id);

        // Панель управления — в текстовый чат самой комнаты
        if (creator.PostPanel && room is IMessageChannel roomChat)
        {
            try
            {
                var (embed, comps) = TempRoomManager.Panel(PanelTextStore.Instance?.GetText(guild.Id, PanelDefs.Rooms.Key));
                var greeting = Tx.Get(guild.Id, "room.welcome", ("user", $"<@{user.Id}>"));
                await roomChat.SendMessageAsync(greeting.Length == 0 ? null : greeting, embed: embed, components: comps,
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
            // «Сразу закрыть» — после переноса, чтобы владелец уже был внутри
            if ((mine?.Locked == true || creator.StartLocked) && guild.GetVoiceChannel(room.Id) is { } socketRoom)
                await rooms.LockAsync(new RoomRef(socketRoom, stored));
        }
        catch (Discord.Net.HttpException)
        {
            // пользователь успел выйти из голоса — комната никому не нужна
            await room.DeleteAsync();
            store.RemoveRoom(room.Id);
        }
    }

    /// <summary>Удаляет только комнату, которую создал сам бот (есть в списке комнат), на её сервере и не создатель.</summary>
    /// <param name="delayed">Повторная проверка после задержки удаления (не откладывать снова).</param>
    private async Task DeleteIfEmptyAsync(SocketVoiceChannel channel, bool delayed = false)
    {
        if (store.GetRoom(channel.Id) is not { } room) return;
        if (room.GuildId != channel.Guild.Id) return;
        if (store.GetCreator(channel.Id) is not null) return;
        if (channel.ConnectedUsers.Count > 0) return;

        // Задержка удаления: вернулись за это время — комната остаётся
        if (!delayed && store.GetCreator(room.CreatorChannelId)?.DeleteDelaySeconds is > 0 and var delay)
        {
            var guildId = channel.Guild.Id;
            var channelId = channel.Id;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delay));
                if (client.GetGuild(guildId)?.GetVoiceChannel(channelId) is { } again)
                {
                    try { await DeleteIfEmptyAsync(again, delayed: true); }
                    catch (Exception ex) { logger.LogWarning(ex, "Temp voice: could not delete room {Room}", again.Name); }
                }
            });
            return;
        }

        try
        {
            await channel.DeleteAsync();
            logger.LogInformation("Temp room {Room} deleted (empty)", channel.Name);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
        {
            // уже удалён вручную
        }
        catch (Discord.Net.HttpException ex)
        {
            // нет прав, Discord недоступен и т.п. — комнату НЕ забываем: повторит проверка раз в минуту
            logger.LogWarning(ex, "Could not delete empty temp room {Room} ({Code}) — will retry", channel.Name, ex.DiscordCode?.ToString() ?? ex.HttpCode.ToString());
            return;
        }
        store.RemoveRoom(channel.Id);
        _emptySince.TryRemove(channel.Id, out _);
    }

    /// <summary>После перезапуска: удаляем пустые комнаты и забываем удалённые вручную.</summary>
    private async Task CleanupAsync()
    {
        foreach (var room in store.GetAllRooms())
        {
            var channel = client.GetGuild(room.GuildId)?.GetVoiceChannel(room.ChannelId);
            if (channel is null)
            {
                if (GuildCache.Loaded(client.GetGuild(room.GuildId)) &&
                    SafetyValve.Allow(room.GuildId, "tempvoice-forget", 10, TimeSpan.FromHours(1), "forgetting temp rooms whose channels are gone"))
                    store.RemoveRoom(room.ChannelId);   // only when the server is fully loaded
                continue;
            }
            if (channel.ConnectedUsers.Count == 0 &&
                !SafetyValve.Allow(room.GuildId, "tempvoice-sweep", 15, TimeSpan.FromMinutes(10), "deleting temp rooms that look empty")) continue;
            await DeleteIfEmptyAsync(channel);
        }

        // Наводим порядок во всех категориях, где есть создатели или их комнаты — один раз после старта
        // (Ready приходит и при каждом переподключении; массовые перестановки упираются в лимиты Discord)
        if (_orderedOnce) return;
        _orderedOnce = true;
        foreach (var guild in client.Guilds.Where(GuildCache.Loaded))
        foreach (var categoryId in store.GetCreators(guild.Id)
                     .SelectMany(c => new[] { guild.GetVoiceChannel(c.ChannelId)?.CategoryId, c.RoomCategoryId ?? guild.GetVoiceChannel(c.ChannelId)?.CategoryId })
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
