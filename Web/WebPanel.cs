using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LfgBot.Web;

/// <summary>
/// Веб-админка бота: ASP.NET Core в том же процессе, вход через Discord OAuth2.
/// Слушает только Web:Urls (по умолчанию http://127.0.0.1:5080) — наружу её отдаёт Caddy с HTTPS.
/// Доступ решает <see cref="PanelAccess"/>: владелец бота и те, кому он выдал права в разделе Access.
/// </summary>
public static class WebPanel
{
    public const string Policy = "Panel";
    public const string DiscordScheme = "Discord";
    public const string CsrfHeader = "X-Panel";

    public static void AddWebPanel(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        builder.WebHost.UseUrls(config["Web:Urls"] ?? "http://127.0.0.1:5080");

        var services = builder.Services;
        // Без Client Secret схема Discord не регистрируется: иначе её проверка опций роняла бы каждый запрос
        var loginConfigured = !string.IsNullOrWhiteSpace(config["Web:ClientId"]) &&
                              !string.IsNullOrWhiteSpace(config["Web:ClientSecret"]);
        services.AddSingleton(new WebPanelState(loginConfigured));
        services.AddHttpClient();
        services.AddSingleton<WebAccessStore>();
        services.AddSingleton<PanelAccess>();
        services.AddSingleton<IAuthorizationHandler, PanelAccessHandler>();

        // Ключи шифрования cookie — в data/, чтобы после перезапуска бота не приходилось входить заново
        var keysPath = config["Web:KeysPath"] ?? "data/web-keys";
        services.AddDataProtection()
            .SetApplicationName("GreatnessBotPanel")
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        // За Caddy: настоящая схема (https) и IP клиента из X-Forwarded-*; прокси — только localhost
        services.Configure<ForwardedHeadersOptions>(o =>
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

        var auth = services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(o =>
            {
                o.Cookie.Name = "gb_panel";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromDays(7);
                o.SlidingExpiration = true;
                // Это API для своей страницы: вместо редиректов — коды 401/403
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        if (loginConfigured) AddDiscordLogin(auth, config);

        services.AddAuthorization(o => o.AddPolicy(Policy, p => p
            .RequireAuthenticatedUser()
            .AddRequirements(new PanelAccessRequirement())));
    }

    private static void AddDiscordLogin(AuthenticationBuilder auth, IConfiguration config)
    {
        auth.AddOAuth(DiscordScheme, o =>
        {
            o.ClientId = config["Web:ClientId"] ?? "";
            o.ClientSecret = config["Web:ClientSecret"] ?? "";
            o.CallbackPath = "/signin-discord";
            o.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
            o.TokenEndpoint = "https://discord.com/api/oauth2/token";
            o.UserInformationEndpoint = "https://discord.com/api/users/@me";
            o.Scope.Add("identify");
            o.UsePkce = true;
            o.SaveTokens = false;
            o.CorrelationCookie.SameSite = SameSiteMode.Lax;
            o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

            o.Events.OnCreatingTicket = async ctx =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                using var response = await ctx.Backchannel.SendAsync(request, ctx.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));
                var user = doc.RootElement;
                var id = user.GetProperty("id").GetString() ?? "";
                var username = user.TryGetProperty("username", out var u) ? u.GetString() : null;
                var globalName = user.TryGetProperty("global_name", out var g) && g.ValueKind == JsonValueKind.String ? g.GetString() : null;
                var avatar = user.TryGetProperty("avatar", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;

                ctx.Identity!.AddClaim(new Claim(ClaimTypes.NameIdentifier, id));
                ctx.Identity.AddClaim(new Claim(ClaimTypes.Name, globalName ?? username ?? id));
                if (avatar is not null) ctx.Identity.AddClaim(new Claim(PanelClaims.Avatar, avatar));
            };

            // Отказ на странице Discord или ошибка обмена кода — назад на главную с пометкой
            o.Events.OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/?login=failed");
                ctx.HandleResponse();
                return Task.CompletedTask;
            };
        });
    }

    public static void UseWebPanel(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();

        var configured = app.Services.GetRequiredService<WebPanelState>().LoginConfigured;

        app.MapGet("/login", (HttpContext ctx) => configured
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [DiscordScheme])
            : Results.Text("Discord login is not configured: set Web:ClientSecret (env Web__ClientSecret).", statusCode: 503));

        app.MapPost("/logout", async (HttpContext ctx) =>
        {
            if (ctx.Request.Headers[CsrfHeader] != "1") return Results.BadRequest();
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // Статика вшита в exe (Web/wwwroot)
        MapStatic(app, "/", "index.html", "text/html; charset=utf-8");
        MapStatic(app, "/app.js", "app.js", "text/javascript; charset=utf-8");
        MapStatic(app, "/app.css", "app.css", "text/css; charset=utf-8");

        // Иконка сайта = аватар бота (берётся из Discord, кэшируется на час)
        app.MapGet("/favicon.png", async (DiscordSocketClient client, IHttpClientFactory http) =>
        {
            var bytes = await BotAvatar.GetAsync(client, http);
            return bytes is null ? Results.NotFound() : Results.Bytes(bytes, "image/png");
        });
        app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.png"));

        PanelApi.Map(app);
    }

    private static void MapStatic(WebApplication app, string route, string file, string contentType)
    {
        var bytes = ReadResource(file);
        app.MapGet(route, (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "same-origin";
            ctx.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; img-src 'self' https://cdn.discordapp.com data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'";
            return Results.Bytes(bytes, contentType);
        });
    }

    private static byte[] ReadResource(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"wwwroot/{file}")
                           ?? throw new InvalidOperationException($"Embedded web file wwwroot/{file} not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

public sealed record WebPanelState(bool LoginConfigured);

/// <summary>Аватар бота для иконки сайта: скачивается с CDN Discord и держится в памяти час.</summary>
public static class BotAvatar
{
    private static byte[]? _bytes;
    private static DateTimeOffset _loadedAt;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<byte[]?> GetAsync(DiscordSocketClient client, IHttpClientFactory http)
    {
        if (_bytes is not null && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromHours(1)) return _bytes;
        if (client.CurrentUser is not { } bot) return _bytes;

        await Gate.WaitAsync();
        try
        {
            if (_bytes is not null && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromHours(1)) return _bytes;
            var url = bot.GetAvatarUrl(ImageFormat.Png, 128) ?? bot.GetDefaultAvatarUrl();
            _bytes = await http.CreateClient().GetByteArrayAsync(url);
            _loadedAt = DateTimeOffset.UtcNow;
        }
        catch (HttpRequestException)
        {
            // CDN недоступен — отдаём то, что было
        }
        finally
        {
            Gate.Release();
        }
        return _bytes;
    }
}

public static class PanelClaims
{
    public const string Avatar = "urn:discord:avatar";

    public static ulong? UserId(this ClaimsPrincipal user) =>
        ulong.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}

/// <summary>
/// Кто может пользоваться сайтом.
/// Админ-разделы: владелец бота (всё, включая Access) и те, кому он выдал права в разделе Access (<see cref="WebAccessStore"/>).
/// Раздел Member: у кого на сервере есть роль «участник» (Settings → Member roles), и владелец.
/// Проверка идёт на каждый запрос, поэтому изменения действуют сразу.
/// </summary>
public sealed class PanelAccess(BotOwner owner, WebAccessStore grants, BotSettingsStore settings, DiscordSocketClient client)
{
    private static readonly string[] OwnerPermissions = [.. PanelPermissions.Grantable, PanelPermissions.Access];

    public bool IsOwner(ulong? userId) => userId is { } id && owner.Is(id);

    /// <summary>Админ-права (разделы). Пусто — не админ.</summary>
    public IReadOnlyList<string> Permissions(ulong? userId) =>
        IsOwner(userId) ? OwnerPermissions : userId is { } id ? grants.GetPermissions(id) : Array.Empty<string>();

    public bool IsAdmin(ulong? userId) => Permissions(userId).Count > 0;

    public bool Has(ulong? userId, string permission) => Permissions(userId).Contains(permission);

    /// <summary>Участник сервера: есть одна из ролей «участник» (или владелец бота).</summary>
    public bool IsMember(ulong? userId, ulong guildId)
    {
        if (userId is not { } id) return false;
        if (owner.Is(id)) return client.GetGuild(guildId) is not null;
        var roles = settings.GetMemberRoles(guildId);
        return roles.Count > 0 && client.GetGuild(guildId)?.GetUser(id) is { } member && member.Roles.Any(r => roles.Contains(r.Id));
    }

    public IReadOnlyList<ulong> MemberGuilds(ulong? userId) =>
        client.Guilds.Where(g => IsMember(userId, g.Id)).Select(g => g.Id).ToList();

    public bool IsAllowed(ulong? userId) => IsAdmin(userId) || MemberGuilds(userId).Count > 0;
}

public sealed class PanelAccessRequirement : IAuthorizationRequirement { }

public sealed class PanelAccessHandler(PanelAccess access) : AuthorizationHandler<PanelAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PanelAccessRequirement requirement)
    {
        if (access.IsAllowed(context.User.UserId())) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
