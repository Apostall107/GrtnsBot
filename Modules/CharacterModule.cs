using System.Text;
using Discord;
using Discord.Interactions;
using LfgBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Modules;

[Group("character", "Saved characters for group signups")]
[RequireContext(ContextType.Guild)]
public sealed class CharacterModule(CharacterStore store, LfgProfileService profiles, AuditLog audit) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("list", "Show my saved characters")]
    public async Task ListAsync()
    {
        var list = store.Get(Context.Guild.Id, Context.User.Id);
        if (list.Count == 0)
        {
            await RespondAsync("No saved characters. They are added when you sign up for a group or via `/character add`.",
                ephemeral: true);
            return;
        }

        var sb = new StringBuilder("**Your characters:**\n");
        foreach (var name in list)
        {
            var info = store.GetInfo(Context.Guild.Id, Context.User.Id, name);
            var cls = info?.ClassRoleId is { } r ? $" — <@&{r}>" : "";
            sb.AppendLine($"• **{Format.Sanitize(name)}**{cls}");
            if (!string.IsNullOrWhiteSpace(info?.Comment)) sb.AppendLine($"-# 💬 {Format.Sanitize(info.Comment)}");
        }
        sb.AppendLine("-# Set a class with `/character class`, a note with `/character note`.");
        await RespondAsync(sb.ToString(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("add", "Add a character")]
    public async Task AddAsync(
        [Summary("name", "Character name"), MaxLength(CharacterStore.MaxNameLength)] string name)
    {
        store.Add(Context.Guild.Id, Context.User.Id, name);
        await RespondAsync($"Added: **{Format.Sanitize(name.Trim())}**", ephemeral: true);
    }

    [SlashCommand("class", "Set the class role of a character (one of your server roles); empty = remove")]
    public async Task ClassAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("role", "One of your roles, shown next to your name in group posts")] IRole? role = null)
    {
        if (!HasCharacter(name, out var exact))
        {
            await RespondAsync("Character not found — add it with `/character add` first.", ephemeral: true);
            return;
        }

        if (role is not null)
        {
            var mine = profiles.ClassRoles(Context.Guild.Id, Context.User.Id);
            if (mine.All(r => r.Id != role.Id))
            {
                await RespondAsync("You can only pick a role you have (not bot/integration roles).", ephemeral: true);
                return;
            }
        }

        await DeferAsync(ephemeral: true);
        store.SetInfo(Context.Guild.Id, Context.User.Id, exact, true, role?.Id, false, null);
        var groups = await profiles.RefreshCharacterAsync(Context.Guild.Id, Context.User.Id, exact);
        audit.FromDiscord(Context.Guild.Id, Context.User, "lfg", "Set character class", $"{exact}: {role?.Name ?? "none"}");
        await FollowupAsync((role is null ? $"Class removed from **{Format.Sanitize(exact)}**." : $"**{Format.Sanitize(exact)}** — class <@&{role.Id}>.") +
                            (groups > 0 ? $" Updated {groups} active group(s)." : ""),
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("note", "Set a note for a character (shown under your name in group posts); empty = remove")]
    public async Task NoteAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name,
        [Summary("text", "e.g. 2.1k gear, can lead"), MaxLength(CharacterStore.MaxCommentLength)] string? text = null)
    {
        if (!HasCharacter(name, out var exact))
        {
            await RespondAsync("Character not found — add it with `/character add` first.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        store.SetInfo(Context.Guild.Id, Context.User.Id, exact, false, null, true, text);
        var groups = await profiles.RefreshCharacterAsync(Context.Guild.Id, Context.User.Id, exact);
        audit.FromDiscord(Context.Guild.Id, Context.User, "lfg", "Set character note", $"{exact}: {text ?? "removed"}");
        await FollowupAsync((string.IsNullOrWhiteSpace(text) ? $"Note removed from **{Format.Sanitize(exact)}**." : $"Note saved for **{Format.Sanitize(exact)}**.") +
                            (groups > 0 ? $" Updated {groups} active group(s)." : ""),
            ephemeral: true);
    }

    private bool HasCharacter(string name, out string exact)
    {
        exact = store.Get(Context.Guild.Id, Context.User.Id)
            .FirstOrDefault(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
        return exact.Length > 0;
    }

    [SlashCommand("remove", "Remove a character")]
    public async Task RemoveAsync(
        [Summary("name", "Character name"), Autocomplete(typeof(CharacterAutocomplete))] string name)
    {
        var removed = store.Remove(Context.Guild.Id, Context.User.Id, name);
        await RespondAsync(removed ? $"Removed: **{Format.Sanitize(name)}**" : "Character not found.", ephemeral: true);
    }
}

public sealed class CharacterAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        var store = services.GetRequiredService<CharacterStore>();
        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";

        var results = store.Get(context.Guild.Id, context.User.Id)
            .Where(n => n.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(n => new AutocompleteResult(n, n));

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}
