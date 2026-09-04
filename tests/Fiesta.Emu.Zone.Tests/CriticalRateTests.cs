using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`roe_CriticalRate` — the chance a hit crits.
///
/// <para>⚠️ This existed as a COMMENT for three days before it existed as code.
/// `CharacterParameters.Equip` carried a verified paragraph saying `Item.Rate[CriDamRate]` is the live
/// slot and `Item.Plus[Critical]` is dead, and went on writing the dead one — so every character's crit
/// rate was 0 whatever they wore, and nothing read it anyway. These tests exist so that cannot recur
/// quietly.</para></summary>
public class CriticalRateTests
{
    private static ParameterContainer Wearing(params int[] critRates)
    {
        var c = new ParameterContainer();
        foreach (var r in critRates)
            CharacterParameters.Equip(c, new EquipmentPiece("piece", CriRate: r));
        return c;
    }

    private static ICombatant Of(ParameterContainer p, int spentMen = 0) => new MenCombatant(60, p, spentMen);

    /// <summary>A combatant with a MEN free-stat allocation, which `roe_FreeStatCriRate` reads.</summary>
    private sealed class MenCombatant(int level, ParameterContainer p, int spentMen) : ICombatant
    {
        public int Level { get; } = level;
        public ParameterContainer Parameters { get; } = p;
        public int FreeStatMenCriRate => FreeStatTables.MenCriRate(spentMen);
    }

    [Fact]
    public void ACharacterWearingNothingHasNoCriticalRate()
        => DamageCalculator.CriticalRate(Of(new ParameterContainer()),
                                         Of(new ParameterContainer())).ShouldBe(1.0);

    /// <summary>⭐ THE CRIT SLOT IS AN ACCUMULATOR LIVING IN A RATE CLUSTER, and the container already
    /// knows it: every other slot of `Item.Rate` is seeded to the eraser's 1000, while `CriDamRate` and
    /// `MagCriDamRate` are seeded to 0. That is what makes a plain <c>+=</c> in
    /// <see cref="CharacterParameters.Equip"/> correct and a 1000-relative rate wrong — an untouched slot
    /// has to mean "no crit gear", not "+100% crit".</summary>
    [Fact]
    public void TheCritSlotIsSeededToZeroWhileTheRestOfTheRateHalfIsAThousand()
    {
        var untouched = new ParameterContainer();
        var rate = untouched.Rate(StatModifier.Item);

        rate[Stat.WCmin].ShouldBe(ParameterCluster.RateIdentity);
        rate[Stat.CriDam].ShouldBe(ParameterCluster.RateIdentity);
        rate[Stat.CriDamRate].ShouldBe(0);
        rate[Stat.MagCriDamRate].ShouldBe(0);

        DamageCalculator.CriticalRate(Of(untouched), Of(new ParameterContainer())).ShouldBe(1.0);
    }

    /// <summary>Equipped crit sources SUM. Four pieces at 30/50/50/50 give 180, not a compounded rate.</summary>
    [Fact]
    public void EquippedCriticalRatesSum()
        => DamageCalculator.CriticalRate(Of(Wearing(30, 50, 50, 50)),
                                         Of(new ParameterContainer())).ShouldBe(180.0);

    /// <summary>The MEN free stat adds its own term, from `roe_FreeStatCriRate`.</summary>
    [Fact]
    public void TheMenFreeStatAddsToIt()
        => DamageCalculator.CriticalRate(Of(Wearing(30), 25), Of(new ParameterContainer())).ShouldBe(30 + FreeStatTables.MenCriRate(25));

    /// <summary>The defender's `CriticalTB` subtracts. It is one of the seven slots the rate eraser seeds
    /// at 0, which is what makes the subtraction safe against a mob that has none.</summary>
    [Fact]
    public void TheDefendersCriticalBlockSubtracts()
    {
        var defender = new ParameterContainer();
        defender.Rate(StatModifier.Item)[Stat.CriticalTB] = 40;

        DamageCalculator.CriticalRate(Of(Wearing(180)), Of(defender)).ShouldBe(140.0);
    }

    /// <summary>⭐ THE SLOT THE NAMES POINT AT IS NOT THE ONE THE ENGINE READS. `Critical` and `MACri` are
    /// never read by `roe_CriticalRate`; writing them changes nothing, which is exactly the bug this file
    /// is here to prevent.</summary>
    [Fact]
    public void TheCriticalAndMaCriSlotsAreNotTheCritPath()
    {
        var p = new ParameterContainer();
        p.Plus(StatModifier.Item)[Stat.Critical] = 500;
        p.Plus(StatModifier.Item)[Stat.MACri] = 500;

        DamageCalculator.CriticalRate(Of(p), Of(new ParameterContainer())).ShouldBe(1.0);
    }

    /// <summary>⭐⭐ AGAINST A REAL CAPTURE, and the reason the operator caught this at all.
    ///
    /// <para>`MageDamageLvl60.pcapng`'s Enchanter crits on <b>15 of 67</b> landed skill hits — 224
    /// permille. Its nine equipped items and 25 spent MEN predict <b>230</b>.</para>
    ///
    /// <para>⚠️ It only works with all NINE. This project spent three sessions reasoning about five items
    /// — the wand and four Nature armour pieces — because those were the ones the magic-attack question
    /// needed. The other four are a costume, wings, a hat and a pet, and THREE OF THEM CARRY `CriRate` 50,
    /// two of them named `[Critical]` in `ItemInfo.Name`. From the wand alone the prediction is 80
    /// permille against an observed 224; the costumes are 150 of the 180.</para>
    ///
    /// <para>The tolerance is binomial, not fudge: at p = 0.23 over 67 trials one standard deviation is
    /// about 3.4 hits, so anything from roughly 9 to 22 criticals is consistent. 15 observed against 15.4
    /// expected is the middle of that.</para></summary>
    [SkippableFact]
    public void TheCapturesEnchanterCritRateMatchesItsGear()
    {
        var shine = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        Skip.If(!File.Exists(Path.Combine(shine, "ItemInfo.shn")), "server data not present; set SHINE_DATA");
        var items = ShnFile.Load(Path.Combine(shine, "ItemInfo.shn"));

        // The nine ids `damage_buckets.py` recovers from NC_ITEM_EQUIPCHANGE_CMD.
        int[] equipped = [1521, 1523, 35738, 32413, 1804, 1522, 1520, 30817, 32409];

        var mage = new ParameterContainer();
        foreach (var id in equipped)
        {
            var row = items.Rows.First(r => ShnFile.Int(r, "ID") == id);
            CharacterParameters.Equip(mage, new EquipmentPiece(ShnFile.Str(row, "InxName"),
                                                               CriRate: ShnFile.Int(row, "CriRate")));
        }

        var predicted = DamageCalculator.CriticalRate(Of(mage, 25), Of(new ParameterContainer()));
        predicted.ShouldBe(230.0);

        // 15 of 67, and the wand alone would have predicted 80.
        const int observed = 15, landed = 67;
        var expectedCriticals = predicted * landed / 1000.0;
        Math.Abs(observed - expectedCriticals).ShouldBeLessThan(3.4 * 2,
            $"predicted {predicted} permille over {landed} hits is {expectedCriticals:F1} criticals; saw {observed}");
    }
}
