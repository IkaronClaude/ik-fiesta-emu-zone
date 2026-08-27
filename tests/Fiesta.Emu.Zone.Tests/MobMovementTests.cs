using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Mob;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Per-mob movement and turning, from the columns that were shadowed by invented constants.</summary>
public class MobMovementTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobInfoServer.shn")) ? root : null;
    }

    /// <summary>Chase speed is `MobInfo.RunSpeed`, per mob — what `ShineMob::so_RunSpeed` returns. Every mob
    /// used to chase at the same invented 50 units/second.</summary>
    [SkippableFact]
    public void ChaseSpeedComesFromRunSpeed()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        var sim = new CombatSimulation(seed: 1);
        var orc = sim.AddMob(handle: 10, x: 0, y: 0);
        orc.Define(MobCombatant.Build(box, "Orc")!);

        orc.Arg.Combat.RunSpeed.ShouldBe(box.InfoFor("Orc")!.RunSpeed);
        orc.Arg.Combat.RunSpeed.ShouldBe(127);

        // And mobs genuinely differ -- a Mushroom is slower than an Orc.
        var shroom = sim.AddMob(handle: 11, x: 0, y: 0);
        shroom.Define(MobCombatant.Build(box, "MushRoom")!);
        shroom.Arg.Combat.RunSpeed.ShouldBeLessThan(orc.Arg.Combat.RunSpeed);
    }

    /// <summary>A faster mob closes a gap sooner. This is the behaviour the shared constant was hiding.</summary>
    [SkippableFact]
    public void AFasterMobClosesTheGapSooner()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        static int DistanceAfterChasing(MobDataBox box, string name)
        {
            var sim = new CombatSimulation(seed: 1);
            var mob = sim.AddMob(handle: 10, x: 0, y: 0, configure: m => m.RespawnSeconds = 99_999);
            mob.Define(MobCombatant.Build(box, name)!);
            mob.Mob.so_getDetectRange = 4000;
            sim.Player.X = 3000;
            sim.Player.Y = 0;
            sim.Player.Hp = sim.Player.MaxHp = 10_000_000;
            sim.Run(maxTicks: 100);
            return Math.Abs(sim.Player.X - mob.Mob.X);
        }

        // Orc runs at 127, Mushroom at 105, so the Orc has covered more ground.
        DistanceAfterChasing(box, "Orc").ShouldBeLessThan(DistanceAfterChasing(box, "MushRoom"));
    }

    /// <summary>`TurnSpeed == 0` means the mob turns INSTANTLY — `mat_Reserv` returns the caller's next
    /// action rather than the turning state, so the mob faces its target and acts in the same tick.
    ///
    /// <para>60 mobs have it. ⚠️ What a NON-zero value means is deliberately not modelled: the column has
    /// only four values in the whole table and the distribution cannot say whether larger is faster or
    /// slower. Only the branch that was read is acted on.</para></summary>
    [Fact]
    public void AZeroTurnSpeedFacesTheTargetWithoutEnteringTheTurningState()
    {
        var sim = new CombatSimulation(seed: 1);
        var mob = sim.AddMob(handle: 10, x: 0, y: 0, configure: m => m.Hp = m.MaxHp = 100_000);
        sim.Player.X = 50;
        sim.Player.Y = 0;                       // direction to target is 0 units
        sim.Player.Hp = sim.Player.MaxHp = 100_000;

        mob.Arg.Target = sim.Player;
        mob.Arg.Combat.Facing = 90;             // 180 degrees away
        mob.Arg.Combat.AttackRange = 100;       // in range, so only facing is at issue
        mob.Arg.Combat.TurnSpeed = 0;

        var decision = MobActionAttack.Actor_Attack is MobActionAttack a
            ? a.Decide(mob.Arg)
            : throw new InvalidOperationException();

        mob.Arg.Combat.Facing.ShouldBe(0, "an instant turner snaps to face its target");
        decision.NextState.ShouldNotBe(MobActionAttack.Actor_Turning);
    }

    /// <summary>A mob with a non-zero TurnSpeed still reserves a turn, as before.</summary>
    [Fact]
    public void ANonZeroTurnSpeedStillReservesATurn()
    {
        var sim = new CombatSimulation(seed: 1);
        var mob = sim.AddMob(handle: 10, x: 0, y: 0, configure: m => m.Hp = m.MaxHp = 100_000);
        sim.Player.X = 50;
        sim.Player.Hp = sim.Player.MaxHp = 100_000;

        mob.Arg.Target = sim.Player;
        mob.Arg.Combat.Facing = 90;
        mob.Arg.Combat.AttackRange = 100;
        mob.Arg.Combat.TurnSpeed = 100;

        var decision = ((MobActionAttack)MobActionAttack.Actor_Attack).Decide(mob.Arg);

        decision.NextState.ShouldBe(MobActionAttack.Actor_Turning);
        mob.Arg.Combat.Facing.ShouldBe(90, "it has not turned yet");
    }

    /// <summary>`WalkChase` is a DISTANCE, not a speed: inside it a chasing mob WALKS, beyond it RUNS.
    /// The column sits between two speed columns, which is what makes the misreading tempting.</summary>
    [Fact]
    public void WalkChaseIsTheDistanceInsideWhichAMobWalks()
    {
        static int Travelled(int walkChase)
        {
            var sim = new CombatSimulation(seed: 1);
            var mob = sim.AddMob(handle: 10, x: 0, y: 0, configure: m => m.RespawnSeconds = 99_999);
            mob.Mob.so_getDetectRange = 4000;
            mob.Arg.Combat.RunSpeed = 400;
            mob.Arg.Combat.WalkSpeed = 100;
            mob.Arg.Combat.WalkChaseDistance = walkChase;
            mob.Arg.Combat.AttackRange = 5;
            mob.Arg.Target = sim.Player;
            mob.Arg.Current = MobActionAttack.Actor_Chase;

            sim.Player.X = 900;
            sim.Player.Hp = sim.Player.MaxHp = 10_000_000;
            sim.Run(maxTicks: 10);
            return mob.Mob.X;
        }

        // Threshold 0 = always run, so it covers the most ground.
        var alwaysRun = Travelled(0);

        // A threshold LARGER than the whole gap means it walks the entire way.
        var alwaysWalk = Travelled(2000);

        alwaysWalk.ShouldBeLessThan(alwaysRun, "inside the threshold the mob walks");
        alwaysRun.ShouldBeGreaterThan(0);
    }

    /// <summary>The 16 mobs that use it, and what they use it for — a `B_SubHel` sprints in at 400 and
    /// covers the last 400 units at 115.</summary>
    [SkippableFact]
    public void OnlySixteenMobsWalkTheLastStretch()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        box.Server.Values.Count(s => s.WalkChase != 0).ShouldBe(16);

        var hel = box.ServerFor("B_SubHel01")!;
        hel.WalkChase.ShouldBe(400);
        var info = box.InfoFor("B_SubHel01")!;
        info.WalkSpeed.ShouldBeLessThan(info.RunSpeed);
    }

    /// <summary>The columns decode, and TurnSpeed really does have only four values — which is why its
    /// non-zero meaning is left unread rather than guessed.</summary>
    [SkippableFact]
    public void TurnSpeedHasOnlyFourValuesInTheWholeTable()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        var values = box.Server.Values.Select(s => s.TurnSpeed).Distinct().OrderBy(v => v).ToList();
        values.ShouldBe(new[] { 0, 100, 300, 500 });

        box.Server.Values.Count(s => s.TurnsInstantly).ShouldBeInRange(1, 200);

        // WalkChase is almost always zero. It is read by MobActionChase::mab_Think, but as a DISTANCE
        // threshold rather than a speed -- see WalkChaseIsTheDistanceInsideWhichAMobWalks.
        box.Server.Values.Count(s => s.WalkChase == 0).ShouldBeGreaterThan(2800);
    }
}
