using System.Text.Json.Serialization;

namespace LfgBot.Models;

// ============================================================
// Welcome — приветствие новичков и автороль
// ============================================================

/// <summary>Одно сообщение приветствия (в канал или в ЛС): обычный текст и/или embed.</summary>
public sealed class WelcomeMessage
{
    public bool Enabled { get; set; }
    /// <summary>Текст над embed (здесь работает пинг {user}). Пусто — без текста.</summary>
    public string? Content { get; set; }
    /// <summary>Показывать embed-карточку.</summary>
    public bool UseEmbed { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
    /// <summary>Аватар новичка справа вверху карточки.</summary>
    public bool ShowAvatar { get; set; } = true;
    /// <summary>Большая картинка внизу карточки (https-ссылка).</summary>
    public string? ImageUrl { get; set; }
    public string? Footer { get; set; }
}

/// <summary>Кнопка-ссылка под приветствием.</summary>
public sealed class WelcomeLink
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Emoji { get; set; }
}

public sealed class WelcomeSettings
{
    public ulong GuildId { get; set; }

    public ulong? ChannelId { get; set; }
    public WelcomeMessage Channel { get; set; } = WelcomeDefaults.ChannelMessage();
    public WelcomeMessage Dm { get; set; } = WelcomeDefaults.DmMessage();

    /// <summary>Кнопка 📝 Apply — ссылка на панель тикетов (если она опубликована).</summary>
    public bool ApplyButton { get; set; } = true;
    public string? ApplyLabel { get; set; }
    /// <summary>Свои кнопки-ссылки (правила, сайт…), до 4.</summary>
    public List<WelcomeLink> Links { get; set; } = [];

    /// <summary>Автороль: off — ничего не выдавать (по умолчанию), join — сразу при входе.</summary>
    public string AutoRoleMode { get; set; } = WelcomeDefaults.AutoRoleOff;
    public List<ulong> AutoRoleIds { get; set; } = [];
    public bool SkipBots { get; set; } = true;
    /// <summary>Задержка автороли в секундах (0 — сразу), чтобы не цеплять рейд-ботов.</summary>
    public int AutoRoleDelaySeconds { get; set; }

    // ---- Старый формат (до разделения на модули): читается и переносится в Channel/Dm ----
    [JsonPropertyName("ChannelText"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyChannelText { get; set; }
    [JsonPropertyName("DmText"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyDmText { get; set; }
    [JsonPropertyName("SendDm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacySendDm { get; set; }

    /// <summary>Перенос старых полей. true — что-то изменилось.</summary>
    public bool Migrate()
    {
        if (LegacyChannelText is null && LegacyDmText is null) return false;
        if (LegacyChannelText is not null)
            Channel = new WelcomeMessage { Enabled = ChannelId is not null, Content = LegacyChannelText };
        if (LegacyDmText is not null)
            Dm = new WelcomeMessage { Enabled = LegacySendDm, Content = LegacyDmText };
        LegacyChannelText = null;
        LegacyDmText = null;
        LegacySendDm = false;
        return true;
    }
}

public static class WelcomeDefaults
{
    public const string AutoRoleOff = "off";
    public const string AutoRoleJoin = "join";
    public const int MaxText = 2000;
    public const int MaxAutoRoles = 10;
    public const int MaxLinks = 4;

    public static WelcomeMessage ChannelMessage() => new()
    {
        Enabled = true,
        Content = "{user}",
        UseEmbed = true,
        Title = "Welcome to {server}!",
        Description = "Hi **{name}**, you are member **#{members}**.\nTo join a project, open an application: {tickets}",
        ShowAvatar = true,
        Footer = "Greatness"
    };

    public static WelcomeMessage DmMessage() => new()
    {
        Enabled = false,
        Content = "👋 Welcome to **{server}**, {name}!\nTo join WWM or Aion 2, open an application: {tickets}"
    };
}

// ============================================================
// Role panels — самовыдача ролей
// ============================================================

/// <summary>Чем член сервера выбирает роль: кнопка под постом, реакция или пункт выпадающего списка.</summary>
public enum RoleTrigger
{
    Button,
    Reaction,
    Option
}

/// <summary>Цвет кнопки Discord.</summary>
public enum RoleButtonColor
{
    Grey,
    Blurple,
    Green,
    Red
}

/// <summary>
/// Normal — нажал/выбрал = получил, ещё раз = снял.
/// Unique — только одна связка из панели (новая заменяет прежнюю).
/// Verify — роли только выдаются (снять через панель нельзя), например «принял правила».
/// Remove — роли только снимаются (например «убрать уведомления»).
/// </summary>
public enum RolePanelMode
{
    Normal,
    Unique,
    Verify,
    Remove
}

/// <summary>Связка: кнопка / реакция / пункт списка → одна или несколько ролей.</summary>
public sealed class RoleBinding
{
    public string Id { get; set; } = IdGen.New();
    public RoleTrigger Trigger { get; set; } = RoleTrigger.Button;
    /// <summary>Подпись кнопки / пункта списка; пусто — имя первой роли.</summary>
    public string? Label { get; set; }
    /// <summary>Unicode-эмодзи или &lt;:name:id&gt;. Для реакции обязателен.</summary>
    public string? Emoji { get; set; }
    /// <summary>Подпись под пунктом списка / строка в тексте панели.</summary>
    public string? Description { get; set; }
    public RoleButtonColor Color { get; set; } = RoleButtonColor.Grey;
    /// <summary>Какие роли выдаёт связка (1–5).</summary>
    public List<ulong> RoleIds { get; set; } = [];
}

/// <summary>Панель самовыдачи ролей: embed + кнопки / выпадающий список / реакции в любом сочетании.</summary>
public sealed class RolePanel
{
    public const int MaxBindings = 25;
    public const int MaxRolesPerBinding = 5;
    public const int MaxPerGuild = 25;

    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }

    // ---- Карточка ----
    public string? Title { get; set; } = "Pick your roles";
    public string? Text { get; set; } = "";
    public string? Color { get; set; }
    public string? ImageUrl { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? Footer { get; set; }
    /// <summary>Добавить в текст список «эмодзи — роли — описание».</summary>
    public bool ShowList { get; set; } = true;
    /// <summary>Подсказка под панелью о режиме (Click a role to get it…).</summary>
    public bool ShowHint { get; set; } = true;

    // ---- Поведение ----
    public RolePanelMode Mode { get; set; } = RolePanelMode.Normal;
    /// <summary>Подсказка выпадающего списка.</summary>
    public string? Placeholder { get; set; }
    /// <summary>Сколько пунктов списка можно выбрать за раз (0 — сколько угодно).</summary>
    public int MaxPick { get; set; }
    /// <summary>Выбирать роли могут только с одной из этих ролей (пусто — все).</summary>
    public List<ulong> RequiredRoleIds { get; set; } = [];

    public List<RoleBinding> Bindings { get; set; } = [];

    /// <summary>Где опубликована (одно сообщение на панель).</summary>
    public ulong? ChannelId { get; set; }
    public ulong? MessageId { get; set; }
    /// <summary>Держать внизу канала: после новых сообщений бот публикует панель заново.</summary>
    public bool Sticky { get; set; }

    // ---- Старый формат: Style + Options (одна роль на пункт) ----
    [JsonPropertyName("Style"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyStyle { get; set; }
    [JsonPropertyName("Options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegacyRoleOption>? LegacyOptions { get; set; }

    public bool Migrate()
    {
        if (LegacyOptions is null) return false;
        // Старый enum Style сохранялся числом: 0 Buttons, 1 Dropdown, 2 Reactions
        var trigger = LegacyStyle switch { 1 => RoleTrigger.Option, 2 => RoleTrigger.Reaction, _ => RoleTrigger.Button };
        // Id связки = id роли: старые кнопки/пункты (rp:b:panel:roleId) продолжают работать
        Bindings = LegacyOptions.Select(o => new RoleBinding
        {
            Id = o.RoleId.ToString(),
            Trigger = trigger,
            Label = o.Label,
            Emoji = o.Emoji,
            Description = o.Description,
            RoleIds = [o.RoleId]
        }).ToList();
        ShowList = trigger == RoleTrigger.Reaction;
        LegacyOptions = null;
        LegacyStyle = null;
        return true;
    }

    [JsonIgnore] public IEnumerable<RoleBinding> Buttons => Bindings.Where(b => b.Trigger == RoleTrigger.Button);
    [JsonIgnore] public IEnumerable<RoleBinding> Reactions => Bindings.Where(b => b.Trigger == RoleTrigger.Reaction);
    [JsonIgnore] public IEnumerable<RoleBinding> Options => Bindings.Where(b => b.Trigger == RoleTrigger.Option);
}

public sealed class LegacyRoleOption
{
    public ulong RoleId { get; set; }
    public string? Label { get; set; }
    public string? Emoji { get; set; }
    public string? Description { get; set; }
}

public sealed class CommunityData
{
    public List<WelcomeSettings> Welcome { get; set; } = [];
    public List<RolePanel> RolePanels { get; set; } = [];
}
