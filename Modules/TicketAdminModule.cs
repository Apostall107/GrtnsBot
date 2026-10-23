using System.Text;
using Discord;
using Discord.Interactions;
using LfgBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Modules;

[Group("ticket-project", "Manage ticket projects and their roles")]
[RequireContext(ContextType.Guild)]
[RequireBotAdmin]
public sealed class TicketAdminModule(TicketStore store) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("add", "Add a project (e.g. WWM, Aion 2)")]
    public async Task AddAsync(
        [Summary("name", "Project name")] string name,
        [Summary("member-role", "Role given to the applicant on Approve")] IRole memberRole)
    {
        var error = store.AddProject(Context.Guild.Id, name.Trim(), memberRole.Id);
        await RespondAsync(error ?? $"Project **{name.Trim()}** added. Member role: {memberRole.Mention}. " +
            "Add more roles with `/ticket-project member-role-add`, link leadership with `/ticket-project staff-add`.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("remove", "Remove a project")]
    public async Task RemoveAsync(
        [Summary("project", "Project"), Autocomplete(typeof(TicketProjectAutocomplete))] string project)
    {
        var removed = store.RemoveProject(Context.Guild.Id, project);
        await RespondAsync(removed is null ? "Project not found." : $"Project **{removed.Name}** removed.", ephemeral: true);
    }

    [SlashCommand("staff-add", "Link a leadership role to a project (sees tickets, can Approve)")]
    public async Task StaffAddAsync(
        [Summary("project", "Project"), Autocomplete(typeof(TicketProjectAutocomplete))] string project,
        [Summary("role", "Leadership role")] IRole role)
    {
        var updated = store.UpdateProject(Context.Guild.Id, project, p =>
        {
            if (!p.StaffRoleIds.Contains(role.Id)) p.StaffRoleIds.Add(role.Id);
        });
        await RespondAsync(updated is null ? "Project not found." : $"{role.Mention} is now leadership of **{updated.Name}**.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("staff-remove", "Unlink a leadership role from a project")]
    public async Task StaffRemoveAsync(
        [Summary("project", "Project"), Autocomplete(typeof(TicketProjectAutocomplete))] string project,
        [Summary("role", "Leadership role")] IRole role)
    {
        var updated = store.UpdateProject(Context.Guild.Id, project, p => p.StaffRoleIds.Remove(role.Id));
        await RespondAsync(updated is null ? "Project not found." : $"{role.Mention} removed from **{updated.Name}** leadership.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("member-role-add", "Add a role given on Approve")]
    public async Task MemberRoleAddAsync(
        [Summary("project", "Project"), Autocomplete(typeof(TicketProjectAutocomplete))] string project,
        [Summary("role", "Role to give on Approve")] IRole role)
    {
        var updated = store.UpdateProject(Context.Guild.Id, project, p =>
        {
            if (!p.MemberRoleIds.Contains(role.Id)) p.MemberRoleIds.Add(role.Id);
        });
        await RespondAsync(updated is null ? "Project not found." : $"{role.Mention} will be given on **{updated.Name}** Approve.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("member-role-remove", "Remove a role given on Approve")]
    public async Task MemberRoleRemoveAsync(
        [Summary("project", "Project"), Autocomplete(typeof(TicketProjectAutocomplete))] string project,
        [Summary("role", "Role")] IRole role)
    {
        var updated = store.UpdateProject(Context.Guild.Id, project, p => p.MemberRoleIds.Remove(role.Id));
        await RespondAsync(updated is null ? "Project not found." : $"{role.Mention} is no longer given on **{updated.Name}** Approve.",
            ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [SlashCommand("category", "Set the category where this project's ticket channels are created")]
    public async Task CategoryAsync(
        [Summary("project", "Project"), Autocomplete(typeof(TicketProjectAutocomplete))] string project,
        [Summary("category", "Category"), ChannelTypes(ChannelType.Category)] IGuildChannel category)
    {
        var updated = store.UpdateProject(Context.Guild.Id, project, p => p.CategoryId = category.Id);
        await RespondAsync(updated is null ? "Project not found." : $"**{updated.Name}** tickets will be created in **{category.Name}**.",
            ephemeral: true);
    }

    [SlashCommand("list", "List projects and their roles")]
    public async Task ListAsync()
    {
        var projects = store.GetProjects(Context.Guild.Id);
        if (projects.Count == 0)
        {
            await RespondAsync("No projects yet. Add one with `/ticket-project add`.", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var p in projects)
        {
            sb.AppendLine($"**{p.Name}**");
            sb.AppendLine($"• Roles on approve: {(p.MemberRoleIds.Count > 0 ? string.Join(", ", p.MemberRoleIds.Select(id => $"<@&{id}>")) : "*not set*")}");
            sb.AppendLine($"• Leadership: {(p.StaffRoleIds.Count > 0 ? string.Join(", ", p.StaffRoleIds.Select(id => $"<@&{id}>")) : "*not set*")}");
            sb.AppendLine($"• Category: {(p.CategoryId is { } c ? $"<#{c}>" : $"*{TicketRenderer.DefaultCategoryName}*")}");
            sb.AppendLine();
        }

        await RespondAsync(sb.ToString(), ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}

public sealed class TicketProjectAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        var store = services.GetRequiredService<TicketStore>();
        var input = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";

        var results = store.GetProjects(context.Guild.Id)
            .Where(p => p.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(p => new AutocompleteResult(p.Name, p.Key));

        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}
