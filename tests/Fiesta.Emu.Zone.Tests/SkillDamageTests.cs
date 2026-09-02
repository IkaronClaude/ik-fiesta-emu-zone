using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Parameter;
using Fiesta.Emu.Zone.Skill;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>What a CAST skill changes about a swing, measured against the real functions by
/// `tools/oracle_skill.py`.
///
/// <para>The plan expected a skill's damage bonus to come from `ActiveSkillInfoServer`'s `DmgIncRate` /
/// `DmgIncValue`. It does not. `roe_AttackPower@PhisycalSkill` dereferences <c>sdi_Activ</c> and reads the
/// CLIENT row; the server row is read by the HIT path instead. Both halves are pinned here.</para></summary>
public class SkillDamageTests
{
    private sealed record Fighter : ICombatant
    {
        public int Level { get; init; } = 60;
        public ParameterContainer Parameters { get; } = new();
        public int FreeStatDexTHRate { get; init; }
        public int FreeStatDexTBRate { get; init; }
        public int FreeStatMenCriRate { get; init; }
        public bool IsInMoving { get; init; }
        public int AttackRange { get; init; } = DamageCalculator.MeleeAttackRange;
        public bool HasItemActionObserves { get; init; }
    }

    /// <summary>The exact attacker the oracle drove: base Str 371, WCmin 1709, WCmax 1840, level 82.</summary>
    private static Fighter Attacker()
    {
        var f = new Fighter { Level = 82 };
        f.Parameters.Base[Stat.Str] = 371;
        f.Parameters.Base[Stat.WCmin] = 1709;
        f.Parameters.Base[Stat.WCmax] = 1840;
        f.Parameters.Rate(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery] = 1000;
        return f;
    }

    private static Fighter Defender()
    {
        var f = new Fighter { Level = 61 };
        f.Parameters.Base[Stat.Con] = 140;
        f.Parameters.Base[Stat.AC] = 102;
        return f;
    }

    private static (double Low, double High) Bounds(ActiveSkillInfo? skill, SkillEmpower empower = default)
    {
        var a = Attacker();
        return (DamageCalculator.AttackPower(a, 0, EngagementRule.PhysicalSkill, skill: skill, empower: empower),
                DamageCalculator.AttackPower(a, 1000, EngagementRule.PhysicalSkill, skill: skill, empower: empower));
    }

    /// <summary>The baseline the oracle measured with an all-zero skill row: 2080 .. 2211.</summary>
    [Fact]
    public void AnEmptySkillRowLeavesTheWeaponRangeAlone()
    {
        Bounds(null).ShouldBe((2080.0, 2211.0));
        Bounds(ActiveSkillInfo.Neutral).ShouldBe((2080.0, 2211.0));
    }

    /// <summary>⭐ <b>The two bounds are scaled INDEPENDENTLY</b>, each by its own column — so a skill can
    /// WIDEN its damage range, not merely shift it. Every figure here came off the real
    /// `roe_AttackPower@PhisycalSkill` with the draw pinned to each end.
    ///
    /// <para>This is the case a static read gets wrong quietly: a single-multiplier model agrees on the
    /// midpoint and only diverges at the ends.</para></summary>
    [Theory]
    // MinFlat  MinRate  MaxFlat  MaxRate      low     high
    [InlineData(0, 1000, 0, 0, 4160.0, 2211.0)]   // doubles the LOW bound only
    [InlineData(0, 0, 0, 1000, 2080.0, 4422.0)]   // doubles the HIGH bound only
    [InlineData(500, 0, 0, 0, 2580.0, 2211.0)]    // flat, LOW bound only
    [InlineData(0, 0, 500, 0, 2080.0, 2711.0)]    // flat, HIGH bound only
    [InlineData(500, 1000, 500, 1000, 4660.0, 4922.0)]
    public void EachBoundAnswersToItsOwnPairOfColumns(uint minFlat, uint minRate, uint maxFlat,
                                                      uint maxRate, double low, double high)
        => Bounds(new ActiveSkillInfo(minFlat, minRate, maxFlat, maxRate)).ShouldBe((low, high));

    /// <summary>⚠️ Nothing clamps low ≤ high. `MinWCRate` alone pushes the low bound ABOVE the high one,
    /// and the server does not notice: it goes on to take <c>ftol(high - low)</c>, which is negative, and
    /// hands that to the RNG as a span.
    ///
    /// <para>Kept as a test because it is a property of the real function, not an artefact of the port —
    /// the oracle produced low 4160 against high 2211 for this row. A "fix" here would be a divergence.</para></summary>
    [Fact]
    public void AMisconfiguredSkillRowCanInvertTheRangeAndTheServerDoesNotCare()
    {
        var (low, high) = Bounds(new ActiveSkillInfo(MinRatePermille: 1000));
        low.ShouldBeGreaterThan(high);
    }

    /// <summary>The empower term is ONE lookup added to BOTH bounds, so empowering a cast for damage
    /// shifts the range without widening it. Read at `roe_AttackPower@PhisycalSkill+0x17E..+0x1E6`, where
    /// the same `fild` result is added to each bound in turn.</summary>
    [Fact]
    public void TheEmpowerTermShiftsBothBoundsEqually()
    {
        var table = SkillEmpowerTable.FromArrays([100, 200, 300, 400, 500], [0, 0, 0, 0, 0],
                                                 [0, 0, 0, 0, 0], [0, 0, 0, 0, 0]);
        var skill = new ActiveSkillInfo(Empower: table);

        Bounds(skill, SkillEmpower.From(damage: 0)).ShouldBe((2080.0, 2211.0));
        Bounds(skill, SkillEmpower.From(damage: 1)).ShouldBe((2180.0, 2311.0));
        Bounds(skill, SkillEmpower.From(damage: 3)).ShouldBe((2380.0, 2511.0));
    }

    /// <summary>A skill row handed to a NON-skill rule is ignored, not half-applied — only the two skill
    /// `roe_AttackPower` overrides dereference <c>sdi_Activ</c>.</summary>
    [Fact]
    public void ThePlainSwingRuleNeverReadsTheSkillRow()
    {
        var a = Attacker();
        var fat = new ActiveSkillInfo(9999, 9999, 9999, 9999);
        DamageCalculator.AttackPower(a, 0, EngagementRule.NormalPhysical, skill: fat)
            .ShouldBe(DamageCalculator.AttackPower(a, 0, EngagementRule.NormalPhysical));
    }

    // ---- the hit half ----------------------------------------------------------------------------------

    /// <summary>`SkilPyHitRate` replaces the plain swing's hard-coded 850 in the SAME position and the
    /// same units. With aim equal to evasion the rate comes straight out — 850 in, 850 out — which is what
    /// makes "a skill row holding 850 is exactly as accurate as an ordinary attack" a statement of fact
    /// rather than an analogy. Measured on the real `roe_HitRate@PhisycalSkill`.</summary>
    [Theory]
    [InlineData(850u, 850.0)]
    [InlineData(1700u, 1700.0)]
    [InlineData(425u, 425.0)]
    public void TheSkillSuppliesItsOwnAccuracyConstant(uint rate, double expected)
        => DamageCalculator.SkillHitRate(Attacker(), Defender(), new ActiveSkillInfoServer(rate))
            .ShouldBe(expected);

    /// <summary>⭐ `SkillHitType` non-zero switches to <c>HitRate * attackerLevel / defenderLevel</c>, and
    /// aim, evasion, Dex, the defender's movement and every modifier feeding them drop out ENTIRELY.
    ///
    /// <para>Measured both ways on the real function: at level 82 against 61 the answers were 1142 and
    /// 2285, and raising the attacker's Dex from 20 to 2000 moved the normal branch from 850 to 1700000
    /// while leaving this one at 1142.</para></summary>
    [Theory]
    [InlineData(850u, 1142.0)]
    [InlineData(1700u, 2285.0)]
    public void TheLevelRatioBranchIgnoresAimAndEvasion(uint rate, double expected)
    {
        var skill = new ActiveSkillInfoServer(rate, SkillHitType.ByLevelRatio);
        DamageCalculator.SkillHitRate(Attacker(), Defender(), skill).ShouldBe(expected);

        var keen = Attacker();
        keen.Parameters.Base[Stat.Dex] = 2000;
        DamageCalculator.SkillHitRate(keen, Defender(), skill).ShouldBe(expected);
    }

    /// <summary>The branch is <c>!= 0</c>, not <c>== 1</c>. Driven on the real function with the column
    /// set to 2 and 7, both of which behaved exactly like 1 — which is why the enum has one member for
    /// the whole non-zero side rather than inventing distinctions the code does not make.</summary>
    [Fact]
    public void AnyNonZeroHitTypeTakesTheLevelRatioBranch()
    {
        var expected = DamageCalculator.SkillHitRate(
            Attacker(), Defender(), new ActiveSkillInfoServer(850, SkillHitType.ByLevelRatio));

        foreach (var odd in new[] { 2, 7, 255 })
            DamageCalculator.SkillHitRate(Attacker(), Defender(),
                    new ActiveSkillInfoServer(850, (SkillHitType)odd))
                .ShouldBe(expected, $"HitType {odd} must take the level-ratio branch, not fall back to normal");
    }
}
