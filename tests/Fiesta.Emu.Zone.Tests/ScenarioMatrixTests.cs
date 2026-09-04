using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ THE LUA TEST MATRIX — every class, every level band, one field map and one dungeon, at full
/// gear and skills.
///
/// <para>These are the INVARIANTS, not the scores. A scorecard is meant to move as the driver improves;
/// what must not move is the shape of the experiment — full decades, real maps, a loadout for every class,
/// bosses excluded, and a run that measures the driver rather than the harness. Score the driver with
/// <see cref="ScenarioRunner.RunMatrix"/>; a full sweep is ~1,000 cells and belongs in a tool, not a test
/// suite.</para></summary>
[Collection(HeavySimulationCollection.Name)]
public class ScenarioMatrixTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return Directory.Exists(Path.Combine(root, "MobRegen")) ? root : null;
    }

    private static string? Ressystem()
    {
        var root = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return File.Exists(Path.Combine(root, "ActiveSkillView.shn")) ? root : null;
    }

    private static string? DriverPath()
    {
        var p = Environment.GetEnvironmentVariable("LEVEL_QUEST_LUA")
                ?? @"C:/Projects/ik-fiesta-bots/scripts/level_quest.lua";
        return File.Exists(p) ? p : null;
    }

    /// <summary>⚠️ <b>THE OPERATOR'S ONE HARD CONDITION: every band is a full decade, 10x to 10x+9.</b>
    /// The areas may be hard-coded — they were found from the data first — but the banding may not
    /// drift.</summary>
    [Fact]
    public void EveryBandIsAWholeDecade()
    {
        ScenarioCatalog.Bands.ShouldNotBeEmpty();
        foreach (var band in ScenarioCatalog.Bands)
        {
            (band.Band % 10).ShouldBe(0, $"band {band.Band} does not start a decade");
            band.High.ShouldBe(band.Band + 9);
            band.Contains(band.Band).ShouldBeTrue();
            band.Contains(band.High).ShouldBeTrue();
            band.Contains(band.High + 1).ShouldBeFalse();
        }

        // ...and they tile without a gap, so every level in range resolves.
        var expected = ScenarioCatalog.FirstBand;
        foreach (var band in ScenarioCatalog.Bands.OrderBy(b => b.Band))
        {
            band.Band.ShouldBe(expected, "the decades must tile with no gap");
            expected += 10;
        }
    }

    /// <summary>⭐ THE TWO AREAS THE OPERATOR NAMED FROM PLAY, in the bands they named them for. This is
    /// the check that earned the rest of the table: the selection method was run blind over every map and
    /// independently put both of these where the operator said they belonged.</summary>
    [Fact]
    public void TheOperatorsOwnTwoAreasAreWhereTheySaidTheyWere()
    {
        ScenarioCatalog.For(25)!.Dungeon.Map.ShouldBe("ValDn01");
        ScenarioCatalog.For(25)!.Dungeon.DisplayName.ShouldBe("Marlone Clan's Hideout");
        ScenarioCatalog.For(75)!.Dungeon.Map.ShouldBe("ForDn01");
        ScenarioCatalog.For(75)!.Dungeon.DisplayName.ShouldBe("Trumpy Remains");
    }

    /// <summary>Every named map has to actually exist, and its population has to sit in its band —
    /// otherwise a "level 25 dungeon" is quietly testing level-60 mobs.</summary>
    [SkippableFact]
    public void EveryAreaExistsAndItsMobsMatchItsBand()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var box = MobDataBox.Load(shine!);
        foreach (var band in ScenarioCatalog.Bands)
            foreach (var area in new[] { band.Field, band.Dungeon })
            {
                var path = Path.Combine(shine!, "MobRegen", $"{area.Map}.txt");
                File.Exists(path).ShouldBeTrue($"{area.Map} ({area.DisplayName}) has no MobRegen file");

                var map = MobRegenData.Load(path);
                var levels = new List<int>();
                foreach (var g in map.Groups)
                    foreach (var e in map.EntriesFor(g.GroupIndex))
                    {
                        var info = box.InfoFor(e.MobIndex);
                        // The 150s are event/boss spawns that sit in almost every map; the median is taken
                        // over the real population, which is why the band is assigned from it and not from
                        // the range.
                        if (info is null || !info.IsFightable || info.Level <= 0 || info.Level >= 150) continue;
                        for (var i = 0; i < e.MobNum; i++) levels.Add(info.Level);
                    }

                levels.Count.ShouldBeGreaterThan(20, $"{area.Map} is too thin to grind in");
                levels.Sort();
                var median = levels[levels.Count / 2];
                output.WriteLine($"{band.Band,4}-{band.High} {(area.IsDungeon ? "dungeon" : "field  ")} "
                                 + $"{area.Map,-12} {area.DisplayName,-30} median={median}");
                median.ShouldBe(area.MedianLevel, $"{area.Map}'s population has shifted");
                band.Contains(median).ShouldBeTrue($"{area.Map} median {median} is outside {band.Band}-{band.High}");
            }
    }

    /// <summary>⚠️ <b>NORMAL MOBS, NOT BOSSES.</b> A dungeon carries five or six repeated spawns of one
    /// high-rank mob and the operator's instruction was to fight the ordinary population — a solo
    /// character sent at those measures how fast it dies.</summary>
    [SkippableFact]
    public void DungeonRunsSpawnNoBosses()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var box = MobDataBox.Load(shine!);
        var band = ScenarioCatalog.For(75)!;
        var map = MobRegenData.Load(Path.Combine(shine!, "MobRegen", $"{band.Dungeon.Map}.txt"));

        var all = new CombatSimulation();
        all.SpawnFightable(map, box, spawnSeed: 7);
        var normal = new CombatSimulation();
        normal.SpawnFightable(map, box, spawnSeed: 7, maxRank: MapSpawner.NormalMobMaxRank);

        var dropped = all.Mobs.Count - normal.Mobs.Count;
        output.WriteLine($"{band.Dungeon.DisplayName}: {all.Mobs.Count} fightable, "
                         + $"{normal.Mobs.Count} normal, {dropped} boss-rank excluded");

        dropped.ShouldBeGreaterThan(0, "Trumpy Remains carries 76 rank-3 D_SpadeGuardTrumpy; they must go");
        normal.Mobs.ShouldAllBe(m => (box.ServerFor(m.Name)!.Rank) <= MapSpawner.NormalMobMaxRank);
        normal.Mobs.ShouldNotBeEmpty("excluding bosses must not empty the map");
    }

    /// <summary>Every class must build a loadout at every level it can reach — with a WEAPON. An unarmed
    /// character's WC comes entirely from the class table, and a matrix full of those would measure
    /// nothing.</summary>
    [SkippableFact]
    public void EveryClassGetsRealGearAtEveryLevel()
    {
        var (shine, ressystem) = (Shine(), Ressystem());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");

        var items = EquipmentCatalog.Load(shine!);
        var skills = SkillCatalog.Load(shine!, ressystem!);
        var classes = ScenarioRunner.AllClasses(shine!);
        classes.Count.ShouldBeGreaterThan(20, "the game has 26 playable classes");

        foreach (var className in classes)
        {
            var table = ClassParamTable.Load(Path.Combine(shine!, "World", $"Param{className}Server.txt"));
            foreach (var level in ScenarioCatalog.Levels())
            {
                if (table.At(level) is null) continue;
                var loadout = LoadoutBuilder.Build(items, skills, table, className, level);
                loadout.ShouldNotBeNull($"{className} at level {level} has no loadout");
                loadout.Equipment.ShouldNotBeEmpty();

                // ⚠️ A flat MinWC == MaxWC weapon is a GM test item; `LooksObtainable` filters them, and
                // without it the best loadout for every class collapses onto the same few debug weapons.
                var weapon = loadout.Equipment.FirstOrDefault(e => e.MaxWC > 0);
                if (weapon is not null) weapon.MinWC.ShouldBeLessThan(weapon.MaxWC, $"{className} L{level}");
            }
        }
    }

    /// <summary>Empower points come from `SkillPwrPt` and get spent on damage, spread across the offensive
    /// LINES rather than stacked on one rank — the wire reports an allocation against a line's base rank.</summary>
    [SkippableFact]
    public void EmpowerPointsAreSpentOnDamageAcrossTheOffensiveLines()
    {
        var (shine, ressystem) = (Shine(), Ressystem());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");

        var items = EquipmentCatalog.Load(shine!);
        var skills = SkillCatalog.Load(shine!, ressystem!);
        var table = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamWarriorServer.txt"));
        var loadout = LoadoutBuilder.Build(items, skills, table, "Warrior", 60)!;

        loadout.EmpowerPoints.ShouldBe(29, "a level-60 Warrior's SkillPwrPt");
        var spend = LoadoutBuilder.AllocateEmpower(loadout);

        spend.Count.ShouldBe(loadout.Offensive.Count, "every offensive line should get a share");
        spend.Values.Sum(e => e.Damage).ShouldBe(loadout.EmpowerPoints, "all the points get spent");
        spend.Values.ShouldAllBe(e => e.Sp == 0 && e.KeepTime == 0 && e.CoolTime == 0,
            "a damage build puts nothing in the other three fields");
    }

    /// <summary>⭐ A SMOKE RUN. Two cells — a field map and a dungeon — proving the runner produces a real,
    /// self-consistent measurement.</summary>
    [SkippableFact]
    public void TheMatrixProducesAScorecard()
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var src = File.ReadAllText(driver!);
        foreach (var dungeon in new[] { false, true })
        {
            var r = ScenarioRunner.Run(shine!, ressystem!, src, "Warrior", 25, dungeon, ticks: 600);
            r.ShouldNotBeNull();

            output.WriteLine($"{r.ClassName} L{r.Level} {r.AreaName} ({(r.IsDungeon ? "dungeon" : "field")}): "
                             + $"kills={r.Kills} exp={r.Experience} casts={r.Casts} "
                             + $"{(r.Died ? $"DIED at {r.SurvivedSeconds}s" : $"alive {r.SurvivedSeconds}s")} "
                             + $"hp={r.HpLeftPercent}% k/min={r.KillsPerMinute:F1}");

            r.Errors.ShouldBe(0, $"the driver raised: {r.FirstError}");
            r.SurvivedSeconds.ShouldBeGreaterThan(0);
            r.HpLeftPercent.ShouldBeInRange(0, 100, "a percentage, even when the character died");
            if (!r.Died) r.SurvivedSeconds.ShouldBe(r.SimulatedSeconds);
        }
    }

    /// <summary>⚠️ THE HARNESS TRAP THIS MATRIX WALKED INTO. `Run` reloads the script and re-fires its
    /// entry points, so slicing it restarts the driver — every Lua local thrown away — and a same-seed run
    /// silently changed its answer. `Load` + `Step` is the observable form.</summary>
    [Fact]
    public void SteppingDoesNotRestartTheScript()
    {
        const string counter = """
            count = 0
            function tick() count = count + 1 end
            """;

        var sliced = new CombatSimulation(seed: 1);
        var h1 = LevelingBotHarness.Attach(sliced, counter);
        h1.Load(counter).ShouldBeTrue();
        for (var i = 0; i < 4; i++) h1.Step(10);

        var whole = new CombatSimulation(seed: 1);
        var h2 = LevelingBotHarness.Attach(whole, counter);
        h2.Run(counter, ticks: 40);

        sliced.Script.Globals.Get("count").Number.ShouldBe(40,
            "four ten-tick steps must accumulate, not reset");
        whole.Script.Globals.Get("count").Number.ShouldBe(40);
    }
}
