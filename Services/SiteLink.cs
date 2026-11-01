using Discord;

namespace LfgBot.Services;

/// <summary>
/// Встроенная кнопка-ссылка на сайт бота в панелях LFG и Room controls (в панели тикетов её нет — это вход для новичков).
/// Подпись и адрес зашиты в код: через тексты панелей (сайт, /panel-text) их не изменить и не убрать.
/// Адрес — Web:PublicUrl (appsettings.json или env Web__PublicUrl, его пишет setup-web.sh).
/// </summary>
public static class SiteLink
{
    public const string DefaultUrl = "https://greatness-bot.duckdns.org";
    public const string Label = "Website";
    public static readonly Emoji Emoji = new("🌐");

    /// <summary>Абсолютный http(s)-адрес сайта; null — адрес в конфиге неверный, кнопки нет.</summary>
    public static string? Url { get; private set; } = DefaultUrl;

    public static void Configure(string? url)
    {
        var value = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim().TrimEnd('/');
        Url = Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
    }

    /// <summary>Добавляет кнопку-ссылку в указанный ряд (если адрес задан).</summary>
    public static ComponentBuilder WithSiteButton(this ComponentBuilder builder, int row = 0) =>
        Url is null ? builder : builder.WithButton(Label, style: ButtonStyle.Link, url: Url, emote: Emoji, row: row);
}
