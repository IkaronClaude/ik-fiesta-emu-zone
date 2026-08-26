using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The stat pipeline: clusters, the two operators, and `c_MakeTotal`'s combining order.</summary>
public class ParameterClusterTests
{
    [Fact]
    public void AClusterHasExactlyAsManySlotsAsThereAreStats()
        => Enum.GetValues<Stat>().Length.ShouldBe(ParameterCluster.SlotCount);

    /// <summary>`c_clear` copies 0x33 dwords per cluster. 0x33 is 51.</summary>
    [Fact]
    public void TheSlotCountIsTheRepMovsdCount()
        => ParameterCluster.SlotCount.ShouldBe(0x33);

    [Fact]
    public void AddIsFieldWise()
    {
        var a = ParameterCluster.Plus();
        var b = ParameterCluster.Plus();
        a[Stat.Str] = 10; a[Stat.AC] = 4;
        b[Stat.Str] = 5; b[Stat.MR] = 7;

        a.Add(b);

        a[Stat.Str].ShouldBe(15);
        a[Stat.AC].ShouldBe(4);
        a[Stat.MR].ShouldBe(7);
    }

    /// <summary>A rate of exactly 1000 is SKIPPED by the original (`cmp eax, 0x3E8; je`), not applied.</summary>
    [Fact]
    public void ARateOfOneThousandIsTheIdentity()
    {
        var c = ParameterCluster.Plus();
        c[Stat.WCmax] = 7;

        c.ApplyRate(ParameterCluster.Rate());

        c[Stat.WCmax].ShouldBe(7);
    }

    [Fact]
    public void RatesArePermille()
    {
        var c = ParameterCluster.Plus();
        c[Stat.WCmax] = 200;
        var rate = ParameterCluster.Rate();
        rate[Stat.WCmax] = 1150;

        c.ApplyRate(rate);

        c[Stat.WCmax].ShouldBe(230);
    }

    /// <summary>The divide truncates TOWARD ZERO, which is what the `shr eax,31; add eax,edx` correction
    /// after the magic-number divide achieves. 7 * 1100 / 1000 is 7.7, stored as 7.</summary>
    [Fact]
    public void RateScalingTruncatesTowardZero()
    {
        var c = ParameterCluster.Plus();
        c[Stat.WCmax] = 7;
        c[Stat.AC] = -7;
        var rate = ParameterCluster.Rate();
        rate[Stat.WCmax] = 1100;
        rate[Stat.AC] = 1100;

        c.ApplyRate(rate);

        c[Stat.WCmax].ShouldBe(7);     // 7.7 -> 7
        c[Stat.AC].ShouldBe(-7);       // -7.7 -> -7, toward zero rather than down
    }

    /// <summary>A zero rate really zeroes the slot. Zero is a value, not "unset" — the only value the
    /// original treats specially is 1000.</summary>
    [Fact]
    public void AZeroRateZeroesTheSlot()
    {
        var c = ParameterCluster.Plus();
        c[Stat.WCmax] = 500;
        var rate = ParameterCluster.Rate();
        rate[Stat.WCmax] = 0;

        c.ApplyRate(rate);

        c[Stat.WCmax].ShouldBe(0);
    }
}

public class ParameterContainerTests
{
    [Fact]
    public void ThereIsAPlusAndARateClusterForEverySource()
    {
        var c = new ParameterContainer();
        foreach (var source in Enum.GetValues<StatModifier>())
        {
            c.Plus(source).ShouldNotBeNull();
            c.Rate(source).ShouldNotBeNull();
        }
    }

    /// <summary>Fifteen clusters: one base plus a (Plus, Rate) pair per source. That is exactly how many
    /// `c_clear` seeds, at a stride of 0xCC.</summary>
    [Fact]
    public void TheContainerHoldsFifteenClusters()
        => (1 + Enum.GetValues<StatModifier>().Length * 2).ShouldBe(15);

    [Fact]
    public void AnEmptyContainerTotalsToItsBase()
    {
        var c = new ParameterContainer();
        c.Base[Stat.WCmax] = 42;

        c.MakeTotal()[Stat.WCmax].ShouldBe(42);
    }

    /// <summary>The five primaries are floored at 1 — the tail of `c_MakeTotal`. Nothing else is.</summary>
    [Fact]
    public void ThePrimariesAreFlooredAtOneAndNothingElseIs()
    {
        var c = new ParameterContainer();
        c.Base[Stat.Str] = 0;
        c.Base[Stat.Men] = -50;
        c.Base[Stat.AC] = 0;

        var total = c.MakeTotal();

        total[Stat.Str].ShouldBe(1);
        total[Stat.Men].ShouldBe(1);
        total[Stat.AC].ShouldBe(0);      // slot 7 is past the floored range
    }

    /// <summary>THE ORDER IS THE FORMULA. Gear's flat bonus is added BEFORE the rate steps, so buffs scale
    /// it; an upgrade's flat bonus is added AFTER, so they do not. Collapsing the layers into one sum would
    /// silently change this.</summary>
    [Fact]
    public void ItemPlusIsScaledByBuffsButUpgradePlusIsNot()
    {
        static int Total(StatModifier source)
        {
            var c = new ParameterContainer();
            c.Plus(source)[Stat.WCmax] = 100;
            c.Rate(StatModifier.AbnormalState)[Stat.WCmax] = 2000;   // a x2 buff
            return c.MakeTotal()[Stat.WCmax];
        }

        Total(StatModifier.Item).ShouldBe(200);      // added before the rate step
        Total(StatModifier.Upgrade).ShouldBe(100);   // added after it
    }

    /// <summary>Rates compound multiplicatively in sequence rather than summing, and each step truncates
    /// independently — so the order of the three rate steps is observable too.</summary>
    [Fact]
    public void RateStepsCompoundRatherThanSum()
    {
        var c = new ParameterContainer();
        c.Base[Stat.WCmax] = 100;
        c.Rate(StatModifier.PassiveSkill)[Stat.WCmax] = 1500;
        c.Rate(StatModifier.AbnormalState)[Stat.WCmax] = 1500;

        // 100 * 1.5 = 150, then 150 * 1.5 = 225. Summing the rates would give 100 * 2.0 = 200.
        c.MakeTotal()[Stat.WCmax].ShouldBe(225);
    }

    /// <summary>Layers `c_MakeTotal` deliberately does NOT fold in. They are not dead — the damage formula
    /// reads them directly — so a stat system that combined everything would lose them.</summary>
    [Theory]
    [InlineData(StatModifier.WeaponTitle)]
    public void SomeLayersNeverReachTheTotal(StatModifier source)
    {
        var c = new ParameterContainer();
        c.Plus(source)[Stat.WCmax] = 999;

        c.MakeTotal()[Stat.WCmax].ShouldBe(0);
    }
}

/// <summary>⚠️ KNOWN RED — deliberately. See PROJECT_PLAN.md: known-red tests mark what has not been read,
/// and are closed by reading the binary, never by asserting current behaviour.</summary>
public class BaseCombatStatTests
{
    [Fact]
    public void BaseWeaponAndArmourStatsAreUnreadPerClassVirtuals()
        => Assert.Fail(
            "Unread: the eight per-class virtual methods that fill the base WC/AC/TH/TB/MA/MR/MH/MB slots. "
            + "c_Storepure writes slots 5..14 from virtual calls on the CharClass object, so each class "
            + "computes its own -- there is no shared stat-to-attack formula. CharClass's own bodies are all "
            + "folded to 0x00449600 (MaxHP 0x00449610, MaxSP 0x00449660); the per-class overrides have not "
            + "been located. Until then CharacterParameters.StorePure leaves those slots at zero. "
            + "See docs/PARAMETERS.md. Expected red.");
}
