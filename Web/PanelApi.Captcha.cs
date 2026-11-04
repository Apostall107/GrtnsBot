using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Только владельцу бота (право Access): капча — список проверяемых пользователей, их статус, выдача
/// пройденной капчи из панели, а также полное управление (список, настройки, превью) — как в /super-panel.
/// </summary>
public static partial class PanelApi
{
    public sealed record CaptchaGrantBody(string? UserId, int? Minutes);
    public sealed record CaptchaUsersBody(string[]? UserIds);
    public sealed record CaptchaSettingsBody(int ValidityMinutes, int MaxAttempts, int BlockMinutes, int CodeLength,
        int DistortionPercent, int TimeLimitSeconds, bool Mirror, bool TimeoutOnFail);

    private static object CaptchaSettingsDto(CaptchaSettings s) => new
    {
        validityMinutes = s.ValidityMinutes,
        maxAttempts = s.MaxAttempts,
        blockMinutes = s.BlockMinutes,
        codeLength = s.CodeLength,
        distortionPercent = s.DistortionPercent,
        timeLimitSeconds = s.TimeLimitSeconds,
        mirror = s.Mirror,
        timeoutOnFail = s.TimeoutOnFail,
        minLength = CaptchaImage.MinLength,
        maxLength = CaptchaImage.MaxLength
    };

    private static void MapCaptcha(RouteGroupBuilder g)
    {
        // Список проверяемых пользователей + их текущий статус + все настройки капчи
        g.MapGet("/", (DiscordSocketClient client, [FromServices] CaptchaStore store, [FromServices] CaptchaService captcha) =>
        {
            var s = store.Get();
            var users = s.Users.Select(id =>
            {
                var blocked = captcha.BlockedUntil(id);
                var verified = captcha.VerifiedUntil(id);
                var status = blocked is not null ? "blocked"
                    : verified is not null ? "verified"
                    : captcha.HasChallenge(id) ? "pending"
                    : "waiting";
                return new
                {
                    member = FindMember(client, id),
                    status,
                    until = (blocked ?? verified)?.ToUnixTimeMilliseconds()
                };
            }).ToList();
            return Results.Ok(new
            {
                users,
                max = CaptchaStore.MaxUsers,
                count = s.Users.Count,
                settings = CaptchaSettingsDto(s)
            });
        });

        // Выдать пройденную капчу: снимает блокировку и задание, отмечает как пройденную.
        // minutes: не задано — на срок из настроек; 0 — разовый проход; >0 — на столько минут.
        g.MapPost("/grant", (CaptchaGrantBody body, [FromServices] CaptchaStore store, [FromServices] CaptchaService captcha) =>
        {
            if (ParseId(body.UserId) is not { } userId) return Error("Enter a valid Discord user ID.");
            if (body.Minutes is { } m && m is < 0 or > 10080) return Error("Minutes must be from 0 to 10080.");
            if (!store.IsListed(userId))
                return Error("This user is not on the captcha list, so the bot never asks them for a captcha.");
            var until = captcha.GrantPass(userId, body.Minutes);
            return Results.Ok(new { until = until?.ToUnixTimeMilliseconds() });
        });

        // Потребовать капчу снова (отменить проход): пользователь пройдёт капчу при следующем действии.
        g.MapPost("/require", (CaptchaGrantBody body, [FromServices] CaptchaService captcha) =>
        {
            if (ParseId(body.UserId) is not { } userId) return Error("Enter a valid Discord user ID.");
            captcha.Forget(userId);
            return Results.Ok(new { ok = true });
        });

        // Добавить пользователей в список капчи (боты и владелец пропускаются)
        g.MapPost("/users", (CaptchaUsersBody body, DiscordSocketClient client, [FromServices] CaptchaStore store,
            [FromServices] CaptchaService captcha, [FromServices] BotOwner owner) =>
        {
            var ids = ParseIds(body.UserIds);
            if (ids.Count == 0) return Error("Enter at least one valid Discord user ID.");
            var bots = ids.Count(id => client.GetUser(id)?.IsBot == true || owner.Is(id));
            var toAdd = ids.Where(id => !owner.Is(id) && client.GetUser(id)?.IsBot != true).ToList();
            var added = store.AddUsers(toAdd);
            foreach (var id in toAdd) captcha.Forget(id);
            return Results.Ok(new { added, skippedBotsOrOwner = bots, count = store.Get().Users.Count });
        });

        // Убрать пользователей из списка капчи
        g.MapPost("/users/remove", (CaptchaUsersBody body, [FromServices] CaptchaStore store, [FromServices] CaptchaService captcha) =>
        {
            var ids = ParseIds(body.UserIds);
            if (ids.Count == 0) return Error("No users to remove.");
            var removed = store.RemoveUsers(ids);
            foreach (var id in ids) captcha.Forget(id);
            return Results.Ok(new { removed, count = store.Get().Users.Count });
        });

        // Настройки капчи (как в /super-panel)
        g.MapPut("/settings", (CaptchaSettingsBody b, [FromServices] CaptchaStore store) =>
        {
            if (b.ValidityMinutes is < 0 or > 10080) return Error("Validity must be from 0 to 10080 minutes.");
            if (b.MaxAttempts is < 1 or > 10) return Error("Attempts must be from 1 to 10.");
            if (b.BlockMinutes is < 1 or > 1440) return Error("Block time must be from 1 to 1440 minutes.");
            if (b.CodeLength < CaptchaImage.MinLength || b.CodeLength > CaptchaImage.MaxLength)
                return Error($"Code length must be from {CaptchaImage.MinLength} to {CaptchaImage.MaxLength}.");
            if (b.DistortionPercent is < 0 or > 100) return Error("Distortion must be from 0 to 100.");
            if (b.TimeLimitSeconds is < 0 or > 3600) return Error("Time limit must be from 0 to 3600 seconds.");
            store.Update(s =>
            {
                s.ValidityMinutes = b.ValidityMinutes;
                s.MaxAttempts = b.MaxAttempts;
                s.BlockMinutes = b.BlockMinutes;
                s.CodeLength = b.CodeLength;
                s.DistortionPercent = b.DistortionPercent;
                s.TimeLimitSeconds = b.TimeLimitSeconds;
                s.Mirror = b.Mirror;
                s.TimeoutOnFail = b.TimeoutOnFail;
            });
            return Results.Ok(CaptchaSettingsDto(store.Get()));
        });

        // Сброс: все снова проходят капчу, блокировки снимаются
        g.MapPost("/reset", ([FromServices] CaptchaService captcha) =>
        {
            captcha.ResetAll();
            return Results.Ok(new { ok = true });
        });

        // Превью капчи (как её видят пользователи) — PNG + код
        g.MapGet("/preview", ([FromServices] CaptchaStore store) =>
        {
            var s = store.Get();
            var code = CaptchaImage.NewCode(s.CodeLength, s.Mirror);
            var png = CaptchaService.Render(code, s);
            var dataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
            return Results.Ok(new { code, image = dataUrl, howto = CaptchaService.HowToRead(code.Length, s.Mirror) });
        });
    }
}
