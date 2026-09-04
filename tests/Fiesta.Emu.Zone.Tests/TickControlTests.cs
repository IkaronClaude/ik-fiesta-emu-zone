using Fiesta.Emu.Zone.Mob;
using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Time in this simulation is a dial, not a clock.
///
/// <para>Nothing in `src/` reads `DateTime`, `Environment.TickCount` or a `Stopwatch` — `Now` advances
/// only when <see cref="CombatSimulation.Tick"/> is called, by exactly <see cref="CombatSimulation.TickMs"/>.
/// These tests pin that, because the moment anything reaches for a real clock every other test in the
/// suite becomes timing-dependent and starts failing on a busy machine.</para></summary>
public class TickControlTests
{
    private static CombatSimulation Sim() => new(seed: 1);

    [Fact]
    public void TimeDoesNotMoveOnItsOwn()
    {
        var sim = Sim();
        sim.Now.ShouldBe(0u);

        Thread.Sleep(50);          // real time passes...
        sim.Now.ShouldBe(0u);      // ...and the simulation does not notice
    }

    [Fact]
    public void EachTickAdvancesExactlyTickMs()
    {
        var sim = Sim();
        sim.TickMs = 250;

        sim.Tick();
        sim.Now.ShouldBe(250u);
        sim.Tick();
        sim.Now.ShouldBe(500u);
    }

    /// <summary>The rate can change mid-run — coarse while nothing is happening, fine through a fight.</summary>
    [Fact]
    public void TheTickRateCanChangeMidRun()
    {
        var sim = Sim();
        sim.TickMs = 1000;
        sim.Tick();
        sim.Now.ShouldBe(1000u);

        sim.TickMs = 10;
        sim.Tick();
        sim.Now.ShouldBe(1010u);
    }

    [Fact]
    public void ATickWithoutAScriptStillAdvancesTheWorld()
    {
        var sim = Sim();
        var mob = sim.AddMob(10, 20, 0, m => m.Hp = m.MaxHp = 1000);
        sim.Player.X = 0;

        for (var i = 0; i < 5; i++) sim.Tick();      // no script loaded at all

        mob.Arg.Target.ShouldBe(sim.Player);          // the mob still acquired
    }

    /// <summary>`Run` is only a loop over `Step`, so driving manually gives an identical world.</summary>
    [Fact]
    public void SteppingManuallyMatchesRunning()
    {
        static CombatSimulation Build()
        {
            var sim = new CombatSimulation(seed: 5);
            sim.AddMob(10, 30, 0, m => { m.Hp = m.MaxHp = 500; m.AttackDamage = 10; });
            sim.Player.Hp = sim.Player.MaxHp = 5000;
            sim.LoadScript("""
                function on_tick()
                  local m = bot.nearbyMobs()
                  if #m == 0 then return end
                  if not bot.swing(m[1].handle) then bot.walkTo(m[1].x, m[1].y) end
                end
                """);
            return sim;
        }

        var ran = Build();
        ran.Run(maxTicks: 200);

        var stepped = Build();
        for (var i = 0; i < 200 && !stepped.IsFinished; i++) stepped.Step();

        stepped.Now.ShouldBe(ran.Now);
        stepped.Kills.ShouldBe(ran.Kills);
        stepped.Player.Hp.ShouldBe(ran.Player.Hp);
        stepped.Log.ShouldBe(ran.Log);
    }

    /// <summary>Stopping AT the moment of interest, rather than running on and inspecting the wreckage
    /// afterwards. This is the shape that would have avoided several bad assertions earlier: the world
    /// keeps evolving, so "what did it look like at the end" is rarely the question worth asking.</summary>
    [Fact]
    public void RunUntilStopsAtTheConditionRatherThanTheBudget()
    {
        var sim = Sim();
        var mob = sim.AddMob(10, 15, 0, m => { m.Hp = m.MaxHp = 200; m.RespawnSeconds = 999; });
        sim.Player.AttackDamage = 40;
        sim.Player.AttackRange = 20;
        sim.LoadScript("""
            function on_tick()
              local m = bot.nearbyMobs()
              if #m > 0 then bot.swing(m[1].handle) end
            end
            """);

        var ticks = sim.RunUntil(s => s.Kills > 0, maxTicks: 500);

        ticks.ShouldBeLessThan(500);           // it stopped early, at the event
        sim.Kills.ShouldBe(1);
        mob.Mob.IsAlive.ShouldBeFalse();       // and the world is frozen right there
    }

    /// <summary>Durations stored in milliseconds are independent of the tick rate. Respawn is the
    /// cleanest case: it is a direct `Now >= RespawnAt` comparison with no state machine in the way.
    ///
    /// <para>Agreement is to within one tick, which is the honest bound — a 100 ms tick cannot resolve a
    /// deadline more precisely than 100 ms.</para></summary>
    [Fact]
    public void MillisecondDurationsAreIndependentOfTheTickRate()
    {
        static uint RespawnTime(uint tickMs)
        {
            var sim = new CombatSimulation(seed: 2) { TickMs = tickMs };
            var mob = sim.AddMob(10, 5, 0, m => { m.Hp = m.MaxHp = 10; m.RespawnSeconds = 2; });
            sim.Player.AttackDamage = 100;
            sim.PlayerAttack(mob);                       // dies at Now = 0
            sim.RunUntil(s => mob.Mob.IsAlive, maxTicks: 10_000);
            return sim.Now;
        }

        var coarse = RespawnTime(100);
        var fine = RespawnTime(10);

        coarse.ShouldBeInRange(2000u, 2100u);
        fine.ShouldBeInRange(2000u, 2010u);
        Math.Abs((int)coarse - (int)fine).ShouldBeLessThanOrEqualTo(100);
    }

    /// <summary>A mob's turn speed is its own property, not a function of how finely the caller ticks.
    ///
    /// <para>This is a REGRESSION TEST for a real bug the tick-rate work exposed: turn rate and chase
    /// speed were expressed per TICK, so running at a five-times finer tick made mobs turn and move five
    /// times faster in world time. Rates are now per second and scaled by the elapsed tick.</para>
    ///
    /// <para>State TRANSITIONS still cost a tick each, and that is by design — the server's think loop is
    /// per-tick too, so the tick rate is the simulation's think interval. That is why the tolerance below
    /// is generous rather than exact.</para></summary>
    [Fact]
    public void TurnSpeedIsAPropertyOfTheMobNotOfTheTickRate()
    {
        static uint TimeToFaceTarget(uint tickMs)
        {
            var sim = new CombatSimulation(seed: 2) { TickMs = tickMs };
            // Mob at the origin, player along +x, so the direction TO the target is 0 units. Facing 90 is
            // then a full 180 degrees away. (Getting this backwards makes the mob already face its target
            // and the exit condition never fire, which is how an earlier version of this test spent
            // 10,000 ticks measuring nothing.)
            var mob = sim.AddMob(10, 0, 0, m => m.Hp = m.MaxHp = 100_000);
            sim.Player.X = 50;
            sim.Player.Y = 0;
            mob.Arg.Combat.Facing = 90;
            mob.Arg.Target = sim.Player;
            mob.Arg.Current = MobActionAttack.Actor_Turning;
            sim.Player.Hp = sim.Player.MaxHp = 100_000;
            sim.RunUntil(s => mob.Arg.Combat.Facing == 0, maxTicks: 10_000);
            return sim.Now;
        }

        var coarse = TimeToFaceTarget(100);
        var fine = TimeToFaceTarget(20);

        coarse.ShouldBeGreaterThan(0u);
        // Within a factor of two, not the factor of five a per-tick rate would produce.
        ((double)Math.Max(coarse, fine) / Math.Min(coarse, fine)).ShouldBeLessThan(2.0);
    }
}
