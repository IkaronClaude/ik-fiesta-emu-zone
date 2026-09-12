using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>What one journey cost.</summary>
/// <param name="Arrived">Did it get there at all.</param>
/// <param name="Seconds">Simulated seconds taken, or the whole budget when it never arrived.</param>
/// <param name="Remaining">How far short it stopped, in world units. 0 when it arrived.</param>
/// <param name="DamageTaken">What the trip cost in HP.</param>
/// <param name="Died">Whether it died on the way.</param>
/// <param name="LowWaterHpPercent">The worst it got to.</param>
/// <param name="MountedPercent">Share of the trip spent riding.</param>
/// <param name="Kills">What it stopped to kill.</param>
/// <param name="Errors">Script errors.</param>
public sealed record TravelResult(
    bool Arrived, double Seconds, int Remaining, long DamageTaken, bool Died,
    int LowWaterHpPercent, double MountedPercent, int Kills, int Errors, string? FirstError);

/// <summary>⭐ A JOURNEY, scored. The combat bench measures standing and fighting in one place; this
/// measures GETTING SOMEWHERE, which is a different problem with different right answers -- the operator's
/// brief for it is "run through enemies if target location is in a relatively danger free spot ... fight
/// your way through otherwise, or run through", plus mounts, dismounting somewhere safe, and shedding the
/// tail before the last stretch.
///
/// <para>Everything here is real: the map's own walls, its own spawns at their real detect ranges, and a
/// mover the character could actually have bought at its level. The destination is a genuinely distant
/// walkable point rather than a clear line, so the route has to deal with whatever is in the way.</para>
///
/// <para>⚠️ ONE MAP. There are still no gates and no map transitions, so the half of the brief about
/// doubling back through a gate to drop aggro cannot be scored here and is NOT implemented -- see the
/// travel ticket. What this does score is the run-through-or-fight-through decision, the mount, and the
/// arrival, which is the part that can be honest.</para></summary>
public static class TravelRunner
{
    public const int DefaultTicks = 4000;

    /// <summary>Close enough to count as arrived. A little over a screen, so the journey is judged on
    /// getting there rather than on landing on an exact tile.</summary>
    public const int ArrivedWithin = 300;

    public static TravelResult? Run(
        string shineDirectory, string ressystemDirectory, string driverSource,
        string className, int level, string mapName,
        int ticks = DefaultTicks, uint seed = 42, List<string>? driverLog = null)
    {
        var worldFile = Path.Combine(shineDirectory, "World", $"Param{className}Server.txt");
        if (!File.Exists(worldFile)) return null;
        var table = ClassParamTable.Load(worldFile);
        if (table.At(level) is null) return null;

        var items = EquipmentCatalog.Load(shineDirectory);
        var skills = SkillCatalog.Load(shineDirectory, ressystemDirectory);
        var loadout = LoadoutBuilder.Build(items, skills, table, className, level);
        if (loadout is null) return null;

        var sim = new CombatSimulation(seed: seed)
        {
            Skills = skills,
            LevelGaps = LevelGapTable.Load(shineDirectory),
            Placements = MobCoordinateCatalog.Load(ressystemDirectory),
        };

        var map = MobRegenData.Load(Path.Combine(shineDirectory, "MobRegen", $"{mapName}.txt"));
        sim.Walkable = WalkabilityGrid.Load(Path.Combine(shineDirectory, "BlockInfo"), mapName);
        sim.SpawnFightable(map, MobDataBox.Load(shineDirectory),
                           spawnSeed: 7, maxRank: MapSpawner.NormalMobMaxRank);

        sim.Player.Become(table, level, equipment: loadout.Equipment, skills: skills);
        sim.Player.AttackRange = loadout.AttackRange;
        sim.Worn = loadout.Worn;
        sim.Movers = MoverCatalog.Load(shineDirectory);
        if (sim.Movers.BestFor(level) is { } mover)
        {
            sim.Player.CarriedMover = mover;
            sim.Player.MoverSlot = 0;
        }

        var npcs = NpcPlacementCatalog.Load(shineDirectory);
        var (start, dest, what) = Endpoints(sim, map, npcs, mapName);
        if (dest is null) return null;
        driverLog?.Add($"travel: {mapName} ({start.X},{start.Y}) -> {what} ({dest.Value.X},{dest.Value.Y})");
        sim.Player.X = start.X;
        sim.Player.Y = start.Y;

        // The destination is handed to the script the way a travel goal reaches it live.
        sim.TravelGoal = dest;

        var harness = LevelingBotHarness.Attach(sim, driverSource);

        // ⚠️ ATTACH DOES NOT RUN THE CHUNK. Without Load the script's `tick` does not exist, Step returns
        // false on the very first call, and the run reports a character that never moved -- which reads
        // as "the travel script does nothing" rather than "the script was never loaded". Both scripts
        // scored an identical 5,653u short before this.
        if (!harness.Load(driverSource))
        {
            driverLog?.AddRange(harness.Output);
            return new TravelResult(false, 0, 0, 0, false, 100, 0, 0,
                                    harness.Errors.Count, harness.Errors.FirstOrDefault());
        }

        var low = 100;
        var mountedTicks = 0;
        var arrivedAt = -1;
        for (var t = 0; t < ticks; t++)
        {
            if (!harness.Step(1)) break;
            if (!sim.Player.IsAlive) break;
            if (sim.Player.RidingMover is not null) mountedTicks++;
            if (sim.Player.MaxHp > 0)
                low = Math.Min(low, 100 * sim.Player.Hp / sim.Player.MaxHp);

            long dx = sim.Player.X - dest.Value.X, dy = sim.Player.Y - dest.Value.Y;
            if (dx * dx + dy * dy <= (long)ArrivedWithin * ArrivedWithin) { arrivedAt = t; break; }
        }

        var elapsedTicks = arrivedAt >= 0 ? arrivedAt + 1 : ticks;
        long rx = sim.Player.X - dest.Value.X, ry = sim.Player.Y - dest.Value.Y;

        driverLog?.AddRange(harness.Output);

        return new TravelResult(
            Arrived: arrivedAt >= 0,
            Seconds: elapsedTicks * sim.TickMs / 1000.0,
            Remaining: arrivedAt >= 0 ? 0 : (int)Math.Sqrt(rx * rx + ry * ry),
            DamageTaken: sim.DamageTaken,
            Died: !sim.Player.IsAlive,
            LowWaterHpPercent: low,
            MountedPercent: elapsedTicks == 0 ? 0 : 100.0 * mountedTicks / elapsedTicks,
            Kills: sim.Kills,
            Errors: harness.Errors.Count,
            FirstError: harness.Errors.FirstOrDefault());
    }

    /// <summary>Start and destination, both read from the server's own files.
    ///
    /// <para>Start is the map's busiest spawn area (`MobRegen`) -- where a grinding character actually
    /// stands. Destination is the map's FURTHEST MAP-LINK GATE, from `World/NPC.txt`: a place the server
    /// files put there, that a bot genuinely has to reach to leave the zone.</para>
    ///
    /// <para>⚠️ An earlier version invented the destination -- "the furthest spawned mob" -- and that is
    /// a point with no meaning. Nothing about the map says a character would ever go there, so the
    /// journey being scored was one nobody makes. Operator: use the real server files for the map.</para></summary>
    private static ((int X, int Y) Start, (int X, int Y)? Dest, string? What) Endpoints(
        CombatSimulation sim, MobRegenData map, NpcPlacementCatalog npcs, string mapName)
    {
        var start = map.BusiestArea();

        // ⚠️ A GATE'S OWN TILE IS OFTEN NOT WALKABLE, and a destination nothing can route to is not a
        // journey -- both scripts simply stood still for the whole 400s, 5,653u short, because `walkTo`
        // correctly refuses an unroutable target. So each gate is SNAPPED to a walkable point beside it
        // and then checked for an actual route; the furthest gate that can be reached wins.
        foreach (var gate in npcs.GatesOn(mapName)
                     .OrderByDescending(g => (long)(g.X - start.X) * (g.X - start.X)
                                             + (long)(g.Y - start.Y) * (g.Y - start.Y)))
        {
            if (Reachable(sim, start, (gate.X, gate.Y)) is { } at)
                return (start, at, $"{gate.Role} {gate.RoleArg0}");
        }
        return (start, null, null);
    }

    /// <summary>A walkable point at or beside <paramref name="want"/> that the start can actually route
    /// to, or null. Searched outwards in rings so the point stays as close to the gate as possible.</summary>
    private static (int X, int Y)? Reachable(CombatSimulation sim, (int X, int Y) from, (int X, int Y) want)
    {
        if (sim.Walkable is not { } grid) return want;

        foreach (var radius in new[] { 0, 60, 120, 200, 320, 480 })
        {
            var steps = radius == 0 ? 1 : 12;
            for (var i = 0; i < steps; i++)
            {
                var a = 2 * Math.PI * i / steps;
                var x = want.X + (int)Math.Round(Math.Cos(a) * radius);
                var y = want.Y + (int)Math.Round(Math.Sin(a) * radius);
                if (x < 0 || y < 0 || !grid.IsWalkable(x, y)) continue;
                if (TilePathFinder.FindPath(grid, from.X, from.Y, x, y) is null) continue;
                return (x, y);
            }
        }
        return null;
    }
}
