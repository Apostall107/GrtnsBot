using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.Extensions.Logging;

namespace LfgBot.Modules;

/// <summary>Кнопки и окно ввода капчи. Работают и на сервере, и в ЛС (кнопки из ЛС-оповещений тоже проверяются).</summary>
public sealed class CaptchaModule(CaptchaService captcha, CaptchaStore store, CaptchaVoiceGuard voiceGuard)
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

        // Время на эту картинку уже вышло — окно ввода не открываем, сразу засчитываем неверную попытку
        if (captcha.IsExpired(Context.User.Id))
        {
            await HandleResultAsync(captcha.Submit(Context.User.Id, null, out _));
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
    public Task OnSubmit(CaptchaModal modal) => HandleResultAsync(captcha.Submit(Context.User.Id, modal.Code, out _));

    private async Task HandleResultAsync(CaptchaResult result)
    {
        var userId = Context.User.Id;
        var settings = store.Get();

        switch (result)
        {
            case CaptchaResult.Passed:
                await RespondAsync(settings.ValidityMinutes <= 0
                    ? "✅ Verified. Run your command again."
                    : $"✅ Verified for {settings.ValidityMinutes} min. Run your command again.", ephemeral: true);
                break;

            case CaptchaResult.Wrong or CaptchaResult.TimedOut
                when captcha.Regenerate(userId, result == CaptchaResult.TimedOut
                    ? "⏰ Time ran out — that counted as a wrong attempt. Try the new picture."
                    : "❌ Wrong code. Try the new picture.") is { } msg:
                using (msg.File)
                    await RespondWithFileAsync(msg.File, embed: msg.Embed, components: msg.Components, ephemeral: true);
                break;

            case CaptchaResult.Failed:
                var until = captcha.BlockedUntil(userId);
                var text = "⛔ No attempts left." +
                           (until is { } u ? $" You can use the bot again <t:{u.ToUnixTimeSeconds()}:R>." : "");
                await RespondAsync(text, ephemeral: true);
                if (settings.TimeoutOnFail && Context.Guild?.GetUser(userId) is { } member)
                    await voiceGuard.PunishAsync(member, TimeSpan.FromMinutes(Math.Max(1, settings.BlockMinutes)));
                break;

            default:
                await RespondAsync(ExpiredText, ephemeral: true);
                break;
        }
    }
}

public sealed class CaptchaModal : IModal
{
    public string Title => "Verification";

    [InputLabel("Code from the picture")]
    [ModalTextInput("code", TextInputStyle.Paragraph, placeholder: "Spaces are allowed, e.g. K7MRQ A3XHT", minLength: 1, maxLength: 250)]
    public string Code { get; set; } = "";
}
