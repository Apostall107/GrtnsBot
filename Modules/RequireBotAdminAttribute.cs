using Discord;
using Discord.Interactions;
using LfgBot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Modules;

/// <summary>Пропускает только админов бота: Administrator, Manage Server или роль из /admin → Bot admins.</summary>
public sealed class RequireBotAdminAttribute : PreconditionAttribute
{
    public override Task<PreconditionResult> CheckRequirementsAsync(
        IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
    {
        if (context.User is not IGuildUser user)
            return Task.FromResult(PreconditionResult.FromError("This works only on a server."));

        var admins = services.GetRequiredService<AdminStore>();
        return Task.FromResult(admins.IsAdmin(user)
            ? PreconditionResult.FromSuccess()
            : PreconditionResult.FromError("only bot admins (Manage Server or a bot admin role) can use this."));
    }
}
