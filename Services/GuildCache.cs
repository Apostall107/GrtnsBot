using Discord.WebSocket;

namespace LfgBot.Services;

/// <summary>
/// Is the server fully loaded in the bot's cache? Until it is (bot just connected, Discord hiccup, a failed
/// GUILD_AVAILABLE) "channel not found" means nothing — no data may be forgotten or cleaned up because of it.
/// </summary>
public static class GuildCache
{
    public static bool Loaded(SocketGuild? guild) => guild is { IsConnected: true } && guild.Channels.Count > 0 && guild.CurrentUser is not null;

    /// <summary>Wait (up to <paramref name="max"/>) until every server the bot is in is loaded.</summary>
    public static async Task WaitAllAsync(DiscordSocketClient client, TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until && client.Guilds.Any(g => !Loaded(g))) await Task.Delay(2000);
    }
}
