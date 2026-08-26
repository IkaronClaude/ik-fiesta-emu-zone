using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`ShineMob::so_DamagedBy` and the damage-to-aggro rule, per docs/AGGRO.md.
///
/// Still a reading of the binary rather than oracle-measured values — see the note on the other test
/// classes.</summary>
public class ShineMobTests
{
    private static ShineMob Mob(int detect = 100) =>
        new() { Handle = 1, X = 0, Y = 0, so_getDetectRange = detect };

    /// <summary>`damage * ratePermille / 1000`, integer, truncating toward zero.</summary>
    [Theory]
    [InlineData(100, 1000, 100)]      // neutral rate passes the damage straight through
    [InlineData(100, 500, 50)]        // half hate
    [InlineData(100, 2000, 200)]      // double hate
    [InlineData(100, 0, 0)]           // a rate of zero generates NO hate -- 0 is a real value here
    [InlineData(1, 1000, 1)]
    [InlineData(1, 999, 0)]           // truncates toward zero rather than rounding
    [InlineData(7, 143, 1)]           // 1.001 -> 1
    [InlineData(-100, 1000, -100)]    // negative damage yields negative hate, not zero
    public void AggroFromDamage_IsDamageTimesRateOverAThousand(int damage, int rate, int expected)
        => ShineMob.AggroFromDamage(damage, rate).ShouldBe(expected);

    /// <summary>The multiply is 32-bit in the original and WRAPS. Promoting it to 64-bit would be
    /// arithmetically nicer and less faithful; at `damage * rate` past int32 the server wraps and so does
    /// this.</summary>
    ///
    /// <para>Expected values are literals, not the same expression re-evaluated — a test that computes its
    /// expectation the way the code does passes no matter what either of them says. The first case wraps
    /// to a positive number and the second to a negative one, so both sides of the boundary are covered.</para>
    [Theory]
    [InlineData(100_000, 100_000, 1_410_065)]    // 1e10 -> wraps to 1410065408, /1000. 64-bit would give 10,000,000
    [InlineData(50_000, 50_000, -1_794_967)]     // 2.5e9 -> wraps NEGATIVE, and the divide truncates toward zero
    public void AggroFromDamage_WrapsLikeThe32BitMultiply(int damage, int rate, int expected)
        => ShineMob.AggroFromDamage(damage, rate).ShouldBe(expected);

    [Fact]
    public void DamageAppendsAggroAndRecordsContributionSeparately()
    {
        var mob = Mob();
        var attacker = new Obj { Handle = 7, X = 10, Y = 0 };

        mob.so_DamagedBy(attacker, damage: 250, aggroRatePermille: 1000);

        mob.Selector.mts_GetTopAggroTarget().ShouldBe(attacker);
        mob.Selector.AggroList.Single().AggroPoint.ShouldBe(250);
        mob.EnemyList[7].ShouldBe(250);       // reward attribution, tracked separately
    }

    /// <summary>Hate and contribution diverge as soon as the rate is not 1000 — which is exactly why the
    /// two lists cannot be collapsed into one.</summary>
    [Fact]
    public void AggroAndDamageContributionDivergeWhenTheRateIsNotNeutral()
    {
        var mob = Mob();
        var attacker = new Obj { Handle = 7 };

        mob.so_DamagedBy(attacker, damage: 200, aggroRatePermille: 250);

        mob.Selector.AggroList.Single().AggroPoint.ShouldBe(50);   // hate
        mob.EnemyList[7].ShouldBe(200);                            // loot/exp contribution
    }

    [Fact]
    public void TheFirstAttackerIsLatchedAndNeverOverwritten()
    {
        var mob = Mob();
        var first = new Obj { Handle = 7 };
        var second = new Obj { Handle = 9 };

        mob.FirstAttackerHandle.ShouldBe(ShineMob.NoAttacker);

        mob.so_DamagedBy(first, 10, 1000);
        mob.FirstAttackerHandle.ShouldBe((ushort)7);

        mob.so_DamagedBy(second, 9999, 1000);          // far more damage
        mob.FirstAttackerHandle.ShouldBe((ushort)7);   // still the first
    }

    [Fact]
    public void RepeatedHitsAccumulateOnBothLists()
    {
        var mob = Mob();
        var attacker = new Obj { Handle = 7 };

        mob.so_DamagedBy(attacker, 100, 1000);
        mob.so_DamagedBy(attacker, 50, 1000);

        mob.Selector.AggroList.Single().AggroPoint.ShouldBe(150);
        mob.EnemyList[7].ShouldBe(150);
    }

    [Fact]
    public void TheHighestHateAttackerBecomesTheTopTarget_NotTheHighestDamage()
    {
        var mob = Mob();
        var bigHitter = new Obj { Handle = 7 };
        var taunter = new Obj { Handle = 9 };

        mob.so_DamagedBy(bigHitter, damage: 1000, aggroRatePermille: 100);   // 100 hate, 1000 damage
        mob.so_DamagedBy(taunter, damage: 10, aggroRatePermille: 20000);     // 200 hate, 10 damage

        mob.Selector.mts_GetTopAggroTarget().ShouldBe(taunter);
        mob.EnemyList[7].ShouldBeGreaterThan(mob.EnemyList[9]);
    }
}
