using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Раз в час сверяет роль дня рождения: у кого сегодня ДР и есть роль «Участник» — выдаёт указанную роль,
/// у остальных с этой ролью — снимает (то есть на следующий день роль уходит сама). Необязательно — поздравление в канал.
/// Дата «сегодня» берётся в часовом поясе сервера (Settings → часовой пояс).
/// </summary>
public sealed class BirthdayService(DiscordSocketClient client, BirthdayStore store, TimeService time,
    AnnouncementService announce, ILogger<BirthdayService> logger) : BackgroundService
{
    public sealed record Reconciled(int Granted, int Removed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Небольшая пауза, чтобы клиент успел подключиться и прогреть участников
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            // Тик раз в минуту: поздравительный пост — в своё время; сверка роли — раз в час
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            var lastReconcile = DateTimeOffset.MinValue;
            do
            {
                var reconcile = DateTimeOffset.UtcNow - lastReconcile >= TimeSpan.FromHours(1);
                if (reconcile) lastReconcile = DateTimeOffset.UtcNow;
                foreach (var guildId in store.Guilds())
                {
                    if (reconcile)
                        try { await ReconcileAsync(guildId); }
                        catch (Exception e) { logger.LogWarning(e, "Birthday reconcile for guild {Guild} failed", guildId); }
                    try { await GreetIfDueAsync(guildId); }
                    catch (Exception e) { logger.LogWarning(e, "Birthday greeting for guild {Guild} failed", guildId); }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // остановка
        }
    }

    /// <summary>Сверить роль дня рождения на сервере прямо сейчас. Возвращает, сколько выдано и снято.</summary>
    public async Task<Reconciled> ReconcileAsync(ulong guildId)
    {
        var g = store.Get(guildId);
        if (!g.Enabled || g.BirthdayRoleId is not { } roleId) return new Reconciled(0, 0);
        if (client.GetGuild(guildId) is not { } guild || guild.GetRole(roleId) is not { } role) return new Reconciled(0, 0);
        // members not all in the cache yet: a celebrant could look "not on the server" and lose the role
        if (!GuildCache.Loaded(guild) || !(guild.HasAllMembers || guild.Users.Count >= guild.MemberCount * .9)) return new Reconciled(0, 0);

        var today = time.LocalNow.Date;

        // Кто сегодня именинник: дата совпадает, есть роль «Участник» (если задана) и человек на сервере
        var celebrants = new HashSet<ulong>();
        foreach (var (userId, b) in g.People)
        {
            if (!b.IsOn(today)) continue;
            if (guild.GetUser(userId) is not { } member) continue;
            if (g.MemberRoleId is { } memberRole && member.Roles.All(r => r.Id != memberRole)) continue;
            celebrants.Add(userId);
        }

        var granted = 0;
        foreach (var userId in celebrants)
        {
            var member = guild.GetUser(userId)!;
            if (member.Roles.Any(r => r.Id == roleId)) continue;
            try
            {
                await member.AddRoleAsync(role);
                granted++;
                if (g.AnnounceChannelId is { } chId && guild.GetTextChannel(chId) is { } channel)
                {
                    try { await channel.SendMessageAsync(Tx.Get(guildId, "birthday.announce", ("user", $"<@{userId}>")),
                        allowedMentions: new AllowedMentions { UserIds = new List<ulong> { userId } }); }
                    catch (Discord.Net.HttpException) { /* нет прав писать в канал */ }
                }
            }
            catch (Discord.Net.HttpException ex)
            {
                logger.LogWarning(ex, "Birthday: can't give role to {User} (check the bot's role is above the birthday role)", userId);
            }
        }

        // Снять роль у всех, у кого она есть, но сегодня не их день (включая вчерашних именинников)
        var removed = 0;
        var toRemove = role.Members.Where(m => !celebrants.Contains(m.Id)).ToList();
        // a birthday role is on a handful of people; dozens means a wrong role was chosen as the birthday role (e.g. Member)
        if (!SafetyValve.AllowBatch(guildId, "birthday-remove", toRemove.Count, 15, $"removing the birthday role @{role.Name} from members"))
            toRemove.Clear();
        foreach (var member in toRemove)
        {
            try { await member.RemoveRoleAsync(role); removed++; }
            catch (Discord.Net.HttpException ex) { logger.LogWarning(ex, "Birthday: can't remove role from {User}", member.Id); }
        }

        if (granted > 0 || removed > 0)
            logger.LogInformation("Birthdays on {Guild}: +{Granted} / -{Removed}", guild.Name, granted, removed);
        return new Reconciled(granted, removed);
    }

    /// <summary>Именинники сегодня: дата совпадает, человек на сервере и с ролью «Участник» (если задана).</summary>
    public List<SocketGuildUser> Celebrants(ulong guildId)
    {
        var g = store.Get(guildId);
        if (client.GetGuild(guildId) is not { } guild) return [];
        var today = time.LocalNow.Date;
        return g.People
            .Where(kv => kv.Value.IsOn(today))
            .Select(kv => guild.GetUser(kv.Key))
            .Where(m => m is not null && (g.MemberRoleId is not { } r || m.Roles.Any(x => x.Id == r)))
            .Select(m => m!)
            .ToList();
    }

    /// <summary>Пора поздравлять: включено, время наступило, сегодня ещё не поздравляли.</summary>
    private async Task GreetIfDueAsync(ulong guildId)
    {
        var g = store.Get(guildId);
        if (!g.GreetEnabled || g.GreetChannelId is null) return;
        var now = time.LocalNow;
        var today = now.ToString("yyyy-MM-dd");
        if (g.GreetLastDate == today || now.Hour * 60 + now.Minute < g.GreetHour * 60 + g.GreetMinute) return;
        store.UpdateSettings(guildId, s => s.GreetLastDate = today); // один раз в день, даже если именинников нет
        if (Celebrants(guildId).Count == 0) return;
        var error = await PostGreetingAsync(guildId, null, null);
        if (error is not null) logger.LogWarning("Birthday greeting on {Guild}: {Error}", guildId, error);
    }

    /// <summary>Выбрать шаблон по режиму (random / sequence / shuffle) и сдвинуть очередь.</summary>
    private BirthdayTemplate? PickTemplate(ulong guildId)
    {
        BirthdayTemplate? picked = null;
        store.UpdateSettings(guildId, s =>
        {
            var pool = s.Templates.Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            if (pool.Count == 0) return;
            switch (s.GreetMode)
            {
                case "sequence":
                    picked = pool[((s.GreetNextIndex % pool.Count) + pool.Count) % pool.Count];
                    s.GreetNextIndex = (s.GreetNextIndex + 1) % pool.Count;
                    break;
                case "random":
                    picked = pool[Random.Shared.Next(pool.Count)];
                    break;
                default: // shuffle — без повторов, пока не пройдут все включённые шаблоны
                    var left = pool.Where(t => !s.GreetUsed.Contains(t.Id)).ToList();
                    if (left.Count == 0) { s.GreetUsed.Clear(); left = pool; }
                    picked = left[Random.Shared.Next(left.Count)];
                    s.GreetUsed.Add(picked.Id);
                    break;
            }
        });
        return picked;
    }

    /// <summary>
    /// Текст поздравления: {users} — упоминание КАЖДОГО именинника (не роли), {names} — их имена, {count}, {server}.
    /// Если {users} в тексте нет, упоминания идут первой строкой.
    /// </summary>
    public static string RenderGreeting(string template, IReadOnlyList<IGuildUser> users, string serverName)
    {
        var mentions = JoinList(users.Select(u => $"<@{u.Id}>").ToList());
        var names = JoinList(users.Select(u => Format.Sanitize(u.DisplayName)).ToList());
        var text = template.Replace("{users}", mentions, StringComparison.OrdinalIgnoreCase)
            .Replace("{names}", names, StringComparison.OrdinalIgnoreCase)
            .Replace("{count}", users.Count.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{server}", serverName, StringComparison.OrdinalIgnoreCase);
        return template.Contains("{users}", StringComparison.OrdinalIgnoreCase) ? text : $"{mentions}\n{text}";
    }

    private static string JoinList(List<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items[..^1]) + " & " + items[^1];

    /// <summary>
    /// Опубликовать поздравление. templateId — конкретный шаблон (иначе по режиму); testUser — тест: в ЛС этому человеку
    /// с ним самим как именинником. Возвращает текст ошибки или null.
    /// </summary>
    public async Task<string?> PostGreetingAsync(ulong guildId, string? templateId, IGuildUser? testUser)
    {
        var g = store.Get(guildId);
        if (client.GetGuild(guildId) is not { } guild) return "Server unavailable.";
        var template = templateId is not null ? g.Templates.Find(t => t.Id == templateId) : PickTemplate(guildId);
        if (template is null || string.IsNullOrWhiteSpace(template.Text)) return "Add at least one enabled template with text.";
        IReadOnlyList<IGuildUser> users = testUser is not null ? [testUser] : Celebrants(guildId).Cast<IGuildUser>().ToList();
        if (users.Count == 0) return "Nobody has a birthday today.";

        IMessageChannel? channel = testUser is not null ? await testUser.CreateDMChannelAsync()
            : g.GreetChannelId is { } cid ? guild.GetTextChannel(cid) : null;
        if (channel is null) return "Choose the channel for the greeting.";

        var text = RenderGreeting(template.Text, users, guild.Name);
        var mentions = new AllowedMentions { UserIds = users.Select(u => u.Id).Take(100).ToList() };
        try
        {
            foreach (var part in PostSplitter.Split(await announce.ResolveEmotesAsync(guildId, text)))
                await announce.SendPartAsync(guildId, channel, part, mentions);
        }
        catch (Discord.Net.HttpException)
        {
            return testUser is not null ? "I can't DM you — allow direct messages from server members."
                : "I can't post there — give the bot View Channel, Send Messages, Embed Links and Attach Files.";
        }
        if (testUser is null)
            logger.LogInformation("Birthday greeting on {Guild}: {Count} member(s), template {Template}", guild.Name, users.Count, template.Name);
        return null;
    }

    /// <summary>Ближайшие дни рождения (по локальной дате сервера), для панели. Для каждого — через сколько дней.</summary>
    public IReadOnlyList<(ulong UserId, Birthday Birthday, int InDays)> Upcoming(ulong guildId, int take = 50)
    {
        var g = store.Get(guildId);
        var today = time.LocalNow.Date;
        return g.People
            .Select(kv => (kv.Key, kv.Value, InDays: DaysUntil(today, kv.Value)))
            .OrderBy(x => x.InDays)
            .Take(take)
            .ToList();
    }

    private static int DaysUntil(DateTime today, Birthday b)
    {
        var (m, d) = (b.Month, b.Day);
        if (m == 2 && d == 29) d = 28; // ближайшее сравнение без учёта високосности
        for (var i = 0; i < 366; i++)
        {
            var date = today.AddDays(i);
            if (date.Month == m && date.Day == d) return i;
        }
        return 999;
    }
}
