using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Weapon LICENSES — `smo_ply_WeaponTitleSet`, and the two halves that behave differently.</summary>
public class WeaponTitleLicenseTests
{
    private static WeaponTitleData Orc(int minAdd = 1200, int maxAdd = 1400, uint crit = 30)
        => new(MobId: 42, Level: 3, MobKillCount: 5000, minAdd, maxAdd,
               Options: [(Reference: 1, Type: 1, Value: crit)]);

    /// <summary>⭐ The values land in the ORDINARY `WeaponTitle` cluster, which the damage accessors and
    /// `roe_CriticalRate` already read.
    ///
    /// <para>The object-relative offsets (+0x1634 etc.) look like bespoke player fields and are not: the
    /// container sits at object +0xFC0 and `WeaponTitle.Rate` at container +0x660, so +0x1634 is
    /// `Rate[WCmin]` exactly. Resolving an offset against the container before calling it a field is the
    /// lesson.</para></summary>
    [Fact]
    public void TheLicenseLandsInTheWeaponTitleCluster()
    {
        var c = new ParameterContainer();
        WeaponTitleLicense.Stage(c, Orc(minAdd: 1200, maxAdd: 1400, crit: 30));
        var rate = c.Rate(StatModifier.WeaponTitle);

        rate[Stat.WCmin].ShouldBe(1200);
        rate[Stat.WCmax].ShouldBe(1400);
        rate[Stat.MAmin].ShouldBe(1200, "the magic pair gets the same two numbers");
        rate[Stat.MAmax].ShouldBe(1400);
        rate[Stat.CriDamRate].ShouldBe(30);
    }

    /// <summary>⚠️ Staging RESETS first, so the previous target's bonus goes. This is what makes a
    /// per-target bonus safe to keep in a per-character container: it is rebuilt every swing.</summary>
    [Fact]
    public void StagingANewTargetClearsThePreviousBonus()
    {
        var c = new ParameterContainer();
        WeaponTitleLicense.Stage(c, Orc());
        c.Rate(StatModifier.WeaponTitle)[Stat.WCmin].ShouldBe(1200);

        // Now swing at something we have no license for.
        WeaponTitleLicense.Stage(c, null);
        var rate = c.Rate(StatModifier.WeaponTitle);
        rate[Stat.WCmin].ShouldBe(ParameterCluster.RateIdentity, "back to the neutral eraser value");
        rate[Stat.CriDamRate].ShouldBe(0, "and the crit half goes with it");
    }

    /// <summary>⭐ The reset explains what zeroes `CriDamRate` after the eraser fill — a question this
    /// port carried open from a live container read. The eraser seeds 1000 into nearly every slot, and
    /// this function takes it back out of the two critical rates.
    ///
    /// <para>Without it a bare character would carry a free 100% weapon-title crit, because
    /// `roe_CriticalRate` reads that slot ADDITIVELY.</para></summary>
    [Fact]
    public void TheResetIsWhatZeroesTheCriticalRates()
    {
        var c = new ParameterContainer();
        WeaponTitleLicense.Stage(c, null);
        var rate = c.Rate(StatModifier.WeaponTitle);

        rate[Stat.CriDamRate].ShouldBe(0);
        rate[Stat.MagCriDamRate].ShouldBe(0);
        rate[Stat.WCmin].ShouldBe(ParameterCluster.RateIdentity, "every other slot keeps the eraser's 1000");
    }

    /// <summary>Only Reference 1 / Type 1 is honoured — every other combination logs "Invalid" and does
    /// nothing. And the honoured one ACCUMULATES, so several options of that kind stack.</summary>
    [Fact]
    public void OnlyOneOptionKindIsHonouredAndItAccumulates()
    {
        WeaponTitleLicense.OptionIsHonoured(1, 1).ShouldBeTrue();
        WeaponTitleLicense.OptionIsHonoured(1, 2).ShouldBeFalse();
        WeaponTitleLicense.OptionIsHonoured(2, 1).ShouldBeFalse();
        WeaponTitleLicense.OptionIsHonoured(0, 0).ShouldBeFalse();

        var c = new ParameterContainer();
        WeaponTitleLicense.Stage(c, new WeaponTitleData(42, 3, 5000, 1000, 1000,
            Options: [(1, 1, 20), (1, 2, 500), (1, 1, 15), (3, 1, 900)]));

        c.Rate(StatModifier.WeaponTitle)[Stat.CriDamRate]
            .ShouldBe(35, "20 + 15; the type-2 and reference-3 options are ignored");
    }

    /// <summary>⭐ End to end: the crit half reaches `roe_CriticalRate` unconditionally, exactly as the
    /// operator described — a license's crit applies against everything, not only the licensed mob.</summary>
    [Fact]
    public void TheCritHalfReachesTheCriticalRateFormula()
    {
        var attacker = new Combatant(60, new ParameterContainer());
        var defender = new Combatant(60, new ParameterContainer());

        var before = DamageCalculator.CriticalRate(attacker, defender);
        WeaponTitleLicense.Stage(attacker.Parameters, Orc(crit: 30));
        var after = DamageCalculator.CriticalRate(attacker, defender);

        // ⚠️ NOT a delta of 30. `roe_CriticalRate` floors its result at 1, so an unlicensed character sits
        // at the floor rather than at zero and the observed change is 29. Asserting the delta would have
        // encoded the floor away; assert both ends instead.
        before.ShouldBe(1.0, "the floor -- one crit in a thousand, never zero");
        after.ShouldBe(30.0, "the licence's crit is a straight addend, and it clears the floor");
    }
}
