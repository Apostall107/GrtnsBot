using System.Text.Json;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>
/// Проект (игра) для сборов и персонажей: Aion 2, WWM… Персонажи у каждого проекта свои;
/// добавить/выбрать персонажа и записаться в сбор проекта можно только с одной из его ролей.
/// </summary>
public sealed class LfgProject
{
    public string Key { get; set; } = IdGen.New();
    public string Name { get; set; } = "";
    /// <summary>Нужна одна из ролей. Пусто — проект открыт всем.</summary>
    public List<ulong> RoleIds { get; set; } = [];
}

public sealed class GuildLfgSetup
{
    public List<LfgProject> Projects { get; set; } = [];
    /// <summary>Проект по умолчанию: к нему относятся персонажи, созданные до появления проектов.</summary>
    public string? DefaultKey { get; set; }
    /// <summary>Иконка класса для роли: юникод-эмодзи или эмодзи сервера &lt;:name:id&gt;. Если задано — классом можно выбрать только эти роли.</summary>
    public Dictionary<ulong, string> ClassIcons { get; set; } = new();
    /// <summary>Классы отдельного проекта. Если у проекта список пуст — действуют общие (ClassIcons).</summary>
    public Dictionary<string, Dictionary<ulong, string>> ProjectClassIcons { get; set; } = new();
    /// <summary>Категория для голосовых комнат сборов: "" — общая, иначе ключ проекта.</summary>
    public Dictionary<string, ulong> VoiceCategories { get; set; } = new();
    /// <summary>Куда автоматически дублировать посты сборов (текстовые и голосовые каналы): "" — общий список, иначе ключ проекта.</summary>
    public Dictionary<string, List<ulong>> AutoMirrors { get; set; } = new();
}

/// <summary>data/lfg-setup.json — проекты, проект по умолчанию, иконки классов (по серверам).</summary>
public sealed class LfgProjectStore
{
    public const int MaxProjects = 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<ulong, GuildLfgSetup> _data;

    public LfgProjectStore(IConfiguration config)
    {
        _path = config["Storage:LfgSetupPath"] ?? "data/lfg-setup.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<ulong, GuildLfgSetup>>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    public IReadOnlyList<LfgProject> GetProjects(ulong guildId)
    {
        lock (_lock) return Guild(guildId).Projects.Select(Clone).ToList();
    }

    public LfgProject? Find(ulong guildId, string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_lock) return Guild(guildId).Projects.Find(p => p.Key == key) is { } p ? Clone(p) : null;
    }

    public string? DefaultKey(ulong guildId)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            return g.Projects.Any(p => p.Key == g.DefaultKey) ? g.DefaultKey : g.Projects.FirstOrDefault()?.Key;
        }
    }

    /// <summary>Проект сбора/персонажа: указанный, иначе проект по умолчанию (null — проектов нет).</summary>
    public LfgProject? Effective(ulong guildId, string? key) => Find(guildId, key) ?? Find(guildId, DefaultKey(guildId));

    /// <summary>
    /// Ключ хранения персонажей: null — «старое» хранилище (проект по умолчанию и всё, что было до проектов).
    /// </summary>
    public string? Scope(ulong guildId, string? projectKey)
    {
        var effective = Effective(guildId, projectKey);
        return effective is null || effective.Key == DefaultKey(guildId) ? null : effective.Key;
    }

    /// <summary>Есть ли у участника роль проекта (проект без ролей — открыт всем).</summary>
    public static bool CanUse(SocketGuildUser? member, LfgProject? project) =>
        project is null || project.RoleIds.Count == 0 || (member is not null && member.Roles.Any(r => project.RoleIds.Contains(r.Id)));

    public string? Save(ulong guildId, LfgProject project)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            var i = g.Projects.FindIndex(p => p.Key == project.Key);
            if (i >= 0) g.Projects[i] = project;
            else
            {
                if (g.Projects.Count >= MaxProjects) return $"Limit of {MaxProjects} projects reached.";
                g.Projects.Add(project);
                g.DefaultKey ??= project.Key;
            }
            Write();
            return null;
        }
    }

    public bool Remove(ulong guildId, string key)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            if (g.Projects.RemoveAll(p => p.Key == key) == 0) return false;
            if (g.DefaultKey == key) g.DefaultKey = g.Projects.FirstOrDefault()?.Key;
            Write();
            return true;
        }
    }

    public void SetDefault(ulong guildId, string key)
    {
        lock (_lock)
        {
            Guild(guildId).DefaultKey = key;
            Write();
        }
    }

    /// <summary>Классы как заданы: projectKey null — общие для всех проектов, иначе — только этого проекта.</summary>
    public IReadOnlyDictionary<ulong, string> ClassIcons(ulong guildId, string? projectKey = null)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            if (projectKey is null) return new Dictionary<ulong, string>(g.ClassIcons);
            return g.ProjectClassIcons.TryGetValue(projectKey, out var own) ? new Dictionary<ulong, string>(own) : new Dictionary<ulong, string>();
        }
    }

    /// <summary>Классы, действующие в проекте: свои, а если их нет — общие.</summary>
    public IReadOnlyDictionary<ulong, string> EffectiveClassIcons(ulong guildId, string? projectKey)
    {
        var key = Effective(guildId, projectKey)?.Key;
        var own = key is null ? null : ClassIcons(guildId, key);
        return own is { Count: > 0 } ? own : ClassIcons(guildId);
    }

    public void SetClassIcons(ulong guildId, string? projectKey, Dictionary<ulong, string> icons)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            if (projectKey is null) g.ClassIcons = icons;
            else if (icons.Count == 0) g.ProjectClassIcons.Remove(projectKey);
            else g.ProjectClassIcons[projectKey] = icons;
            Write();
        }
    }

    /// <summary>Категория голосовых комнат как задана: projectKey null — общая, иначе только проекта.</summary>
    public ulong? VoiceCategory(ulong guildId, string? projectKey)
    {
        lock (_lock)
            return Guild(guildId).VoiceCategories.TryGetValue(projectKey ?? "", out var id) ? id : null;
    }

    /// <summary>Категория для комнаты сбора: проекта, иначе общая (null — категория канала с постом).</summary>
    public ulong? EffectiveVoiceCategory(ulong guildId, string? projectKey)
    {
        var key = Effective(guildId, projectKey)?.Key;
        return (key is null ? null : VoiceCategory(guildId, key)) ?? VoiceCategory(guildId, null);
    }

    /// <summary>Автодубли как заданы: projectKey null — общие, иначе только проекта.</summary>
    public IReadOnlyList<ulong> AutoMirrors(ulong guildId, string? projectKey)
    {
        lock (_lock)
            return Guild(guildId).AutoMirrors.TryGetValue(projectKey ?? "", out var list) ? list.ToList() : [];
    }

    /// <summary>Каналы для автодублей сбора: свои у проекта, иначе общие.</summary>
    public IReadOnlyList<ulong> EffectiveAutoMirrors(ulong guildId, string? projectKey)
    {
        var key = Effective(guildId, projectKey)?.Key;
        IReadOnlyList<ulong> own = key is null ? Array.Empty<ulong>() : AutoMirrors(guildId, key);
        return own.Count > 0 ? own : AutoMirrors(guildId, null);
    }

    public void SetAutoMirrors(ulong guildId, string? projectKey, List<ulong> channelIds)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            if (channelIds.Count == 0) g.AutoMirrors.Remove(projectKey ?? "");
            else g.AutoMirrors[projectKey ?? ""] = channelIds;
            Write();
        }
    }

    public void SetVoiceCategory(ulong guildId, string? projectKey, ulong? categoryId)
    {
        lock (_lock)
        {
            var g = Guild(guildId);
            if (categoryId is { } id) g.VoiceCategories[projectKey ?? ""] = id;
            else g.VoiceCategories.Remove(projectKey ?? "");
            Write();
        }
    }

    /// <summary>Иконка класса в проекте: заданная админом (проекта или общая), иначе юникод-эмодзи самой роли.</summary>
    public string? ClassIcon(ulong guildId, IRole? role, string? projectKey = null)
    {
        if (role is null) return null;
        if (EffectiveClassIcons(guildId, projectKey).TryGetValue(role.Id, out var icon) && !string.IsNullOrWhiteSpace(icon)) return icon;
        if (ClassIcons(guildId).TryGetValue(role.Id, out var common) && !string.IsNullOrWhiteSpace(common)) return common;
        return role.Emoji is { } e && !string.IsNullOrWhiteSpace(e.Name) ? e.Name : null;
    }

    /// <summary>Строка иконки → IEmote для меню (юникод или &lt;:name:id&gt;).</summary>
    public static IEmote? ToEmote(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        if (Emote.TryParse(icon, out var custom)) return custom;
        return Emoji.TryParse(icon, out var emoji) ? emoji : new Emoji(icon);
    }

    private GuildLfgSetup Guild(ulong guildId)
    {
        if (!_data.TryGetValue(guildId, out var g)) _data[guildId] = g = new GuildLfgSetup();
        return g;
    }

    private static LfgProject Clone(LfgProject p) => new() { Key = p.Key, Name = p.Name, RoleIds = p.RoleIds.ToList() };

    private void Write()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
