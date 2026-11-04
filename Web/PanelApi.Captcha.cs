using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Только владельцу бота (право Access): капча — список проверяемых пользователей, их статус и выдача
/// пройденной капчи из панели (когда человек застрял на капче в чате).
/// </summary>
public static partial class PanelApi
{
    public sealed record CaptchaGrantBody(string? UserId, int? Minutes);

    private static void MapCaptcha(RouteGroupBuilder g)
    {
        // Список проверяемых пользователей + их текущий статус + краткие настройки
        g.MapGet("/", (DiscordSocketClient client, [FromServices] CaptchaStore store, [FromServices] CaptchaService captcha) =>
        {
            var s = store.Get();
            var now = DateTimeOffset.UtcNow;
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
                settings = new { validityMinutes = s.ValidityMinutes, max = CaptchaStore.MaxUsers, count = s.Users.Count }
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
    }
}
