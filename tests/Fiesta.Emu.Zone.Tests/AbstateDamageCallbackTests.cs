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

    // ---- the one-subclass actor slots -------------------------------------------------------------

    /// <summary>⭐ TWO SHIELDS, TWO BEHAVIOURS against one big hit. The HP-pool shield breaks and lets the
    /// remainder through; the counted absorb (<see cref="TheRangeInterceptAbsorbsTheWholeHitAndSpendsOneCharge"/>)
    /// eats the whole thing however large. Confusing them would be invisible on small hits.</summary>
    [Theory]
    [InlineData(500, 800, 0, 300, false)]    // pool survives, hit fully absorbed
    [InlineData(800, 500, 300, 0, true)]     // pool breaks, 300 passes THROUGH
    [InlineData(500, 500, 0, 0, true)]       // exactly exhausted: pool <= damage, so it ends
    public void TheHpPoolShieldLetsTheOverflowThrough(int damage, int pool, int dealt, int left, bool ends)
        => AbstateDamageCallbacks.ShieldHpPool(damage, pool).ShouldBe((dealt, left, ends));

    /// <summary>⭐ Damage-down is a REDUCTION, not a scale: 300 removes 30% and leaves 70%. The inverted
    /// reading is plausible on every value and wrong on all of them.</summary>
    [Theory]
    [InlineData(1000, 300, 700)]
    [InlineData(1000, 100, 900)]
    [InlineData(457, 333, 305)]
    [InlineData(1000, 1000, 0)]     // >= 1000 fully negates, and the test is >=, not >
    [InlineData(1000, 1500, 0)]
    public void DamageDownRemovesItsShareRatherThanScalingTo(int damage, int rate, int expected)
        => AbstateDamageCallbacks.DamageDown(damage, rate).ShouldBe(expected);

    /// <summary>⚠️ These slots call `assa_IsHaveEffect` FIRST, so a row without the action leaves the
    /// damage ALONE — the opposite of `sasa_Act_LastDamegeInterceptByAtk`, which multiplies by the
    /// missing-value 0 and zeroes it. Both conventions live in the same family of callbacks.</summary>
    [Fact]
    public void AnAbsentDamageDownLeavesTheDamageAloneUnlikeTheAttackerRate()
    {
        AbstateDamageCallbacks.DamageDown(1000, null).ShouldBe(1000);
        AbstateDamageCallbacks.LastDamageRateByAttacker(1000, 0).ShouldBe(0);
    }

    /// <summary>The HP floor is RAISED, never lowered, so the highest floor among an object's states wins
    /// and the order the pass visits them in does not matter.</summary>
    [Fact]
    public void TheHpFloorIsRaisedAndOrderIndependent()
    {
        AbstateDamageCallbacks.MinHp(0, 1).ShouldBe(1u);
        AbstateDamageCallbacks.MinHp(500, 100).ShouldBe(500u, "a lower floor must not pull it down");
        AbstateDamageCallbacks.MinHp(100, null).ShouldBe(100u);

        uint ascending = 0, descending = 0;
        foreach (var v in new uint[] { 1, 250, 90 }) ascending = AbstateDamageCallbacks.MinHp(ascending, v);
        foreach (var v in new uint[] { 90, 250, 1 }) descending = AbstateDamageCallbacks.MinHp(descending, v);
        ascending.ShouldBe(descending);
        ascending.ShouldBe(250u);
    }

    /// <summary>SP-cost reduction is the same shape as damage-down and is what the shipped `MagicDance` /
    /// `PointAttack` passives resolve to — the passive path and this actor are two ends of one mechanic.
    ///
    /// <para>⚠️ Unlike damage-down there is NO `&gt;= 1000` clamp, so a rate above 1000 makes the cost
    /// negative rather than zero. Nothing shipped does that; the difference is the binary's.</para></summary>
    [Theory]
    [InlineData(100u, 300, 70u)]
    [InlineData(100u, 1000, 0u)]
    [InlineData(57u, 333, 39u)]
    public void UseSpDownRemovesItsShareOfTheCost(uint cost, int rate, uint expected)
        => AbstateDamageCallbacks.UseSpDown(cost, rate).ShouldBe(expected);

    /// <summary>Absent leaves the cost alone — this slot checks `assa_IsHaveEffect` first.</summary>
    [Fact]
    public void AnAbsentUseSpDownLeavesTheCostAlone()
        => AbstateDamageCallbacks.UseSpDown(100, null).ShouldBe(100u);

    /// <summary>⚠️ SelfRevive uses `assa_FindEffect` with NO `IsHaveEffect` guard, so a row without
    /// `SAA_REVIVEHEALRATE` still flags the rebirth and revives at rate 0. The guarded and unguarded
    /// conventions genuinely sit side by side in this family, which is why each was read rather than
    /// assumed from its neighbours.</summary>
    [Fact]
    public void SelfReviveAlwaysFlagsRebirthEvenAtRateZero()
    {
        AbstateDamageCallbacks.SelfRevive(500).ShouldBe((true, 500));
        AbstateDamageCallbacks.SelfRevive(0).ShouldBe((true, 0));
    }
}
