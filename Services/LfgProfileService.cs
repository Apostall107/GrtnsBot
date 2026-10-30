using Discord;
using Discord.WebSocket;
using LfgBot.Models;

namespace LfgBot.Services;

/// <summary>
/// Профиль персонажа в сборе: класс (одна из ролей сервера, которые есть у игрока) и заметка.
/// Меню показывается под ответом после записи; то же самое — командами /character class и /character note.
/// </summary>
public sealed class LfgProfileService(DiscordSocketClient client, LfgStore store, CharacterStore characters, LfgSignupService signup,
    LfgProjectStore projects)
{
    public const string NoClassValue = "0";

    /// <summary>Роли игрока, которые можно выбрать классом (без @everyone и ролей ботов/интеграций), сверху — старшие.</summary>
    /// <remarks>Если админ задал иконки классов (LFG → Classes) — только эти роли.</remarks>
    public IReadOnlyList<SocketRole> ClassRoles(ulong guildId, ulong userId)
    {
        if (client.GetGuild(guildId)?.GetUser(userId) is not { } member) return new List<SocketRole>();
        var configured = projects.ClassIcons(guildId);
        return member.Roles
            .Where(r => !r.IsEveryone && !r.IsManaged && (configured.Count == 0 || configured.ContainsKey(r.Id)))
            .OrderByDescending(r => r.Position).ToList();
    }

    /// <summary>Персонажи игрока для выбора при записи в этот сбор (проект сбора): имя, заметка, иконка класса.</summary>
    public IReadOnlyList<(string Name, string? Note, string? Icon)> PickerList(LfgGroup group, ulong userId)
    {
        var scope = signup.Scope(group);
        var guild = client.GetGuild(group.GuildId);
        return characters.Get(group.GuildId, userId, scope).Select(name =>
        {
            var info = characters.GetInfo(group.GuildId, userId, name, scope);
            var role = info?.ClassRoleId is { } id ? guild?.GetRole(id) : null;
            return (name, info?.Comment, projects.ClassIcon(group.GuildId, role));
        }).ToList();
    }

    /// <summary>Иконка класса для показа (заданная админом или эмодзи роли).</summary>
    public string? Icon(ulong guildId, IRole? role) => projects.ClassIcon(guildId, role);

    /// <summary>Меню «класс» + кнопка «заметка» под ответом о записи. null — человека нет в сборе.</summary>
    public MessageComponent? Controls(LfgGroup group, ulong userId)
    {
        var current = CurrentEntry(group, userId);
        if (current is null) return null;

        var builder = new ComponentBuilder();
        var roles = ClassRoles(group.GuildId, userId);
        if (roles.Count > 0)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId($"lfgp:class:{group.Id}")
                .WithPlaceholder($"🎭 Class role for {Trim(current.Value.Name ?? "this character", 60)}")
                .AddOption("No class", NoClassValue, "Don't show a class role");
            foreach (var role in roles.Take(24))
            {
                var option = new SelectMenuOptionBuilder()
                    .WithLabel(Trim(role.Name, 100))
                    .WithValue(role.Id.ToString())
                    .WithDefault(current.Value.ClassRoleId == role.Id);
                if (LfgProjectStore.ToEmote(projects.ClassIcon(group.GuildId, role)) is { } emote) option.WithEmote(emote);
                menu.AddOption(option);
            }
            builder.WithSelectMenu(menu, row: 0);
        }

        builder.WithButton(string.IsNullOrEmpty(current.Value.Comment) ? "Note & GS" : "Edit note & GS",
            $"lfgp:note:{group.Id}", ButtonStyle.Secondary, new Emoji("💬"), row: 1);
        return builder.Build();
    }

    /// <summary>Окно заметки с текущим текстом. null — человека нет в сборе.</summary>
    public Modal? NoteModal(LfgGroup group, ulong userId)
    {
        if (CurrentEntry(group, userId) is not { } current) return null;
        var gs = characters.GetInfo(group.GuildId, userId, current.Name, signup.Scope(group))?.Gs;
        return new ModalBuilder()
            .WithTitle("Character note & GS")
            .WithCustomId($"lfgp:notemodal:{group.Id}")
            .AddTextInput("GS (gear score, empty = remove)", "gs", TextInputStyle.Short,
                placeholder: "e.g. 1450", maxLength: 6, required: false, value: gs?.ToString())
            .AddTextInput("Note (empty = remove)", "note", TextInputStyle.Paragraph,
                placeholder: "e.g. can lead, healer alt", maxLength: CharacterStore.MaxCommentLength,
                required: false, value: current.Comment)
            .Build();
    }

    public Task<string> SetClassAsync(string groupId, ulong userId, string value)
    {
        if (!store.Groups.TryGetValue(groupId, out var group)) return Task.FromResult("This group is no longer active.");

        ulong? roleId = null;
        if (value != NoClassValue)
        {
            if (!ulong.TryParse(value, out var id) || ClassRoles(group.GuildId, userId).All(r => r.Id != id))
                return Task.FromResult("You don't have this role.");
            roleId = id;
        }

        return UpdateAsync(group, userId, info => (true, roleId, false, null),
            roleId is { } r ? $"🎭 Class set: <@&{r}>" : "🎭 Class removed.");
    }

    public Task<string> SetCommentAsync(string groupId, ulong userId, string? comment, string? gsText = null)
    {
        if (!store.Groups.TryGetValue(groupId, out var group)) return Task.FromResult("This group is no longer active.");
        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        int? gs = null;
        if (!string.IsNullOrWhiteSpace(gsText))
        {
            if (!int.TryParse(gsText.Trim(), out var v) || v is < 0 or > 99999) return Task.FromResult("GS must be a number (0–99999).");
            gs = v;
        }
        return UpdateAsync(group, userId, info => (false, null, true, text),
            $"💬 Saved: note {(text is null ? "removed" : "set")}, GS {(gs?.ToString() ?? "removed")}",
            name => characters.SetGs(group.GuildId, userId, name, gs, signup.Scope(group)));
    }

    /// <summary>
    /// После /character class|note: обновить все активные сборы, где человек записан этим персонажем.
    /// </summary>
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
            if (CurrentEntry(group, userId) is not { } entry) return "You are not in this group anymore.";
            name = entry.Name;
            if (string.IsNullOrWhiteSpace(name)) return "Your signup has no character name.";

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
        return $"{okText} — for **{Format.Sanitize(name!)}**. It's saved for this character and shown in your groups.";
    }

    private static (string? Name, ulong? ClassRoleId, string? Comment)? CurrentEntry(LfgGroup group, ulong userId)
    {
        if (group.Slots.Find(s => s.UserId == userId) is { } slot) return (slot.CharacterName, slot.ClassRoleId, slot.Comment);
        if (group.FindReserve(userId) is { } r) return (r.CharacterName, r.ClassRoleId, r.Comment);
        return null;
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
