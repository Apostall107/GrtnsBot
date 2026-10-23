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
                if (settings.TimeoutOnFail) await TimeoutAsync(TimeSpan.FromMinutes(Math.Max(1, settings.BlockMinutes)));
                break;

            default:
                await RespondAsync(ExpiredText, ephemeral: true);
                break;
        }
    }

    /// <summary>Таймаут Discord (нельзя писать и заходить в голос). Максимум Discord — 28 дней.</summary>
    private async Task TimeoutAsync(TimeSpan duration)
    {
        if (Context.Guild?.GetUser(Context.User.Id) is not { } member) return;
        if (duration > TimeSpan.FromDays(28)) duration = TimeSpan.FromDays(28);
        try
        {
            await member.SetTimeOutAsync(duration, new RequestOptions { AuditLogReason = "Failed bot captcha" });
            logger.LogInformation("Timed out {User} ({UserId}) for {Minutes} min for failing the captcha",
                member.Username, member.Id, (int)duration.TotalMinutes);
        }
        catch (Discord.Net.HttpException ex)
        {
            logger.LogWarning(ex, "Could not time out {UserId} after a failed captcha " +
                                  "(Moderate Members permission, or the member's role is above the bot's)", member.Id);
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
