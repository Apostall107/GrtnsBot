using System.Text;
using Discord;
using Discord.Interactions;
using LfgBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Modules;

[Group("activity", "Activities for group signups")]
[RequireContext(ContextType.Guild)]
[DefaultMemberPermissions(GuildPermission.ManageGuild)]
public sealed class ActivityModule(ActivityStore store) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("add", "Add an activity with a role to ping")]
    public async Task AddAsync(
        [Summary("name", "Dungeon / activity name")] string name,
        [Summary("role", "Role to ping when a group is posted")] IRole role)
    {
        var error = store.Add(Context.Guild.Id, name.Trim(), role.Id);
        await RespondAsync(error ?? $"Added: **{name.Trim()}** → {role.Mention}",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("remove", "Remove an activity")]
    public async Task RemoveAsync(
        [Summary("activity", "Activity"), Autocomplete(typeof(ActivityAutocomplete))] string key)
    {
        var removed = store.Remove(Context.Guild.Id, key);
        await RespondAsync(removed is null ? "Activity not found." : $"Removed: **{removed.Name}**",
            ephemeral: true);
    }

    [SlashCommand("list", "List activities")]
    public async Task ListAsync()
    {
        var list = store.Get(Context.Guild.Id);
        if (list.Count == 0)
        {
            await RespondAsync("No activities yet. Add one with `/activity add`.", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var a in list) sb.AppendLine($"• **{a.Name}** → <@&{a.RoleId}>");
        await RespondAsync(sb.ToString(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}

public sealed class ActivityAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        var store = services.GetRequiredService<ActivityStore>();
        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";

        var results = store.Get(context.Guild.Id)
            .Where(a => a.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(a => new AutocompleteResult(a.Name, a.Key));

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}
