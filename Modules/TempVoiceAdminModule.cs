using System.Text;
using Discord;
using Discord.Interactions;
using LfgBot.Models;
using LfgBot.Services;

namespace LfgBot.Modules;

[Group("tempvoice", "Set up auto-created voice rooms")]
[RequireContext(ContextType.Guild)]
[RequireBotAdmin]
public sealed class TempVoiceAdminModule(TempVoiceStore store, TempRoomManager rooms, PanelService panels)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("create", "Create a new 'join to create' voice channel")]
    public async Task CreateAsync(
        [Summary("name", "Channel name")] string name = "➕ Create room",
        [Summary("category", "Category for the channel and its rooms"), ChannelTypes(ChannelType.Category)]
        IGuildChannel? category = null,
        [Summary("room-name", "Room name template, {user} = member name. Empty = channel name without 'Create room' + name")] string roomName = "",
        [Summary("limit", "User limit for rooms, 0 = no limit"), MinValue(0), MaxValue(99)] int limit = 0)
    {
        await DeferAsync(ephemeral: true);

        var channel = await Context.Guild.CreateVoiceChannelAsync(name, p =>
        {
            if (category is not null) p.CategoryId = category.Id;
        });

        store.SetCreator(new TempVoiceCreator
        {
            GuildId = Context.Guild.Id,
            ChannelId = channel.Id,
            NameTemplate = string.IsNullOrWhiteSpace(roomName) ? null : roomName,
            UserLimit = limit > 0 ? limit : null
        });
        await rooms.ReorderCategoryAsync(Context.Guild, category?.Id, channel.Id);

        await FollowupAsync($"Created <#{channel.Id}>. Joining it creates a personal room. " +
                            "Rooms copy this channel's permissions, so set who can see it right there.", ephemeral: true);
    }

    [SlashCommand("add", "Turn an existing voice channel into a 'join to create' channel")]
    public async Task AddAsync(
        [Summary("channel", "Voice channel"), ChannelTypes(ChannelType.Voice)] IGuildChannel channel,
        [Summary("room-name", "Room name template, {user} = member name. Empty = channel name without 'Create room' + name")] string roomName = "",
        [Summary("limit", "User limit for rooms, 0 = no limit"), MinValue(0), MaxValue(99)] int limit = 0)
    {
        if (store.GetRoom(channel.Id) is not null)
        {
            await RespondAsync("That channel is a temporary room — pick a normal voice channel.", ephemeral: true);
            return;
        }

        store.SetCreator(new TempVoiceCreator
        {
            GuildId = Context.Guild.Id,
            ChannelId = channel.Id,
            NameTemplate = string.IsNullOrWhiteSpace(roomName) ? null : roomName,
            UserLimit = limit > 0 ? limit : null
        });
        await RespondAsync($"<#{channel.Id}> is now a 'join to create' channel. Everyone who joins it will be moved " +
                           "to a new room, so don't use a channel people normally sit in.", ephemeral: true);
        await rooms.ReorderCategoryAsync(Context.Guild, (channel as INestedChannel)?.CategoryId, channel.Id);
    }

    [SlashCommand("remove", "Stop using a channel as 'join to create' (the channel is not deleted)")]
    public async Task RemoveAsync(
        [Summary("channel", "Voice channel"), ChannelTypes(ChannelType.Voice)] IGuildChannel channel)
    {
        var removed = store.RemoveCreator(channel.Id);
        await RespondAsync(removed ? $"<#{channel.Id}> is a normal channel again." : "That channel is not a 'join to create' channel.",
            ephemeral: true);
    }

    [SlashCommand("panel", "Post the room control panel in this channel")]
    public async Task PanelAsync()
    {
        if (Context.Channel is null)
        {
            await RespondAsync("I can't access this channel.", ephemeral: true);
            return;
        }

        await panels.PostAsync(Context.Channel, Context.Guild.Id, "rooms");
        await RespondAsync("Room control panel posted.", ephemeral: true);
    }

    [SlashCommand("list", "List 'join to create' channels")]
    public async Task ListAsync()
    {
        var creators = store.GetCreators(Context.Guild.Id);
        if (creators.Count == 0)
        {
            await RespondAsync("None yet. Use `/tempvoice create` or `/tempvoice add`.", ephemeral: true);
            return;
        }

        var sb = new StringBuilder();
        foreach (var c in creators)
        {
            var example = TempVoiceService.BuildRoomName(c.NameTemplate,
                Context.Guild.GetVoiceChannel(c.ChannelId)?.Name ?? "", Context.User.GlobalName ?? Context.User.Username);
            var template = string.IsNullOrWhiteSpace(c.NameTemplate) ? "auto" : $"`{c.NameTemplate}`";
            sb.AppendLine($"• <#{c.ChannelId}> → {template}, e.g. **{Format.Sanitize(example)}**, limit: {(c.UserLimit is { } l ? l.ToString() : "none")}");
        }
        await RespondAsync(sb.ToString(), ephemeral: true);
    }
}
