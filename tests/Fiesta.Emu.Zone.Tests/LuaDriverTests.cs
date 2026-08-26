using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Real Lua, driving the simulated world — the point of the whole exercise.
///
/// The scripts here are written the way the bot's driver is: poll `bot.*`, decide, act, return. Nothing
/// is stubbed on the Lua side.</summary>
public class LuaDriverTests
{
    private const string KillNearest = """
        function on_tick()
          local mobs = bot.nearbyMobs()
          if #mobs == 0 then return end
          local best, bestDist = nil, 1e9
          for i = 1, #mobs do
            if mobs[i].dist < bestDist then best, bestDist = mobs[i], mobs[i].dist end
          end
          if not bot.attack(best.handle) then
            bot.walkTo(best.x, best.y)
          end
        end
        """;

    private static CombatSimulation Sim(uint seed = 1)
    {
        var sim = new CombatSimulation(seed: seed);
        sim.Player.X = 0;
        sim.Player.Y = 0;
        return sim;
    }

    [Fact]
    public void LuaCanReadTheSimulatedWorld()
    {
        var sim = Sim();
        sim.AddMob(handle: 10, x: 30, y: 0);

        sim.LoadScript("""
            function probe()
              local m = bot.nearbyMobs()
              return #m, m[1].handle, m[1].dist, bot.hpPct(), bot.now()
            end
            """);
        sim.Tick();

        var r = sim.Script.Call(sim.Script.Globals.Get("probe"));
        r.Tuple[0].Number.ShouldBe(1);
        r.Tuple[1].Number.ShouldBe(10);
        r.Tuple[2].Number.ShouldBe(30.0, 0.001);
        r.Tuple[3].Number.ShouldBe(100.0);
        r.Tuple[4].Number.ShouldBe(sim.Now);
    }

    /// <summary>The end-to-end case: a Lua driver closes the distance and kills a mob, unaided.</summary>
    [Fact]
    public void ALuaDriverWalksToAMobAndKillsIt()
    {
        var sim = Sim();
        var mob = sim.AddMob(handle: 10, x: 60, y: 0, configure: m => m.Hp = m.MaxHp = 200);
        sim.LoadScript(KillNearest);

        var ticks = sim.Run(maxTicks: 400);

        mob.Mob.IsAlive.ShouldBeFalse();
        sim.Player.IsAlive.ShouldBeTrue();
        ticks.ShouldBeLessThan(400);
        sim.Player.X.ShouldBeGreaterThan(0);          // it actually walked
    }

    /// <summary>The mob fights back through the ported paths: it acquires, closes, swings, and its damage
    /// lands on a delay.</summary>
    [Fact]
    public void TheMobFightsBackAndItsDamageLandsOnADelay()
    {
        var sim = Sim();
        sim.AddMob(handle: 10, x: 20, y: 0, configure: m =>
        {
            m.Hp = m.MaxHp = 100_000;                  // unkillable, so the fight runs long enough to observe
            m.AttackDamage = 30;
        });
        sim.LoadScript(KillNearest);

        sim.Run(maxTicks: 120);

        sim.Player.Hp.ShouldBeLessThan(sim.Player.MaxHp);
        sim.Log.ShouldContain(l => l.Contains("swings"));
        sim.Log.ShouldContain(l => l.Contains("hits for 30"));
    }

    [Fact]
    public void TheMobAcquiresThePlayerThroughTheAggroPath()
    {
        var sim = Sim();
        var mob = sim.AddMob(handle: 10, x: 20, y: 0, configure: m => m.Hp = m.MaxHp = 100_000);
        sim.LoadScript("function on_tick() end");     // the player does nothing at all

        sim.Run(maxTicks: 30);

        mob.Arg.Target.ShouldBe(sim.Player);
        sim.Api.inCombat().ShouldBeTrue();
        sim.Api.aggressorHandles().Length.ShouldBe(1);
    }

    [Fact]
    public void AMobOutsideItsDetectRangeIsNeverAcquired()
    {
        var sim = Sim();
        var mob = sim.AddMob(handle: 10, x: 500, y: 0);
        sim.LoadScript("function on_tick() end");

        sim.Run(maxTicks: 30);

        mob.Arg.Target.ShouldBeNull();
        sim.Api.inCombat().ShouldBeFalse();
    }

    /// <summary>The reason the RNG was ported exactly: the same seed replays the same fight, so a driver
    /// change can be attributed to the driver rather than to luck.
    ///
    /// <para>The mob is given a skill chance deliberately. Without one the RNG is never consulted and the
    /// fight is deterministic whatever the seed — which is exactly what an earlier version of this test
    /// discovered by asserting that two seeds differ and finding they did not. A reproducibility test
    /// proves nothing unless the scenario actually contains a random decision.</para></summary>
    [Fact]
    public void TheSameSeedReplaysTheSameFight()
    {
        static List<string> RunOnce(uint seed)
        {
            var sim = Sim(seed);
            var mob = sim.AddMob(10, 40, 0, m => { m.Hp = m.MaxHp = 4000; m.AttackDamage = 5; });
            mob.Arg.Combat.SkillChancePermille = 500;      // now the RNG decides something
            sim.LoadScript(KillNearest);
            sim.Run(400);
            return sim.Log;
        }

        RunOnce(7).ShouldBe(RunOnce(7));
        RunOnce(7).ShouldContain(l => l.Contains("Skill"));   // the draw really did fire
        RunOnce(7).ShouldNotBe(RunOnce(8));
    }

    /// <summary>A hundred seconds of ten-mob combat in a fraction of a second — the speed-up the whole
    /// simulator exists for.
    ///
    /// <para>The player is given enough HP to survive the whole run. An earlier version did not, and
    /// `Run` correctly stopped at tick 78 when it died — the simulation was right and the test's premise
    /// was wrong.</para></summary>
    [Fact]
    public void AThousandTicksRunFastEnoughToIterateOn()
    {
        var sim = Sim();
        sim.Player.Hp = sim.Player.MaxHp = 10_000_000;
        for (ushort h = 10; h < 20; h++)
            sim.AddMob(h, 30 + h, 0, m => m.Hp = m.MaxHp = 100_000);
        sim.LoadScript(KillNearest);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ticks = sim.Run(maxTicks: 1000);      // 1000 ticks x 100ms = 100 simulated seconds
        sw.Stop();

        ticks.ShouldBe(1000);
        sim.Now.ShouldBe(100_000u);
        sim.Player.IsAlive.ShouldBeTrue();
        sw.ElapsedMilliseconds.ShouldBeLessThan(2000);
    }

    /// <summary>The boundary is stated rather than hidden: the real driver needs quest, inventory and
    /// navigation calls this simulation does not model, and asking for one fails loudly instead of
    /// returning a plausible lie.</summary>
    [Fact]
    public void UnmodelledPartsOfTheApiFailLoudlyRatherThanReturningSomethingPlausible()
    {
        var sim = Sim();
        sim.LoadScript("function q() return bot.questName(1) end");

        Should.Throw<Exception>(() => sim.Script.Call(sim.Script.Globals.Get("q")));
    }
}
