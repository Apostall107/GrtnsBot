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

    // --- more mascot counters (per flight) ---
    public int Spotted { get; set; }       // robots that flew by while I was on the site
    public int Dodges { get; set; }        // it ran away from my cursor
    public int Dashes { get; set; }        // I cornered it — it dashed past me
    public int Exhausted { get; set; }     // I wore it out (💦)
    public int NightCatches { get; set; }  // caught 00–06 my time
    public int MorningCatches { get; set; } // caught 06–10 my time
    public int Streak { get; set; }        // catches in a row without letting one escape
    public int BestStreak { get; set; }
    public int CatchDays { get; set; }     // different days with a catch
    public string? LastCatchDay { get; set; }
    public int PointBlank { get; set; }    // caught within 3 s of it appearing
    public int Marathon { get; set; }      // caught after chasing it for 10+ s
    public int Lucky { get; set; }         // a spell in two flights in a row
    public bool LastFlightSpell { get; set; }
    public int GoldenSeen { get; set; }    // the golden robot came to me
    public int GoldenCaught { get; set; }
    public int RainbowCaught { get; set; }
    public int DoubleSeen { get; set; }    // golden and rainbow at once

    // --- the site (tracked by the site) ---
    public int SiteDays { get; set; }
    public string? LastSiteDay { get; set; }
    public int Searches { get; set; }
    public string? SearchDay { get; set; }
    public int SearchesToday { get; set; }
    public List<string> Tabs { get; set; } = [];

    // --- from the bot (filled per request, not stored) ---
    [System.Text.Json.Serialization.JsonIgnore] public int GroupsJoined { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int GroupsAttended { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int GroupsOrganized { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int Characters { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int HasBirthday { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int BestAttendStreak { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int FullHouses { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int Activities { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int RecurringJoined { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int MinRoleGames { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int VoiceGroups { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int Applied { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int Approved { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int ArmoryLinked { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int GearUps { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int MonthsOnServer { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public int Projects { get; set; }
}

/// <summary>
/// A graded achievement: the higher the count, the higher the grade (rarity colour). Grade 0 — locked (hidden on the site).
/// </summary>
public sealed record MascotAchievement(string Id, string Group, string Icon, string Title, string Description, int[] Tiers, Func<MascotStats, int> Value)
{
    public int Grade(MascotStats s) { var v = Value(s); return Tiers.Count(t => v >= t); }

    /// <summary>Achievement class: I common, II rare (single spells, Grimoire), III legendary (legendary casts, special robots), IV ultra rare (the poop barrage).</summary>
    public int TierClass => Id.Contains(MascotStatsStore.UltraRare) ? 4
        : ReferenceEquals(Tiers, MascotStatsStore.LegendTiers) || Group is "legend" or "combos" ? 3
        : Group == "spells" || Id == "kinds" ? 2 : 1;

    /// <summary>Grade points per grade by class: I ×1, II ×2, III ×10, IV ×50.</summary>
    public int Multiplier => TierClass switch { 4 => 50, 3 => 10, 2 => 2, _ => 1 };
    public int Points(MascotStats s) => Grade(s) * Multiplier;
    public int MaxPoints => Tiers.Length * Multiplier;
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
        ("glitch", "📺", "Glitch"), ("sakura", "🌸", "Sakura storm"), ("fireworks", "🎆", "Fireworks"),
        ("lava", "🌋", "Eruption"), ("bubbles", "🫧", "Bubbles"), ("eclipse", "🌑", "Eclipse"), ("spores", "🍄", "Spores"), ("portal", "🌌", "Portal"),
        ("pixel", "👾", "Pixel art"), ("printer", "📠", "Printer"), ("banhammer", "🔨", "Banhammer"), ("money", "💸", "Money rain"), ("swarm", "🐝", "Swarm"),
        ("rainbow", "🌈", "Rainbow"), ("leaves", "🍂", "Leaf storm"), ("tomato", "🍅", "Tomatoes"), ("poop", "💩", "Poop barrage")
    ];
    /// <summary>The ultra-rare spell (5× rarer): its achievements are one-time, straight to Mythic, Tier IV.</summary>
    public const string UltraRare = "poop";

    private static readonly string[] SpellTitles =
    [
        "Event Horizon", "Feel the Heat", "Thunderstruck", "Rock Bottom", "Gone with the Wind", "Brain Freeze", "Extinction Event",
        "Wet Screen", "Sealed with Greatness", "Toxic", "Orbital Strike", "Scratched", "Have You Tried Turning It Off", "Hanami", "Celebration",
        "The Floor Is Lava", "Bubble Trouble", "Total Eclipse", "Fungal Bloom", "Now You See Me", "8-Bit Dreams", "Paper Jam", "Banned!", "Make It Rain",
        "Buzzing", "Prismatic", "Autumn Breeze", "Tough Crowd", "Code Brown"
    ];

    /// <summary>Grades: grey, white, green, blue, purple, gold, orange.</summary>
    public static readonly string[] GradeNames = ["Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Mythic"];
    /// <summary>Most achievements: geometric, from 5 doubling each grade.</summary>
    public static readonly int[] PowerTiers = [5, 10, 20, 40, 80, 160, 320];
    /// <summary>One-time achievements: done once — straight to Mythic (all seven grades).</summary>
    public static readonly int[] Once = [1, 1, 1, 1, 1, 1, 1];
    /// <summary>Each single spell: an easier climb.</summary>
    public static readonly int[] SpellTiers = [1, 3, 5, 10, 15, 25, 40];
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
            // every catch or escape counted before this achievement existed was also a sighting
            new("spotted", "general", "👀", "Spotter", "See the robot fly by {0} times.", PowerTiers, s => Math.Max(s.Spotted, s.Caught + s.Escaped)),
            new("dodges", "general", "🖱️", "Near Miss", "Make the robot run from your cursor {0} times.", PowerTiers, s => s.Dodges),
            new("dashes", "general", "💥", "Cornered", "Corner the robot so it dashes past you {0} times.", PowerTiers, s => s.Dashes),
            new("tired", "general", "😮‍💨", "Exhausted", "Wear the robot out {0} times.", PowerTiers, s => s.Exhausted),
            new("night", "general", "🦉", "Night Owl", "Catch the robot between midnight and 6 am {0} times.", PowerTiers, s => s.NightCatches),
            new("morning", "general", "🐦", "Early Bird", "Catch the robot between 6 and 10 am {0} times.", PowerTiers, s => s.MorningCatches),
            new("streak", "general", "🔁", "On a Roll", "Catch the robot {0} times in a row without letting one escape.", PowerTiers, s => s.BestStreak),
            new("days", "general", "📅", "Daily Visitor", "Catch the robot on {0} different days.", PowerTiers, s => s.CatchDays),
            new("pointblank", "general", "🎯", "Point Blank", "Catch the robot within 3 seconds of it appearing {0} times.", SpellTiers, s => s.PointBlank),
            new("marathon", "general", "🏃", "Marathon", "Chase the robot for 10+ seconds and still catch it {0} times.", SpellTiers, s => s.Marathon),
            new("lucky", "general", "🎲", "Lucky", "See spells in two flights in a row {0} times.", SpellTiers, s => s.Lucky),
            new("site", "general", "🌐", "Site Regular", "Visit the site on {0} different days.", PowerTiers, s => s.SiteDays),
            new("search", "general", "🔍", "Seeker", "Use the quick search (Ctrl+K) {0} times.", PowerTiers, s => s.Searches),
            new("explorer", "general", "🧭", "Explorer", "Open every Member section: Home, Groups, My characters and Achievements.", Once,
                s => new[] { "home", "play", "chars", "achievements" }.All(s.Tabs.Contains) ? 1 : 0),
            new("joined", "guild", "⚔️", "Party Member", "Sign up for {0} groups.", PowerTiers, s => s.GroupsJoined),
            new("attended", "guild", "✅", "Showed Up", "Be marked as present in {0} groups.", PowerTiers, s => s.GroupsAttended),
            new("organized", "guild", "📣", "Organizer", "Organize {0} groups.", PowerTiers, s => s.GroupsOrganized),
            new("punctual", "guild", "⏱️", "Punctual", "Show up in {0} groups in a row without a no-show.", [3, 5, 7, 10, 15, 20, 30], s => s.BestAttendStreak),
            new("fullhouse", "guild", "🏟️", "Full House", "Fill every slot of a group you organized {0} times.", SpellTiers, s => s.FullHouses),
            new("variety", "guild", "🎮", "Variety", "Play {0} different activities.", [2, 3, 4, 5, 7, 10, 15], s => s.Activities),
            new("regular", "guild", "🔁", "Regular", "Join recurring groups {0} times.", PowerTiers, s => s.RecurringJoined),
            new("roles", "guild", "🛡️", "Role Player", "Play at least {0} groups as each role — DD, Tank and Sup.", [1, 2, 3, 5, 7, 10, 15], s => s.MinRoleGames),
            new("voice", "guild", "🎙️", "Voice Room", "Join {0} groups that had a voice room.", PowerTiers, s => s.VoiceGroups),
            new("gearup", "guild", "📈", "Gear Up", "Update your characters (class, GS, armory) {0} times.", PowerTiers, s => s.GearUps),
            new("veteran", "guild", "🏅", "Veteran", "Be on the server for {0} months.", [1, 2, 3, 6, 12, 18, 24], s => s.MonthsOnServer),
            new("applicant", "guild", "🎫", "Applicant", "Apply through a ticket.", Once, s => Math.Min(1, s.Applied)),
            new("accepted", "guild", "🤝", "Accepted", "Get an application approved.", Once, s => Math.Min(1, s.Approved)),
            new("armory", "guild", "🔗", "Armory Linked", "Link a character to the official armory.", Once, s => Math.Min(1, s.ArmoryLinked)),
            new("twoworlds", "guild", "🌍", "Two Worlds", "Have characters in two games of the server.", Once, s => s.Projects >= 2 ? 1 : 0),
            new("roster", "guild", "🧙", "Roster", "Have {0} characters in My characters.", [1, 2, 3, 4, 5, 7, 10], s => s.Characters),
            // one-time, straight to Mythic (all seven grades at once)
            new("registered", "guild", "🪪", "Welcome to Greatness", "Register a character in My characters.", Once, s => Math.Min(1, s.Characters)),
            new("birthday", "guild", "🎂", "Birthday Registered", "Tell the bot your birthday.", Once, s => s.HasBirthday),
            new("spells", "general", "✨", "Spellbound", "See {0} spells in total.", PowerTiers, Total),
            new("kinds", "general", "📖", "Grimoire", "See {0} different spells.", [1, 5, 10, 15, 20, 25, Spells.Length], Seen),
            new("golden-seen", "legend", "🏆", "Golden Hour", "Meet the golden robot {0} times (it comes after 10 catches in a row).", LegendTiers, s => s.GoldenSeen),
            new("golden-caught", "legend", "👑", "Midas Touch", "Catch the golden robot {0} times.", LegendTiers, s => s.GoldenCaught),
            new("rainbow-caught", "legend", "🌈", "Prism Catcher", "Catch the rainbow robot {0} times.", LegendTiers, s => s.RainbowCaught),
            new("golden-rainbow", "legend", "💫", "Double Rainbow", "Meet the golden rainbow robot.", Once, s => Math.Min(1, s.DoubleSeen)),
            new("legend", "legend", "🌟", "Legendary!", "Witness {0} legendary casts — two spells at once.", LegendTiers, s => s.Legendary),
        };
        for (var i = 0; i < Spells.Length; i++)
        {
            var key = Spells[i].Key;
            list.Add(key == UltraRare
                ? new($"spell-{key}", "spells", Spells[i].Icon, SpellTitles[i], $"See the robot cast {Spells[i].Name} (ultra rare).", Once, s => Math.Min(1, s.Spells.GetValueOrDefault(key)))
                : new($"spell-{key}", "spells", Spells[i].Icon, SpellTitles[i], $"See the robot cast {Spells[i].Name} {{0}} times.", SpellTiers, s => s.Spells.GetValueOrDefault(key)));
        }
        for (var i = 0; i < Spells.Length; i++)
        for (var j = i + 1; j < Spells.Length; j++)
        {
            var (a, b) = (Spells[i], Spells[j]);
            var combo = ComboKey(a.Key, b.Key);
            list.Add(a.Key == UltraRare || b.Key == UltraRare
                ? new($"combo-{combo}", "combos", $"{a.Icon}{b.Icon}", $"{a.Name} + {b.Name}", $"Witness the ultra-rare legendary cast {a.Name} + {b.Name}.",
                    Once, s => Math.Min(1, s.LegendCombos.GetValueOrDefault(combo)))
                : new($"combo-{combo}", "combos", $"{a.Icon}{b.Icon}", $"{a.Name} + {b.Name}", $"Witness the legendary cast {a.Name} + {b.Name} {{0}} times.",
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

    /// <summary>Site activity for achievements: a visit (day), a quick search, a Member tab opened.</summary>
    public void Track(ulong guildId, ulong userId, string what, string? day, string? tab)
    {
        lock (_lock)
        {
            var key = $"{guildId}:{userId}";
            if (!_data.TryGetValue(key, out var s)) _data[key] = s = new MascotStats();
            var changed = false;
            if (day is { Length: 10 } && day != s.LastSiteDay) { s.SiteDays++; s.LastSiteDay = day; changed = true; }
            if (what == "search")
            {
                if (day != s.SearchDay) { s.SearchDay = day; s.SearchesToday = 0; }
                if (s.SearchesToday < 30) { s.SearchesToday++; s.Searches++; changed = true; }   // no farming: 30 a day
            }
            if (what == "tab" && tab is "home" or "play" or "chars" or "achievements" && !s.Tabs.Contains(tab)) { s.Tabs.Add(tab); changed = true; }
            if (!changed) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            DataFiles.Save(_path, JsonSerializer.Serialize(_data, JsonOptions));
        }
    }

    /// <summary>Record an event from the site; returns the member's catches after it (null — ignored: too fast / not valid).</summary>
    public int? Record(ulong guildId, ulong userId, string kind, bool quick, IEnumerable<string>? spells, bool legendary, int? legacyCaught = null,
        int dodges = 0, int dashes = 0, bool tired = false, int? hour = null, string? day = null, bool fast = false, bool longChase = false, bool golden = false, bool rainbow = false)
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
                if (hour is >= 0 and < 6) s.NightCatches++;
                else if (hour is >= 6 and < 10) s.MorningCatches++;
                s.Streak++; s.BestStreak = Math.Max(s.BestStreak, s.Streak);
                if (fast) s.PointBlank++;
                if (golden) s.GoldenCaught++;
                if (rainbow) s.RainbowCaught++;
                if (longChase) s.Marathon++;
                if (day is { Length: 10 } && day != s.LastCatchDay) { s.CatchDays++; s.LastCatchDay = day; }
                // once: catches the browser counted before the server kept stats (capped)
                if (!s.LegacyImported) { s.LegacyImported = true; if (legacyCaught is > 0) s.Caught = Math.Max(s.Caught, Math.Min(legacyCaught.Value, 500)); }
                s.Caught++;
                if (quick) s.QuickCatches++;
            }
            else if (kind == "escaped") { s.Escaped++; s.Streak = 0; }
            else return null;
            // what happened during the flight (capped: one robot can't give much)
            s.Spotted++;
            s.Dodges += Math.Clamp(dodges, 0, 10);
            s.Dashes += Math.Clamp(dashes, 0, 10);
            if (tired) s.Exhausted++;
            if (golden) s.GoldenSeen++;
            if (golden && rainbow) s.DoubleSeen++;
            // spells it cast — when it escaped or, now and then, when it was caught
            var valid = (spells ?? []).Distinct().Where(k => Spells.Any(x => x.Key == k)).Take(2).ToList();
            foreach (var k in valid) s.Spells[k] = s.Spells.GetValueOrDefault(k) + 1;
            if (valid.Count > 0 && s.LastFlightSpell) s.Lucky++;
            s.LastFlightSpell = valid.Count > 0;
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
