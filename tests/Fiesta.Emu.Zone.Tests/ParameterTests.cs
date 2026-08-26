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

    /// <summary>COMPLIANCE: the <see cref="Stat"/> enum against `Parameter::Cluster` as the PDB declares it.
    ///
    /// <para>This list is not a restatement of the enum — it is the field list dumped out of the PDB's TPI
    /// type stream (`tools/pdb_types.py --struct "Parameter::Cluster"`), in declaration order. Until that
    /// stream was parsed, the enum was inherited from the damage-engine port and had never been checked
    /// against anything; it is now ground truth, misspellings and all.</para>
    ///
    /// <para>If someone reorders or renames a member, this fails — which is the point, because the enum's
    /// numbering IS the memory layout.</para></summary>
    [Fact]
    public void TheStatEnumMatchesTheDeclaredClusterFields()
    {
        string[] declared =
        [
            "Str", "Con", "Dex", "Int", "Men",
            "WCmin", "WCmax", "AC", "TH", "TB",
            "MAmin", "MAmax", "MR", "MH", "MB",
            "AbsoluteAttack", "AbsoluteDefend", "AbsoluteHit", "AbsoluteBlock",
            "MoveSpeed", "HPRecover", "SPRecover", "CastingTime", "Critical",
            "PhisycalWeaponMastery", "MagicalWeaponMastery", "ShieldAC",
            "HitRate", "EvaRate", "MACri", "CriDam", "MagCriDam", "CriDamRate", "MagCriDamRate",
            "AttSpeed", "MaxHP", "MaxHP_2", "MaxSP",
            "HPAbsorption_Hitted", "SPAbsorption_Hitted", "HPAbsorption_Hit", "SPAbsorption_Hit",
            "CriticalTB", "RegistNone", "ResistPoison", "ResistDeaseas", "ResistCurse",
            "ResistMoveSpdDown", "ResistGTI", "MaxLP", "LPRecover",
        ];

        Enum.GetNames<Stat>().ShouldBe(declared);
        // Every field is a 4-byte int, so slot index times four is the byte offset the server uses.
        ((int)Stat.MaxHP * 4).ShouldBe(0x8C);
        ((int)Stat.CriticalTB * 4).ShouldBe(0xA8);
        ((int)Stat.ResistGTI * 4).ShouldBe(0xC0);
    }

    /// <summary>COMPLIANCE: `Parameter::Container`'s cluster fields, as the PDB declares them.
    ///
    /// <para>One `PureCharParam` cluster, then seven `{Plus, Rate}` pairs, then `Total`. The pair order is
    /// the <see cref="StatModifier"/> order, and it was previously INHERITED rather than verified — the docs
    /// carried an open question asking whether the naming might be off by a pair. It is not.</para></summary>
    [Fact]
    public void TheStatModifierOrderMatchesTheDeclaredContainerFields()
    {
        string[] declared = ["Item", "ItemPowerRate", "Upgrade", "WeaponTitle", "PassiveSkill", "AbnormalState", "LastTune"];
        Enum.GetNames<StatModifier>().ShouldBe(declared);

        // Declared offsets: PureCharParam +0x000, then pairs every 0x198 (two 0xCC clusters), Total +0xBF4.
        const int cluster = 0xCC;
        foreach (var (source, offset) in new[]
                 {
                     (StatModifier.Item, 0x0CC), (StatModifier.ItemPowerRate, 0x264),
                     (StatModifier.Upgrade, 0x3FC), (StatModifier.WeaponTitle, 0x594),
                     (StatModifier.PassiveSkill, 0x72C), (StatModifier.AbnormalState, 0x8C4),
                     (StatModifier.LastTune, 0xA5C),
                 })
            (cluster + (int)source * cluster * 2).ShouldBe(offset, $"{source} sits at +0x{offset:X}");

        (cluster + Enum.GetValues<StatModifier>().Length * cluster * 2).ShouldBe(0xBF4);   // Total
    }

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

    /// <summary>The rate eraser is NOT uniformly 1000: slots 42..48 are zero.
    ///
    /// <para>Read out of a live zone server's memory at 0x0DA3FA78, because both eraser globals sit in the
    /// executable's uninitialised section and the image file contains nothing for them. An earlier version
    /// of this port inferred uniform 1000 from `operator*=` skipping on 1000 — correct in general, wrong in
    /// the tail. This test exists so that inference cannot come back.</para></summary>
    [Fact]
    public void TheRateEraserZeroesTheResistanceRun()
    {
        var rate = ParameterCluster.Rate();

        foreach (var stat in ParameterCluster.RateErasedSlots)
            rate[stat].ShouldBe(0, $"{stat} is one of the zeroed slots");

        // Its neighbours on both sides are 1000, which is what makes it a bounded run rather than a tail.
        rate[Stat.SPAbsorption_Hit].ShouldBe(1000);   // slot 41
        rate[Stat.MaxLP].ShouldBe(1000);              // slot 49
        rate[Stat.LPRecover].ShouldBe(1000);          // slot 50
    }

    /// <summary>The zeroed run is contiguous, slots 42..48. Stated as indices because that is the form the
    /// memory dump came in, and it is what would break if the <see cref="Stat"/> enum were reordered.</summary>
    [Fact]
    public void TheZeroedRunIsSlots42To48()
    {
        var indices = ParameterCluster.RateErasedSlots.Select(s => (int)s).ToArray();
        indices.ShouldBe(new[] { 42, 43, 44, 45, 46, 47, 48 });
    }

    /// <summary>The plus eraser really is all zeros — 51 of them, confirmed from the same live process.</summary>
    [Fact]
    public void ThePlusEraserIsEntirelyZero()
    {
        var plus = ParameterCluster.Plus();
        foreach (var stat in Enum.GetValues<Stat>())
            plus[stat].ShouldBe(0);
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

/// <summary>The base weapon and armour slots — resolved. This class previously held a deliberately red
/// test claiming the eight per-class virtuals were "unread"; the premise was wrong, and reading them
/// turned out to be easier than assumed rather than harder.</summary>
public class BaseCombatStatTests
{
    /// <summary>`c_Storepure` fills cluster slots 5..14 from eight virtual methods, and all eight resolve to
    /// the SAME address, 0x00449600, whose entire body is:
    ///
    /// <code>xor eax, eax
    /// ret 8</code>
    ///
    /// <para>They share an address because Identical Code Folding merges functions with identical bodies —
    /// eight <c>return 0</c>s collapse into one. And no player class overrides them: across all 32
    /// `CharClass` subclasses there is exactly ONE symbol for each of WC, AC, MA, MR, TH, TB, MH and MB.</para>
    ///
    /// <para>So this is not a missing piece of the port. A player's base weapon and armour values ARE zero,
    /// and every point of them comes from equipment.</para></summary>
    [Fact]
    public void BaseWeaponAndArmourSlotsAreZeroBecauseTheClassVirtualsReturnZero()
    {
        var c = new ParameterContainer();
        CharacterParameters.StorePure(c, TinyTable(), level: 40, new FreeStats(Str: 50, Con: 50));

        foreach (var slot in new[]
                 {
                     Stat.WCmin, Stat.WCmax, Stat.AC, Stat.TH, Stat.TB,
                     Stat.MAmin, Stat.MAmax, Stat.MR, Stat.MH, Stat.MB,
                 })
            c.Base[slot].ShouldBe(0, $"{slot} should come from gear, not from the class");
    }

    /// <summary>The tail of `c_Storepure`: <c>mov eax, 0x3E8</c> into three slots, then zeroes.</summary>
    [Fact]
    public void MoveSpeedAndRecoveryStartAtOneThousand()
    {
        var c = new ParameterContainer();
        CharacterParameters.StorePure(c, TinyTable(), level: 1);

        c.Base[Stat.MoveSpeed].ShouldBe(1000);
        c.Base[Stat.HPRecover].ShouldBe(1000);
        c.Base[Stat.SPRecover].ShouldBe(1000);
        c.Base[Stat.CastingTime].ShouldBe(0);
    }

    /// <summary>`CharClass::MaxHP` is the table column PLUS five per spent Constitution point. Reading the
    /// column alone — which an earlier version of this port did — robs a character of the HP it bought.</summary>
    [Fact]
    public void SpentConstitutionPointsAreWorthFiveHpEach()
    {
        var table = TinyTable();

        var plain = CharacterParameters.Build(table, 10);
        var beefy = CharacterParameters.Build(table, 10, new FreeStats(Con: 12));

        CharacterParameters.MaxHp(table, 10, plain.MakeTotal()).ShouldBe(500);
        CharacterParameters.MaxHp(table, 10, beefy.MakeTotal()).ShouldBe(500 + 12 * 5);
    }

    [Fact]
    public void SpentMentalPowerPointsAreWorthFiveSpEach()
    {
        var table = TinyTable();
        var wise = CharacterParameters.Build(table, 10, new FreeStats(Men: 8));

        CharacterParameters.MaxSp(table, 10, wise.MakeTotal()).ShouldBe(200 + 8 * 5);
    }

    /// <summary>A two-row stand-in, so these run without a server-files tree.</summary>
    private static ClassParamTable TinyTable() => new()
    {
        ClassName = "Test",
        ByLevel = new Dictionary<int, ClassParamRow>
        {
            [1] = new(1, 5, 4, 1, 3, 4, 46, 32, 32, 15, 3, 25, 11, 1),
            [10] = new(10, 25, 17, 11, 19, 16, 500, 200, 120, 21, 13, 159, 22, 7),
            [40] = new(40, 80, 60, 30, 55, 45, 2000, 700, 400, 40, 30, 500, 40, 20),
        },
    };
}
