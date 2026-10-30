using System.Text;
using Discord;
using Discord.Interactions;
using LfgBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Modules;

/// <summary>
/// Персонажи игрока. У каждого проекта (игры, LFG → Projects) свои персонажи;
/// добавлять и менять персонажей проекта можно только с его ролью. Без «project» — проект по умолчанию.
/// </summary>
[Group("character", "Saved characters for group signups")]
[RequireContext(ContextType.Guild)]
public sealed class CharacterModule(CharacterStore store, LfgProfileService profiles, LfgProjectStore projects, AuditLog audit)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("list", "Show my saved characters (all projects)")]
    public async Task ListAsync()
    {
        var sb = new StringBuilder();
        var all = projects.GetProjects(Context.Guild.Id);
        // Без проектов — один общий список
        var scopes = all.Count == 0
            ? new List<(string? Scope, string? Title, string? Key)> { (null, null, null) }
            : all.Select(p => (projects.Scope(Context.Guild.Id, p.Key), (string?)p.Name, (string?)p.Key)).ToList();

        foreach (var (scope, title, key) in scopes)
        {
            var list = store.Get(Context.Guild.Id, Context.User.Id, scope);
            if (list.Count == 0) continue;
            sb.AppendLine(title is null ? T("char.listTitle") : T("char.listProject", ("project", Format.Sanitize(title))));
            foreach (var name in list)
            {
                var info = store.GetInfo(Context.Guild.Id, Context.User.Id, name, scope);
                var icon = profiles.Icon(Context.Guild.Id, info?.ClassRoleId is { } r ? Context.Guild.GetRole(r) : null, key);
                var gsText = info?.Gs is { } g ? $" · GS {g}" : "";
                sb.AppendLine($"• {(icon is null ? "" : icon + " ")}**{Format.Sanitize(name)}**{gsText}");
                if (!string.IsNullOrWhiteSpace(info?.Comment)) sb.AppendLine($"-# 💬 {Format.Sanitize(info.Comment)}");
            }
        }

        if (sb.Length == 0)
        {
            await RespondAsync(T("char.none"), ephemeral: true);
            return;
        }
        if (T("char.listHint") is { Length: > 0 } hint) sb.AppendLine(hint);
        await RespondAsync(sb.ToString(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("add", "Add a character")]
    public async Task AddAsync(
        [Summary("name", "Character name"), MaxLength(CharacterStore.MaxNameLength)] string name,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (await ScopeAsync(project) is not { } scope) return;
        store.Add(Context.Guild.Id, Context.User.Id, name, scope.Value);
        await RespondAsync(T("char.added", ("name", Format.Sanitize(name.Trim())), ("project", scope.Label)), ephemeral: true);
    }

    [SlashCommand("class", "Set the class of a character (one of the project's classes); empty = remove")]
    public async Task ClassAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("role", "One of the project's class roles, shown as an icon next to your name")] IRole? role = null,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (await ScopeAsync(project) is not { } scope || await ExactAsync(name, scope.Value) is not { } exact) return;

        if (role is not null && profiles.ClassRoles(Context.Guild.Id, Context.User.Id, scope.ProjectKey).All(r => r.Id != role.Id))
        {
            await RespondAsync(T("char.classUnavailable"), ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        store.SetInfo(Context.Guild.Id, Context.User.Id, exact, true, role?.Id, false, null, scope.Value);
        var groups = await profiles.RefreshCharacterAsync(Context.Guild.Id, Context.User.Id, exact, scope.Value);
        audit.FromDiscord(Context.Guild.Id, Context.User, "lfg", "Set character class", $"{exact}: {role?.Name ?? "none"}");
        await FollowupAsync((role is null ? T("char.classRemoved", ("name", Format.Sanitize(exact))) : T("char.classSet", ("name", Format.Sanitize(exact)), ("role", $"<@&{role.Id}>"))) +
                            Updated(groups),
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("note", "Set a note for a character (you see it when picking the character); empty = remove")]
    public async Task NoteAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("text", "e.g. can lead, healer alt"), MaxLength(CharacterStore.MaxCommentLength)] string? text = null,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (await ScopeAsync(project) is not { } scope || await ExactAsync(name, scope.Value) is not { } exact) return;

        store.SetInfo(Context.Guild.Id, Context.User.Id, exact, false, null, true, text, scope.Value);
        audit.FromDiscord(Context.Guild.Id, Context.User, "lfg", "Set character note", $"{exact}: {text ?? "removed"}");
        await RespondAsync(T(string.IsNullOrWhiteSpace(text) ? "char.noteRemoved" : "char.noteSaved", ("name", Format.Sanitize(exact))),
            ephemeral: true);
    }

    [SlashCommand("gs", "Set the gear score (GS) of a character — used by group requirements; empty = remove")]
    public async Task GsAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("gs", "Gear score, e.g. 1450")] int? gs = null,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (gs is < 0 or > 99999)
        {
            await RespondAsync(T("char.badGs"), ephemeral: true);
            return;
        }
        if (await ScopeAsync(project) is not { } scope || await ExactAsync(name, scope.Value) is not { } exact) return;

        await DeferAsync(ephemeral: true);
        store.SetGs(Context.Guild.Id, Context.User.Id, exact, gs, scope.Value);
        var groups = await profiles.RefreshCharacterAsync(Context.Guild.Id, Context.User.Id, exact, scope.Value);
        audit.FromDiscord(Context.Guild.Id, Context.User, "lfg", "Set character GS", $"{exact}: {gs?.ToString() ?? "removed"}");
        await FollowupAsync((gs is null ? T("char.gsRemoved", ("name", Format.Sanitize(exact))) : T("char.gsSet", ("name", Format.Sanitize(exact)), ("gs", gs))) +
                            Updated(groups), ephemeral: true);
    }

    [SlashCommand("rename", "Rename a character (class, note, GS and active groups keep it)")]
    public async Task RenameAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("new_name", "New name"), MaxLength(CharacterStore.MaxNameLength)] string newName,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (await ScopeAsync(project) is not { } scope || await ExactAsync(name, scope.Value) is not { } exact) return;
        if (store.Rename(Context.Guild.Id, Context.User.Id, exact, newName, scope.Value) is { } error)
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var clean = newName.Replace("`", "").Trim();
        var groups = await profiles.RenameInGroupsAsync(Context.Guild.Id, Context.User.Id, exact, clean, scope.Value);
        audit.FromDiscord(Context.Guild.Id, Context.User, "lfg", "Renamed character", $"{exact} → {clean}");
        await FollowupAsync(T("char.renamed", ("old", Format.Sanitize(exact)), ("new", Format.Sanitize(clean))) + Updated(groups), ephemeral: true);
    }

    [SlashCommand("remove", "Remove a character")]
    public async Task RemoveAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (await ScopeAsync(project) is not { } scope) return;
        var removed = store.Remove(Context.Guild.Id, Context.User.Id, name, scope.Value);
        await RespondAsync(removed ? T("char.removed", ("name", Format.Sanitize(name))) : T("char.notFound"), ephemeral: true);
    }

    /// <summary>Проект из опции → ключ хранения. null — ответ с ошибкой уже отправлен (нет такого проекта или нет его роли).</summary>
    private async Task<(string? Value, string Label, string? ProjectKey)?> ScopeAsync(string? projectKey)
    {
        var project = projects.Effective(Context.Guild.Id, string.IsNullOrWhiteSpace(projectKey) ? null : projectKey);
        if (!string.IsNullOrWhiteSpace(projectKey) && projects.Find(Context.Guild.Id, projectKey) is null)
        {
            await RespondAsync(T("char.unknownProject"), ephemeral: true);
            return null;
        }
        if (!LfgProjectStore.CanUse(Context.Guild.GetUser(Context.User.Id), project))
        {
            await RespondAsync(T("char.projectRole", ("project", project!.Name), ("roles", string.Join(T("lfg.or"), project.RoleIds.Select(id => $"<@&{id}>")))),
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return null;
        }
        return (projects.Scope(Context.Guild.Id, project?.Key), project is null ? "" : $" ({Format.Sanitize(project.Name)})", project?.Key);
    }

    private async Task<string?> ExactAsync(string name, string? scope)
    {
        var exact = store.Get(Context.Guild.Id, Context.User.Id, scope)
            .FirstOrDefault(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is null) await RespondAsync(T("char.notFoundAdd"), ephemeral: true);
        return exact;
    }

    private string T(string key, params (string Name, object? Value)[] args) => Tx.Get(Context.Guild.Id, key, args);

    private string Updated(int groups) => groups > 0 ? T("char.updatedGroups", ("count", groups)) : "";
}

/// <summary>Проекты, роли которых есть у игрока.</summary>
public sealed class ProjectAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        var projects = services.GetRequiredService<LfgProjectStore>();
        var member = (context.Guild as Discord.WebSocket.SocketGuild)?.GetUser(context.User.Id);
        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";
        var results = projects.GetProjects(context.Guild.Id)
            .Where(p => LfgProjectStore.CanUse(member, p) && p.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => new AutocompleteResult(p.Name, p.Key));
        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}

/// <summary>Персонажи игрока в выбранном (в опции project) проекте.</summary>
public sealed class CharacterAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        var store = services.GetRequiredService<CharacterStore>();
        var projects = services.GetRequiredService<LfgProjectStore>();
        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";
        var projectKey = autocompleteInteraction.Data.Options.FirstOrDefault(o => o.Name == "project")?.Value?.ToString();
        var scope = projects.Scope(context.Guild.Id, string.IsNullOrWhiteSpace(projectKey) ? null : projectKey);

        var results = store.Get(context.Guild.Id, context.User.Id, scope)
            .Where(n => n.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(n => new AutocompleteResult(n, n));

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}
