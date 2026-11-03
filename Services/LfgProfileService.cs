using Discord;
using Discord.WebSocket;
using LfgBot.Models;

namespace LfgBot.Services;

/// <summary>
/// Профиль персонажа в сборе: класс (одна из ролей сервера, которые есть у игрока) и заметка.
/// Меню показывается под ответом после записи; то же самое — командами /character class и /character note.
/// </summary>
/// <summary>Персонаж в меню выбора при записи: ник, заметка, иконка класса, GS и боевая мощь (CP).</summary>
public sealed record PickerCharacter(string Name, string? Note, string? Icon, int? Gs = null, int? Cp = null);

public sealed class LfgProfileService(DiscordSocketClient client, LfgStore store, CharacterStore characters, LfgSignupService signup,
    LfgProjectStore projects)
{
    public const string NoClassValue = "0";

    /// <summary>Роли, которые можно выбрать классом в проекте, сверху — старшие.</summary>
    /// <remarks>Заданы классы (LFG → Classes, свои у проекта или общие) — любой из них, роль иметь не нужно.
    /// Не заданы — роли самого игрока (без @everyone и ролей ботов/интеграций).</remarks>
    public IReadOnlyList<SocketRole> ClassRoles(ulong guildId, ulong userId, string? projectKey = null)
    {
        if (client.GetGuild(guildId) is not { } guild) return new List<SocketRole>();
        // Классы проекта (свои или общие) — любой из них, иметь роль не нужно
        var configured = projects.EffectiveClassIcons(guildId, projectKey);
        if (configured.Count > 0)
            return configured.Keys.Select(guild.GetRole).OfType<SocketRole>()
                .OrderByDescending(r => r.Position).ToList();
        // Классы не настроены — как раньше: роли самого игрока
        if (guild.GetUser(userId) is not { } member) return new List<SocketRole>();
        return member.Roles.Where(r => !r.IsEveryone && !r.IsManaged)
            .OrderByDescending(r => r.Position).ToList();
    }

    /// <summary>Персонажи игрока для выбора при записи в этот сбор (проект сбора): имя, заметка, иконка класса.</summary>
    /// <param name="slotRole">Роль записи: показываются только персонажи, чей класс может её играть (LFG → Classes). null — все.</param>
    public IReadOnlyList<PickerCharacter> PickerList(LfgGroup group, ulong userId, SlotRole? slotRole = null)
    {
        var scope = signup.Scope(group);
        var guild = client.GetGuild(group.GuildId);
        return characters.Get(group.GuildId, userId, scope).Select(name =>
        {
            var info = characters.GetInfo(group.GuildId, userId, name, scope);
            var role = info?.ClassRoleId is { } id ? guild?.GetRole(id) : null;
            return (Character: new PickerCharacter(name, info?.Comment, projects.ClassIcon(group.GuildId, role, group.ProjectKey), info?.Gs,
                info?.Armory is { Error: null } a ? a.CombatPower : null), ClassId: info?.ClassRoleId);
        })
        .Where(x => slotRole is not { } r || projects.ClassFits(group.GuildId, group.ProjectKey, x.ClassId, r))
        .Select(x => x.Character).ToList();
    }

    /// <summary>Персонажи игрока в проекте (для выбора организатором при создании сбора).</summary>
    public IReadOnlyList<PickerCharacter> PickerFor(ulong guildId, ulong userId, string? projectKey)
    {
        var scope = projects.Scope(guildId, projectKey);
        var guild = client.GetGuild(guildId);
        return characters.Get(guildId, userId, scope).Select(name =>
        {
            var info = characters.GetInfo(guildId, userId, name, scope);
            var role = info?.ClassRoleId is { } id ? guild?.GetRole(id) : null;
            return new PickerCharacter(name, info?.Comment, projects.ClassIcon(guildId, role, projectKey), info?.Gs,
                info?.Armory is { Error: null } a ? a.CombatPower : null);
        }).ToList();
    }

    /// <summary>Роль слота для персонажа по его классу (LFG → Classes): первая из ролей класса; null — класс не задан или играет любую.</summary>
    public SlotRole? OwnerSlotRole(ulong guildId, ulong userId, string? characterName, string? projectKey)
    {
        if (characterName is null) return null;
        var info = characters.GetInfo(guildId, userId, characterName, projects.Scope(guildId, projectKey));
        return info?.ClassRoleId is { } id && projects.EffectiveClassSlotRoles(guildId, projectKey).TryGetValue(id, out var roles) && roles.Count > 0
            ? roles[0] : null;
    }

    /// <summary>Иконка класса для показа (заданная админом или эмодзи роли).</summary>
    public string? Icon(ulong guildId, IRole? role, string? projectKey = null) => projects.ClassIcon(guildId, role, projectKey);

    /// <summary>
    /// Под ответом о записи — только то, что у персонажа ещё не заполнено: меню класса (класса нет) и кнопка «Note &amp; GS» (GS нет).
    /// Всё заполнено — null: класс и GS меняют на сайте (My characters) или командой /character.
    /// </summary>
    public MessageComponent? Controls(LfgGroup group, ulong userId)
    {
        var current = CurrentEntry(group, userId);
        if (current is null) return null;

        var gid = group.GuildId;
        var info = characters.GetInfo(gid, userId, current.Value.Name, signup.Scope(group));
        // Привязанный к оружейной: класс и GS приходят оттуда — вручную не спрашиваем
        var needClass = info?.ClassRoleId is null && !CharacterStore.IsLinked(info);
        var needGs = info?.Gs is null && !characters.GsFromArmory(gid, info);
        if (!needClass && !needGs) return null;

        var builder = new ComponentBuilder();
        // Только классы, которые могут играть роль, с которой человек записался
        var taken = group.Slots.Find(s => s.UserId == userId) is { } seat ? seat.TakenAs ?? seat.Role : group.FindReserve(userId)?.Role ?? SlotRole.Any;
        var roles = needClass
            ? ClassRoles(group.GuildId, userId, group.ProjectKey).Where(r => projects.ClassFits(gid, group.ProjectKey, r.Id, taken)).ToList()
            : [];
        if (roles.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId($"lfgp:class:{group.Id}")
                .WithPlaceholder(Tx.Clip(Tx.Get(gid, "lfg.profile.classPlaceholder", ("name", Trim(current.Value.Name ?? Tx.Get(gid, "lfg.profile.thisCharacter"), 60))), 150))
                .AddOption(Tx.Get(gid, "lfg.profile.noClass"), NoClassValue, Tx.Get(gid, "lfg.profile.noClassOption"));
            foreach (var role in roles.Take(24))
            {
                var option = new SelectMenuOptionBuilder()
                    .WithLabel(Trim(role.Name, 100))
                    .WithValue(role.Id.ToString())
                    .WithDefault(current.Value.ClassRoleId == role.Id);
                if (LfgProjectStore.ToEmote(projects.ClassIcon(group.GuildId, role, group.ProjectKey)) is { } emote) option.WithEmote(emote);
                menu.AddOption(option);
            }
            builder.WithSelectMenu(menu, row: 0);
        }

        if (needGs)
            builder.WithButton(Tx.Get(gid, string.IsNullOrEmpty(current.Value.Comment) ? "lfg.profile.note" : "lfg.profile.editNote"),
                $"lfgp:note:{group.Id}", ButtonStyle.Secondary, new Emoji("💬"), row: 1);
        return roles.Count == 0 && !needGs ? null : builder.Build();
    }

    /// <summary>Окно заметки с текущим текстом. null — человека нет в сборе.</summary>
    public Modal? NoteModal(LfgGroup group, ulong userId)
    {
        if (CurrentEntry(group, userId) is not { } current) return null;
        var gs = characters.GetInfo(group.GuildId, userId, current.Name, signup.Scope(group))?.Gs;
        var gid = group.GuildId;
        return new ModalBuilder()
            .WithTitle(Tx.Get(gid, "lfg.profile.modalTitle"))
            .WithCustomId($"lfgp:notemodal:{group.Id}")
            .AddTextInput(Tx.Get(gid, "lfg.profile.gsLabel"), "gs", TextInputStyle.Short,
                placeholder: Tx.Get(gid, "lfg.profile.gsHint"), maxLength: 6, required: false, value: gs?.ToString())
            .AddTextInput(Tx.Get(gid, "lfg.profile.noteLabel"), "note", TextInputStyle.Paragraph,
                placeholder: Tx.Get(gid, "lfg.profile.noteHint"), maxLength: CharacterStore.MaxCommentLength,
                required: false, value: current.Comment)
            .Build();
    }

    public Task<string> SetClassAsync(string groupId, ulong userId, string value)
    {
        if (!store.Groups.TryGetValue(groupId, out var group)) return Task.FromResult(Tx.Get(0, "lfg.gone"));

        ulong? roleId = null;
        if (value != NoClassValue)
        {
            if (!ulong.TryParse(value, out var id) || ClassRoles(group.GuildId, userId, group.ProjectKey).All(r => r.Id != id))
                return Task.FromResult(Tx.Get(group.GuildId, "lfg.profile.classUnavailable"));
            roleId = id;
        }
        if (CurrentEntry(group, userId) is { } entry &&
            CharacterStore.IsLinked(characters.GetInfo(group.GuildId, userId, entry.Name, signup.Scope(group))))
            return Task.FromResult(Tx.Get(group.GuildId, "char.classArmory", ("name", Format.Sanitize(entry.Name ?? ""))));

        return UpdateAsync(group, userId, info => (true, roleId, false, null),
            roleId is { } r ? Tx.Get(group.GuildId, "lfg.profile.classSet", ("role", $"<@&{r}>")) : Tx.Get(group.GuildId, "lfg.profile.classRemoved"));
    }

    public Task<string> SetCommentAsync(string groupId, ulong userId, string? comment, string? gsText = null)
    {
        if (!store.Groups.TryGetValue(groupId, out var group)) return Task.FromResult(Tx.Get(0, "lfg.gone"));
        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        int? gs = null;
        if (!string.IsNullOrWhiteSpace(gsText))
        {
            if (!int.TryParse(gsText.Trim(), out var v) || v is < 0 or > 99999) return Task.FromResult(Tx.Get(group.GuildId, "lfg.profile.badGs"));
            gs = v;
        }
        // GS привязанного персонажа — из оружейной: введённое значение не применяется, в ответе — текущий GS
        if (CurrentEntry(group, userId) is { } entry &&
            characters.GetInfo(group.GuildId, userId, entry.Name, signup.Scope(group)) is { } current && characters.GsFromArmory(group.GuildId, current))
            gs = current.Gs;
        return UpdateAsync(group, userId, info => (false, null, true, text),
            Tx.Get(group.GuildId, "lfg.profile.saved",
                ("note", Tx.Get(group.GuildId, text is null ? "lfg.profile.removed" : "lfg.profile.set")),
                ("gs", gs?.ToString() ?? Tx.Get(group.GuildId, "lfg.profile.removed"))),
            name => characters.SetGs(group.GuildId, userId, name, gs, signup.Scope(group)));
    }

    /// <summary>
    /// После /character class|note: обновить все активные сборы, где человек записан этим персонажем.
    /// </summary>
    /// <summary>Персонаж переименован: новое имя во всех активных сборах этого проекта (в группе и резерве).</summary>
    public async Task<int> RenameInGroupsAsync(ulong guildId, ulong userId, string oldName, string newName, string? scope = null)
    {
        var updated = 0;
        foreach (var group in store.Groups.Values.Where(g => g.GuildId == guildId && !g.IsClosed && signup.Scope(g) == scope).ToList())
        {
            await group.Gate.WaitAsync();
            try
            {
                var changed = false;
                foreach (var slot in group.Slots.Where(s => s.UserId == userId &&
                             string.Equals(s.CharacterName, oldName, StringComparison.OrdinalIgnoreCase)))
                {
                    slot.CharacterName = newName;
                    changed = true;
                }
                if (group.FindReserve(userId) is { } reserve && string.Equals(reserve.CharacterName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    reserve.CharacterName = newName;
                    changed = true;
                }
                if (!changed) continue;
                signup.ApplyProfile(group, userId);
                store.SaveGroups();
                await signup.RefreshPostAsync(group);
                updated++;
            }
            finally
            {
                group.Gate.Release();
            }
        }
        return updated;
    }

    public async Task<int> RefreshCharacterAsync(ulong guildId, ulong userId, string name, string? scope = null)
    {
        var updated = 0;
        // Только сборы того же проекта: у разных проектов персонажи с одинаковым именем — разные
        foreach (var group in store.Groups.Values.Where(g => g.GuildId == guildId && !g.IsClosed && signup.Scope(g) == scope).ToList())
        {
            await group.Gate.WaitAsync();
            try
            {
                var entry = CurrentEntry(group, userId);
                if (entry is null || !string.Equals(entry.Value.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                signup.ApplyProfile(group, userId);
                store.SaveGroups();
                await signup.RefreshPostAsync(group);
                updated++;
            }
            finally
            {
                group.Gate.Release();
            }
        }
        return updated;
    }

    private async Task<string> UpdateAsync(LfgGroup group, ulong userId,
        Func<CharacterInfo?, (bool SetClass, ulong? ClassRoleId, bool SetComment, string? Comment)> change, string okText,
        Action<string>? extra = null)
    {
        string? name;
        await group.Gate.WaitAsync();
        try
        {
            if (CurrentEntry(group, userId) is not { } entry) return Tx.Get(group.GuildId, "lfg.profile.notInGroup");
            name = entry.Name;
            if (string.IsNullOrWhiteSpace(name)) return Tx.Get(group.GuildId, "lfg.profile.noName");

            var scope = signup.Scope(group);
            var (setClass, classRoleId, setComment, comment) = change(characters.GetInfo(group.GuildId, userId, name, scope));
            characters.SetInfo(group.GuildId, userId, name, setClass, classRoleId, setComment, comment, scope);
            extra?.Invoke(name);
            signup.ApplyProfile(group, userId);
            store.SaveGroups();
            await signup.RefreshPostAsync(group);
        }
        finally
        {
            group.Gate.Release();
        }

        // Тот же персонаж может стоять и в других сборах
        await RefreshCharacterAsync(group.GuildId, userId, name!, signup.Scope(group));
        return Tx.Get(group.GuildId, "lfg.profile.done", ("result", okText), ("name", Format.Sanitize(name!)));
    }

    private static (string? Name, ulong? ClassRoleId, string? Comment)? CurrentEntry(LfgGroup group, ulong userId)
    {
        if (group.Slots.Find(s => s.UserId == userId) is { } slot) return (slot.CharacterName, slot.ClassRoleId, slot.Comment);
        if (group.FindReserve(userId) is { } r) return (r.CharacterName, r.ClassRoleId, r.Comment);
        return null;
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
