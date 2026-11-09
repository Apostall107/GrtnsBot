using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace LfgBot.Services;

/// <summary>What one member has done with the site mascot (the robot easter egg).</summary>
public sealed class MascotStats
{
    public int Caught { get; set; }
    /// <summary>Caught before it got tired.</summary>
    public int QuickCatches { get; set; }
    public int Escaped { get; set; }
    /// <summary>Spells seen: key → times.</summary>
    public Dictionary<string, int> Spells { get; set; } = [];
    public int Legendary { get; set; }
    public DateTimeOffset? Last { get; set; }
    /// <summary>The catches counted only in the browser before the server kept stats were taken over (once).</summary>
    public bool LegacyImported { get; set; }
}

/// <summary>One achievement: id, icon, title, how to get it, and whether the stats unlock it.</summary>
public sealed record MascotAchievement(string Id, string Icon, string Title, string Description, Func<MascotStats, bool> Done);

/// <summary>
/// data/mascot-stats.json — per server, per member: robots caught, escapes, spells seen, legendary casts.
/// The site reports events; the achievements are computed here (a locked one sends no title or description).
/// </summary>
public sealed class MascotStatsStore
{
    /// <summary>Spell keys the site may report (same order as on the site).</summary>
    public static readonly (string Key, string Icon, string Name)[] Spells =
    [
        ("hole", "🌀", "Black hole"), ("fire", "🔥", "Fireball"), ("bolt", "⚡", "Lightning"), ("rock", "🪨", "Stone barrage"),
        ("air", "🌪️", "Tornado"), ("ice", "❄️", "Ice storm"), ("meteor", "☄️", "Meteor"), ("water", "🌊", "Tidal wave"),
        ("seal", "👑", "Seal of Greatness"), ("acid", "☠️", "Acid cloud"), ("laser", "🔴", "Orbital beam"), ("claws", "🖤", "Shadow claws"),
        ("glitch", "📺", "Glitch"), ("sakura", "🌸", "Sakura storm"), ("fireworks", "🎆", "Fireworks")
    ];

    private static readonly string[] SpellTitles =
    [
        "Event Horizon", "Feel the Heat", "Thunderstruck", "Rock Bottom", "Gone with the Wind", "Brain Freeze", "Extinction Event",
        "Wet Screen", "Sealed with Greatness", "Toxic", "Orbital Strike", "Scratched", "Have You Tried Turning It Off", "Hanami", "Celebration"
    ];

    public static readonly IReadOnlyList<MascotAchievement> All = Build();

    private static List<MascotAchievement> Build()
    {
        int Seen(MascotStats s) => s.Spells.Count(kv => kv.Value > 0);
        int Total(MascotStats s) => s.Spells.Values.Sum();
        var list = new List<MascotAchievement>
        {
            new("catch1", "🤖", "Gotcha!", "Catch the robot for the first time.", s => s.Caught >= 1),
            new("catch10", "🎯", "Bot Hunter", "Catch the robot 10 times.", s => s.Caught >= 10),
            new("catch50", "🏹", "Exterminator", "Catch the robot 50 times.", s => s.Caught >= 50),
            new("catch100", "🏆", "Robot Whisperer", "Catch the robot 100 times.", s => s.Caught >= 100),
            new("quick1", "⚡", "Quick Hands", "Catch the robot before it gets tired.", s => s.QuickCatches >= 1),
            new("quick10", "🥷", "Lightning Reflexes", "Catch the robot before it gets tired 10 times.", s => s.QuickCatches >= 10),
            new("escape10", "💨", "It Got Away", "Let the robot escape 10 times.", s => s.Escaped >= 10),
        };
        for (var i = 0; i < Spells.Length; i++)
        {
            var key = Spells[i].Key;
            list.Add(new($"spell-{key}", Spells[i].Icon, SpellTitles[i], $"See the robot cast {Spells[i].Name}.", s => s.Spells.GetValueOrDefault(key) > 0));
        }
        list.AddRange([
            new("seen5", "📖", "Apprentice", "See 5 different spells.", s => Seen(s) >= 5),
            new("seen10", "📚", "Spellbook", "See 10 different spells.", s => Seen(s) >= 10),
            new("seenAll", "🧙", "Grimoire Complete", "See every spell at least once.", s => Seen(s) >= Spells.Length),
            new("spells25", "✨", "Spellbound", "See 25 spells in total.", s => Total(s) >= 25),
            new("spells100", "🔮", "Arcane Veteran", "See 100 spells in total.", s => Total(s) >= 100),
            new("legend1", "🌟", "Legendary!", "Witness a legendary cast — two spells at once.", s => s.Legendary >= 1),
            new("legend5", "💫", "Myth Keeper", "Witness 5 legendary casts.", s => s.Legendary >= 5),
        ]);
        return list;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly Dictionary<string, MascotStats> _data;   // "guild:user"

    public MascotStatsStore(IConfiguration config)
    {
        _path = config["Storage:MascotStatsPath"] ?? "data/mascot-stats.json";
        _data = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, MascotStats>>(File.ReadAllText(_path)) ?? new() : new();
    }

    public MascotStats Get(ulong guildId, ulong userId)
    {
        lock (_lock)
            return _data.TryGetValue($"{guildId}:{userId}", out var s) ? JsonSerializer.Deserialize<MascotStats>(JsonSerializer.Serialize(s))! : new MascotStats();
    }

    /// <summary>Everyone on a server who has stats: (user, copy of stats).</summary>
    public IReadOnlyList<(ulong UserId, MascotStats Stats)> Players(ulong guildId)
    {
        var prefix = $"{guildId}:";
        lock (_lock)
            return _data.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && ulong.TryParse(kv.Key[prefix.Length..], out _))
                .Select(kv => (ulong.Parse(kv.Key[prefix.Length..]), JsonSerializer.Deserialize<MascotStats>(JsonSerializer.Serialize(kv.Value))!)).ToList();
    }

    /// <summary>Record an event from the site; returns the member's catches after it (null — ignored: too fast / not valid).</summary>
    public int? Record(ulong guildId, ulong userId, string kind, bool quick, IEnumerable<string>? spells, bool legendary, int? legacyCaught = null)
    {
        lock (_lock)
        {
            var key = $"{guildId}:{userId}";
            if (!_data.TryGetValue(key, out var s)) _data[key] = s = new MascotStats();
            var now = DateTimeOffset.UtcNow;
            if (s.Last is { } last && now - last < TimeSpan.FromSeconds(3)) return null;   // one robot at a time — no spam
            s.Last = now;
            if (kind == "caught")
            {
                // once: catches the browser counted before the server kept stats (capped)
                if (!s.LegacyImported) { s.LegacyImported = true; if (legacyCaught is > 0) s.Caught = Math.Max(s.Caught, Math.Min(legacyCaught.Value, 500)); }
                s.Caught++;
                if (quick) s.QuickCatches++;
            }
            else if (kind == "escaped") s.Escaped++;
            else return null;
            // spells it cast — when it escaped or, now and then, when it was caught
            var valid = (spells ?? []).Distinct().Where(k => Spells.Any(x => x.Key == k)).Take(2).ToList();
            foreach (var k in valid) s.Spells[k] = s.Spells.GetValueOrDefault(k) + 1;
            if (legendary && valid.Count == 2) s.Legendary++;
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
            return s.Caught;
        }
    }
}
