using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Web;

/// <summary>Права веб-админки. Владелец бота имеет все права всегда, в том числе <see cref="Access"/>.</summary>
public static class PanelPermissions
{
    public const string Lfg = "lfg";
    public const string Tickets = "tickets";
    public const string Voice = "voice";
    public const string Texts = "texts";
    /// <summary>Сообщения бота: панели и посты — просмотр, обновление, удаление.</summary>
    public const string Posts = "posts";
    /// <summary>Журнал действий.</summary>
    public const string Log = "log";
    /// <summary>Настройки бота (часовой пояс, автоудаление, напоминание).</summary>
    public const string Settings = "settings";
    /// <summary>Управление доступом — только владелец, выдать нельзя.</summary>
    public const string Access = "access";

    /// <summary>Права, которые владелец может выдавать.</summary>
    public static readonly string[] Grantable = [Lfg, Tickets, Voice, Texts, Posts, Log, Settings];
}

public sealed class WebGrant
{
    public ulong UserId { get; set; }
    /// <summary>Имя на момент выдачи — чтобы показать, даже если человек ушёл с сервера.</summary>
    public string? Name { get; set; }
    public List<string> Permissions { get; set; } = [];
    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Кому владелец выдал доступ к веб-админке (data/web-access.json).</summary>
public sealed class WebAccessStore
{
    public const int MaxGrants = 100;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly List<WebGrant> _data;

    public WebAccessStore(IConfiguration config)
    {
        _path = config["Storage:WebAccessPath"] ?? "data/web-access.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<WebGrant>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public IReadOnlyList<WebGrant> GetAll()
    {
        lock (_lock)
            return _data.Select(Copy).ToList();
    }

    public IReadOnlyList<string> GetPermissions(ulong userId)
    {
        lock (_lock)
            return _data.Find(g => g.UserId == userId)?.Permissions.ToList() ?? [];
    }

    /// <summary>Выдаёт или заменяет права. Пустой список — убрать доступ.</summary>
    public string? Set(ulong userId, string? name, IEnumerable<string> permissions)
    {
        var perms = permissions.Where(p => PanelPermissions.Grantable.Contains(p)).Distinct().ToList();
        lock (_lock)
        {
            var existing = _data.Find(g => g.UserId == userId);
            if (perms.Count == 0)
            {
                if (existing is not null) _data.Remove(existing);
                Save();
                return null;
            }

            if (existing is null)
            {
                if (_data.Count >= MaxGrants) return $"Limit of {MaxGrants} people reached.";
                _data.Add(new WebGrant { UserId = userId, Name = name, Permissions = perms });
            }
            else
            {
                existing.Permissions = perms;
                if (!string.IsNullOrWhiteSpace(name)) existing.Name = name;
            }
            Save();
            return null;
        }
    }

    public bool Remove(ulong userId)
    {
        lock (_lock)
        {
            var removed = _data.RemoveAll(g => g.UserId == userId) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private static WebGrant Copy(WebGrant g) => new()
    {
        UserId = g.UserId,
        Name = g.Name,
        Permissions = g.Permissions.ToList(),
        GrantedAt = g.GrantedAt
    };

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
