using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Two separate mechanisms, and conflating them is what this file exists to prevent:
/// so_mob_ChaseRangeSquar ends the CHASE, and mts_Routine's CutInterval/CutNonAT tests are what free the
/// aggro entry.
///
/// <list type="table">
///   <item><term>so_mob_ChaseRangeSquar 0x005564E0</term><description>(serv-&gt;FollowCha)^2.</description></item>
///   <item><term>MobActionChase::mab_Think +0xC0</term><description>chase ends past that, measured from
///         so_mob_LastHittedLocation.</description></item>
///   <item><term>so_DamagedBy@ShineMob +0x1F2</term><description>that location follows the mob on every
///         hit, unless ResetInterval != 0.</description></item>
///   <item><term>MobTarget_EnemyAnalysis ctor +0xC9, lid_Call +0x61/+0xEB</term><description>an entry is
///         freed at CutInterval units or CutNonAT ms of silence.</description></item>
/// </list></summary>
public class ChaseRangeAndAggroCutTests
{
    private static (CombatSimulation Sim, SimMob Mob) Chasing(
        int followCha = 1500, int cutInterval = 500, int cutNonAtMs = 20_000,
        int resetInterval = 0, int return2Regen = 1)
    {
        var sim = new CombatSimulation(seed: 1);
        sim.Player.MaxHp = sim.Player.Hp = 1_000_000;
        sim.Player.X = 0;
        sim.Player.Y = 0;

        var mob = sim.AddMob(10, 0, 0, m => m.Hp = m.MaxHp = 1_000_000);
        mob.Name = "Orc";
        mob.Arg.Combat.ChaseRangeSquar = (long)followCha * followCha;
        mob.Arg.Combat.Return2Regen = return2Regen;
        mob.Arg.Combat.RunSpeed = 127;
        mob.Mob.ResetInterval = resetInterval;
        mob.Mob.Selector.CutIntervalSquar = (long)cutInterval * cutInterval;
        mob.Mob.Selector.CutNonAtTenths = (int)((uint)cutNonAtMs / 1000u) * 10;
        mob.Mob.so_getDetectRange = 264;
        mob.Arg.Target = sim.Player;
        mob.Arg.Current = MobActionAttack.Actor_Chase;
        return (sim, mob);
    }

    // ---- so_mob_ChaseRangeSquar ----------------------------------------------------------------------

    [Fact]
    public void TheChaseEndsPastFollowChaFromLastHittedLocation()
    {
        var (_, mob) = Chasing(followCha: 1500);
        mob.Mob.LastHittedLocation = (0, 0);

        mob.Mob.X = 1400;
        mob.Arg.Current.mab_Think(mob.Arg).ShouldNotBeOfType<MobAction2Region>();

        mob.Mob.X = 1600;
        mob.Arg.Current.mab_Think(mob.Arg).ShouldBeOfType<MobAction2Region>();
    }

    /// <summary>`jbe` keeps chasing, so exactly at the limit it still chases - the same boundary as the
    /// detect circle, and the opposite of what a &gt;= would give.</summary>
    [Fact]
    public void ExactlyAtTheLimitItStillChases()
    {
        var (_, mob) = Chasing(followCha: 1500);
        mob.Mob.LastHittedLocation = (0, 0);
        mob.Mob.X = 1500;
        mob.Arg.Current.mab_Think(mob.Arg).ShouldNotBeOfType<MobAction2Region>();
    }

    /// <summary>The anchor is so_mob_LastHittedLocation, not RegenLocation, so a fight that walks across
    /// the map takes the chase limit with it.</summary>
    [Fact]
    public void DamageMovesLastHittedLocationToTheMob()
    {
        var (sim, mob) = Chasing();
        mob.Mob.so_mob_RegenComplete(0, 0);
        mob.Mob.X = 900;

        mob.Mob.so_DamagedBy(sim.Player, damage: 100, aggroRatePermille: 1000, nowTenths: 5);

        mob.Mob.LastHittedLocation.ShouldBe((900, 0));
        mob.Mob.RegenLocation.ShouldBe((0, 0));
    }

    /// <summary>The 141 mobs with ResetInterval == 1 keep the location they were given. The name reads
    /// like a duration and the branch is a boolean, inverted.</summary>
    [Fact]
    public void ResetIntervalOneKeepsLastHittedLocationWhereItWas()
    {
        var (sim, mob) = Chasing(resetInterval: 1);
        mob.Mob.so_mob_RegenComplete(0, 0);
        mob.Mob.X = 900;

        mob.Mob.so_DamagedBy(sim.Player, damage: 100, aggroRatePermille: 1000, nowTenths: 5);

        mob.Mob.LastHittedLocation.ShouldBe((0, 0));
    }

    /// <summary>A mob with no data row has no limit and chases for ever - the simulation's behaviour
    /// before this was ported, now an explicit default rather than a silent one.</summary>
    [Fact]
    public void ZeroChaseRangeMeansNoLimit()
    {
        var (_, mob) = Chasing();
        mob.Arg.Combat.ChaseRangeSquar = 0;
        mob.Mob.LastHittedLocation = (0, 0);
        mob.Mob.X = 100_000;
        mob.Arg.Current.mab_Think(mob.Arg).ShouldNotBeOfType<MobAction2Region>();
    }

    // ---- MobAction2Region / DuringReturn2Regen -------------------------------------------------------

    [Fact]
    public void AReturningMobWalksHomeAndThenResumesTargetting()
    {
        var (_, mob) = Chasing(return2Regen: 1);
        mob.Mob.so_mob_RegenComplete(0, 0);
        mob.Mob.X = 2000;

        var next = MobActionBase.Actor_ToRegion.mab_Think(mob.Arg);
        next.ShouldBeOfType<DuringReturn2Regen>();
        mob.Mob.LastHittedLocation.ShouldBe((0, 0), "re-anchored before the walk starts");
        mob.Arg.so_mobile_IsInMoving().ShouldBeTrue();

        next.mab_Think(mob.Arg).ShouldBeOfType<DuringReturn2Regen>();

        mob.Arg.AdvanceRunTo(5000);
        mob.Mob.X.ShouldBe(0);
        next.mab_Think(mob.Arg).ShouldBeOfType<MobActionTargetting>();
    }

    /// <summary>Return2Regen == 0 (250 mobs): it stops where it gave up and re-anchors there, so its next
    /// chase gets a fresh FollowCha measured from the new spot.</summary>
    [Fact]
    public void ANonReturningMobReAnchorsWhereItStands()
    {
        var (_, mob) = Chasing(return2Regen: 0);
        mob.Mob.so_mob_RegenComplete(0, 0);
        mob.Mob.X = 2000;

        MobActionBase.Actor_ToRegion.mab_Think(mob.Arg).ShouldBeOfType<MobActionTargetting>();
        mob.Mob.LastHittedLocation.ShouldBe((2000, 0));
        mob.Mob.RegenLocation.ShouldBe((2000, 0));
        mob.Arg.so_mobile_IsInMoving().ShouldBeFalse();
    }

    /// <summary>MobAction2Region calls so_Mob_SetSendTagetInfo(0) and nothing else toward the target, so
    /// the aggro list survives and Targetting picks the same entry back up. This is why mts_Routine, not
    /// the chase range, is what a kite has to beat.</summary>
    [Fact]
    public void EndingTheChaseDoesNotClearTheAggroList()
    {
        var (sim, mob) = Chasing();
        mob.Mob.so_DamagedBy(sim.Player, 100, 1000, nowTenths: 0);
        mob.Mob.X = 5000;

        MobActionBase.Actor_ToRegion.mab_Think(mob.Arg);

        mob.Mob.Selector.mts_GetTopAggroTarget().ShouldBe(sim.Player);
    }

    // ---- mts_Routine ---------------------------------------------------------------------------------

    /// <summary>CutInterval is a distance despite the name, and it is 500 on every ordinary mob in the
    /// file - Slime, MushRoom, Pinky, Orc and D_MarloneMegaton alike.</summary>
    [Fact]
    public void PastCutIntervalTheAggroEntryIsFreed()
    {
        var (sim, mob) = Chasing(cutInterval: 500);
        mob.Mob.so_DamagedBy(sim.Player, 100, 1000, nowTenths: 0);

        sim.Player.X = 400;
        mob.Mob.Selector.mts_Routine(mob.Mob, nowTenths: 1).ShouldBe(0);
        mob.Mob.Selector.mts_GetTopAggroTarget().ShouldBe(sim.Player);

        sim.Player.X = 501;
        mob.Mob.Selector.mts_Routine(mob.Mob, nowTenths: 1).ShouldBe(1);
        mob.Mob.Selector.mts_GetTopAggroTarget().ShouldBeNull();
    }

    /// <summary>`ja` frees, so exactly at the distance the entry is kept.</summary>
    [Fact]
    public void ExactlyAtCutIntervalTheEntryIsKept()
    {
        var (sim, mob) = Chasing(cutInterval: 500);
        mob.Mob.so_DamagedBy(sim.Player, 100, 1000, nowTenths: 0);
        sim.Player.X = 500;
        mob.Mob.Selector.mts_Routine(mob.Mob, nowTenths: 1).ShouldBe(0);
    }

    [Fact]
    public void PastCutNonAtTheAggroEntryIsFreedEvenAtPointBlank()
    {
        var (sim, mob) = Chasing(cutNonAtMs: 20_000);
        mob.Mob.so_DamagedBy(sim.Player, 100, 1000, nowTenths: 0);

        mob.Mob.Selector.mts_Routine(mob.Mob, nowTenths: 200).ShouldBe(0, "200 tenths is exactly 20s");
        mob.Mob.Selector.mts_Routine(mob.Mob, nowTenths: 201).ShouldBe(1);
    }

    /// <summary>The timeout truncates to whole seconds before scaling to tenths - the ctor's unsigned
    /// divide by 1000 then times 10. 20500 ms is 200 tenths, not 205.</summary>
    [Fact]
    public void TheTimeoutTruncatesToWholeSeconds()
        => new MobInfoServer(0, "x", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                             EnemyDetect.Bout, 0, 0, 0, CutNonAt: 20_500)
            .CutNonAtTenths.ShouldBe(200);

    /// <summary>Every hit restamps mts_LastHit, so a bot that keeps attacking can never time a mob out -
    /// only distance frees an entry you are still feeding.</summary>
    [Fact]
    public void EveryHitRestampsTheTimeout()
    {
        var (sim, mob) = Chasing();
        mob.Mob.so_DamagedBy(sim.Player, 100, 1000, nowTenths: 0);
        mob.Mob.so_DamagedBy(sim.Player, 100, 1000, nowTenths: 150);
        mob.Mob.Selector.mts_Routine(mob.Mob, nowTenths: 201).ShouldBe(0);
        mob.Mob.Selector.mts_Routine(mob.Mob, nowTenths: 351).ShouldBe(1);
    }

    /// <summary>0 in either column switches that test off, which is what a mob with no data row gets.
    /// A squared distance of 0 would otherwise free every entry at once.</summary>
    [Fact]
    public void ZeroDisablesEachTestRatherThanFiringImmediately()
    {
        var (sim, mob) = Chasing(cutInterval: 0, cutNonAtMs: 0);
        mob.Mob.so_DamagedBy(sim.Player, 100, 1000, nowTenths: 0);
        sim.Player.X = 100_000;
        mob.Mob.Selector.mts_Routine(mob.Mob, nowTenths: 100_000).ShouldBe(0);
    }
}
