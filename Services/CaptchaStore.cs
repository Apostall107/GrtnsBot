using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

public sealed class CaptchaSettings
{
    /// <summary>Кому показывать капчу перед любым действием с ботом.</summary>
    public List<ulong> Users { get; set; } = [];
    /// <summary>Сколько минут действует пройденная капча. 0 — капча перед каждой командой.</summary>
    public int ValidityMinutes { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Сколько секунд даётся на одну картинку; не успел — попытка засчитывается как неверная. 0 — без лимита.</summary>
    public int TimeLimitSeconds { get; set; } = 180;
    /// <summary>Длина кода, 4–100. Строки по 10 символов.</summary>
    public int CodeLength { get; set; } = 30;
    /// <summary>Отражать каждый символ по горизонтали.</summary>
    public bool Mirror { get; set; } = true;
    /// <summary>Сила искажения, 0–100 (%).</summary>
    public int DistortionPercent { get; set; } = 50;
    /// <summary>Блокировка после неудачи, минут.</summary>
    public int BlockMinutes { get; set; } = 10;
    /// <summary>Отправлять в таймаут Discord на время блокировки (дополнительно к блокировке бота).</summary>
    public bool TimeoutOnFail { get; set; }

    public CaptchaSettings Clone() => new()
    {
        Users = Users.ToList(),
        ValidityMinutes = ValidityMinutes,
        MaxAttempts = MaxAttempts,
        TimeLimitSeconds = TimeLimitSeconds,
        CodeLength = CodeLength,
        Mirror = Mirror,
        DistortionPercent = DistortionPercent,
        BlockMinutes = BlockMinutes,
        TimeoutOnFail = TimeoutOnFail
    };
}

/// <summary>Список и настройки капчи из супер-панели (data/captcha.json).</summary>
public sealed class CaptchaStore
{
    public const int MaxUsers = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly CaptchaSettings _data;

    public CaptchaStore(IConfiguration config)
    {
        _path = config["Storage:CaptchaPath"] ?? "data/captcha.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<CaptchaSettings>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public CaptchaSettings Get()
    {
        lock (_lock) return _data.Clone();
    }

    public bool IsListed(ulong userId)
    {
        lock (_lock) return _data.Users.Contains(userId);
    }

    /// <summary>Добавляет пользователей, возвращает сколько реально добавлено.</summary>
    public int AddUsers(IEnumerable<ulong> userIds)
    {
        lock (_lock)
        {
            var added = 0;
            foreach (var id in userIds)
            {
                if (_data.Users.Count >= MaxUsers) break;
                if (_data.Users.Contains(id)) continue;
                _data.Users.Add(id);
                added++;
            }
            if (added > 0) Save();
            return added;
        }
    }

    public int RemoveUsers(IEnumerable<ulong> userIds)
    {
        lock (_lock)
        {
            var set = userIds.ToHashSet();
            var removed = _data.Users.RemoveAll(set.Contains);
            if (removed > 0) Save();
            return removed;
        }
    }

    public void Update(Action<CaptchaSettings> update)
    {
        lock (_lock)
        {
            update(_data);
            Save();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
