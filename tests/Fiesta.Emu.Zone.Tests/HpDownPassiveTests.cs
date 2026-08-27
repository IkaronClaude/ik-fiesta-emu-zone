using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`ChangeByConditionParam` — the "as your HP drops, your stats rise" passives that
/// `roe_AttackPower` and `roe_DefendPower` read, and which this port had never applied.</summary>
public class HpDownPassiveTests
{
    private static Combatant With(Action<ParameterContainer> configure, int level = 60)
    {
        var p = new ParameterContainer();
        p.Base[Stat.Str] = 100;
        p.Base[Stat.Con] = 100;
        p.Base[Stat.WCmin] = 200;
        p.Base[Stat.WCmax] = 300;
        p.Base[Stat.AC] = 150;
        configure(p);
        return new Combatant(level, p);
    }

    /// <summary>The bucket lookup, including both of its zero returns. Neither clamps to the nearest
    /// bucket — an out-of-range key contributes nothing at all.</summary>
    [Fact]
    public void TheBucketLookupReturnsZeroOutsideItsRange()
    {
        var block = new ChangeByConditionParam(Condition: 100, Values: [0, 5, 10, 20]);

        block.Value(0).ShouldBe(0);
        block.Value(99).ShouldBe(0, "still bucket 0");
        block.Value(100).ShouldBe(5);
        block.Value(250).ShouldBe(10);
        block.Value(399).ShouldBe(20);
        block.Value(400).ShouldBe(0, "past the last bucket is ZERO, not the last value");
        block.Value(99_999).ShouldBe(0);

        ChangeByConditionParam.None.Value(500).ShouldBe(0, "an unconfigured block never contributes");
    }

    /// <summary>The condition key: HP MISSING in permille, so 0 at full health and 1000 at death. The
    /// direction is the whole point of the passive and is easy to invert.</summary>
    [Fact]
    public void TheConditionIsHowMuchHpIsMissing()
    {
        ChangeByConditionParam.HpMissingPermille(hp: 100, maxHp: 100).ShouldBe(0, "untouched");
        ChangeByConditionParam.HpMissingPermille(hp: 75, maxHp: 100).ShouldBe(250);
        ChangeByConditionParam.HpMissingPermille(hp: 0, maxHp: 100).ShouldBe(1000, "dead");

        // The guards: `roe_AttackPower` skips the block entirely unless maxHp > 0.
        ChangeByConditionParam.HpMissingPermille(hp: 50, maxHp: 0).ShouldBe(0);
        ChangeByConditionParam.HpMissingPermille(hp: 150, maxHp: 100).ShouldBe(0, "overhealed is not negative");
    }

    /// <summary>⭐ The behaviour that matters: a wounded character with the passive hits HARDER, and the
    /// bonus lands on the BOUNDS before the roll — so it shifts the whole range, min and max alike.</summary>
    [Fact]
    public void BeingHurtRaisesBothWeaponBounds()
    {
        var hurt = With(p =>
        {
            p.PassiveHpDownWcMin = new ChangeByConditionParam(500, [0, 100, 400]);
            p.PassiveHpDownWcMax = new ChangeByConditionParam(500, [0, 150, 600]);
        });

        var healthyLow = DamageCalculator.AttackPower(hurt, rollPermille: 0, hpMissingPermille: 0);
        var healthyHigh = DamageCalculator.AttackPower(hurt, rollPermille: 1000, hpMissingPermille: 0);

        // Half HP missing -> bucket 1.
        var woundedLow = DamageCalculator.AttackPower(hurt, rollPermille: 0, hpMissingPermille: 500);
        var woundedHigh = DamageCalculator.AttackPower(hurt, rollPermille: 1000, hpMissingPermille: 500);

        woundedLow.ShouldBe(healthyLow + 100);
        woundedHigh.ShouldBe(healthyHigh + 150);

        // Nearly dead -> bucket 2, a much bigger bonus.
        DamageCalculator.AttackPower(hurt, rollPermille: 0, hpMissingPermille: 1000)
            .ShouldBe(healthyLow + 400);
    }

    /// <summary>The defensive half. ⚠️ It is `roe_DefendPower` that reads the AC block, NOT `roe_AC` — so
    /// armour read directly is unchanged and only the defence used in the formula moves.</summary>
    [Fact]
    public void DefendPowerReadsTheBlockButArmourClassDoesNot()
    {
        var c = With(p => p.PassiveHpDownAc = new ChangeByConditionParam(500, [0, 60, 200]));

        var armour = DamageCalculator.ArmourClass(c);
        DamageCalculator.DefendPower(c, hpMissingPermille: 0).ShouldBe(armour);
        DamageCalculator.DefendPower(c, hpMissingPermille: 500).ShouldBe(armour + 60);

        DamageCalculator.ArmourClass(c).ShouldBe(armour,
            "roe_AC has no cbcp call at all -- reading armour directly must not change");
    }

    /// <summary>A character WITHOUT the passive is unaffected, which is what makes this safe to add: every
    /// existing result stands because an unconfigured block returns 0 at every condition.</summary>
    [Fact]
    public void ACharacterWithoutThePassiveIsUnchangedAtAnyHealth()
    {
        var plain = With(_ => { });

        foreach (var missing in new[] { 0, 1, 250, 500, 999, 1000 })
        {
            DamageCalculator.AttackPower(plain, rollPermille: 500, hpMissingPermille: missing)
                .ShouldBe(DamageCalculator.AttackPower(plain, rollPermille: 500));
            DamageCalculator.DefendPower(plain, hpMissingPermille: missing)
                .ShouldBe(DamageCalculator.DefendPower(plain));
        }
    }

    /// <summary>It reaches a resolved swing through <see cref="AttackModifiers"/>, not just the accessors.</summary>
    [Fact]
    public void AResolvedSwingUsesTheAttackersAndDefendersOwnCondition()
    {
        var attacker = With(p => p.PassiveHpDownWcMin = new ChangeByConditionParam(500, [0, 1000, 1000]));
        var defender = With(p => p.PassiveHpDownAc = new ChangeByConditionParam(500, [0, 5000, 5000]));
        var pinned = new AttackModifiers { RollPermille = 0, ForceCritical = false };

        var baseline = DamageCalculator.ResolveDamage(attacker, defender, pinned);
        var attackerHurt = DamageCalculator.ResolveDamage(attacker, defender,
            pinned with { AttackerHpMissingPermille = 500 });
        var defenderHurt = DamageCalculator.ResolveDamage(attacker, defender,
            pinned with { DefenderHpMissingPermille = 500 });

        attackerHurt.ShouldBeGreaterThan(baseline, "a hurt attacker hits harder");
        defenderHurt.ShouldBeLessThan(baseline, "a hurt defender takes less");
    }
}
