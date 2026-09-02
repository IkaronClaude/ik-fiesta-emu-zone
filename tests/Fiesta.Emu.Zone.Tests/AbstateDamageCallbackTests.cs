using Fiesta.Emu.Zone.Abstate;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The two abstate damage callbacks in `roe_CalcDamage`'s tail that actually do something.</summary>
public class AbstateDamageCallbackTests
{
    /// <summary>⭐ A COUNTED absorb, not a percentage: the hit is zeroed whatever its size, and one charge
    /// is spent. A 10,000-damage hit and a 1-damage hit cost the same single charge.</summary>
    [Theory]
    [InlineData(10000, 3)]
    [InlineData(1, 3)]
    [InlineData(457, 1)]
    public void TheRangeInterceptAbsorbsTheWholeHitAndSpendsOneCharge(int damage, int charges)
    {
        var (dealt, left, ends) = AbstateDamageCallbacks.RangeIntercept(damage, charges);
        dealt.ShouldBe(0);
        left.ShouldBe(charges - 1);
        ends.ShouldBeFalse();
    }

    /// <summary>⭐ The state expires on the first hit AFTER the last charge, not the moment it is spent —
    /// so a one-charge shield stops one hit and then costs a second hit to clear. That second hit takes
    /// full damage.</summary>
    [Fact]
    public void TheStateEndsOnTheHitAfterTheLastCharge()
    {
        var (afterFirst, left, ends) = AbstateDamageCallbacks.RangeIntercept(500, charges: 1);
        afterFirst.ShouldBe(0);
        left.ShouldBe(0);
        ends.ShouldBeFalse("the state survives the hit that spends its last charge");

        var (afterSecond, still, nowEnds) = AbstateDamageCallbacks.RangeIntercept(500, charges: 0);
        afterSecond.ShouldBe(500, "with no charges left the hit lands in full");
        still.ShouldBe(0);
        nowEnds.ShouldBeTrue();
    }

    /// <summary>The pass is gated on the ATTACK's range, strictly over 300 — the same test
    /// `roe_FreeStatHitRate` uses. A melee attacker walks straight past a shield that would have stopped
    /// an arrow, which is what "RangeIntercept" means.</summary>
    [Theory]
    [InlineData(450, true)]    // an Archer
    [InlineData(301, true)]
    [InlineData(300, false)]   // strictly greater, so 300 itself does not
    [InlineData(100, false)]   // every other class
    public void OnlyRangedAttacksReachTheIntercept(int attackRange, bool applies)
        => AbstateDamageCallbacks.RangeInterceptApplies(attackRange).ShouldBe(applies);

    /// <summary>`sasa_Act_LastDamegeInterceptByAtk` scales the damage by the attacker's
    /// `SAA_TOTALDAMAGERATE`, truncating.</summary>
    [Theory]
    [InlineData(1000, 500, 500)]
    [InlineData(1000, 1000, 1000)]
    [InlineData(1000, 2000, 2000)]   // nothing caps it at 1
    [InlineData(457, 333, 152)]      // 152.18 -> 152, truncated
    public void TheAttackerRateScalesEverythingItDeals(int damage, int rate, int expected)
        => AbstateDamageCallbacks.LastDamageRateByAttacker(damage, rate).ShouldBe(expected);

    /// <summary>⚠️ `assa_FindEffect` returns 0 for an action the row does not carry, and this callback
    /// multiplies by it — so an element reaching here without `SAA_TOTALDAMAGERATE` ZEROES the damage
    /// rather than leaving it alone.
    ///
    /// <para>Pinned deliberately. It is the binary's behaviour, survivable only because the two actors
    /// that share this body are bound to rows carrying the action; substituting a neutral 1000 would hide
    /// a mis-built row instead of failing on it.</para></summary>
    [Fact]
    public void AMissingRateZeroesTheDamageRatherThanLeavingItAlone()
    {
        AbstateDamageCallbacks.FindEffect([], SubAbstateAction.SAA_TOTALDAMAGERATE).ShouldBe(0);
        AbstateDamageCallbacks.LastDamageRateByAttacker(1000, 0).ShouldBe(0);
    }

    /// <summary>Four slots, scanned linearly, first match wins — and "absent" is indistinguishable from
    /// "present, valued zero", in the original as here.</summary>
    [Fact]
    public void FindEffectScansFourSlotsAndFallsBackToZero()
    {
        (SubAbstateAction, int)[] slots =
        [
            (SubAbstateAction.SAA_MARATE, 700),
            (SubAbstateAction.SAA_TOTALDAMAGERATE, 250),
            (SubAbstateAction.SAA_DOTRATE, 900),
            (SubAbstateAction.SAA_HEALRATE, 100),
        ];

        AbstateDamageCallbacks.FindEffect(slots, SubAbstateAction.SAA_TOTALDAMAGERATE).ShouldBe(250);
        AbstateDamageCallbacks.FindEffect(slots, SubAbstateAction.SAA_AWAY).ShouldBe(0);

        // A fifth entry is past the fixed four and cannot be found, matching the original's bound.
        var overlong = slots.Append((SubAbstateAction.SAA_AWAY, 42)).ToArray();
        AbstateDamageCallbacks.FindEffect(overlong, SubAbstateAction.SAA_AWAY).ShouldBe(0);
    }
}
