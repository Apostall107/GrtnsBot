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
            ? new List<(string? Scope, string? Title)> { (null, null) }
            : all.Select(p => (projects.Scope(Context.Guild.Id, p.Key), (string?)p.Name)).ToList();

        foreach (var (scope, title) in scopes)
        {
            var list = store.Get(Context.Guild.Id, Context.User.Id, scope);
            if (list.Count == 0) continue;
            sb.AppendLine(title is null ? "**Your characters:**" : $"**{Format.Sanitize(title)}:**");
            foreach (var name in list)
            {
                var info = store.GetInfo(Context.Guild.Id, Context.User.Id, name, scope);
                var icon = profiles.Icon(Context.Guild.Id, info?.ClassRoleId is { } r ? Context.Guild.GetRole(r) : null);
                var gsText = info?.Gs is { } g ? $" · GS {g}" : "";
                sb.AppendLine($"• {(icon is null ? "" : icon + " ")}**{Format.Sanitize(name)}**{gsText}");
                if (!string.IsNullOrWhiteSpace(info?.Comment)) sb.AppendLine($"-# 💬 {Format.Sanitize(info.Comment)}");
            }
        }

        if (sb.Length == 0)
        {
            await RespondAsync("No saved characters. They are added when you sign up for a group or via `/character add`.", ephemeral: true);
            return;
        }
        sb.AppendLine("-# `/character class`, `/character gs`, `/character note` — add `project:` for another game.");
        await RespondAsync(sb.ToString(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("add", "Add a character")]
    public async Task AddAsync(
        [Summary("name", "Character name"), MaxLength(CharacterStore.MaxNameLength)] string name,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (await ScopeAsync(project) is not { } scope) return;
        store.Add(Context.Guild.Id, Context.User.Id, name, scope.Value);
        await RespondAsync($"Added: **{Format.Sanitize(name.Trim())}**{scope.Label}", ephemeral: true);
    }

    [SlashCommand("class", "Set the class role of a character (one of your roles); empty = remove")]
    public async Task ClassAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("role", "One of your class roles, shown as an icon next to your name")] IRole? role = null,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (await ScopeAsync(project) is not { } scope || await ExactAsync(name, scope.Value) is not { } exact) return;

        if (role is not null && profiles.ClassRoles(Context.Guild.Id, Context.User.Id).All(r => r.Id != role.Id))
        {
            await RespondAsync("You can only pick a class role you have (see LFG → Classes on the site).", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        store.SetInfo(Context.Guild.Id, Context.User.Id, exact, true, role?.Id, false, null, scope.Value);
        var groups = await profiles.RefreshCharacterAsync(Context.Guild.Id, Context.User.Id, exact, scope.Value);
        audit.FromDiscord(Context.Guild.Id, Context.User, "lfg", "Set character class", $"{exact}: {role?.Name ?? "none"}");
        await FollowupAsync((role is null ? $"Class removed from **{Format.Sanitize(exact)}**." : $"**{Format.Sanitize(exact)}** — class <@&{role.Id}>.") +
                            (groups > 0 ? $" Updated {groups} active group(s)." : ""),
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
        await RespondAsync(string.IsNullOrWhiteSpace(text) ? $"Note removed from **{Format.Sanitize(exact)}**." : $"Note saved for **{Format.Sanitize(exact)}**.",
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
            await RespondAsync("GS must be 0–99999.", ephemeral: true);
            return;
        }
        if (await ScopeAsync(project) is not { } scope || await ExactAsync(name, scope.Value) is not { } exact) return;

        await DeferAsync(ephemeral: true);
        store.SetGs(Context.Guild.Id, Context.User.Id, exact, gs, scope.Value);
        var groups = await profiles.RefreshCharacterAsync(Context.Guild.Id, Context.User.Id, exact, scope.Value);
        audit.FromDiscord(Context.Guild.Id, Context.User, "lfg", "Set character GS", $"{exact}: {gs?.ToString() ?? "removed"}");
        await FollowupAsync((gs is null ? $"GS removed from **{Format.Sanitize(exact)}**." : $"**{Format.Sanitize(exact)}** — GS {gs}.") +
                            (groups > 0 ? $" Updated {groups} active group(s)." : ""), ephemeral: true);
    }

    [SlashCommand("remove", "Remove a character")]
    public async Task RemoveAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("project", "Game / project (empty = default)"), Autocomplete(typeof(ProjectAutocomplete))] string? project = null)
    {
        if (await ScopeAsync(project) is not { } scope) return;
        var removed = store.Remove(Context.Guild.Id, Context.User.Id, name, scope.Value);
        await RespondAsync(removed ? $"Removed: **{Format.Sanitize(name)}**" : "Character not found.", ephemeral: true);
    }

    /// <summary>Проект из опции → ключ хранения. null — ответ с ошибкой уже отправлен (нет такого проекта или нет его роли).</summary>
    private async Task<(string? Value, string Label)?> ScopeAsync(string? projectKey)
    {
        var project = projects.Effective(Context.Guild.Id, string.IsNullOrWhiteSpace(projectKey) ? null : projectKey);
        if (!string.IsNullOrWhiteSpace(projectKey) && projects.Find(Context.Guild.Id, projectKey) is null)
        {
            await RespondAsync("Unknown project — pick one from the list.", ephemeral: true);
            return null;
        }
        if (!LfgProjectStore.CanUse(Context.Guild.GetUser(Context.User.Id), project))
        {
            await RespondAsync($"Characters of **{project!.Name}** need {string.Join(" or ", project.RoleIds.Select(id => $"<@&{id}>"))}.",
                ephemeral: true, allowedMentions: AllowedMentions.None);
            return null;
        }
        return (projects.Scope(Context.Guild.Id, project?.Key), project is null ? "" : $" ({Format.Sanitize(project.Name)})");
    }

    private async Task<string?> ExactAsync(string name, string? scope)
    {
        var exact = store.Get(Context.Guild.Id, Context.User.Id, scope)
            .FirstOrDefault(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is null) await RespondAsync("Character not found — add it with `/character add` first.", ephemeral: true);
        return exact;
    }
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
