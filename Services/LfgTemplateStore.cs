using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Шаблон сбора: активность + слоты с ролями + требования. Создаётся админом, выбирается при создании сбора.</summary>
public sealed class LfgTemplate
{
    public string Key { get; set; } = IdGen.New();
    public string Name { get; set; } = "";
    /// <summary>Активность (пинг её роли). null — своё название <see cref="CustomActivity"/> без пинга.</summary>
    public string? ActivityKey { get; set; }
    public string? CustomActivity { get; set; }
    public List<SlotRole> Slots { get; set; } = [];
    public List<ulong> RequiredRoleIds { get; set; } = [];
    /// <summary>Требование роли по слотам (коды <see cref="SlotRequirement"/>; пусто — у всех общее).</summary>
    public List<ulong> SlotRequirements { get; set; } = [];
    public int? MinGs { get; set; }
    /// <summary>Проект (игра). null — во всех проектах.</summary>
    public string? ProjectKey { get; set; }
}

/// <summary>data/lfg-templates.json — шаблоны сборов по серверам.</summary>
public sealed class LfgTemplateStore
{
    public const int MaxPerGuild = 100;
    /// <summary>Сколько шаблонов помещается в меню мастера /lfg.</summary>
    public const int MaxInMenu = 24;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, List<LfgTemplate>> _data;

    public LfgTemplateStore(IConfiguration config)
    {
        _path = config["Storage:LfgTemplatesPath"] ?? "data/lfg-templates.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<ulong, List<LfgTemplate>>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public IReadOnlyList<LfgTemplate> Get(ulong guildId)
    {
        lock (_lock) return _data.TryGetValue(guildId, out var list) ? list.Select(Clone).ToList() : [];
    }

    public LfgTemplate? Find(ulong guildId, string key)
    {
        lock (_lock) return _data.TryGetValue(guildId, out var list) && list.Find(t => t.Key == key) is { } t ? Clone(t) : null;
    }

    /// <summary>Добавить или заменить (по Key).</summary>
    public string? Save(ulong guildId, LfgTemplate template)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var list)) _data[guildId] = list = [];
            var index = list.FindIndex(t => t.Key == template.Key);
            if (index >= 0) list[index] = template;
            else
            {
                if (list.Count >= MaxPerGuild) return $"Limit of {MaxPerGuild} templates reached.";
                list.Add(template);
            }
            Write();
            return null;
        }
    }

    public bool Remove(ulong guildId, string key)
    {
        lock (_lock)
        {
            if (!_data.TryGetValue(guildId, out var list) || list.RemoveAll(t => t.Key == key) == 0) return false;
            Write();
            return true;
        }
    }

    private static LfgTemplate Clone(LfgTemplate t) => JsonSerializer.Deserialize<LfgTemplate>(JsonSerializer.Serialize(t))!;

    private void Write()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
