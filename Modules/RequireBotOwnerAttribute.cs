using Discord;
using Discord.Interactions;
using LfgBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Modules;

/// <summary>Пропускает только владельца бота (Bot:OwnerId).</summary>
public sealed class RequireBotOwnerAttribute : PreconditionAttribute
{
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
    {
        var owner = services.GetRequiredService<BotOwner>();
        return Task.FromResult(owner.Is(context.User.Id)
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError("only the bot owner can use this."));
    }
}
