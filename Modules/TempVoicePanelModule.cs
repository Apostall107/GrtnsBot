using Discord;
using Discord.Interactions;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>Кнопки и списки панели управления комнатой. Действуют на комнату, в которой сидит нажавший.</summary>
[RequireContext(ContextType.Guild)]
public sealed class TempVoicePanelModule(TempRoomManager rooms, TempVoiceStore store)
    : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("room:rename")]
    public async Task OnRename()
    {
        if (await ResolveAsync() is null) return;
        await RespondWithModalAsync<RoomRenameModal>("room:rename-modal");
    }

    [ModalInteraction("room:rename-modal")]
    public Task OnRenameSubmitted(RoomRenameModal modal) => RunAsync(r => rooms.RenameAsync(r, modal.Name));

    [ComponentInteraction("room:limit")]
    public async Task OnLimit()
    {
        if (await ResolveAsync() is null) return;
        await RespondWithModalAsync<RoomLimitModal>("room:limit-modal");
    }

    [ModalInteraction("room:limit-modal")]
    public Task OnLimitSubmitted(RoomLimitModal modal) =>
        RunAsync(r => int.TryParse(modal.Limit.Trim(), out var limit)
            ? rooms.LimitAsync(r, limit)
            : Task.FromResult("Enter a number from 0 to 99."));

    [ComponentInteraction("room:lock")]
    public Task OnLockToggle() =>
        RunAsync(r => store.GetRoom(r.Channel.Id)?.Locked == true ? rooms.UnlockAsync(r) : rooms.LockAsync(r));

    [ComponentInteraction("room:claim")]
    public Task OnClaim() => RunAsync(r => rooms.ClaimAsync(r, Context.User), ownerOnly: false);

    [ComponentInteraction("room:permit")]
    public Task OnPermit(IUser[] users) => RunAsync(r => rooms.PermitAsync(r, users[0].Id));

    [ComponentInteraction("room:kick")]
    public Task OnKick(IUser[] users) => RunAsync(r => rooms.KickAsync(r, users[0].Id, Context.User.Id));

    /// <summary>Проверка до открытия окна ввода: человек в своей комнате и он владелец.</summary>
    private async Task<RoomRef?> ResolveAsync(bool ownerOnly = true)
    {
        var error = rooms.TryResolve(Context.Guild, Context.User, ownerOnly, out var room);
        if (error is null) return room;

        await RespondAsync(error, ephemeral: true, allowedMentions: AllowedMentions.None);
        return null;
    }

    private async Task RunAsync(Func<RoomRef, Task<string>> action, bool ownerOnly = true)
    {
        if (await ResolveAsync(ownerOnly) is not { } room) return;

        await DeferAsync(ephemeral: true);
        var result = await action(room);
        await FollowupAsync(result, ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}

public sealed class RoomRenameModal : IModal
{
    public string Title => "Rename room";

    [InputLabel("New name")]
    [ModalTextInput("name", TextInputStyle.Short, placeholder: "e.g. Raid night", maxLength: 100)]
    public string Name { get; set; } = "";
}

public sealed class RoomLimitModal : IModal
{
    public string Title => "User limit";

    [InputLabel("Max users (0 = no limit)")]
    [ModalTextInput("limit", TextInputStyle.Short, placeholder: "0–99", maxLength: 2)]
    public string Limit { get; set; } = "";
}
