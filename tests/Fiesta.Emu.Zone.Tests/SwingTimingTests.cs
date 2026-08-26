using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Delayed damage and cooldowns, per docs/COMBAT.md.
///
/// The queue behaviour is ported from `nadt_Routine` (0x00577520). The cooldown class is an
/// expiry-time simplification and its tests pin that model, not the server's fuller one — see the class
/// documentation.</summary>
public class SwingTimingTests
{
    private static Obj Target(ushort h = 2) => new() { Handle = h };

    [Fact]
    public void AQueuedHitDoesNotLandBeforeItsTime()
    {
        var q = new NormalAttackDamageTick();
        q.nadt_PushBack(50, dueTime: 100, Target());

        q.nadt_Routine(99).ShouldBeEmpty();
        q.PendingCount.ShouldBe(1);
    }

    /// <summary>Due exactly now DOES land — the original stops only when `element.time &gt; now`. At tick
    /// granularity an off-by-one here is a whole tick of damage.</summary>
    [Fact]
    public void AHitDueExactlyNowLands()
    {
        var q = new NormalAttackDamageTick();
        q.nadt_PushBack(50, dueTime: 100, Target());

        q.nadt_Routine(100).Count.ShouldBe(1);
        q.nadt_IsEmpty().ShouldBeTrue();
    }

    [Fact]
    public void EverythingDueLandsInOneTick_InOrder()
    {
        var q = new NormalAttackDamageTick();
        q.nadt_PushBack(1, 10, Target());
        q.nadt_PushBack(2, 20, Target());
        q.nadt_PushBack(3, 30, Target());

        var landed = q.nadt_Routine(25);

        landed.Select(e => e.Damage).ShouldBe(new[] { 1, 2 });
        q.PendingCount.ShouldBe(1);
    }

    /// <summary>The walk stops at the first element that is not due, so the queue must be time-ordered —
    /// the original never sorts, and neither does this.</summary>
    [Fact]
    public void TheWalkStopsAtTheFirstHitThatIsNotDue()
    {
        var q = new NormalAttackDamageTick();
        q.nadt_PushBack(1, 100, Target());     // pushed out of order on purpose
        q.nadt_PushBack(2, 10, Target());

        q.nadt_Routine(50).ShouldBeEmpty();    // the early hit is stuck behind the late one
        q.PendingCount.ShouldBe(2);
    }

    [Fact]
    public void NothingQueuedMeansNothingLands()
        => new NormalAttackDamageTick().nadt_Routine(uint.MaxValue).ShouldBeEmpty();

    [Fact]
    public void ClearDropsPendingHits()
    {
        var q = new NormalAttackDamageTick();
        q.nadt_PushBack(1, 10, Target());
        q.nadt_Clear();
        q.nadt_IsEmpty().ShouldBeTrue();
        q.nadt_Routine(uint.MaxValue).ShouldBeEmpty();
    }

    [Fact]
    public void TargetCompareFindsAPendingHitForAHandle()
    {
        var q = new NormalAttackDamageTick();
        q.nadt_PushBack(1, 10, Target(7));

        q.nadt_TargetCompare(7).ShouldBeTrue();
        q.nadt_TargetCompare(8).ShouldBeFalse();
    }

    /// <summary>The behaviour that makes this worth porting: a hit thrown before the target moved away
    /// still lands. Applying damage at swing time would lose it.</summary>
    [Fact]
    public void AHitInFlightStillLandsAfterTheTargetHasMovedAway()
    {
        var q = new NormalAttackDamageTick();
        var victim = new Obj { Handle = 2, X = 0, Y = 0 };
        q.nadt_PushBack(75, dueTime: 100, victim);

        victim.X = 10_000;                     // ran away after the swing

        var landed = q.nadt_Routine(100);
        landed.Count.ShouldBe(1);
        landed[0].Damage.ShouldBe(75);
    }

    // ---- cooldowns ----------------------------------------------------------------------------------

    [Fact]
    public void ASkillNeverUsedIsReady()
        => new CharaterSkillList().csl_CoolTimeCheck(42, now: 0).ShouldBeTrue();

    [Fact]
    public void ASkillOnCooldownIsNotReadyUntilItsTime()
    {
        var skills = new CharaterSkillList();
        skills.csl_SetCoolTime(42, readyAt: 500);

        skills.csl_CoolTimeCheck(42, 499).ShouldBeFalse();
        skills.csl_CoolTimeCheck(42, 500).ShouldBeTrue();   // ready exactly on the boundary
        skills.csl_CoolTimeCheck(42, 501).ShouldBeTrue();
    }

    [Fact]
    public void CooldownsAreTrackedPerSkill()
    {
        var skills = new CharaterSkillList();
        skills.csl_SetCoolTime(1, 500);

        skills.csl_CoolTimeCheck(1, 100).ShouldBeFalse();
        skills.csl_CoolTimeCheck(2, 100).ShouldBeTrue();
    }

    [Fact]
    public void IgnoreCoolTimeMakesEverythingReady()
    {
        var skills = new CharaterSkillList { IgnoreCoolTime = true };
        skills.csl_SetCoolTime(1, uint.MaxValue);
        skills.csl_CoolTimeCheck(1, 0).ShouldBeTrue();
    }

    [Fact]
    public void ClearingCooldownsMakesEverythingReady()
    {
        var skills = new CharaterSkillList();
        skills.csl_SetCoolTime(1, 500);
        skills.csl_DmgCoolTimeDown();
        skills.csl_CoolTimeCheck(1, 0).ShouldBeTrue();
        skills.ReadyAt(1).ShouldBeNull();
    }
}
