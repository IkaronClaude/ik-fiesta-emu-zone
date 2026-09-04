using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Running a real bot driver against the simulation and measuring what it could reach.</summary>
public class LevelingBotHarnessTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return Directory.Exists(Path.Combine(root, "World")) ? root : null;
    }

    /// <summary>The operator's levelling driver. Not in this repo and never copied into it — it is the bot's
    /// own script, loaded from wherever `LEVEL_QUEST_LUA` points (or its usual sibling path).</summary>
    private static string? DriverPath()
    {
        var p = Environment.GetEnvironmentVariable("LEVEL_QUEST_LUA")
                ?? @"C:/Projects/ik-fiesta-bots/scripts/level_quest.lua";
        return File.Exists(p) ? p : null;
    }

    [Fact]
    public void ADriverGetsTheWholeSurfaceItAsksFor()
    {
        var sim = new CombatSimulation(seed: 1);
        const string src = """
            function tick()
              local m = bot.nearbyMobs()
              local q = bot.availableQuests()           -- still unbacked: the sim spawns no quest NPCs
              local healthy = bot.hpPct() > 10          -- evaluated first: `#m > 0 and ...` short-circuits
              if healthy and #m > 0 then bot.swing(m[1].handle) end
              log("tick " .. tostring(bot.level()))
            end
            """;

        var h = LevelingBotHarness.Attach(sim, src);
        h.Run(src, ticks: 5);

        h.Errors.ShouldBeEmpty();
        h.RealCalled.ShouldContain("nearbyMobs");
        h.RealCalled.ShouldContain("hpPct");
        h.RealCalled.ShouldContain("level");
        // ⚠️ This names a call the simulation genuinely does NOT back, and it has to be kept honest: it
        // used to name `inventory`, which is now real, and the test failed for the right reason. Pick a
        // replacement that is still missing rather than weakening the assertion.
        h.StubsCalled.ShouldContain("availableQuests");
        h.Output.ShouldNotBeEmpty("the host-provided log globals must exist");
    }

    /// <summary>Stub SHAPE is what decides whether a driver survives: a number where it indexes, or a table
    /// where it compares, kills the script instantly. The harness works the shape out from usage.</summary>
    [Fact]
    public void StubsTakeTheShapeTheScriptUsesThemAs()
    {
        var sim = new CombatSimulation(seed: 1);
        const string src = """
            function tick()
              local inv = bot.inventory()
              local n = inv[0]
              if bot.hpStones() < 3 then log("low") end
              for _, q in ipairs(bot.activeQuests()) do log(q) end
            end
            """;

        var h = LevelingBotHarness.Attach(sim, src);
        h.Run(src, ticks: 3);

        h.Errors.ShouldBeEmpty("indexing, comparison and ipairs must all survive their stubs");
    }

    /// <summary>THE GOAL: the real 6,200-line levelling driver, run against a populated Uruga.
    ///
    /// <para>It is not expected to LEVEL anything — quests, shops and inventory are stubs, so the world it
    /// sees is empty of everything except combat. What this asserts is that it loads, ticks, reads genuine
    /// state out of the simulation, and reaches deep enough into its own logic to be worth measuring.</para>
    ///
    /// <para>Its own wedge diagnostics firing is the correct outcome, not a failure: the driver notices it is
    /// making no progress, which is exactly what a bot in an empty world should conclude.</para></summary>
    [SkippableFact]
    public void TheRealLevellingDriverRunsAgainstTheSimulation()
    {
        var shine = Shine();
        var driver = DriverPath();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var box = MobDataBox.Load(shine!);
        var map = MobRegenData.Load(Path.Combine(shine!, "MobRegen", "Urg.txt"));
        var cleric = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamClericServer.txt"));
        var src = File.ReadAllText(driver!);

        var sim = new CombatSimulation(seed: 42);
        sim.SpawnFightable(map, box, spawnSeed: 7);
        sim.Player.Become(cleric, level: 40, freeStats: new FreeStats(Str: 20, Con: 20), equipment:
            [new EquipmentPiece("mace", MinWC: 60, MaxWC: 95), new EquipmentPiece("robe", AC: 40)]);
        sim.Player.Hp = sim.Player.MaxHp = 2_000_000;
        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;

        var h = LevelingBotHarness.Attach(sim, src);
        h.Run(src, ticks: 500);

        // It reaches a substantial part of its own logic.
        h.Calls.Count.ShouldBeGreaterThan(30);
        h.RealCalled.Count.ShouldBeGreaterThan(8);

        // And what it reads is genuinely ours, not a stub echoing back a constant.
        h.RealCalled.ShouldContain("level");
        h.RealCalled.ShouldContain("hpPct");
        h.RealCalled.ShouldContain("inCombat");
        h.Output.ShouldContain(l => l.Contains("level_quest: START lvl40"),
            "it read the character's real class-table level");
    }
}
