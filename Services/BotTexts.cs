using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>
/// Редактируемый текст бота (ответы, ЛС, посты, подписи, окна) или цвет полоски embed.
/// Refresh — что перерисовать после изменения: "lfgpost" (активные сборы), "ticketmsg" (открытые тикеты), null — только новые сообщения.
/// </summary>
public sealed record BotTextDef(string Key, string Group, string Name, string Default, string? Placeholders = null,
    int Max = 2000, bool IsColor = false, string? Refresh = null, bool Optional = false);

/// <summary>Тексты бота: свои значения сервера (веб-админка → Bot texts) или текст по умолчанию из <see cref="BotTextCatalog"/>.</summary>
public static class Tx
{
    /// <summary>"-" в своём тексте — пустой текст (только для необязательных строк: подсказки, подвалы).</summary>
    public const string Empty = "-";

    public static BotTextStore? Store { get; set; }

    /// <summary>Черновые тексты для предпросмотра на сайте: действуют только внутри <see cref="Preview"/> (этот async-поток).</summary>
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> Overrides = new();

    /// <summary>Подставить черновые тексты (ключ → значение) на время рендера предпросмотра.</summary>
    public static IDisposable Preview(IReadOnlyDictionary<string, string>? values)
    {
        var previous = Overrides.Value;
        Overrides.Value = values;
        return new Restore(() => Overrides.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Какие тексты брал рендер (предпросмотр на сайте: какой экран показать для редактируемого текста).</summary>
    private static readonly AsyncLocal<HashSet<string>?> Used = new();

    /// <summary>Записывать ключи текстов, которые использует рендер, в <paramref name="keys"/>.</summary>
    public static IDisposable Record(HashSet<string> keys)
    {
        var previous = Used.Value;
        Used.Value = keys;
        return new Restore(() => Used.Value = previous);
    }

    private static string? Custom(ulong guildId, string key)
    {
        Used.Value?.Add(key);
        return Lookup(guildId, key);
    }

    private static string? Lookup(ulong guildId, string key) =>
        Overrides.Value is { } o && o.TryGetValue(key, out var v) ? (string.IsNullOrEmpty(v) ? null : v)
        : guildId == 0 ? null : Store?.Get(guildId, key);

    /// <summary>Текст с подстановками {name}. Неизвестный ключ — сам ключ (видно, что забыли добавить в каталог).</summary>
    public static string Get(ulong guildId, string key, params (string Name, object? Value)[] args)
    {
        BotTextCatalog.ByKey.TryGetValue(key, out var def);
        var custom = Custom(guildId, key);
        var text = custom ?? def?.Default ?? key;
        if (custom is not null && custom.Trim() == Empty && def is { Optional: true }) text = "";
        foreach (var (name, value) in args)
            text = text.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", StringComparison.OrdinalIgnoreCase);
        if (def is not null && text.Length > def.Max) text = text[..def.Max];
        // Пустая подпись кнопки/списка ломает сообщение — тогда текст по умолчанию
        if (text.Length == 0 && def is { Optional: false }) text = def.Default;
        return text;
    }

    /// <summary>Строка с переводом строки перед ней, если текст не пустой (для необязательных подсказок).</summary>
    public static string Line(ulong guildId, string key, params (string Name, object? Value)[] args)
    {
        var text = Get(guildId, key, args);
        return text.Length == 0 ? "" : "\n" + text;
    }

    public static Discord.Color Color(ulong guildId, string key)
    {
        BotTextCatalog.ByKey.TryGetValue(key, out var def);
        var custom = Custom(guildId, key);
        return ParseColor(custom) ?? ParseColor(def?.Default) ?? Discord.Color.Default;
    }

    public static Discord.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? new Discord.Color(v) : null;
    }

    /// <summary>Обрезать до лимита Discord (подписи вариантов, описания и т.п.).</summary>
    public static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}

public static class TextLines
{
    /// <summary>Строка в embed: пустую (текст скрыт через "-") пропускаем.</summary>
    public static System.Text.StringBuilder AppendText(this System.Text.StringBuilder sb, string text) =>
        text.Length == 0 ? sb : sb.AppendLine(text);
}

/// <summary>Свои тексты бота по серверам (data/bot-texts.json): ключ → текст. Храним только отличия от текста по умолчанию.</summary>
public sealed class BotTextStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, Dictionary<string, string>> _data;

    public BotTextStore(IConfiguration config)
    {
        _path = config["Storage:BotTextsPath"] ?? "data/bot-texts.json";
        _data = DataFiles.Load<Dictionary<ulong, Dictionary<string, string>>>(_path) ?? new();
        Tx.Store = this;
    }

    public string? Get(ulong guildId, string key)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var g) && g.TryGetValue(key, out var v) ? v : null;
    }

    public Dictionary<string, string> GetAll(ulong guildId)
    {
        lock (_lock)
            return _data.TryGetValue(guildId, out var g) ? new(g) : new();
    }

    /// <summary>null — вернуть текст по умолчанию. Возвращает ключи, которые изменились.</summary>
    public List<string> Set(ulong guildId, IReadOnlyDictionary<string, string?> values)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var g)) _data[guildId] = g = new();
            var changed = new List<string>();
            foreach (var (key, value) in values)
            {
                var old = g.TryGetValue(key, out var o) ? o : null;
                if (old == value) continue;
                if (value is null) g.Remove(key);
                else g[key] = value;
                changed.Add(key);
            }
            if (g.Count == 0) _data.Remove(guildId);
            if (changed.Count > 0) Save();
            return changed;
        }
    }

    public List<string> Reset(ulong guildId, IEnumerable<string> keys)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var g)) return [];
            var changed = keys.Where(g.Remove).ToList();
            if (g.Count == 0) _data.Remove(guildId);
            if (changed.Count > 0) Save();
            return changed;
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
