using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ THE RATCHET for `docs/BOT_SIM_INTEGRATION.md` — the real levelling driver run against the
/// simulation, with the gap between them asserted rather than admired.
///
/// <para>`LevelingBotHarnessTests` proves the driver STARTS. This proves it keeps running, and pins the
/// three numbers that say how much of it is real: how many `bot.*` the simulation backs, how many it
/// auto-stubs, and whether the script survives. Every one of them is a bound that may only improve.</para>
///
/// <para>⚠️ <b>A green run is not evidence on its own.</b> The harness stubs any `bot.*` the simulation
/// does not back, guessing a shape from the call site, and a stubbed call returns a plausible constant
/// forever. That is why the stub COUNT is asserted next to the pass: without it, "the driver ran for
/// 4,000 ticks" is a statement about the stubs, not about the bot.</para></summary>
public class BotSimIntegrationTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return Directory.Exists(Path.Combine(root, "MobRegen")) ? root : null;
    }

    private static string? DriverPath()
    {
        var p = Environment.GetEnvironmentVariable("LEVEL_QUEST_LUA")
                ?? @"C:/Projects/ik-fiesta-bots/scripts/level_quest.lua";
        return File.Exists(p) ? p : null;
    }

    /// <summary>A level-40 Cleric in Uruga's busiest spawn area, which is what the driver was written to
    /// grind.</summary>
    private static CombatSimulation Uruga(string shine, int seed = 42)
    {
        var box = MobDataBox.Load(shine);
        var map = MobRegenData.Load(Path.Combine(shine, "MobRegen", "Urg.txt"));
        var cleric = ClassParamTable.Load(Path.Combine(shine, "World", "ParamClericServer.txt"));

        var sim = new CombatSimulation(seed: (uint)seed);
        sim.SpawnFightable(map, box, spawnSeed: 7);
        sim.Player.Become(cleric, level: 40, freeStats: new FreeStats(Str: 20, Con: 20), equipment:
            [new EquipmentPiece("mace", MinWC: 60, MaxWC: 95), new EquipmentPiece("robe", AC: 40)]);
        sim.Player.Hp = sim.Player.MaxHp = 2_000_000;
        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;
        return sim;
    }

    /// <summary>⭐ THE DRIVER MUST SURVIVE. It used to die at `level_quest.lua:1360` on
    /// <c>mobs[m.mobId] = (mobs[m.mobId] or 0) + 1</c> — `nearbyMobs()` did not emit `mobId`, and a nil
    /// is a fine value right up until it is a table key.
    ///
    /// <para>That is the whole failure class: <b>the simulation's ENTITY SHAPES have to match the live
    /// API's field for field</b>, spelling included. The live mob entry uses <c>maxhp</c>, lower case,
    /// where every other entity table uses <c>maxHp</c>; the scripts read both, so tidying it silently
    /// returns nil to seven call sites.</para></summary>
    [SkippableFact]
    public void TheDriverRunsWithoutRaising()
    {
        var shine = Shine();
        var driver = DriverPath();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var src = File.ReadAllText(driver!);
        var sim = Uruga(shine!);
        var h = LevelingBotHarness.Attach(sim, src);
        h.Run(src, ticks: 4000);

        output.WriteLine(h.Report());
        h.Errors.ShouldBeEmpty("the driver raised; see the report above");
    }

    /// <summary>⭐ THE STUB RATCHET. These three numbers ARE the integration status, and the assertions
    /// are bounds that may only move one way.
    ///
    /// <para>Measured at the time of writing: <b>57</b> distinct `bot.*` reached, <b>20</b> backed by the
    /// simulation, <b>37</b> auto-stubbed, out of 181 mentioned anywhere in the driver.</para>
    ///
    /// <para>⚠️ <b>THE RAW STUB COUNT IS NOT A PROGRESS METRIC, and asserting it as one was wrong.</b>
    /// This test first bounded stubs at 36 and then failed at 37 — after a change that made things
    /// strictly better. Backing `walkTo` and `autoAttack` properly let the driver get FURTHER into its own
    /// logic, which reached 57 distinct calls instead of 53, which surfaced new stubs. Going deeper always
    /// finds more gaps, so the gap count rises exactly when progress is made.</para>
    ///
    /// <para>The two bounds that do mean something: the number BACKED (a floor that only rises), and the
    /// list of calls that gate a combat decision (which must never be stubbed at all).</para>
    ///
    /// <para>⚠️ And these are MEASURED, not aspirational. The first version asserted 19/31 — numbers
    /// written before running it — and failed on its own guess.</para></summary>
    [SkippableFact]
    public void TheSimulationBacksAGrowingShareOfWhatTheDriverCalls()
    {
        var shine = Shine();
        var driver = DriverPath();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var src = File.ReadAllText(driver!);
        var sim = Uruga(shine!);
        var h = LevelingBotHarness.Attach(sim, src);
        h.Run(src, ticks: 4000);

        output.WriteLine(h.Report());

        // Ratchet. Raise the floor as the simulation grows; never lower it silently.
        h.RealCalled.Count.ShouldBeGreaterThanOrEqualTo(20,
            "the simulation should back at least as many calls as it did last time");

        // ⚠️ NO BOUND ON THE STUB COUNT -- see the summary. It rises when the driver gets further, so
        // bounding it punishes progress. Reported, not asserted.
        output.WriteLine($"stubbed: {h.StubsCalled.Count} of {h.Calls.Count} reached "
                         + $"({h.RealCalled.Count} backed), {h.Surface.Count} in the source");

        // ⚠️ The calls that gate a COMBAT decision must never be stubbed: a stub there is a bot that looks
        // like it handled a case it never saw. `incomingDps` was one, and its stub returned a TABLE, which
        // raised "attempt to compare table with number" and killed the driver at line 2516.
        foreach (var required in new[] { "nearbyMobs", "aggressorHandles", "inCombat", "hp", "hpPct",
                                          "incomingDps", "sustainableHealDps" })
            h.StubsCalled.ShouldNotContain(required,
                $"`{required}` gates a combat decision and must be simulated, not stubbed");
    }

    /// <summary>⭐ STAGE 2's FIRST MILESTONE: the driver actually fights. It went 0 kills for a long time
    /// while looking perfectly healthy, because two calls were modelled as EVENTS where the live ones are
    /// MODES:
    ///
    /// <list type="bullet">
    ///   <item><c>bot.walkTo</c> live starts a walk the character continues; the simulation moved it six
    ///         units and stopped. The driver's own `moving()` calls a change of under 30 units standing
    ///         still, so it crawled six units every 400 ms toward a mob 1,475 units away.</item>
    ///   <item><c>bot.autoAttack</c> live sends BASHSTART once and the SERVER streams swings until the
    ///         target dies; the simulation dealt exactly one hit, against an Orc with 3,562 HP.</item>
    /// </list>
    ///
    /// <para>⚠️ One kill in seven simulated minutes is a long way from a working bot — it is the
    /// difference between "never fights" and "fights", and it is asserted here so it cannot go back to
    /// zero unnoticed. Raise this bound as stage 2 lands skills, consumables and target selection.</para></summary>
    [SkippableFact]
    public void TheDriverKillsSomething()
    {
        var shine = Shine();
        var driver = DriverPath();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var src = File.ReadAllText(driver!);
        var sim = Uruga(shine!);
        var h = LevelingBotHarness.Attach(sim, src);
        h.Run(src, ticks: 4000);

        output.WriteLine($"kills={sim.Kills} player=({sim.Player.X},{sim.Player.Y}) hp={sim.Player.Hp}");
        output.WriteLine(h.Report());

        sim.Kills.ShouldBeGreaterThan(0, "the driver walked up to a mob and never swung");
    }

    /// <summary>Speed, so a regression in it is visible. The point of the simulation is to run a grind
    /// session in seconds; at the time of writing 4,000 ticks of 100 ms — nearly seven simulated minutes —
    /// take about two seconds, which is ~200x realtime.
    ///
    /// <para>⚠️ Asserted LOOSELY and deliberately: this runs on whatever machine CI has, and a tight
    /// bound here would fail for reasons that have nothing to do with the simulation. It is a smoke
    /// alarm for an accidental O(n^2), not a benchmark.</para></summary>
    [SkippableFact]
    public void TheSimulationRunsFarFasterThanRealtime()
    {
        var shine = Shine();
        var driver = DriverPath();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var src = File.ReadAllText(driver!);
        var sim = Uruga(shine!);
        var h = LevelingBotHarness.Attach(sim, src);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        h.Run(src, ticks: 4000);
        sw.Stop();

        var simulatedMs = 4000.0 * sim.TickMs;
        var speedup = simulatedMs / Math.Max(1, sw.ElapsedMilliseconds);
        output.WriteLine($"{simulatedMs / 1000:F0} simulated seconds in {sw.ElapsedMilliseconds} ms => {speedup:F0}x realtime");

        speedup.ShouldBeGreaterThan(10, "the whole point is iterating faster than the game runs");
    }
}
