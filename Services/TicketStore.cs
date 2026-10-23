using System.Text.Json;
using LfgBot.Models;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>Проекты и открытые тикеты, хранятся в JSON — переживают перезапуск бота.</summary>
public sealed class TicketStore
{
    public const int MaxProjects = 25; // лимит пунктов в выпадающем списке

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly TicketData _data;

    public TicketStore(IConfiguration config)
    {
        _path = config["Storage:TicketsPath"] ?? "data/tickets.json";
        _data = File.Exists(_path)
            ? JsonSerializer.Deserialize<TicketData>(File.ReadAllText(_path)) ?? new()
            : new();
    }

    // ---------- Проекты ----------

    public IReadOnlyList<TicketProject> GetProjects(ulong guildId)
    {
        lock (_lock)
            return _data.Projects.TryGetValue(guildId, out var list) ? list.ToList() : [];
    }

    public TicketProject? FindProject(ulong guildId, string key)
    {
        lock (_lock)
            return _data.Projects.TryGetValue(guildId, out var list) ? list.Find(p => p.Key == key) : null;
    }

    public string? AddProject(ulong guildId, string name, ulong memberRoleId)
    {
        lock (_lock)
        {
            if (!_data.Projects.TryGetValue(guildId, out var list))
                _data.Projects[guildId] = list = [];

            if (list.Count >= MaxProjects) return $"Limit of {MaxProjects} projects reached.";
            if (list.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return $"Project \"{name}\" already exists.";

            list.Add(new TicketProject { Name = name, MemberRoleIds = [memberRoleId] });
            Save();
            return null;
        }
    }

    public TicketProject? RemoveProject(ulong guildId, string key)
    {
        lock (_lock)
        {
            if (!_data.Projects.TryGetValue(guildId, out var list)) return null;
            var project = list.Find(p => p.Key == key);
            if (project is null) return null;
            list.Remove(project);
            Save();
            return project;
        }
    }

    public TicketProject? UpdateProject(ulong guildId, string key, Action<TicketProject> update)
    {
        lock (_lock)
        {
            if (!_data.Projects.TryGetValue(guildId, out var list)) return null;
            var project = list.Find(p => p.Key == key);
            if (project is null) return null;
            update(project);
            Save();
            return project;
        }
    }

    // ---------- Тикеты ----------

    public IReadOnlyList<Ticket> GetTickets(ulong guildId)
    {
        lock (_lock)
            return _data.Tickets.Where(t => t.GuildId == guildId).ToList();
    }

    public Ticket? FindTicket(string id)
    {
        lock (_lock)
            return _data.Tickets.Find(t => t.Id == id);
    }

    public Ticket? FindOpenTicket(ulong guildId, ulong userId, string projectKey)
    {
        lock (_lock)
            return _data.Tickets.Find(t => t.GuildId == guildId && t.UserId == userId && t.ProjectKey == projectKey);
    }

    public void AddTicket(Ticket ticket)
    {
        lock (_lock)
        {
            _data.Tickets.Add(ticket);
            Save();
        }
    }

    public void RemoveTicket(string id)
    {
        lock (_lock)
        {
            if (_data.Tickets.RemoveAll(t => t.Id == id) > 0) Save();
        }
    }

    public Ticket? UpdateTicket(string id, Action<Ticket> update)
    {
        lock (_lock)
        {
            var ticket = _data.Tickets.Find(t => t.Id == id);
            if (ticket is null) return null;
            update(ticket);
            Save();
            return ticket;
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_data, JsonOptions));
    }
}
