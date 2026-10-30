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
public sealed class LfgDmModule(LfgStore store, CharacterStore characters, LfgSignupService signup, LfgNotifyStore notifyPrefs,
    LfgProfileService profile)
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

        // Сбор проекта (игры): без роли проекта записаться и выбрать его персонажей нельзя
        if (signup.ProjectBlock(group, Context.User.Id) is { } blocked)
        {
            await RespondAsync(blocked, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        var saved = profile.PickerList(group, Context.User.Id);
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

        if (signup.ProjectBlock(group, Context.User.Id) is { } blocked)
        {
            await RespondAsync(blocked, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        characters.Add(group.GuildId, Context.User.Id, values[0], signup.Scope(group));
        var result = await signup.JoinAsync(group, Context.User.Id, Enum.Parse<SlotRole>(role), values[0]);
        var controls = profile.Controls(group, Context.User.Id);
        await Component.UpdateAsync(m =>
        {
            m.Content = result + (controls is null ? "" : LfgModule.ProfileHint);
            m.Components = controls ?? new ComponentBuilder().Build();
            m.AllowedMentions = AllowedMentions.None;
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

        if (signup.ProjectBlock(group, Context.User.Id) is { } blocked)
        {
            await RespondAsync(blocked, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        characters.Add(group.GuildId, Context.User.Id, name, signup.Scope(group));
        var result = await signup.JoinAsync(group, Context.User.Id, Enum.Parse<SlotRole>(role), name);
        var controls = profile.Controls(group, Context.User.Id);
        await RespondAsync(result + (controls is null ? "" : LfgModule.ProfileHint), components: controls, ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    // ================= Отписка прямо из ЛС =================

    [ComponentInteraction("lfgdm:off:*")]
    public Task OnDmOff(string guildId) => SetDmPreferenceAsync(guildId, false);

    [ComponentInteraction("lfgdm:on:*")]
    public Task OnDmOn(string guildId) => SetDmPreferenceAsync(guildId, true);

    private async Task SetDmPreferenceAsync(string guildIdText, bool enabled)
    {
        if (!ulong.TryParse(guildIdText, out var guildId) || Context.Client.GetGuild(guildId) is not { } guild)
        {
            await RespondAsync("That server is no longer available.", ephemeral: true);
            return;
        }

        notifyPrefs.SetEnabled(guildId, Context.User.Id, enabled);

        var toggle = new ComponentBuilder()
            .WithButton(enabled ? "Turn off these DMs" : "Turn DMs back on", $"lfgdm:{(enabled ? "off" : "on")}:{guildId}",
                ButtonStyle.Secondary, new Emoji(enabled ? "🔕" : "🔔"))
            .Build();

        await RespondAsync(enabled
                ? $"🔔 DMs on: you will get a DM when a group is started for your roles on **{Format.Sanitize(guild.Name)}**."
                : $"🔕 DMs off: you will no longer get LFG DMs from **{Format.Sanitize(guild.Name)}**. " +
                  "You can turn them back on here, with 🔔 on the LFG panel or `/lfg-dm on`.",
            components: toggle, ephemeral: true);
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
