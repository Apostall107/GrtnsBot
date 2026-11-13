using Discord;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>
/// Владелец бота (Bot:OwnerId в appsettings.json): всегда имеет доступ ко всем функциям,
/// единственный видит супер-панель и никогда не получает капчу.
/// </summary>
public sealed class BotOwner
{
    public BotOwner(IConfiguration config)
    {
        Id = ulong.TryParse(config["Bot:OwnerId"], out var id) ? id : 0;
        CurrentId = Id;
    }

    public ulong Id { get; }

    /// <summary>The owner id for static helpers (site checks); set when the singleton is created at start.</summary>
    public static ulong CurrentId { get; private set; }

    /// <summary>Organizer of the group or the bot owner (the developer can manage any group on the site).</summary>
    public static bool CanManage(LfgBot.Models.LfgGroup g, ulong? userId) => userId is { } id && (g.OwnerId == id || (CurrentId != 0 && id == CurrentId));

    public bool Is(ulong userId) => Id != 0 && userId == Id;

    public bool Is(IUser user) => Is(user.Id);
}
