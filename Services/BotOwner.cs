using Discord;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>
/// Владелец бота (Bot:OwnerId в appsettings.json): всегда имеет доступ ко всем функциям,
/// единственный видит супер-панель и никогда не получает капчу.
/// </summary>
public sealed class BotOwner(IConfiguration config)
{
    public ulong Id { get; } = ulong.TryParse(config["Bot:OwnerId"], out var id) ? id : 0;

    public bool Is(ulong userId) => Id != 0 && userId == Id;

    public bool Is(IUser user) => Is(user.Id);
}
