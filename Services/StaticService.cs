using System.Collections.Concurrent;
using System.Text;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>
/// Статики (КП): создание, заявки, решение организатора, пинг состава и пост в канале.
/// Все изменения — под <see cref="StaticGroup.Gate"/>; пост обновляется через очередь правок (<see cref="RefreshPostAsync"/>).
/// Ошибки возвращаются текстом для человека (Bot texts), null — всё прошло.
/// </summary>
public sealed class StaticService
{
    private readonly DiscordSocketClient client;
    private readonly StaticStore store;
    private readonly LfgProjectStore projects;
    private readonly LfgProfileService profile;
    private readonly CharacterStore characters;
    private readonly GuildPrefsStore prefs;
    private readonly AuditLog audit;
    private readonly ILogger<StaticService> logger;

    /// <summary>Для перерисовки постов после смены текстов на сайте (Bot texts).</summary>
    public static StaticService? Instance { get; private set; }

    public StaticService(DiscordSocketClient client, StaticStore store, LfgProjectStore projects, LfgProfileService profile,
        CharacterStore characters, GuildPrefsStore prefs,
        AuditLog audit, ILogger<StaticService> logger)
    {
        this.client = client;
        this.store = store;
        this.projects = projects;
        this.profile = profile;
        this.characters = characters;
        this.prefs = prefs;
        this.audit = audit;
        this.logger = logger;
        Instance = this;
    }

    /// <summary>Перерисовать посты всех статиков сервера (сменились тексты). Возвращает (обновлено, всего).</summary>
    public async Task<(int Updated, int Total)> RefreshAllAsync(ulong guildId)
    {
        var list = store.ForGuild(guildId).Where(s => s.MessageId != 0).ToList();
        foreach (var s in list) await RefreshPostAsync(s);
        return (list.Count, list.Count);
    }

    public static readonly int[] FavoriteSizes = [5, 10, 20];
    private static readonly TimeSpan PingCooldown = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastPing = new();

    private static string T(ulong gid, string key, params (string Name, object? Value)[] args) => Tx.Get(gid, key, args);

    public string? ProjectName(StaticGroup s) => projects.Effective(s.GuildId, s.ProjectKey)?.Name;

    public static string JumpUrl(StaticGroup s) => $"https://discord.com/channels/{s.GuildId}/{s.ChannelId}/{s.MessageId}";

    /// <summary>Организатор, владелец бота или админ (isAdmin считает вызывающий: в Discord — админ сервера бота, на сайте — право LFG).</summary>
    public static bool CanManage(StaticGroup s, ulong userId, bool isAdmin = false) =>
        isAdmin || s.OwnerId == userId || (BotOwner.CurrentId != 0 && userId == BotOwner.CurrentId);

    /// <summary>Персонаж игрока в проекте статика (снимок иконки класса и GS). Не из его списка — null.</summary>
    public PickerCharacter? Character(ulong guildId, ulong userId, string? projectKey, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : profile.PickerFor(guildId, userId, projectKey).FirstOrDefault(c => c.Name == name.Trim());

    /// <summary>Персонаж для показа: класс, GS и — только в проекте с оружейной AION (Settings → Armory) — CP и сервер.</summary>
    public sealed record LiveCharacter(string Name, string? Icon, int? Gs, int? Cp, string? Server);

    /// <summary>Оружейная проекта включена: у персонажей есть CP и сервер (особенность AION 2).</summary>
    public bool ArmoryOn(ulong guildId, string? projectKey)
    {
        var project = projects.Effective(guildId, projectKey);
        return prefs.ArmoryFor(guildId, project?.Key, project?.Name).Enabled;
    }

    /// <summary>Все персонажи человека в проекте (для просмотра организатором и на сайте).</summary>
    public IReadOnlyList<LiveCharacter> CharactersOf(ulong guildId, ulong userId, string? projectKey)
    {
        var armory = ArmoryOn(guildId, projectKey);
        var scope = projects.Scope(guildId, projectKey);
        return profile.PickerFor(guildId, userId, projectKey).Select(c => new LiveCharacter(c.Name, c.Icon, c.Gs,
            armory ? c.Cp : null,
            // Сервер персонажа (как Имя[LUD] в сборах): из оружейной или указанный вручную
            armory ? OfficialArmory.Tag(characters.GetInfo(guildId, userId, c.Name, scope) is { } info ? info.Armory?.ServerId ?? info.ServerId : null) : null))
            .ToList();
    }

    /// <summary>Персонаж участника как он есть сейчас; удалён из профиля — по снимку из заявки. Без персонажа — null.</summary>
    public LiveCharacter? Shown(StaticGroup s, ulong userId, string? name, string? icon, int? gs, int? cp)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return CharactersOf(s.GuildId, userId, s.ProjectKey).FirstOrDefault(c => c.Name == name.Trim())
               ?? new LiveCharacter(name, icon, gs, ArmoryOn(s.GuildId, s.ProjectKey) ? cp : null, null);
    }

    /// <summary>« — 🛡 **Имя** `[TRI]` · GS · CP».</summary>
    public string CharacterLine(StaticGroup s, ulong userId, string? name, string? icon, int? gs, int? cp) =>
        Shown(s, userId, name, icon, gs, cp) is { } c ? CharacterText(c.Name, c.Icon, c.Gs, c.Cp, c.Server) : "";

    // ================= Пост =================

    public (Embed Embed, MessageComponent Components) Render(StaticGroup s)
    {
        var gid = s.GuildId;
        var status = !s.Recruiting ? "closed" : s.IsFull ? "full" : "recruiting";
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(s.Description)) text.Append(s.Description.Trim()).Append("\n\n");
        text.Append(Tx.Get(gid, "static.post.organizer", ("user", $"<@{s.OwnerId}>")));
        if (ProjectName(s) is { } project) text.Append(Tx.Line(gid, "static.post.project", ("project", project)));
        text.Append(Tx.Line(gid, "static.post.roster", ("count", s.Members.Count), ("size", s.Size)));
        text.Append('\n');
        var shown = 0;
        foreach (var m in s.Members)
        {
            var line = $"\n{++shown}. <@{m.UserId}>{CharacterLine(s, m.UserId, m.CharacterName, m.ClassIcon, m.Gs, m.Cp)}";
            if (text.Length + line.Length > 3600) { text.Append($"\n… +{s.Members.Count - shown + 1}"); break; }
            text.Append(line);
        }
        if (s.Applicants.Count > 0) text.Append("\n").Append(Tx.Line(gid, "static.post.applications", ("count", s.Applicants.Count)));

        var embed = new EmbedBuilder()
            .WithSiteTitle("🤝 " + s.Name, SiteLink.StaticUrl(gid, s.Id))
            .WithDescription(text.ToString())
            .WithColor(Tx.Color(gid, $"color.static.{status}"))
            .WithFooter(Tx.Get(gid, "static.post.footer", ("status", Tx.Get(gid, $"static.post.{status}")), ("id", s.Id)))
            .Build();

        var comps = new ComponentBuilder()
            .WithButton(Tx.Get(gid, "static.post.apply"), $"st:apply:{s.Id}", ButtonStyle.Primary, new Emoji("📨"), disabled: !s.Recruiting, row: 0)
            .WithButton(Tx.Get(gid, "static.post.leave"), $"st:leave:{s.Id}", ButtonStyle.Secondary, new Emoji("🚪"), row: 0)
            .WithButton(Tx.Get(gid, "static.post.manage"), $"st:manage:{s.Id}", ButtonStyle.Secondary, new Emoji("⚙️"), row: 0)
            .WithButton(Tx.Get(gid, "static.post.ping"), $"st:ping:{s.Id}", ButtonStyle.Secondary, new Emoji("📣"), row: 0)
            .Build();
        return (embed, comps);
    }

    public static string CharacterText(string? name, string? icon, int? gs, int? cp = null, string? server = null) =>
        name is null ? "" : $" — {(icon is null ? "" : icon + " ")}**{Format.Sanitize(name)}**{(server is { Length: > 0 } ? $" `[{server}]`" : "")}{GsCp(gs, cp)}";

    /// <summary>« · GS 3053 · CP 1.2M» — как в постах сборов.</summary>
    public static string GsCp(int? gs, int? cp) =>
        (gs is { } g ? $" · GS {g}" : "") + (cp is { } c ? $" · CP {LfgRenderer.Compact(c)}" : "");

    // Объединение правок поста: первая уходит сразу, следующие за PostEditGap — одной итоговой правкой (как у сборов)
    private static readonly TimeSpan PostEditGap = TimeSpan.FromMilliseconds(1200);
    private sealed class PostSync
    {
        public readonly object Lock = new();
        public bool Busy, Again;
    }
    private readonly ConcurrentDictionary<string, PostSync> _postSync = new();

    /// <summary>Перерисовать пост статика. Можно вызывать под Gate: пост рисуется таким, какой статик в момент отправки.</summary>
    public async Task RefreshPostAsync(StaticGroup s)
    {
        if (s.MessageId == 0) return;
        var sync = _postSync.GetOrAdd(s.Id, _ => new PostSync());
        lock (sync.Lock)
        {
            if (sync.Busy) { sync.Again = true; return; }
            sync.Busy = true;
        }
        await SendPostAsync(s, Render(s));
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(PostEditGap);
                lock (sync.Lock)
                {
                    if (!sync.Again)
                    {
                        sync.Busy = false;
                        _postSync.TryRemove(s.Id, out _);
                        return;
                    }
                    sync.Again = false;
                }
                if (!store.Statics.ContainsKey(s.Id) || s.MessageId == 0) continue;
                (Embed, MessageComponent)? fresh = null;
                if (await s.Gate.WaitAsync(TimeSpan.FromSeconds(5)))
                {
                    try { fresh = Render(s); }
                    finally { s.Gate.Release(); }
                }
                await SendPostAsync(s, fresh ?? Render(s));
            }
        });
    }

    private async Task SendPostAsync(StaticGroup s, (Embed Embed, MessageComponent Components) post)
    {
        if (await GetChannelAsync(s.ChannelId) is not { } channel) return;
        try
        {
            await channel.ModifyMessageAsync(s.MessageId, m =>
            {
                m.Embeds = new[] { post.Embed };
                m.Components = post.Components;
                m.AllowedMentions = AllowedMentions.None;
            });
        }
        catch (Discord.Net.HttpException ex) when (ex.DiscordCode is DiscordErrorCode.UnknownMessage or DiscordErrorCode.UnknownChannel)
        {
            // Пост удалили в Discord (Discord так и сказал): статик остаётся, пост можно опубликовать заново на сайте
            s.MessageId = 0;
            store.Save();
            logger.LogInformation("Post of static {Id} ({Name}) was deleted in Discord — the static stays without a post", s.Id, s.Name);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not update the post of static {Id}", s.Id);
        }
    }

    private async Task<IMessageChannel?> GetChannelAsync(ulong channelId)
    {
        if (client.GetChannel(channelId) is IMessageChannel cached) return cached;
        try { return await client.Rest.GetChannelAsync(channelId) as IMessageChannel; }
        catch (Discord.Net.HttpException) { return null; }
    }

    /// <summary>Опубликовать пост статика в его канале (новый статик или пост удалили). Ошибка — текстом.</summary>
    public async Task<string?> PostAsync(StaticGroup s)
    {
        if (await GetChannelAsync(s.ChannelId) is not { } channel) return T(s.GuildId, "lfg.noAccess");
        var (embed, comps) = Render(s);
        try
        {
            var msg = await channel.SendMessageAsync(embed: embed, components: comps, allowedMentions: AllowedMentions.None);
            s.MessageId = msg.Id;
            store.Save();
            return null;
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not post static {Id}", s.Id);
            return T(s.GuildId, "lfg.noAccess");
        }
    }

    // ================= Действия =================

    /// <summary>Выполнить изменение под замком; без ошибки — сохранить и обновить пост.</summary>
    private async Task<string?> MutateAsync(StaticGroup s, Func<string?> change)
    {
        await s.Gate.WaitAsync();
        try
        {
            if (!store.Statics.ContainsKey(s.Id)) return T(s.GuildId, "static.gone");
            var error = change();
            if (error is not null) return error;
            store.Save();
            await RefreshPostAsync(s);
            return null;
        }
        finally
        {
            s.Gate.Release();
        }
    }

    public static string? CleanName(string? name) => (name ?? "").Replace("\n", " ").Trim() is { Length: > 0 } n
        ? n.Length > StaticGroup.MaxName ? n[..StaticGroup.MaxName] : n : null;

    public static string? CleanText(string? text, int max) => (text ?? "").Trim() is { Length: > 0 } t
        ? t.Length > max ? t[..max] : t : null;

    /// <summary>Создать статик и опубликовать его пост в канале. Организатор — первый участник.</summary>
    public async Task<(StaticGroup? Static, string? Error)> CreateAsync(ulong guildId, ulong channelId, IUser owner, string? projectKey,
        string? name, string? description, int size, string? character, string source)
    {
        if (CleanName(name) is not { } clean) return (null, T(guildId, "static.create.noName"));
        if (store.OwnedBy(guildId, owner.Id) >= StaticStore.MaxOwnedPerUser && !(BotOwner.CurrentId != 0 && owner.Id == BotOwner.CurrentId))
            return (null, T(guildId, "static.create.limit", ("max", StaticStore.MaxOwnedPerUser)));
        var pick = Character(guildId, owner.Id, projectKey, character);
        var s = new StaticGroup
        {
            GuildId = guildId,
            ChannelId = channelId,
            OwnerId = owner.Id,
            ProjectKey = projects.Effective(guildId, projectKey)?.Key,
            Name = clean,
            Description = CleanText(description, StaticGroup.MaxDescription),
            Size = Math.Clamp(size, StaticGroup.MinSize, StaticGroup.MaxSize),
            Members = [new StaticMember { UserId = owner.Id, CharacterName = pick?.Name, ClassIcon = pick?.Icon, Gs = pick?.Gs, Cp = pick?.Cp }]
        };
        s.Log(owner.Id, "created the static");
        store.Statics[s.Id] = s;
        if (await PostAsync(s) is { } error)
        {
            store.Statics.TryRemove(s.Id, out _);
            store.Save();
            return (null, error);
        }
        audit.Add(guildId, owner.Id, Name(owner), source, "lfg", "Created static", clean);
        return (s, null);
    }

    /// <summary>Подать заявку (персонаж и сообщение — по желанию). Организатору приходит ЛС с кнопками.</summary>
    public async Task<string?> ApplyAsync(StaticGroup s, IUser user, string? character, string? message)
    {
        var gid = s.GuildId;
        var pick = Character(gid, user.Id, s.ProjectKey, character);
        StaticApplicant? app = null;
        var error = await MutateAsync(s, () =>
        {
            if (!s.Recruiting) return T(gid, "static.apply.closed");
            if (s.FindMember(user.Id) is not null) return T(gid, "static.apply.member");
            if (s.FindApplicant(user.Id) is not null) return T(gid, "static.apply.already");
            if (s.Applicants.Count >= StaticGroup.MaxApplicants) return T(gid, "static.apply.tooMany");
            app = new StaticApplicant
            {
                UserId = user.Id, CharacterName = pick?.Name, ClassIcon = pick?.Icon, Gs = pick?.Gs, Cp = pick?.Cp,
                Message = CleanText(message, StaticGroup.MaxMessage)
            };
            s.Applicants.Add(app);
            s.Log(user.Id, "applied" + (pick is null ? "" : $" ({pick.Name})"));
            return null;
        });
        if (error is null && app is not null) _ = NotifyOwnerAsync(s, user, app);
        return error;
    }

    /// <summary>Добавить сообщение к своей заявке.</summary>
    public Task<string?> SetMessageAsync(StaticGroup s, ulong userId, string? message) => MutateAsync(s, () =>
    {
        if (s.FindApplicant(userId) is not { } a) return T(s.GuildId, "static.leave.none");
        a.Message = CleanText(message, StaticGroup.MaxMessage);
        return null;
    });

    /// <summary>Отозвать заявку или выйти из состава. Возвращает (ошибка, текст результата).</summary>
    public async Task<(string? Error, string? Done)> LeaveAsync(StaticGroup s, ulong userId)
    {
        string? done = null;
        var error = await MutateAsync(s, () =>
        {
            if (s.Applicants.RemoveAll(a => a.UserId == userId) > 0)
            {
                s.Log(userId, "withdrew the application");
                done = T(s.GuildId, "static.leave.withdrawn");
                return null;
            }
            if (s.FindMember(userId) is null) return T(s.GuildId, "static.leave.none");
            if (s.OwnerId == userId) return T(s.GuildId, "static.leave.owner");
            s.Members.RemoveAll(m => m.UserId == userId);
            s.Log(userId, "left the static");
            done = T(s.GuildId, "static.leave.left", ("name", s.Name));
            return null;
        });
        return (error, done);
    }

    /// <summary>Принять заявку: человек переходит в состав (если есть место) и получает ЛС.</summary>
    public async Task<string?> ApproveAsync(StaticGroup s, ulong userId, IUser by, string source)
    {
        var error = await MutateAsync(s, () =>
        {
            if (s.FindApplicant(userId) is not { } a) return T(s.GuildId, "static.manage.noSuchApplicant");
            if (s.IsFull) return T(s.GuildId, "static.manage.full", ("size", s.Size));
            s.Applicants.Remove(a);
            s.Members.Add(new StaticMember { UserId = a.UserId, CharacterName = a.CharacterName, ClassIcon = a.ClassIcon, Gs = a.Gs, Cp = a.Cp });
            s.Log(userId, $"accepted by {Name(by)}");
            return null;
        });
        if (error is not null) return error;
        audit.Add(s.GuildId, by.Id, Name(by), source, "lfg", "Accepted into static", $"{s.Name}: {userId}");
        _ = DmAsync(userId, T(s.GuildId, "static.dm.accepted", ("name", s.Name), ("server", ServerName(s)), ("link", Link(s))));
        return null;
    }

    public async Task<string?> DeclineAsync(StaticGroup s, ulong userId, IUser by, string source)
    {
        var error = await MutateAsync(s, () =>
        {
            if (s.Applicants.RemoveAll(a => a.UserId == userId) == 0) return T(s.GuildId, "static.manage.noSuchApplicant");
            s.Log(userId, $"declined by {Name(by)}");
            return null;
        });
        if (error is not null) return error;
        audit.Add(s.GuildId, by.Id, Name(by), source, "lfg", "Declined static application", $"{s.Name}: {userId}");
        _ = DmAsync(userId, T(s.GuildId, "static.dm.declined", ("name", s.Name), ("server", ServerName(s))));
        return null;
    }

    /// <summary>Перенести человека из состава обратно в заявки (на сайте — перетаскиванием).</summary>
    public Task<string?> ToApplicantsAsync(StaticGroup s, ulong userId, IUser by) => MutateAsync(s, () =>
    {
        if (userId == s.OwnerId) return T(s.GuildId, "static.manage.ownerStays");
        if (s.FindMember(userId) is not { } m) return T(s.GuildId, "static.manage.noSuchMember");
        s.Members.Remove(m);
        s.Applicants.Add(new StaticApplicant { UserId = m.UserId, CharacterName = m.CharacterName, ClassIcon = m.ClassIcon, Gs = m.Gs, Cp = m.Cp });
        s.Log(userId, $"moved back to applications by {Name(by)}");
        return null;
    });

    public async Task<string?> RemoveMemberAsync(StaticGroup s, ulong userId, IUser by, string source)
    {
        var error = await MutateAsync(s, () =>
        {
            if (userId == s.OwnerId) return T(s.GuildId, "static.manage.ownerStays");
            if (s.Members.RemoveAll(m => m.UserId == userId) == 0) return T(s.GuildId, "static.manage.noSuchMember");
            s.Log(userId, $"removed by {Name(by)}");
            return null;
        });
        if (error is not null) return error;
        audit.Add(s.GuildId, by.Id, Name(by), source, "lfg", "Removed from static", $"{s.Name}: {userId}");
        _ = DmAsync(userId, T(s.GuildId, "static.dm.removed", ("name", s.Name), ("server", ServerName(s))));
        return null;
    }

    public Task<string?> SetRecruitingAsync(StaticGroup s, bool open, IUser by) => MutateAsync(s, () =>
    {
        s.Recruiting = open;
        s.Log(by.Id, open ? "opened recruiting" : "closed recruiting");
        return null;
    });

    public Task<string?> EditAsync(StaticGroup s, string? name, string? description, int size, IUser by) => MutateAsync(s, () =>
    {
        if (CleanName(name) is not { } clean) return T(s.GuildId, "static.create.noName");
        size = Math.Clamp(size, StaticGroup.MinSize, StaticGroup.MaxSize);
        if (size < s.Members.Count) return T(s.GuildId, "static.manage.sizeTooSmall", ("count", s.Members.Count));
        s.Name = clean;
        s.Description = CleanText(description, StaticGroup.MaxDescription);
        s.Size = size;
        s.Log(by.Id, "edited the static");
        return null;
    });

    /// <summary>Сменить персонажа в составе или в заявке (своего — сам человек).</summary>
    public Task<string?> SetCharacterAsync(StaticGroup s, ulong userId, string? character) => MutateAsync(s, () =>
    {
        var pick = Character(s.GuildId, userId, s.ProjectKey, character);
        if (s.FindMember(userId) is { } m) { m.CharacterName = pick?.Name; m.ClassIcon = pick?.Icon; m.Gs = pick?.Gs; m.Cp = pick?.Cp; }
        else if (s.FindApplicant(userId) is { } a) { a.CharacterName = pick?.Name; a.ClassIcon = pick?.Icon; a.Gs = pick?.Gs; a.Cp = pick?.Cp; }
        else return T(s.GuildId, "static.leave.none");
        if (pick is not null) s.Log(userId, $"changed character → {pick.Name}");
        return null;
    });

    /// <summary>Удалить статик (только вручную: организатор или админ). Пост удаляется.</summary>
    public async Task DeleteAsync(StaticGroup s, IUser by, string source)
    {
        await s.Gate.WaitAsync();
        try
        {
            if (!store.Statics.TryRemove(s.Id, out _)) return;
            store.Save();
        }
        finally
        {
            s.Gate.Release();
        }
        audit.Add(s.GuildId, by.Id, Name(by), source, "lfg", "Deleted static", s.Name);
        if (s.MessageId != 0 && await GetChannelAsync(s.ChannelId) is { } channel)
        {
            try { await channel.DeleteMessageAsync(s.MessageId); }
            catch (Discord.Net.HttpException ex) { logger.LogInformation("Post of deleted static {Id}: {Error}", s.Id, ex.Message); }
        }
    }

    /// <summary>Пинг всего состава сообщением в канале статика (ответом на пост). Возвращает (ошибка, сколько пингнуто).</summary>
    public async Task<(string? Error, int Count)> PingAsync(StaticGroup s, IUser by, string? message, string source)
    {
        var gid = s.GuildId;
        var targets = s.Members.Select(m => m.UserId).Where(id => id != by.Id).Distinct().ToList();
        if (targets.Count == 0) return (T(gid, "static.ping.nobody"), 0);
        if (_lastPing.TryGetValue(s.Id, out var last) && DateTimeOffset.UtcNow - last < PingCooldown)
            return (T(gid, "static.ping.cooldown", ("seconds", (int)Math.Ceiling((PingCooldown - (DateTimeOffset.UtcNow - last)).TotalSeconds))), 0);
        if (await GetChannelAsync(s.ChannelId) is not { } channel) return (T(gid, "lfg.noAccess"), 0);
        _lastPing[s.Id] = DateTimeOffset.UtcNow;

        var text = CleanText(message, 1000);
        var head = T(gid, "static.ping.message", ("name", s.Name), ("user", $"<@{by.Id}>"), ("text", text is null ? "" : ": " + text));
        // Сообщение Discord — до 2000 символов: упоминания, которые не влезли, уходят следующими сообщениями
        var chunks = new List<string>();
        var current = new StringBuilder(head);
        foreach (var id in targets)
        {
            var mention = $" <@{id}>";
            if (current.Length + mention.Length > 1990) { chunks.Add(current.ToString()); current.Clear(); }
            current.Append(mention);
        }
        chunks.Add(current.ToString());
        var allowed = new AllowedMentions { UserIds = targets };
        try
        {
            var reference = s.MessageId != 0 ? new MessageReference(s.MessageId, s.ChannelId, failIfNotExists: false) : null;
            for (var i = 0; i < chunks.Count; i++)
                await channel.SendMessageAsync(chunks[i].Trim(), allowedMentions: allowed, messageReference: i == 0 ? reference : null);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not ping static {Id}", s.Id);
            _lastPing.TryRemove(s.Id, out _);
            return (T(gid, "lfg.noAccess"), 0);
        }
        audit.Add(gid, by.Id, Name(by), source, "lfg", "Pinged static members", s.Name);
        return (null, targets.Count);
    }

    // ================= ЛС =================

    private async Task NotifyOwnerAsync(StaticGroup s, IUser applicant, StaticApplicant app)
    {
        var gid = s.GuildId;
        var text = T(gid, "static.dm.newApplication",
            ("user", $"<@{applicant.Id}> ({Name(applicant)})"), ("name", s.Name), ("server", ServerName(s)),
            ("character", CharacterLine(s, app.UserId, app.CharacterName, app.ClassIcon, app.Gs, app.Cp)),
            ("message", app.Message is null ? "" : $"\n> {app.Message.Replace("\n", "\n> ")}"),
            ("link", Link(s)));
        var comps = new ComponentBuilder()
            .WithButton(T(gid, "static.dm.accept"), $"stdm:ok:{s.Id}:{applicant.Id}", ButtonStyle.Success, new Emoji("✅"))
            .WithButton(T(gid, "static.dm.decline"), $"stdm:no:{s.Id}:{applicant.Id}", ButtonStyle.Danger, new Emoji("❌"))
            .Build();
        await DmAsync(s.OwnerId, text, comps);
    }

    private async Task DmAsync(ulong userId, string text, MessageComponent? components = null)
    {
        try
        {
            IUser? user = client.GetUser(userId);
            user ??= await client.Rest.GetUserAsync(userId);
            if (user is null) return;
            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync(text, components: components, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex) when (ex is Discord.Net.HttpException or InvalidOperationException)
        {
            logger.LogInformation("Static DM to {User} not delivered: {Error}", userId, ex.Message);
        }
    }

    private string ServerName(StaticGroup s) => client.GetGuild(s.GuildId)?.Name ?? "Discord";

    private static string Link(StaticGroup s) => s.MessageId != 0 ? JumpUrl(s) : SiteLink.StaticUrl(s.GuildId, s.Id) ?? "";

    private static string Name(IUser user) => (user as IGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username;
}
