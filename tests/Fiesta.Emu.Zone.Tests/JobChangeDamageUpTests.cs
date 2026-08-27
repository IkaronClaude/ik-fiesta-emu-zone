using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`ShinePlayer::so_ply_JobChangeDamageUp` — the job-change catch-up multiplier every hit a
/// PLAYER lands on a MONSTER goes through, read out of the attacker's own class table.
///
/// <para>The arithmetic here is checked against the real function under emulation by
/// <c>tools/oracle_jobchange_dmgup.py</c>, which runs it on fourteen input sets and gets the same answer
/// every time. These tests hold the shape in place: that it multiplies, that it is applied where the
/// binary applies it, and that "does not apply" is a null rather than a rate of 1000.</para></summary>
public class JobChangeDamageUpTests
{
    /// <summary>A combatant that hits for a known, fixed amount, so the multiplier is the only thing moving.
    ///
    /// <para>Weapon damage comes out of the Base cluster with every modifier layer empty, and both bounds
    /// are equal so the roll cannot vary the result.</para></summary>
    private static ICombatant Attacker(int weapon) => Combatant.FromBaseStats(50, new Dictionary<Stat, int>
    {
        [Stat.Str] = 1, [Stat.WCmin] = weapon, [Stat.WCmax] = weapon,
    });

    private static ICombatant Defender(int armour) => Combatant.FromBaseStats(50, new Dictionary<Stat, int>
    {
        [Stat.Con] = 1, [Stat.AC] = armour,
    });

    private static int Damage(int? rate, int seed = 1)
        => DamageCalculator.ResolveDamage(
            Attacker(20_000), Defender(100),
            new AttackModifiers { RollPermille = 0, ForceCritical = false, JobChangeDamageUpPermille = rate },
            new System.Random(seed));

    /// <summary>A rate of 1000 is "no change" — a base class. The multiplier still RUNS, it just does
    /// nothing beyond the 0-or-1 random.</summary>
    [Fact]
    public void ARateOfOneThousandLeavesTheDamageAlone()
    {
        var plain = Damage(null);
        Damage(1000).ShouldBeInRange(plain, plain + 1);
    }

    /// <summary>Level 20 of a first-job class is on 2000 — literally double damage, and this port applied
    /// none of it until the hook was read.</summary>
    [Fact]
    public void TwoThousandDoublesTheDamage()
    {
        var plain = Damage(null);
        Damage(2000).ShouldBeInRange(plain * 2, plain * 2 + 2);
    }

    /// <summary>Zero is a real rate, not "unset": it zeroes the damage, which the final floor then lifts
    /// to 1. That is why the modifier is nullable — <c>null</c> means the hook never runs.</summary>
    [Fact]
    public void ZeroIsARateAndNotASentinel()
    {
        Damage(0).ShouldBe(1);
        Damage(null).ShouldBeGreaterThan(1);
    }

    /// <summary>The 64-bit multiply is NOT observable through <see cref="DamageCalculator.Resolve"/>, and
    /// that is a fact about the server rather than a gap here: the level gap immediately after it is a
    /// WRAPPING int32 multiply by its own rate, so any damage above about 2.1M collapses to 1 there
    /// whatever this step did. The place the wide multiply shows is the function itself, and
    /// <c>tools/oracle_jobchange_dmgup.py</c> runs it at 3,000,000 damage — 3,840,000 out of the real
    /// binary, and out of this port.
    ///
    /// <para>Recorded as a test so the next reader does not spend the afternoon writing the end-to-end
    /// case that cannot exist.</para></summary>
    [Fact]
    public void DamageAboveTheLevelGapWrapCollapsesToOne()
    {
        var attacker = Combatant.FromBaseStats(50, new Dictionary<Stat, int>
        {
            [Stat.Str] = 1, [Stat.WCmin] = 100_000, [Stat.WCmax] = 100_000,
        });
        var mods = new AttackModifiers { RollPermille = 0, ForceCritical = false };

        // 51 * 100,001 / 2 is 2.55M; the level gap's `1000 * damage` then wraps negative and floors to 1.
        DamageCalculator.ResolveDamage(attacker, Defender(1), mods, new System.Random(1)).ShouldBe(1);
    }

    /// <summary>It runs BEFORE the level gap, which is the order `roe_CalcDamage` uses (+0x5B2 then
    /// +0x5C1) and is observable: both truncate, so swapping them moves the answer.</summary>
    [Fact]
    public void ItIsAppliedBeforeTheLevelGap()
    {
        var mods = new AttackModifiers
        {
            RollPermille = 0, ForceCritical = false,
            JobChangeDamageUpPermille = 1700, LevelGapRatePermille = 1500,
        };
        var attacker = Attacker(1_000);
        var defender = Defender(997);

        var got = DamageCalculator.ResolveDamage(attacker, defender, mods, new System.Random(1));

        var plain = DamageCalculator.ResolveDamage(attacker, defender, mods with
        {
            JobChangeDamageUpPermille = null, LevelGapRatePermille = 1000,
        }, new System.Random(1));

        // job change first, then the gap -- each truncating on the way.
        var expected = plain * 1700 / 1000 * 1500 / 1000;
        got.ShouldBeInRange(expected, expected + 2);
    }
}
