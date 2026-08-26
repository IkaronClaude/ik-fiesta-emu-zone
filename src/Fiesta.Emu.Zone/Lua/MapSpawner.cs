using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Mob;
using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>Stats for a mob, by its table name. Real values live in `MobInfoServer.shn`, which is not
/// wired up yet — until it is, the caller supplies them and the defaults are placeholders.</summary>
public sealed record MobStats(int MaxHp, int AttackDamage, int DetectRange, int AttackRange)
{
    public static MobStats Default { get; } = new(MaxHp: 300, AttackDamage: 25, DetectRange: 60, AttackRange: 12);
}

/// <summary>Populating a simulation from a map's real spawn tables.</summary>
public static class MapSpawner
{
    /// <summary>Spawn every group in the map at full population.
    ///
    /// <para>One mob per `MobNum`, placed by the ported area sampler — rotated rectangle or circle,
    /// whichever the group is. Handles are assigned sequentially from <paramref name="firstHandle"/>.</para>
    ///
    /// <para>Iteration is over the actual row counts, never over the server's fixed
    /// <see cref="MobRegenData.MaxSpawnGroups"/> capacity, so changing that limit needs no change
    /// here.</para></summary>
    /// <summary>Spawn a map and give every mob its REAL stats, from the game's own tables.
    ///
    /// <para>The overload taking a <see cref="MobDataBox"/> is the one to prefer: each spawned mob gets its
    /// level, HP, defences, primaries, weapon damage and swing timings from `MobInfo`/`MobInfoServer`/
    /// `MobWeapon`. A mob the tables do not know falls back to <paramref name="statsFor"/>, and that gap is
    /// reported rather than silently filled — see <see cref="SpawnAll(CombatSimulation, MobRegenData,
    /// MobDataBox, Func{string, MobStats}?, uint, ushort)"/>.</para></summary>
    public static IReadOnlyList<SimMob> SpawnAll(
        this CombatSimulation sim,
        MobRegenData map,
        MobDataBox data,
        Func<string, MobStats>? statsFor = null,
        uint spawnSeed = 1,
        ushort firstHandle = 100)
    {
        var spawned = sim.SpawnAll(map, statsFor, spawnSeed, firstHandle);
        foreach (var mob in spawned)
            if (MobCombatant.Build(data, mob.Name) is { } definition)
                mob.Define(definition);
        return spawned;
    }

    /// <summary>Which mobs in a map have no row in the data box — a decode gap, not a shrug.</summary>
    public static IReadOnlyList<string> MobsMissingData(this MobRegenData map, MobDataBox data)
        => map.Entries.Select(e => e.MobIndex).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => data.InfoFor(n) is null || data.ServerFor(n) is null)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<SimMob> SpawnAll(
        this CombatSimulation sim,
        MobRegenData map,
        Func<string, MobStats>? statsFor = null,
        uint spawnSeed = 1,
        ushort firstHandle = 100)
    {
        statsFor ??= _ => MobStats.Default;
        var rng = new CrtRandom(spawnSeed);
        var spawned = new List<SimMob>();
        var handle = firstHandle;

        foreach (var group in map.Groups)
        {
            foreach (var entry in map.EntriesFor(group.GroupIndex))
            {
                var stats = statsFor(entry.MobIndex);
                for (var i = 0; i < entry.MobNum; i++)
                {
                    var (x, y) = MobRegenLoc.Sample(group, rng);
                    var mob = sim.AddMob(handle++, x, y, m =>
                    {
                        m.Hp = m.MaxHp = stats.MaxHp;
                        m.AttackDamage = stats.AttackDamage;
                    });
                    mob.Mob.so_getDetectRange = stats.DetectRange;
                    mob.Arg.Combat.AttackRange = stats.AttackRange;
                    mob.Name = entry.MobIndex;
                    mob.SpawnGroup = group.GroupIndex;
                    mob.RespawnSeconds = entry.RegStandard;
                    spawned.Add(mob);
                }
            }
        }
        return spawned;
    }

    /// <summary>Where a map's spawns actually are — useful for placing a character somewhere with mobs
    /// rather than in empty terrain, which for a 10,000-unit map is most of it.</summary>
    public static (int X, int Y) BusiestArea(this MobRegenData map)
    {
        var best = map.Groups
            .Select(g => (g, count: map.EntriesFor(g.GroupIndex).Sum(e => e.MobNum)))
            .OrderByDescending(t => t.count)
            .FirstOrDefault();
        return best.g is null ? (0, 0) : (best.g.CenterX, best.g.CenterY);
    }
}
