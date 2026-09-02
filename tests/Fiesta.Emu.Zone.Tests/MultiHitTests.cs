using Fiesta.Emu.Zone.Skill;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The multi-hit path — a skill that lands as a SEQUENCE of strikes rather than one hit.
///
/// <para>Every figure below came off the real instruction block at `smo_SkillBlast+0x93B`, driven by
/// `tools/oracle_multihit.py`: 12 of 12 agree.</para></summary>
public class MultiHitTests
{
    /// <summary>⭐ <b>`roe_CalcDamage` never scales damage by the multi-hit rate.</b> It reads
    /// `pMultiHitArg` exactly once, to gate the critical stun. The scaling is the CALLER's, applied to the
    /// finished integer the rules returned — which is why it truncates like integer arithmetic and not
    /// like the double-precision chain inside the engine.</summary>
    [Theory]
    [InlineData(1000, 1000, 1000)]   // a full-rate strike is the damage unchanged
    [InlineData(1000, 500, 500)]     // half rate
    [InlineData(1000, 2500, 2500)]   // a single strike can be worth MORE than the whole hit
    [InlineData(7, 333, 2)]          // 2.33 truncates to 2 -- truncation, not rounding
    [InlineData(100000, 1000, 100000)]
    public void AStrikeScalesTheFinishedDamage(int damage, int rate, int expected)
        => MultiHit.HitDamage(damage, rate).ShouldBe(expected);

    /// <summary>⭐ The floor to 1 is conditional on THREE things, and the distinctions are the point:
    /// a strike that scales down to nothing still lands for 1, but only if there was damage to begin with
    /// AND the strike carries a positive rate.
    ///
    /// <para>So a low-rate tick can never be rounded away, while a ZERO-rate tick stays at zero — the same
    /// distinction the critical-stun gate makes, from the same field.</para></summary>
    [Theory]
    [InlineData(1000, 1, 1, "rate 1 rounds to 0 and is floored")]
    [InlineData(1, 1, 1, "both tiny, still lands")]
    [InlineData(1000, 0, 0, "a ZERO-rate strike deals nothing -- the floor must not rescue it")]
    [InlineData(0, 1000, 0, "no damage to begin with -- the floor must not invent any")]
    public void TheFloorRescuesARoundedDownStrikeButNeverAZeroRateOne(int damage, int rate, int expected,
                                                                     string why)
        => MultiHit.HitDamage(damage, rate).ShouldBe(expected, why);

    /// <summary>The set-item damage bonus (`se_Argument[2]`, 0x1325EDB8) rides in front of the strike rate, with its own
    /// divide.
    ///
    /// <para>It is a GLOBAL that is NOT server configuration: `setitemskilleffect` stages the CURRENT
    /// object's matched-set bonuses, rebuilt per caster. Read live from a zone at rest, all seventeen
    /// slots hold 1000, so the neutral default is measured rather than assumed.</para></summary>
    [Theory]
    [InlineData(1000, 1000, 2000, 2000)]
    [InlineData(1000, 1000, 0, 0)]      // zeroes everything, floor included
    [InlineData(999, 999, 999, 997)]    // three truncations compounding
    public void TheSetItemBonusAppliesFirstAndSeparately(int damage, int rate, int server, int expected)
        => MultiHit.HitDamage(damage, rate, server).ShouldBe(expected);

    /// <summary>`roe_CalcDamage+0x1AE`: an ordinary swing always attempts the critical stun; a multi-hit
    /// strike only does so when it carries a positive damage rate.
    ///
    /// <para>A filler tick can therefore be flagged critical and never stun. `iscritical` is set BEFORE
    /// the branch, so only the stun is gated — critical DAMAGE is unaffected.</para></summary>
    [Fact]
    public void OnlyAStrikeThatCarriesDamageCanStun()
    {
        MultiHit.AppliesCriticalStun(null).ShouldBeTrue("an ordinary swing always attempts it");
        MultiHit.AppliesCriticalStun(new MultiHitArgument(HitStep: 1, DamageRate: 500)).ShouldBeTrue();
        MultiHit.AppliesCriticalStun(new MultiHitArgument(HitStep: 0, DamageRate: 0)).ShouldBeFalse();
        MultiHit.AppliesCriticalStun(new MultiHitArgument(HitStep: 0, DamageRate: -1)).ShouldBeFalse();
    }

    /// <summary>A sequence is bounded by `mhe_ArrayCnt`, not by the 160-entry array it lives in — the tail
    /// holds stale rows that read as data. Modelling the count as the list length is what keeps that
    /// mistake unavailable.</summary>
    [Fact]
    public void ASequenceIsBoundedByItsCountNotItsCapacity()
    {
        var element = new MultiHitElement(Id: 3, Hits:
        [
            new MultiHitOneHit(HitTimeRate: 0, DamageRate: 300),
            new MultiHitOneHit(HitTimeRate: 500, DamageRate: 300, AbState: 307, AbStateRate: 200),
            new MultiHitOneHit(HitTimeRate: 1000, DamageRate: 400),
        ]);

        element.Count.ShouldBe(3);
        MultiHitElement.Capacity.ShouldBe(160);

        // The three strikes of this sequence against a 1000-damage hit, in order.
        element.Hits.Select(h => MultiHit.HitDamage(1000, h.DamageRate))
            .ShouldBe([300, 300, 400]);
    }

    /// <summary>`mha_AbState` is 48 bytes of 12-byte triples — four slots exactly, not a growable list.</summary>
    [Fact]
    public void AStrikeCarriesAtMostFourAbstates()
        => MultiHitArgument.AbStateSlots.ShouldBe(4);
}
