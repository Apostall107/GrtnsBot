using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>
/// Меню под ответом о записи: класс-роль и заметка к персонажу. Работает и на сервере, и в ЛС
/// (запись из уведомления), поэтому без RequireContext(Guild).
/// </summary>
public sealed class LfgProfileModule(LfgProfileService profiles, LfgStore store, AuditLog audit)
    : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("lfgp:class:*")]
    public async Task OnClass(string groupId, string[] values)
    {
        await DeferAsync(ephemeral: true);
        var result = await profiles.SetClassAsync(groupId, Context.User.Id, values.FirstOrDefault() ?? LfgProfileService.NoClassValue);
        if (store.Groups.TryGetValue(groupId, out var group))
            audit.FromDiscord(group.GuildId, Context.User, "lfg", "Set character class", result);
        await FollowupAsync(result, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [ComponentInteraction("lfgp:note:*")]
    public async Task OnNote(string groupId)
    {
        if (!store.Groups.TryGetValue(groupId, out var group) || profiles.NoteModal(group, Context.User.Id) is not { } modal)
        {
            await RespondAsync(Tx.Get(group?.GuildId ?? 0, "lfg.profile.notInGroup"), ephemeral: true);
            return;
        }
        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("lfgp:notemodal:*")]
    public async Task OnNoteSubmitted(string groupId, LfgNoteModal modal)
    {
        await DeferAsync(ephemeral: true);
        var result = await profiles.SetCommentAsync(groupId, Context.User.Id, modal.Note, modal.Gs);
        if (store.Groups.TryGetValue(groupId, out var group))
            audit.FromDiscord(group.GuildId, Context.User, "lfg", "Set character note", result);
        await FollowupAsync(result, ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}

public sealed class LfgNoteModal : IModal
{
    public string Title => "Character note";

    [InputLabel("Note (empty = remove)")]
    [RequiredInput(false)]
    [ModalTextInput("note", TextInputStyle.Paragraph, maxLength: CharacterStore.MaxCommentLength)]
    public string? Note { get; set; }

    [InputLabel("GS")]
    [RequiredInput(false)]
    [ModalTextInput("gs", TextInputStyle.Short, maxLength: 6)]
    public string? Gs { get; set; }
}
