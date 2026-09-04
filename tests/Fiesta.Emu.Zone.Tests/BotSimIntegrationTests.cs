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
    /// <para>Measured at the time of writing: <b>53</b> distinct `bot.*` reached, <b>17</b> backed by the
    /// simulation, <b>36</b> auto-stubbed, out of 181 mentioned anywhere in the driver. Tightening a bound
    /// is the deliverable; loosening one needs a reason in the commit message.</para>
    ///
    /// <para>⚠️ These are MEASURED, not aspirational. The first version of this test asserted 19/31 —
    /// numbers written before running it — and failed on its own guess. An assertion invented ahead of the
    /// measurement is a wish, and it fails for the wrong reason.</para></summary>
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
        h.RealCalled.Count.ShouldBeGreaterThanOrEqualTo(17,
            "the simulation should back at least as many calls as it did last time");
        h.StubsCalled.Count.ShouldBeLessThanOrEqualTo(36,
            "auto-stubs are the gap; this may only shrink");

        // ⚠️ The calls that gate a COMBAT decision must never be stubbed: a stub there is a bot that looks
        // like it handled a case it never saw. `incomingDps` was one, and its stub returned a TABLE, which
        // raised "attempt to compare table with number" and killed the driver at line 2516.
        foreach (var required in new[] { "nearbyMobs", "aggressorHandles", "inCombat", "hp", "hpPct",
                                          "incomingDps", "sustainableHealDps" })
            h.StubsCalled.ShouldNotContain(required,
                $"`{required}` gates a combat decision and must be simulated, not stubbed");
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
