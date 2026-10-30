using System.Collections.Concurrent;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Архив закрытых тикетов: кто, когда, итог, кем, анкета (data/ticket-archive.json, последние 5000).</summary>
public sealed class TicketArchiveStore
{
    public const int MaxEntries = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<TicketArchiveEntry> _data;

    public TicketArchiveStore(IConfiguration config)
    {
        _path = config["Storage:TicketArchivePath"] ?? "data/ticket-archive.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<TicketArchiveEntry>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public void Add(TicketArchiveEntry entry)
    {
        lock (_lock)
        {
            _data.Add(entry);
            if (_data.Count > MaxEntries) _data.RemoveRange(0, _data.Count - MaxEntries);
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
    }

    /// <summary>Новые сверху; поиск по заявителю, проекту, причине и ответам анкеты.</summary>
    public IReadOnlyList<TicketArchiveEntry> Query(ulong guildId, string? outcome, string? search, ulong? userId, int skip, int take)
    {
        lock (_lock)
        {
            IEnumerable<TicketArchiveEntry> q = _data.Where(e => e.GuildId == guildId);
            if (!string.IsNullOrWhiteSpace(outcome)) q = q.Where(e => e.Outcome == outcome);
            if (userId is { } u) q = q.Where(e => e.UserId == u);
            if (!string.IsNullOrWhiteSpace(search))
                q = q.Where(e => e.UserName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                 e.Project.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                 (e.ByName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 (e.Reason?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                 e.Answers.Any(a => a.Answer.Contains(search, StringComparison.OrdinalIgnoreCase)));
            return q.Reverse().Skip(skip).Take(take).ToList();
        }
    }
}

/// <summary>
/// Создание и закрытие тикетов — общее для кнопок в Discord, /admin и сайта.
/// Каждый закрытый тикет попадает в архив с итогом.
/// </summary>
public sealed class TicketService(
    DiscordSocketClient client,
    TicketStore store,
    TicketArchiveStore archive,
    AuditLog audit,
    ILogger<TicketService> logger)
{
    // Защита от двойного клика: пока создаётся тикет, второй не создаём
    private readonly ConcurrentDictionary<string, byte> _creating = new();

    /// <summary>Уже открытый тикет этого проекта (каналы которого ещё существуют). Устаревший — убирается.</summary>
    public Ticket? OpenTicket(SocketGuild guild, ulong userId, TicketProject project)
    {
        var existing = store.FindOpenTicket(guild.Id, userId, project.Key);
        if (existing is null) return null;
        if (guild.GetTextChannel(existing.TextChannelId) is not null) return existing;
        Archive(existing, "closed", null, "Channels were deleted manually");
        store.RemoveTicket(existing.Id);
        return null;
    }

    /// <summary>Проверяет ответы анкеты: обязательные заполнены, длина в пределах.</summary>
    public static string? Validate(TicketProject project, IReadOnlyList<TicketAnswer> answers)
    {
        if (!project.FormEnabled) return null;
        foreach (var q in project.Questions)
        {
            var a = answers.FirstOrDefault(x => x.Question == q.Label)?.Answer?.Trim() ?? "";
            if (q.Required && a.Length == 0) return $"Answer the question: {q.Label}";
            if (a.Length > q.MaxLength) return $"«{q.Label}» — up to {q.MaxLength} characters.";
        }
        return null;
    }

    /// <summary>Создаёт тикет: приватные текстовый и голосовой каналы, сообщение с анкетой и кнопками.</summary>
    public async Task<(Ticket? Ticket, string? Error)> CreateAsync(SocketGuild guild, IGuildUser user, TicketProject project,
        IReadOnlyList<TicketAnswer> answers, string source)
    {
        if (OpenTicket(guild, user.Id, project) is { } open)
            return (null, $"You already have an open {project.Name} ticket: <#{open.TextChannelId}>");
        if (Validate(project, answers) is { } invalid) return (null, invalid);

        var lockKey = $"{guild.Id}:{user.Id}:{project.Key}";
        if (!_creating.TryAdd(lockKey, 0)) return (null, "Your ticket is already being created…");
        try
        {
            var categoryId = await EnsureCategoryAsync(guild, project);
            var overwrites = new Optional<IEnumerable<Overwrite>>(BuildOverwrites(guild, project, user.Id));

            var text = await guild.CreateTextChannelAsync(TicketRenderer.TextChannelName(project, user), p =>
            {
                p.CategoryId = categoryId;
                p.PermissionOverwrites = overwrites;
                p.Topic = $"{project.Name} application — {user.Username}";
            });

            IVoiceChannel voice;
            try
            {
                voice = await guild.CreateVoiceChannelAsync(TicketRenderer.VoiceChannelName(project, user), p =>
                {
                    p.CategoryId = categoryId;
                    p.PermissionOverwrites = overwrites;
                });
            }
            catch
            {
                await text.DeleteAsync(); // не оставляем полутикет
                throw;
            }

            var ticket = new Ticket
            {
                GuildId = guild.Id,
                ProjectKey = project.Key,
                UserId = user.Id,
                TextChannelId = text.Id,
                VoiceChannelId = voice.Id,
                Answers = answers.Where(a => !string.IsNullOrWhiteSpace(a.Answer))
                    .Select(a => new TicketAnswer { Question = a.Question, Answer = a.Answer.Trim() }).ToList()
            };
            store.AddTicket(ticket);
            audit.Add(guild.Id, user.Id, user.DisplayName, source, "tickets", "Opened ticket", project.Name);

            var (embeds, comps) = TicketRenderer.TicketMessage(ticket, project);
            var pings = $"<@{user.Id}> " + string.Join(" ", project.StaffRoleIds.Select(r => $"<@&{r}>"));
            var message = await text.SendMessageAsync(pings, embeds: embeds, components: comps,
                allowedMentions: new AllowedMentions { UserIds = [user.Id], RoleIds = project.StaffRoleIds.ToList() });
            store.UpdateTicket(ticket.Id, t => t.MessageId = message.Id);
            return (ticket, null);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Failed to create a ticket for {User}", user.Id);
            return (null, "I couldn't create the ticket channels. The bot needs Manage Channels and Manage Roles " +
                          $"(Discord error: {ex.DiscordCode?.ToString() ?? ex.HttpCode.ToString()}).");
        }
        finally
        {
            _creating.TryRemove(lockKey, out _);
        }
    }

    /// <summary>Закрыть тикет: в архив с итогом, убрать из открытых, удалить каналы.</summary>
    public async Task CloseAsync(Ticket ticket, string outcome, IUser? by, string? reason)
    {
        if (!Detach(ticket, outcome, by, reason)) return; // уже закрыт
        await DeleteChannelsAsync(ticket);
    }

    /// <summary>Убрать из открытых и записать в архив (каналы не трогает). false — уже закрыт.</summary>
    public bool Detach(Ticket ticket, string outcome, IUser? by, string? reason)
    {
        if (store.FindTicket(ticket.Id) is null) return false;
        store.RemoveTicket(ticket.Id);
        Archive(ticket, outcome, by, reason);
        return true;
    }

    public async Task DeleteChannelsAsync(Ticket ticket)
    {
        var guild = client.GetGuild(ticket.GuildId);
        foreach (var id in new[] { ticket.VoiceChannelId, ticket.TextChannelId })
        {
            try
            {
                if (guild?.GetChannel(id) is { } channel) await channel.DeleteAsync();
            }
            catch (Discord.Net.HttpException)
            {
                // канал уже удалён
            }
        }
    }

    /// <summary>Пауза перед закрытием после Approve / Deny (чтобы все увидели итог).</summary>
    public static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(15);

    /// <summary>Закрыть тикет через <see cref="CloseDelay"/> (в архив с итогом, каналы удалить).</summary>
    public void CloseLater(Ticket ticket, IUser? by, string outcome, string? reason) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(CloseDelay);
            await CloseAsync(ticket, outcome, by, reason);
        }
        catch (Exception)
        {
            // каналы могли удалить вручную — тикет уже убран из списка
        }
    });

    /// <summary>ЛС заявителю об одобрении (каналы тикета скоро исчезнут, итог должен остаться у него).</summary>
    public async Task NotifyApprovedAsync(Ticket ticket, TicketProject project, IEnumerable<ulong> roleIds)
    {
        try
        {
            var guild = client.GetGuild(ticket.GuildId);
            var user = client.GetUser(ticket.UserId) as IUser ?? await client.Rest.GetUserAsync(ticket.UserId);
            if (user is null) return;
            var roles = string.Join(", ", roleIds.Select(id => guild?.GetRole(id)?.Name ?? "role").Select(Format.Sanitize));
            await user.SendMessageAsync($"✅ Your application to **{Format.Sanitize(project.Name)}** on " +
                $"**{Format.Sanitize(guild?.Name ?? "the server")}** was approved. You got: **{roles}**.");
        }
        catch (Discord.Net.HttpException)
        {
            // закрытые ЛС
        }
    }

    /// <summary>ЛС заявителю об отказе с причиной.</summary>
    public async Task NotifyDeniedAsync(Ticket ticket, TicketProject? project, string? reason)
    {
        try
        {
            var user = client.GetUser(ticket.UserId) as IUser ?? await client.Rest.GetUserAsync(ticket.UserId);
            if (user is null) return;
            var guildName = client.GetGuild(ticket.GuildId)?.Name ?? "the server";
            var text = $"❌ Your application to **{Format.Sanitize(project?.Name ?? "the project")}** on **{Format.Sanitize(guildName)}** was declined.";
            if (!string.IsNullOrWhiteSpace(reason)) text += $"\n**Reason:** {Format.Sanitize(reason)}";
            await user.SendMessageAsync(text);
        }
        catch (Discord.Net.HttpException)
        {
            // закрытые ЛС
        }
    }

    private void Archive(Ticket ticket, string outcome, IUser? by, string? reason)
    {
        var guild = client.GetGuild(ticket.GuildId);
        archive.Add(new TicketArchiveEntry
        {
            Id = ticket.Id,
            GuildId = ticket.GuildId,
            Project = store.FindProject(ticket.GuildId, ticket.ProjectKey)?.Name ?? "Deleted project",
            UserId = ticket.UserId,
            UserName = guild?.GetUser(ticket.UserId)?.DisplayName ?? client.GetUser(ticket.UserId)?.Username ?? ticket.UserId.ToString(),
            OpenedAt = ticket.CreatedAt,
            Outcome = outcome,
            ById = by?.Id,
            ByName = by is null ? null : (by as IGuildUser)?.DisplayName ?? by.GlobalName ?? by.Username,
            Reason = reason,
            Answers = ticket.Answers.ToList()
        });
    }

    /// <summary>Категория проекта, иначе общая «Tickets» (создаётся скрытой при первом тикете).</summary>
    private async Task<ulong?> EnsureCategoryAsync(SocketGuild guild, TicketProject project)
    {
        if (project.CategoryId is { } id && guild.GetCategoryChannel(id) is not null) return id;

        var existing = guild.CategoryChannels.FirstOrDefault(c =>
            c.Name.Equals(TicketRenderer.DefaultCategoryName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;

        var hidden = new Overwrite[]
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role, OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Deny)),
            new(client.CurrentUser.Id, PermissionTarget.User,
                OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Allow, manageChannel: PermValue.Allow))
        };
        var created = await guild.CreateCategoryChannelAsync(TicketRenderer.DefaultCategoryName,
            p => p.PermissionOverwrites = new Optional<IEnumerable<Overwrite>>(hidden));
        return created.Id;
    }

    /// <summary>Канал видят только заявитель, руководство проекта и сам бот.</summary>
    private List<Overwrite> BuildOverwrites(SocketGuild guild, TicketProject project, ulong applicantId)
    {
        var member = OverwritePermissions.InheritAll.Modify(
            viewChannel: PermValue.Allow,
            sendMessages: PermValue.Allow,
            readMessageHistory: PermValue.Allow,
            attachFiles: PermValue.Allow,
            embedLinks: PermValue.Allow,
            connect: PermValue.Allow,
            speak: PermValue.Allow);

        var overwrites = new List<Overwrite>
        {
            new(guild.EveryoneRole.Id, PermissionTarget.Role, OverwritePermissions.InheritAll.Modify(viewChannel: PermValue.Deny)),
            new(client.CurrentUser.Id, PermissionTarget.User, member.Modify(manageChannel: PermValue.Allow)),
            new(applicantId, PermissionTarget.User, member)
        };
        overwrites.AddRange(project.StaffRoleIds.Select(roleId => new Overwrite(roleId, PermissionTarget.Role, member)));
        return overwrites;
    }
}

/// <summary>
/// Раз в час: тикеты проектов с авто-закрытием. Без сообщений N дней — предупреждение,
/// ещё сутки без сообщений после него — тикет закрывается (в архив как auto-closed).
/// </summary>
public sealed class TicketAutoCloseService(
    DiscordSocketClient client,
    TicketStore store,
    TicketService tickets,
    AuditLog audit,
    ILogger<TicketAutoCloseService> logger) : BackgroundService
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var guild in client.Guilds)
                foreach (var ticket in store.GetTickets(guild.Id))
                {
                    try
                    {
                        await CheckAsync(guild, ticket);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Auto-close check failed for ticket {Id}", ticket.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // остановка бота
        }
    }

    private async Task CheckAsync(SocketGuild guild, Ticket ticket)
    {
        var project = store.FindProject(guild.Id, ticket.ProjectKey);
        if (project?.AutoCloseDays is not { } days || days <= 0) return;
        if (guild.GetTextChannel(ticket.TextChannelId) is not { } channel) return;

        var last = (await channel.GetMessagesAsync(1).FlattenAsync()).FirstOrDefault();
        var lastActivity = last?.Timestamp ?? ticket.CreatedAt;
        var now = DateTimeOffset.UtcNow;

        if (ticket.WarnedAt is { } warned)
        {
            // После предупреждения кто-то написал (не бот) — счётчик заново
            if (last is not null && last.Author.Id != client.CurrentUser.Id && last.Timestamp > warned)
            {
                store.UpdateTicket(ticket.Id, t => t.WarnedAt = null);
                return;
            }
            if (now - warned < Grace) return;

            audit.FromSystem(guild.Id, "tickets", "Auto-closed inactive ticket",
                $"{project.Name} · {guild.GetUser(ticket.UserId)?.DisplayName ?? ticket.UserId.ToString()}");
            await tickets.CloseAsync(ticket, "auto-closed", null, $"No messages for {days} day(s)");
            return;
        }

        if (now - lastActivity < TimeSpan.FromDays(days) - Grace) return;
        await channel.SendMessageAsync(
            $"⏳ This ticket has been quiet for a while. It will be **closed automatically in 24 hours** unless someone writes here. <@{ticket.UserId}>",
            allowedMentions: new AllowedMentions { UserIds = [ticket.UserId] });
        store.UpdateTicket(ticket.Id, t => t.WarnedAt = DateTimeOffset.UtcNow);
    }
}
