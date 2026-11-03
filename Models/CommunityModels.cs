namespace LfgBot.Models;

/// <summary>Приветствие новичков и автороль (одна запись на сервер).</summary>
public sealed class WelcomeSettings
{
    public ulong GuildId { get; set; }

    /// <summary>Канал для приветствия; null — в канал не писать.</summary>
    public ulong? ChannelId { get; set; }
    public string ChannelText { get; set; } = WelcomeDefaults.ChannelText;

    /// <summary>Писать новичку в ЛС.</summary>
    public bool SendDm { get; set; }
    public string DmText { get; set; } = WelcomeDefaults.DmText;

    /// <summary>Кнопка-ссылка на панель тикетов под приветствием (если панель опубликована).</summary>
    public bool ApplyButton { get; set; } = true;

    /// <summary>Автороль: off — ничего не выдавать (по умолчанию), join — сразу при входе.</summary>
    public string AutoRoleMode { get; set; } = WelcomeDefaults.AutoRoleOff;
    public List<ulong> AutoRoleIds { get; set; } = [];

    /// <summary>Не выдавать автороль ботам.</summary>
    public bool SkipBots { get; set; } = true;
}

public static class WelcomeDefaults
{
    public const string AutoRoleOff = "off";
    public const string AutoRoleJoin = "join";
    public const int MaxText = 1800;
    public const int MaxAutoRoles = 10;

    public const string ChannelText = "👋 Welcome to **{server}**, {user}! You are member #{members}.\nTo join a project, apply here: {tickets}";
    public const string DmText = "👋 Welcome to **{server}**, {name}!\nTo join WWM or Aion 2, open an application: {tickets}";
}

/// <summary>Как выбираются роли на панели.</summary>
public enum RolePanelStyle
{
    Buttons,
    Dropdown,
    Reactions
}

/// <summary>
/// Normal — нажал/выбрал = получил, ещё раз = снял.
/// Unique — только одна роль из панели (новая заменяет прежнюю).
/// Verify — роль только выдаётся (снять через панель нельзя), например «принял правила».
/// </summary>
public enum RolePanelMode
{
    Normal,
    Unique,
    Verify
}

public sealed class RolePanelOption
{
    public ulong RoleId { get; set; }
    /// <summary>Подпись кнопки/пункта; пусто — имя роли.</summary>
    public string? Label { get; set; }
    /// <summary>Unicode-эмодзи или &lt;:name:id&gt;. Для реакций обязателен.</summary>
    public string? Emoji { get; set; }
    /// <summary>Описание пункта выпадающего списка.</summary>
    public string? Description { get; set; }
}

/// <summary>Панель самовыдачи ролей (кнопки, список или реакции).</summary>
public sealed class RolePanel
{
    public const int MaxOptions = 20;
    public const int MaxPerGuild = 25;

    public string Id { get; set; } = IdGen.New();
    public ulong GuildId { get; set; }
    public string Title { get; set; } = "Pick your roles";
    public string Text { get; set; } = "";
    /// <summary>#hex цвет полоски; null — стандартный.</summary>
    public string? Color { get; set; }
    public RolePanelStyle Style { get; set; } = RolePanelStyle.Buttons;
    public RolePanelMode Mode { get; set; } = RolePanelMode.Normal;
    /// <summary>Подсказка выпадающего списка.</summary>
    public string? Placeholder { get; set; }
    public List<RolePanelOption> Options { get; set; } = [];

    /// <summary>Где опубликована (одно сообщение на панель).</summary>
    public ulong? ChannelId { get; set; }
    public ulong? MessageId { get; set; }
}

public sealed class CommunityData
{
    public List<WelcomeSettings> Welcome { get; set; } = [];
    public List<RolePanel> RolePanels { get; set; } = [];
}
