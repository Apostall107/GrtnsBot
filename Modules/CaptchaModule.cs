using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.Extensions.Logging;

namespace LfgBot.Modules;

/// <summary>Кнопки и окно ввода капчи. Работают и на сервере, и в ЛС (кнопки из ЛС-оповещений тоже проверяются).</summary>
public sealed class CaptchaModule(CaptchaService captcha, CaptchaStore store, ILogger<CaptchaModule> logger)
    : InteractionModuleBase<SocketInteractionContext>
{
    private const string ExpiredText = "This verification has expired. Run your command again.";

    [ComponentInteraction("captcha:enter")]
    public async Task OnEnter()
    {
        if (!captcha.HasChallenge(Context.User.Id))
        {
            await RespondAsync(ExpiredText, ephemeral: true);
            return;
        }

        await RespondWithModalAsync<CaptchaModal>("captcha:submit");
    }

    [ComponentInteraction("captcha:new")]
    public async Task OnNewPicture()
    {
        var component = (SocketMessageComponent)Context.Interaction;
        if (captcha.Regenerate(Context.User.Id) is not { } msg)
        {
            await component.UpdateAsync(m =>
            {
                m.Content = ExpiredText;
                m.Embeds = Array.Empty<Embed>();
                m.Components = new ComponentBuilder().Build();
                m.Attachments = Array.Empty<FileAttachment>();
            });
            return;
        }

        using (msg.File)
            await component.UpdateAsync(m =>
            {
                m.Embeds = new[] { msg.Embed };
                m.Components = msg.Components;
                m.Attachments = new[] { msg.File };
            });
    }

    [ModalInteraction("captcha:submit")]
    public async Task OnSubmit(CaptchaModal modal)
    {
        var userId = Context.User.Id;
        var result = captcha.Submit(userId, modal.Code, out var attemptsLeft);
        var settings = store.Get();

        switch (result)
        {
            case CaptchaResult.Passed:
                await RespondAsync(settings.ValidityMinutes <= 0
                    ? "✅ Verified. Run your command again."
                    : $"✅ Verified for {settings.ValidityMinutes} min. Run your command again.", ephemeral: true);
                break;

            case CaptchaResult.Wrong when captcha.Regenerate(userId, $"❌ Wrong code. Try the new picture.") is { } msg:
                using (msg.File)
                    await RespondWithFileAsync(msg.File, embed: msg.Embed, components: msg.Components, ephemeral: true);
                break;

            case CaptchaResult.Failed:
                var until = captcha.BlockedUntil(userId);
                var text = "⛔ Too many wrong attempts." +
                           (until is { } u ? $" You can use the bot again <t:{u.ToUnixTimeSeconds()}:R>." : "");
                await RespondAsync(text, ephemeral: true);
                if (settings.KickOnFail) await KickAsync();
                break;

            default:
                await RespondAsync(ExpiredText, ephemeral: true);
                break;
        }
    }

    private async Task KickAsync()
    {
        if (Context.Guild?.GetUser(Context.User.Id) is not { } member) return;
        try
        {
            await member.KickAsync("Failed bot captcha");
            logger.LogInformation("Kicked {User} ({UserId}) for failing the captcha", member.Username, member.Id);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not kick {UserId} after a failed captcha (Kick Members permission?)", member.Id);
        }
    }
}

public sealed class CaptchaModal : IModal
{
    public string Title => "Verification";

    [InputLabel("Code from the picture")]
    [ModalTextInput("code", TextInputStyle.Short, placeholder: "e.g. K7MRQ", minLength: 1, maxLength: 10)]
    public string Code { get; set; } = "";
}
