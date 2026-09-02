using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Item options — the channel socketed gems and rolled options share.</summary>
public class ItemOptionsTests
{
    /// <summary>⭐ A socketed crit gem is NOT a separate mechanism. `ROT_CRI` maps onto
    /// `Item.Rate[CriDamRate]` and `ROT_CRITICAL_TB` onto `Item.Rate[CriticalTB]` — exactly the two terms
    /// `roe_CriticalRate` already reads, one added and one subtracted.
    ///
    /// <para>Which is why the live container measurement summed cleanly at weapon 90 + costume 70 +
    /// earrings 40 = 200: there is one path into the Item cluster, not one per source.</para></summary>
    [Fact]
    public void TheCritOptionsMapOntoTheTermsTheFormulaReads()
    {
        ItemOptions.StatFor(RandomOptionType.ROT_CRI).ShouldBe(Stat.CriDamRate);
        ItemOptions.StatFor(RandomOptionType.ROT_CRITICAL_TB).ShouldBe(Stat.CriticalTB);
    }

    /// <summary>Several options of the same type accumulate — nothing dedupes, which is how multiple
    /// sockets of a kind stack.</summary>
    [Fact]
    public void RepeatedOptionsAccumulate()
    {
        var o = new ItemOptions()
            .With(RandomOptionType.ROT_CRI, 20)
            .With(RandomOptionType.ROT_CRI, 15)
            .With(RandomOptionType.ROT_STR, 5);

        o.Find(RandomOptionType.ROT_CRI).ShouldBe(35);
        o.Find(RandomOptionType.ROT_STR).ShouldBe(5);
    }

    /// <summary>`iac_FindOption` returns 0 for an absent type — and 0 is also a legitimate stored value.
    /// The original draws no distinction, so neither does this.</summary>
    [Fact]
    public void AnAbsentOptionReadsAsZeroJustLikeAStoredZero()
    {
        var o = new ItemOptions().With(RandomOptionType.ROT_MEN, 0);
        o.Find(RandomOptionType.ROT_MEN).ShouldBe(0);
        o.Find(RandomOptionType.ROT_MAXHP).ShouldBe(0);
    }

    /// <summary>The capacities the item struct actually declares.</summary>
    [Fact]
    public void TheCapacitiesMatchTheItemStruct()
    {
        ItemOptions.MaxOptions.ShouldBe(24, "ItemOptionStorage::Element[24]");
        ItemOptions.MaxSockets.ShouldBe(9, "ShineItemAttr_Weapon::gemSockets[9]");
    }

    /// <summary>⚠️ Three option types are deliberately UNMAPPED rather than guessed: `ROT_WC` and `ROT_MA`
    /// name a PAIR of bounds rather than one slot, and `ROT_DEMANDLVDOWN` is a level-requirement reduction
    /// with no combat effect. Returning null keeps "not modelled" distinguishable from "maps to
    /// nothing".</summary>
    [Theory]
    [InlineData(RandomOptionType.ROT_WC)]
    [InlineData(RandomOptionType.ROT_MA)]
    [InlineData(RandomOptionType.ROT_DEMANDLVDOWN)]
    public void ThePairAndNonCombatOptionsAreLeftUnmapped(RandomOptionType type)
        => ItemOptions.StatFor(type).ShouldBeNull();
}
