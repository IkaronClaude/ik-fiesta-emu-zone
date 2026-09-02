using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The damage hooks from `OPEN_QUESTIONS.md` §3 — each a real branch of `roe_CalcDamage` or
/// `roe_AttackPower` that a clean unbuffed swing never reaches.
///
/// <para>That is exactly why they need tests rather than a capture: the 556/556 ground truth cannot see
/// any of them, so nothing else in this repo would notice if one were wrong.</para></summary>
public class DamageHookTests
{
    private static ICombatant Attacker() => Combatant.FromBaseStats(60,
        [new(Stat.Str, 300), new(Stat.WCmin, 400), new(Stat.WCmax, 400)]);

    private static ICombatant Defender() => Combatant.FromBaseStats(60,
        [new(Stat.Con, 150), new(Stat.AC, 120)]);

    private static readonly AttackModifiers Pinned = new() { RollPermille = 500, ForceCritical = false };

    /// <summary>`ChargedEffectContainer` is ONE term, not two: only the larger rate acts, and equal rates
    /// cancel exactly — which is why the neutral default is 0/0 rather than 1024/1024.</summary>
    [Fact]
    public void EqualChargedForcesCancel()
    {
        DamageCalculator.ApplyChargedEffect(1000.0, 0, 0).ShouldBe(1000.0);
        DamageCalculator.ApplyChargedEffect(1000.0, 512, 512).ShouldBe(1000.0);
    }

    /// <summary>The two directions are NOT symmetric — an attacker ahead MULTIPLIES by
    /// <c>(1024+diff)/1024</c>, a defender ahead DIVIDES by it. A 1024 advantage doubles; a 1024 deficit
    /// halves.</summary>
    [Fact]
    public void TheChargedEffectMultipliesOneWayAndDividesTheOther()
    {
        DamageCalculator.ApplyChargedEffect(1000.0, 1024, 0).ShouldBe(2000.0);
        DamageCalculator.ApplyChargedEffect(1000.0, 0, 1024).ShouldBe(500.0);

        // Only the DIFFERENCE matters, so a shared baseline is invisible.
        DamageCalculator.ApplyChargedEffect(1000.0, 2048, 1024).ShouldBe(2000.0);
    }

    /// <summary>It reaches the pipeline, and a defender's charge reduces what they take — mobs share one
    /// static container, so this is a player-side term in both directions.</summary>
    [Fact]
    public void TheChargedEffectReachesResolvedDamage()
    {
        var plain = DamageCalculator.ResolveDamage(Attacker(), Defender(), Pinned);

        var charged = DamageCalculator.ResolveDamage(Attacker(), Defender(),
            Pinned with { AttackForceRate1024 = 1024 });
        charged.ShouldBeGreaterThan(plain);

        var warded = DamageCalculator.ResolveDamage(Attacker(), Defender(),
            Pinned with { DefendForceRate1024 = 1024 });
        warded.ShouldBeLessThan(plain);
    }

    /// <summary>`so_ply_DecreaseDmgPassiveSkill` is the ONLY hook in the pipeline that can reduce incoming
    /// damage. 1000 is identity, and it applies to the INTEGER damage after the conversion.</summary>
    [Fact]
    public void TheDamageReductionPassiveOnlyEverReduces()
    {
        var plain = DamageCalculator.ResolveDamage(Attacker(), Defender(), Pinned);

        DamageCalculator.ResolveDamage(Attacker(), Defender(),
            Pinned with { DecreaseDamagePassivePermille = 1000 }).ShouldBe(plain);

        var halved = DamageCalculator.ResolveDamage(Attacker(), Defender(),
            Pinned with { DecreaseDamagePassivePermille = 500 });
        (halved - plain / 2).ShouldBeInRange(-1, 1);
    }

    /// <summary>`EventRun_IncDmgRate` collects the ATTACKER's item actions and the DEFENDER's into one
    /// `ActionResults` and applies it inside attack power — before the mastery rate, which the oracle
    /// confirmed. No results is the neutral case.</summary>
    [Fact]
    public void TheItemActionResultsScaleAttackPowerNotTheFinalDamage()
    {
        var attacker = Attacker();
        var plain = DamageCalculator.AttackPower(attacker, rollPermille: 500);
        var boosted = DamageCalculator.AttackPower(attacker, rollPermille: 500,
                                                   itemActions: new ItemActionResults([2000]));

        // Not exactly 2x: the bounds are truncated to integers before the rate is applied, which the
        // untouched path never does. Being within a unit of double IS the finding.
        (boosted - plain * 2).ShouldBeInRange(-2, 2);
        DamageCalculator.AttackPower(attacker, 500, itemActions: ItemActionResults.None)
            .ShouldBe(plain, "an empty result set is the gate staying shut, so nothing is truncated");
    }

    /// <summary>None of the hooks moves a swing that does not use them — which is what keeps the 556/556
    /// ground truth meaningful now that they exist.</summary>
    [Fact]
    public void TheDefaultsAreAllIdentity()
    {
        var m = AttackModifiers.Default;
        m.AttackForceRate1024.ShouldBe(0);
        m.DefendForceRate1024.ShouldBe(0);
        m.ItemActions.ShouldBeNull("no item actions is the default, and it is the gate staying shut");
        m.DecreaseDamagePassivePermille.ShouldBe(1000);

        DamageCalculator.ResolveDamage(Attacker(), Defender(), Pinned)
            .ShouldBe(DamageCalculator.ResolveDamage(Attacker(), Defender(),
                new AttackModifiers { RollPermille = 500, ForceCritical = false }));
    }
}
