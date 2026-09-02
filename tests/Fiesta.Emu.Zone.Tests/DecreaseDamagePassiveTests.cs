using Fiesta.Emu.Zone.Combat;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`so_ply_DecreaseDmgPassiveSkill` — one of task 5's "hooks no clean swing reaches", and the
/// reason is now concrete: it needs a MONSTER attacker, a configured rate, close range, and the monster
/// looking somewhere else.</summary>
public class DecreaseDamagePassiveTests
{
    private const int Near = 100;      // distanceSquared, well inside the area below
    private const int Area = 50;       // area² = 2500 > 100

    /// <summary>⭐ You are protected when the monster is facing more than 90° AWAY from you — beside or
    /// behind it. Standing in its forward half gives nothing.
    ///
    /// <para>⚠️ The angles below are DIRECTION UNITS of <b>2° each</b> (the bearing halved), verified via
    /// this repo's `sr_degree2sr` port in <see cref="Fiesta.Emu.Zone.Combat.DamageByAngleTable"/>. So 45
    /// units is 90°, and 180 units is a FULL TURN — which is why 180 folds to 0 and is unprotected. Read
    /// as plain degrees the same numbers tell a completely different and wrong story about the rule.</para></summary>
    [Theory]
    [InlineData(0, 1000, "0deg: dead ahead, no protection")]
    [InlineData(45, 1000, "90deg: exactly on the cone edge, and the test is <=, so still none")]
    [InlineData(46, 700, "92deg: just past the cone -- protected")]
    [InlineData(90, 700, "180deg: directly behind -- protected")]
    [InlineData(134, 700, "268deg, folds to 92deg -- protected")]
    [InlineData(180, 1000, "360deg == 0deg: a full turn folds to dead ahead, so no protection")]
    public void ProtectionAppliesBesideAndBehindTheMonster(int facing, int expected, string why)
        => DecreaseDamagePassive.Apply(1000, DecreaseDamagePassive.MonsterKind, 300,
                Near, Area, attackerFacing: facing, bearingToDefender: 0)
            .ShouldBe(expected, why);

    /// <summary>Every gate returns the damage UNCHANGED, never zero — an early exit is "the passive did
    /// not apply", not "the hit was absorbed".</summary>
    [Theory]
    [InlineData(1, 300, Near, "a player attacker: the hook is monster-only")]
    [InlineData(5, 0, Near, "rate 0: the passive is not configured")]
    [InlineData(5, 300, 1_000_000, "out of range")]
    public void EveryGateLeavesTheDamageAlone(int kind, int rate, long distanceSquared, string why)
        => DecreaseDamagePassive.Apply(1000, kind, rate, distanceSquared, Area,
                attackerFacing: 90, bearingToDefender: 0)
            .ShouldBe(1000, why);

    /// <summary>The range test compares SQUARED values — no square root anywhere — and the boundary is
    /// inclusive: <c>area² &lt; distance²</c> rejects, so exactly at the edge still protects.</summary>
    [Fact]
    public void TheRangeTestIsSquaredAndInclusiveAtTheEdge()
    {
        // area 50 -> area² 2500. Exactly 2500 is inside; 2501 is not.
        DecreaseDamagePassive.Apply(1000, 5, 300, 2500, 50, 90, 0).ShouldBe(700);
        DecreaseDamagePassive.Apply(1000, 5, 300, 2501, 50, 90, 0).ShouldBe(1000);
    }

    /// <summary>A bearing from a point to itself is meaningless, and the original checks both coordinates
    /// before taking one.</summary>
    [Fact]
    public void AnAttackerOnTheSameSpotIsNotProtectedAgainst()
        => DecreaseDamagePassive.Apply(1000, 5, 300, 0, Area, 90, 0, samePosition: true)
            .ShouldBe(1000);

    /// <summary>The fold brings a difference into 0..90 units (0..180°) before the cone test, so a
    /// wrap-around bearing is not mistaken for a wide angle.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(91, 89)]
    [InlineData(180, 0)]   // 180 units is a FULL TURN, so it folds to dead ahead
    [InlineData(-45, 45)]
    [InlineData(270, 90)]
    public void TheAngleFoldNormalisesIntoTheHalfTurn(int difference, int expected)
        => DecreaseDamagePassive.FoldAngle(difference).ShouldBe(expected);

    /// <summary>The reduction is a share removed, truncating — the same shape as the abstate damage-down,
    /// and not a scale TO the rate.</summary>
    [Theory]
    [InlineData(1000, 300, 700)]
    [InlineData(457, 333, 305)]
    [InlineData(1000, 1000, 0)]
    public void TheReductionRemovesItsShare(int damage, int rate, int expected)
        => DecreaseDamagePassive.Apply(damage, 5, rate, Near, Area, 90, 0).ShouldBe(expected);
}
