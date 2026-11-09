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
    /// <summary>Legendary casts by combination ("fire+hole", keys sorted) → times.</summary>
    public Dictionary<string, int> LegendCombos { get; set; } = [];
    public DateTimeOffset? Last { get; set; }
    /// <summary>The catches counted only in the browser before the server kept stats were taken over (once).</summary>
    public bool LegacyImported { get; set; }
}

/// <summary>
/// A graded achievement: the higher the count, the higher the grade (rarity colour). Grade 0 — locked (hidden on the site).
/// </summary>
public sealed record MascotAchievement(string Id, string Group, string Icon, string Title, string Description, int[] Tiers, Func<MascotStats, int> Value)
{
    public int Grade(MascotStats s) { var v = Value(s); return Tiers.Count(t => v >= t); }
    public bool Done(MascotStats s) => Grade(s) > 0;
}

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

    /// <summary>Grades: grey, white, green, blue, purple, gold, orange.</summary>
    public static readonly string[] GradeNames = ["Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Mythic"];
    /// <summary>Most achievements: powers of two from 2.</summary>
    public static readonly int[] PowerTiers = [2, 4, 8, 16, 32, 64, 128];
    /// <summary>Legendary casts (and each legendary combination).</summary>
    public static readonly int[] LegendTiers = [1, 2, 3, 4, 5, 7, 10];

    public static string ComboKey(string a, string b) => string.CompareOrdinal(a, b) < 0 ? $"{a}+{b}" : $"{b}+{a}";

    public static readonly IReadOnlyList<MascotAchievement> All = Build();

    private static List<MascotAchievement> Build()
    {
        int Seen(MascotStats s) => s.Spells.Count(kv => kv.Value > 0);
        int Total(MascotStats s) => s.Spells.Values.Sum();
        var list = new List<MascotAchievement>
        {
            new("caught", "general", "🤖", "Bot Hunter", "Catch the robot {0} times.", PowerTiers, s => s.Caught),
            new("quick", "general", "⚡", "Quick Hands", "Catch the robot {0} times before it gets tired.", PowerTiers, s => s.QuickCatches),
            new("escaped", "general", "💨", "It Got Away", "Let the robot escape {0} times.", PowerTiers, s => s.Escaped),
            new("spells", "general", "✨", "Spellbound", "See {0} spells in total.", PowerTiers, Total),
            new("kinds", "general", "📖", "Grimoire", "See {0} different spells.", [1, 2, 4, 8, 15], Seen),
            new("legend", "legend", "🌟", "Legendary!", "Witness {0} legendary casts — two spells at once.", LegendTiers, s => s.Legendary),
        };
        for (var i = 0; i < Spells.Length; i++)
        {
            var key = Spells[i].Key;
            list.Add(new($"spell-{key}", "spells", Spells[i].Icon, SpellTitles[i], $"See the robot cast {Spells[i].Name} {{0}} times.", PowerTiers, s => s.Spells.GetValueOrDefault(key)));
        }
        for (var i = 0; i < Spells.Length; i++)
        for (var j = i + 1; j < Spells.Length; j++)
        {
            var (a, b) = (Spells[i], Spells[j]);
            var combo = ComboKey(a.Key, b.Key);
            list.Add(new($"combo-{combo}", "combos", $"{a.Icon}{b.Icon}", $"{a.Name} + {b.Name}", $"Witness the legendary cast {a.Name} + {b.Name} {{0}} times.",
                LegendTiers, s => s.LegendCombos.GetValueOrDefault(combo)));
        }
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
            if (legendary && valid.Count == 2)
            {
                s.Legendary++;
                var combo = ComboKey(valid[0], valid[1]);
                s.LegendCombos[combo] = s.LegendCombos.GetValueOrDefault(combo) + 1;
            }
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
            return s.Caught;
        }
    }
}
