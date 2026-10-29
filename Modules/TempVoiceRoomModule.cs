using Discord;
using Discord.Interactions;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>Команды владельца комнаты. Та же логика, что у кнопок панели (TempRoomManager).</summary>
[Group("room", "Manage your temporary voice room")]
[RequireContext(ContextType.Guild)]
public sealed class TempVoiceRoomModule(TempRoomManager rooms, PanelService panels) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("rename", "Rename your room")]
    public Task RenameAsync([Summary("name", "New name"), MaxLength(100)] string name) =>
        RunAsync(r => rooms.RenameAsync(r, name));

    [SlashCommand("limit", "Set the user limit of your room (0 = no limit)")]
    public Task LimitAsync([Summary("limit", "Max users"), MinValue(0), MaxValue(99)] int limit) =>
        RunAsync(r => rooms.LimitAsync(r, limit));

    [SlashCommand("lock", "Lock your room: only people inside (and permitted) can join")]
    public Task LockAsync() => RunAsync(rooms.LockAsync);

    [SlashCommand("unlock", "Unlock your room")]
    public Task UnlockAsync() => RunAsync(rooms.UnlockAsync);

    [SlashCommand("permit", "Allow a member to join your room (even when locked)")]
    public Task PermitAsync([Summary("user", "Member")] IGuildUser user) =>
        RunAsync(r => rooms.PermitAsync(r, user.Id));

    [SlashCommand("kick", "Disconnect a member from your room and block them from rejoining")]
    public Task KickAsync([Summary("user", "Member")] IGuildUser user) =>
        RunAsync(r => rooms.KickAsync(r, user.Id, Context.User.Id));

    [SlashCommand("claim", "Become the owner of this room if the owner has left")]
    public Task ClaimAsync() => RunAsync(r => rooms.ClaimAsync(r, Context.User), ownerOnly: false);

    [SlashCommand("panel", "Show the room control panel")]
    public async Task PanelAsync()
    {
        var (embed, comps) = panels.Build(Context.Guild.Id, "rooms");
        await RespondAsync(embed: embed, components: comps, ephemeral: true);
    }

    private async Task RunAsync(Func<RoomRef, Task<string>> action, bool ownerOnly = true)
    {
        var error = rooms.TryResolve(Context.Guild, Context.User, ownerOnly, out var room);
        if (error is not null || room is null)
        {
            await RespondAsync(error, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }

        await DeferAsync(ephemeral: true);
        var result = await action(room);
        await FollowupAsync(result, ephemeral: true, allowedMentions: AllowedMentions.None);
    }
}
