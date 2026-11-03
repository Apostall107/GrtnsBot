using Discord;

namespace LfgBot.Services;

/// <summary>
/// Встроенная кнопка-ссылка на сайт бота в панелях LFG и Room controls (в панели тикетов её нет — это вход для новичков).
/// Подпись, адрес и блок «🌐 Bot website» в тексте панели зашиты в код: через тексты панелей (сайт, /panel-text) их не изменить и не убрать.
/// Адрес — Web:PublicUrl (appsettings.json или env Web__PublicUrl, его пишет setup-web.sh).
/// </summary>
public static class SiteLink
{
    public const string DefaultUrl = "https://greatness-bot.duckdns.org";
    public const string Label = "Open website";
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

    /// <summary>Блок о сайте в панели (поле под текстом): ссылка и что там есть. Не редактируется — как и кнопка.</summary>
    public static EmbedBuilder WithSiteField(this EmbedBuilder embed) => Url is null ? embed : embed.AddField(
        "🌐 Bot website — everything in one place",
        $"### [{new Uri(Url).Host}]({Url})\n" +
        "📅 Calendar of groups · ✍️ sign up in one click · 🧙 your characters with GS, CP and the official AION 2 profile · 👥 guild roster\n" +
        "-# Log in with your Discord account — no password needed.");

    /// <summary>Добавляет кнопку-ссылку в указанный ряд (если адрес задан).</summary>
    public static ComponentBuilder WithSiteButton(this ComponentBuilder builder, int row = 0) =>
        Url is null ? builder : builder.WithButton(Label, style: ButtonStyle.Link, url: Url, emote: Emoji, row: row);
}
