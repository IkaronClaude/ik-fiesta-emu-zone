using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>What one cell of the matrix produced. Everything here is a MEASUREMENT, not a verdict —
/// judging a run is the scorecard's job, and it needs the raw numbers to do it.</summary>
public sealed record ScenarioResult(
    string ClassName,
    int Level,
    string Map,
    string AreaName,
    bool IsDungeon,
    int Kills,
    long Experience,
    int Casts,
    bool Died,
    int SurvivedSeconds,
    int HpLeftPercent,
    int SpLeftPercent,
    int Errors,
    int SimulatedSeconds,
    string? FirstError)
{
    /// <summary>⚠️ Kills per minute OF THE TIME IT WAS ALIVE, not of the run.
    ///
    /// <para>The simulation does not respawn the player, so a character that dies at 30 seconds produces
    /// nothing for the remaining 370 and its raw kill count conflates "grinds slowly" with "died early" —
    /// two failures a script author has to fix differently. Rate is therefore measured over
    /// <see cref="SurvivedSeconds"/>, and <see cref="Died"/> carries the other half of the story.</para></summary>
    public double KillsPerMinute => SurvivedSeconds == 0 ? 0 : Kills * 60.0 / SurvivedSeconds;

    /// <summary>Experience per simulated minute. A levelling bot's actual objective, and NOT the same
    /// ranking as kills: a dungeon's mobs are worth more each, so a slower run there can still win.</summary>
    public double ExpPerMinute => SurvivedSeconds == 0 ? 0 : Experience * 60.0 / SurvivedSeconds;
}

/// <summary>⭐ THE LUA TEST MATRIX — every class, every level band, one field map and one dungeon, at full
/// gear and skills, so the driver can be scored instead of admired.
///
/// <para>The point is comparison. A single run says almost nothing: `level_quest.lua` kills 24 mobs in
/// Uruga whether its weapon does 60 damage or 60,000, which looks like a healthy run until a second cell
/// shows a walk-up-and-swing script reaching 62 on the same map. A matrix makes that kind of ceiling
/// obvious, and shows where it does and does not apply.</para>
///
/// <para>⚠️ <b>Every character is built at its best.</b> Best-in-slot gear for its class and level, every
/// skill rank it has earned, and its full `SkillPwrPt` empower allocation spent on damage. That is
/// deliberate: this project has repeatedly drawn conclusions about the BOT from fixtures that were really
/// conclusions about an invented character, and a matrix of under-geared characters would do it at
/// scale.</para></summary>
public static class ScenarioRunner
{
    /// <summary>Four thousand ticks is 400 simulated seconds at the default 100 ms tick — long enough for a
    /// grind rate to stabilise, short enough that a full matrix finishes.</summary>
    public const int DefaultTicks = 4000;

    /// <summary>How often to look for a death. Fine enough to time one usefully, coarse enough that the
    /// slicing costs nothing — 50 ticks is 5 simulated seconds.</summary>
    public const int DeathCheckTicks = 50;

    /// <summary>Build and run one cell.</summary>
    /// <param name="dungeon">Run the band's dungeon rather than its field map.</param>
    /// <returns>null when the class cannot be built at this level — no loadout, or no class table.</returns>
    public static ScenarioResult? Run(
        string shineDirectory, string ressystemDirectory, string driverSource,
        string className, int level, bool dungeon,
        int ticks = DefaultTicks, uint seed = 42, bool withWalls = true,
        CombatLog? combatLog = null, List<string>? simLog = null, List<string>? driverLog = null,
        Action<CombatSimulation>? inspect = null, bool logRouteDetours = false)
    {
        var band = ScenarioCatalog.For(level);
        if (band is null) return null;
        var area = dungeon ? band.Dungeon : band.Field;

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
            // Off unless a caller asks: a readiness list per learned skill per incoming hit is not free,
            // and a full matrix does not want it. One cell at a time, when a run needs explaining.
            CombatLog = combatLog,
            LogRouteDetours = logRouteDetours,
        };

        sim.Placements = MobCoordinateCatalog.Load(ressystemDirectory);

        var map = MobRegenData.Load(Path.Combine(shineDirectory, "MobRegen", $"{area.Map}.txt"));

        // ⭐ WALLS ARE ON BY DEFAULT — a real zone has geometry, and the matrix is meant to be zone
        // equivalent.
        //
        // ⚠️ They were opt-in for exactly as long as the harness could not route around them. With a
        // hand-rolled pathfinder `walkTo` was refused 394 times out of 395 and the character wedged; using
        // the BOT'S OWN navmesh (see Pathfinding/) that is 0 of 27, and the picture reverses:
        //
        //     Warrior L25 dungeon    walls off: DIED at 125s      walls on: survived 150s
        //     HighCleric L25 dungeon walls off: DIED at  45s      walls on: survived 150s
        //     Warrior L75 field      walls off: 10 kills          walls on: 10 kills  (identical)
        //     HighCleric L75 field   walls off:  7 kills          walls on:  7 kills  (identical)
        //
        // Geometry PREVENTS deaths at low level and costs nothing at all at high level. It does cost kills
        // at level 25 (19 -> 6 on Burning Hill), which is the honest price of walking real terrain rather
        // than through it -- and a number to improve, not to hide by turning the walls off.
        if (withWalls)
            sim.Walkable = WalkabilityGrid.Load(Path.Combine(shineDirectory, "BlockInfo"), area.Map);

        // ⭐ Normal population only. The operator's rule for the matrix: a dungeon's five or six repeated
        // bosses are a party's problem, and sending a solo character at them measures dying, not grinding.
        sim.SpawnFightable(map, MobDataBox.Load(shineDirectory),
                           spawnSeed: 7, maxRank: MapSpawner.NormalMobMaxRank);

        sim.Player.Become(table, level, equipment: loadout.Equipment, skills: skills);
        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;

        var startHp = sim.Player.MaxHp;
        var harness = LevelingBotHarness.Attach(sim, driverSource);

        // ⚠️ RUN IN SLICES, so death has a TIME. `Run(ticks: n)` plays the whole budget out whether or not
        // the character is alive at the end -- an Enchanter that died in the first minute still reported a
        // full 150 seconds survived, which made `KillsPerMinute` measure exactly what it was written to
        // stop measuring: "grinds slowly" and "got itself killed early" collapsed back into one number.
        var died = false;
        var aliveMs = 0u;
        if (harness.Load(driverSource))
        {
            for (var done = 0; done < ticks; done += DeathCheckTicks)
            {
                harness.Step(Math.Min(DeathCheckTicks, ticks - done));
                aliveMs = sim.Now;
                if (sim.Player.IsAlive) continue;
                died = true;
                break;
            }
        }

        simLog?.AddRange(sim.Log);
        driverLog?.AddRange(harness.Output);
        inspect?.Invoke(sim);

        return new ScenarioResult(
            className, level, area.Map, area.DisplayName, area.IsDungeon,
            Kills: sim.Kills,
            Experience: sim.Player.Experience,
            Casts: sim.Casts,
            Died: died,
            SurvivedSeconds: (int)(aliveMs / 1000),
            HpLeftPercent: startHp == 0 ? 0 : Math.Clamp(100 * sim.Player.Hp / startHp, 0, 100),
            SpLeftPercent: sim.Player.MaxSp == 0 ? 0 : 100 * sim.Player.Sp / sim.Player.MaxSp,
            Errors: harness.Errors.Count,
            SimulatedSeconds: (int)(ticks * sim.TickMs / 1000),
            FirstError: harness.Errors.FirstOrDefault());

    }

    /// <summary>Every class the game has, from `ClassName.shn`.</summary>
    public static IReadOnlyList<string> AllClasses(string shineDirectory)
    {
        var names = ShnFile.Load(Path.Combine(shineDirectory, "ClassName.shn"));
        var world = Path.Combine(shineDirectory, "World");
        return [.. names.Rows
            .Select(r => ShnFile.Str(r, "acEngName"))
            .Where(n => n.Length > 1 && File.Exists(Path.Combine(world, $"Param{n}Server.txt")))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Run the whole matrix. Yields as it goes so a long sweep can be watched rather than waited
    /// on, and so a crash mid-run still leaves the completed rows.</summary>
    public static IEnumerable<ScenarioResult> RunMatrix(
        string shineDirectory, string ressystemDirectory, string driverSource,
        IEnumerable<string>? classes = null, int levelStep = 5, int ticks = DefaultTicks)
    {
        classes ??= AllClasses(shineDirectory);
        foreach (var className in classes)
            foreach (var level in ScenarioCatalog.Levels(levelStep))
                foreach (var dungeon in new[] { false, true })
                {
                    var result = Run(shineDirectory, ressystemDirectory, driverSource,
                                     className, level, dungeon, ticks);
                    if (result is not null) yield return result;
                }
    }
}
