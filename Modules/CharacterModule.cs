using System.Text;
using Discord;
using Discord.Interactions;
using LfgBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Modules;

[Group("character", "Saved characters for group signups")]
[RequireContext(ContextType.Guild)]
public sealed class CharacterModule(CharacterStore store) : InteractionModuleBase<SocketInteractionContext>
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
        foreach (var name in list) sb.AppendLine($"• {Format.Sanitize(name)}");
        await RespondAsync(sb.ToString(), ephemeral: true);
    }

    [SlashCommand("add", "Add a character")]
    public async Task AddAsync(
        [Summary("name", "Character name"), MaxLength(CharacterStore.MaxNameLength)] string name)
    {
        store.Add(Context.Guild.Id, Context.User.Id, name);
        await RespondAsync($"Added: **{Format.Sanitize(name.Trim())}**", ephemeral: true);
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
