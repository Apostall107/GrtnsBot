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

    /// <summary>
    /// Блок о сайте в конце текста панели — крупным заголовком (в полях embed заголовки Discord не рисует).
    /// Не редактируется — как и кнопка: дописывается после текста из админки.
    /// </summary>
    public static string SiteBlock() => Url is null ? "" :
        "\n\n## 🌐 [" + new Uri(Url).Host + "](" + Url + ")\n" +
        "**Bot website — everything in one place:** 📅 calendar of groups · ✍️ sign up in one click · " +
        "🧙 your characters with GS, CP and the official AION 2 profile · 👥 guild roster\n" +
        "-# Log in with your Discord account — no password needed.";

    /// <summary>Заголовок embed — ссылка на сайт с припиской « | Bot Website» (не редактируется).</summary>
    public static EmbedBuilder WithSiteTitle(this EmbedBuilder embed, string? title)
    {
        if (Url is null) return embed.WithTitle(title);
        const string suffix = " | Bot Website";
        var text = (title ?? "").Trim();
        if (text.Length + suffix.Length > EmbedBuilder.MaxTitleLength) text = text[..(EmbedBuilder.MaxTitleLength - suffix.Length)];
        return embed.WithTitle(text + suffix).WithUrl(Url);
    }

    /// <summary>Добавляет блок о сайте в конец описания панели.</summary>
    public static EmbedBuilder WithSiteField(this EmbedBuilder embed)
    {
        if (Url is null) return embed;
        var block = SiteBlock();
        var text = (embed.Description ?? "").TrimEnd();
        var max = EmbedBuilder.MaxDescriptionLength - block.Length;
        if (text.Length > max) text = text[..max];
        return embed.WithDescription(text + block);
    }

    /// <summary>Добавляет кнопку-ссылку в указанный ряд (если адрес задан).</summary>
    public static ComponentBuilder WithSiteButton(this ComponentBuilder builder, int row = 0) =>
        Url is null ? builder : builder.WithButton(Label, style: ButtonStyle.Link, url: Url, emote: Emoji, row: row);
}
