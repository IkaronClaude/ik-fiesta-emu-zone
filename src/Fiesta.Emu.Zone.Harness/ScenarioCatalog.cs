namespace Fiesta.Emu.Zone.Lua;

/// <summary>One place to grind: a map, and whether it is open field or a dungeon.</summary>
/// <param name="Map">`MobRegen` file name, e.g. `ValDn01`.</param>
/// <param name="DisplayName">The name a player sees, from `MapInfo.shn`.</param>
/// <param name="MedianLevel">The median level of its fightable population — the figure the band is
/// assigned from, not the min or the max.</param>
/// <param name="IsDungeon">`MapInfo.InSide == 1`.</param>
public sealed record GrindArea(string Map, string DisplayName, int MedianLevel, bool IsDungeon);

/// <summary>A level decade, with one field map and one dungeon to test a character in.</summary>
/// <param name="Band">The decade's first level: 20 means 20-29.</param>
public sealed record LevelBand(int Band, GrindArea Field, GrindArea Dungeon)
{
    public int Low => Band;
    public int High => Band + 9;
    public bool Contains(int level) => level >= Low && level <= High;
}

/// <summary>⭐ THE TEST MATRIX'S MAPS — one field map and one dungeon per level decade.
///
/// <para><b>These are baked deliberately, and only after being found from the data.</b> The project rule
/// is that game facts are loaded, not hard-coded; the operator lifted it for this table once the areas
/// were identified, with one condition: <b>every band must be a full decade, 10x to 10x+9</b>. The
/// selection is recorded here rather than recomputed because a scorecard has to compare like with like
/// across runs, and a map that silently changed between runs would make the numbers meaningless.</para>
///
/// <para><b>How they were chosen.</b> Every `MobRegen` map was swept, each spawn expanded by its count,
/// and the <b>median</b> level of the fightable population taken — the min and max are useless here
/// because most maps carry a stray level-150 spawn that stretches the range to nonsense. A map belongs to
/// the band its median falls in; within a band the most populous field map and the most populous dungeon
/// win, since a thin map starves the bot of targets and measures walking instead of fighting.
/// `MapInfo.InSide == 1` is the dungeon marker.</para>
///
/// <para>⭐ <b>The method was validated before being trusted:</b> the operator named two areas from play —
/// "Marlone hideout (20-29)" and "that trump map (70-79)". The sweep puts `ValDn01` "Marlone Clan's
/// Hideout" at median 23 and `ForDn01` "Trumpy Remains" at median 73, each the top dungeon of exactly the
/// band the operator gave. Two independent hits is what earned the rest of the table.</para>
///
/// <para>⚠️ <b>Dungeon runs spawn normal mobs only.</b> The operator's instruction was to fight the
/// ordinary population, not the five or six repeated bosses a dungeon carries — those are a party's job
/// and would turn every run into a death test. See <see cref="MapSpawner"/>'s rank filter.</para></summary>
public static class ScenarioCatalog
{
    /// <summary>Below this the game has no dungeon worth testing and characters are still in their
    /// starter zone; the matrix starts at the first decade with a real pairing.</summary>
    public const int FirstBand = 10;

    /// <summary>The highest decade with both a field map and a dungeon in the shipped data.</summary>
    public const int LastBand = 100;

    public static IReadOnlyList<LevelBand> Bands { get; } =
    [
        new(10,  new("RouCos03",   "Sea of Greed",               18, false),
                 new("EchoCave",   "Echo Cave",                  12, true)),
        // ⭐ the operator's own example for this band.
        new(20,  new("RouVal02",   "Burning Hill",               24, false),
                 new("ValDn01",    "Marlone Clan's Hideout",     23, true)),
        new(30,  new("EldCem02",   "Vine Tomb",                  38, false),
                 new("CemDn01",    "Graveyard of the Dead",      33, true)),
        new(40,  new("EldGbl01",   "Goblin Camp",                45, false),
                 new("GblDn02",    "Abysmal Summit",             48, true)),
        new(50,  new("EldPri01",   "Collapsed Prison",           54, false),
                 new("PriDn02",    "Passage of the Abyss",       58, true)),
        new(60,  new("EldFor01",   "Ancient Elven Woods",        69, false),
                 new("EldPriDn01", "Concealed Prison 1st Floor", 65, true)),
        // ⭐ and this band's dungeon is the operator's second example.
        new(70,  new("EldSleep01", "Forest of Slumber",          76, false),
                 new("ForDn01",    "Trumpy Remains",             73, true)),
        new(80,  new("UrgFire01",  "Burning Rock",               86, false),
                 new("FireDn01",   "Guardian's Holy Shrine",     84, true)),
        new(90,  new("UrgSwa01",   "Swamp of Dawn",              98, false),
                 new("SwaDn01",    "Tear's Marsh",               97, true)),
        new(100, new("UrgDark01",  "Dark Land",                 108, false),
                 new("AlDn01",     "Origin of Life Tree",       108, true)),
    ];

    /// <summary>The band a level belongs to, or null when it is outside the matrix.</summary>
    public static LevelBand? For(int level) => Bands.FirstOrDefault(b => b.Contains(level));

    /// <summary>The levels the matrix runs, in steps of five — the operator's cadence.</summary>
    public static IEnumerable<int> Levels(int step = 5)
    {
        for (var level = FirstBand; level <= LastBand + 9; level += step) yield return level;
    }
}
