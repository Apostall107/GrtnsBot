using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>
/// Запись в сбор прямо из ЛС-оповещения. Без RequireContext(Guild): кнопки нажимают в личке.
/// Сервер берётся из самого сбора; записаться может только участник этого сервера.
/// </summary>
public sealed class LfgDmModule(LfgStore store, CharacterStore characters, LfgSignupService signup)
    : InteractionModuleBase<SocketInteractionContext>
{
    private SocketMessageComponent Component => (SocketMessageComponent)Context.Interaction;

    [ComponentInteraction("lfgdm:join:*:*")]
    public async Task OnJoin(string groupId, string role)
    {
        if (await GetGroupAsync(groupId) is not { } group) return;

        var slotRole = Enum.Parse<SlotRole>(role);
        string? error;
        await group.Gate.WaitAsync();
        try { error = group.CheckJoin(Context.User.Id, slotRole); }
        finally { group.Gate.Release(); }

        if (error is not null)
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        var saved = characters.Get(group.GuildId, Context.User.Id);
        if (saved.Count == 0)
        {
            await RespondWithModalAsync<CharacterModal>($"lfgdm:charnew:{groupId}:{role}");
            return;
        }

        var picker = LfgRenderer.CharacterPicker(group, slotRole, saved, prefix: "lfgdm");
        await RespondAsync(picker.Content, components: picker.Components, ephemeral: true);
    }

    [ComponentInteraction("lfgdm:char:*:*")]
    public async Task OnCharacterPicked(string groupId, string role, string[] values)
    {
        if (await GetGroupAsync(groupId) is not { } group) return;

        if (values[0] == LfgRenderer.NewCharacterValue)
        {
            await RespondWithModalAsync<CharacterModal>($"lfgdm:charnew:{groupId}:{role}");
            return;
        }

        characters.Add(group.GuildId, Context.User.Id, values[0]);
        var result = await signup.JoinAsync(group, Context.User.Id, Enum.Parse<SlotRole>(role), values[0]);
        await Component.UpdateAsync(m =>
        {
            m.Content = result;
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ModalInteraction("lfgdm:charnew:*:*")]
    public async Task OnNewCharacter(string groupId, string role, CharacterModal modal)
    {
        if (await GetGroupAsync(groupId) is not { } group) return;

        var name = modal.Name.Replace("`", "").Trim();
        if (name.Length == 0)
        {
            await RespondAsync("Character name cannot be empty.", ephemeral: true);
            return;
        }

        characters.Add(group.GuildId, Context.User.Id, name);
        var result = await signup.JoinAsync(group, Context.User.Id, Enum.Parse<SlotRole>(role), name);
        await RespondAsync(result, ephemeral: true);
    }

    /// <summary>Сбор существует, и нажавший — участник сервера этого сбора.</summary>
    private async Task<LfgGroup?> GetGroupAsync(string groupId)
    {
        if (!store.Groups.TryGetValue(groupId, out var group))
        {
            await RespondAsync("This group is no longer active.", ephemeral: true);
            return null;
        }

        if (Context.Client.GetGuild(group.GuildId)?.GetUser(Context.User.Id) is null)
        {
            await RespondAsync("You are not a member of this group's server.", ephemeral: true);
            return null;
        }

        return group;
    }
}
