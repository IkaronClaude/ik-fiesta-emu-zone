using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The real per-class stat tables. Needs a server-files tree; set <c>SHINE_DATA</c> to the
/// `9Data/Shine` directory or these skip.</summary>
public class ClassParamTableTests
{
    private static string? WorldDir()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        var p = Path.Combine(root, "World");
        return Directory.Exists(p) && Directory.EnumerateFiles(p, "Param*Server.txt").Any() ? p : null;
    }

    [SkippableFact]
    public void EveryClassTableInTheDirectoryLoads()
    {
        var dir = WorldDir();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var all = ClassParamTable.LoadAll(dir!);

        all.Count.ShouldBeGreaterThan(20);
        all.Keys.ShouldContain("Cleric");
        all.Keys.ShouldContain("Fighter");

        foreach (var (name, table) in all)
        {
            table.ByLevel.ShouldNotBeEmpty($"{name} has no rows");
            table.At(1).ShouldNotBeNull($"{name} has no level 1");
            // Levels are contiguous from 1, which is what makes a direct index safe in the original.
            table.ByLevel.Keys.Min().ShouldBe(1);
        }
    }

    /// <summary>Spot values read straight out of `ParamClericServer.txt`, level 1.</summary>
    [SkippableFact]
    public void TheClericTableMatchesTheFile()
    {
        var dir = WorldDir();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var cleric = ClassParamTable.Load(Path.Combine(dir!, "ParamClericServer.txt"));
        cleric.ClassName.ShouldBe("Cleric");

        var l1 = cleric.At(1)!;
        l1.Str.ShouldBe(5);
        l1.Con.ShouldBe(4);
        l1.Int.ShouldBe(1);
        l1.Dex.ShouldBe(3);
        l1.Men.ShouldBe(4);
        l1.MaxHp.ShouldBe(46);
        l1.MaxSp.ShouldBe(32);

        // The soul-stone columns, which are per level too -- how much one stone heals, and how many fit.
        l1.SoulHp.ShouldBe(32);
        l1.MaxSoulHp.ShouldBe(15);
        l1.PriceHpStone.ShouldBe(3);
    }

    /// <summary>THE CROSSOVER. Dexterity and Intelligence sit in different places in the file than in the
    /// cluster, and `c_Storepure` is what proves which is which: it reads row offsets +0x14 and +0xC into
    /// cluster slots 2 (Dex) and 3 (Int).
    ///
    /// <para>Cleric is the right class to catch it with: at level 1 Intelligence is 1 and Dexterity is 3,
    /// so a swapped mapping produces visibly wrong numbers rather than a coincidence.</para></summary>
    [SkippableFact]
    public void DexterityAndIntelligenceLandInTheRightClusterSlots()
    {
        var dir = WorldDir();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var cleric = ClassParamTable.Load(Path.Combine(dir!, "ParamClericServer.txt"));
        var container = CharacterParameters.Build(cleric, level: 1);

        container.Base[Stat.Dex].ShouldBe(3);
        container.Base[Stat.Int].ShouldBe(1);
    }

    /// <summary>⭐ FREE-STAT POINTS DO NOT REACH THE BASE CLUSTER — the reverse of what this test used to
    /// assert.
    ///
    /// <para>It claimed <c>base = table + points</c>, from `c_Storepure` computing each primary as
    /// <c>call [vtable+0x8E0] + classRow[column]</c>. The call is real; reading it as "this stat's free
    /// allocation" is not — it is the SAME no-argument virtual invoked five times, so it cannot carry a
    /// per-stat allocation.</para>
    ///
    /// <para>`MageDamageLvl60` settles it. The character allocates 50 Int and 25 MentalPower, and its
    /// reported Int is <b>288</b>. The wire's primaries are `Total` (proved by `c_compare@Cluster`, and by
    /// the pet's equip moving them), and the level-60 Enchanter row is 273:</para>
    ///
    /// <code>
    /// (273 class row + 2 pet) * 1.05 Power of Love = 288.75 -> 288   ... matches
    ///  273 + 50 points                              = 323, x1.05    = 341   ... does not
    /// </code>
    ///
    /// <para>The points feed the DERIVED tables instead — `Int.MAAbsolute`, `Con.MaxHP`, `Men.MaxSP`,
    /// `Dex.THRate`, `Men.CriRate` — which is where `MaxHp`, `MaxSp` and the damage path now take
    /// them, each as an explicit argument rather than by inflating a primary.</para></summary>
    [SkippableFact]
    public void FreeStatPointsDoNotReachTheBaseCluster()
    {
        var dir = WorldDir();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var cleric = ClassParamTable.Load(Path.Combine(dir!, "ParamClericServer.txt"));

        var plain = CharacterParameters.Build(cleric, level: 20);
        var spent = CharacterParameters.Build(cleric, level: 20, new FreeStats(Str: 10, Men: 5));

        spent.Base[Stat.Str].ShouldBe(plain.Base[Stat.Str], "allocation does not raise the primary");
        spent.Base[Stat.Men].ShouldBe(plain.Base[Stat.Men]);
        spent.Base[Stat.Dex].ShouldBe(plain.Base[Stat.Dex]);

        // ...but the HP and SP they buy are still real, taken from the free-stat table.
        CharacterParameters.MaxHp(cleric, 20, spent.MakeTotal(), freeStatConPoints: 4)
            .ShouldBe(CharacterParameters.MaxHp(cleric, 20, plain.MakeTotal())
                      + 4 * CharacterParameters.HpPerConstitutionPoint);
    }

    /// <summary>`c_Storepure` clamps the level against 0x96 before indexing the class's per-level array.</summary>
    [SkippableFact]
    public void LevelsBeyondTheTableClampRatherThanThrow()
    {
        var dir = WorldDir();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var cleric = ClassParamTable.Load(Path.Combine(dir!, "ParamClericServer.txt"));
        CharacterParameters.MaxTableLevel.ShouldBe(0x96);

        var top = CharacterParameters.Build(cleric, cleric.MaxLevel);
        var beyond = CharacterParameters.Build(cleric, 9999);

        beyond.Base[Stat.Str].ShouldBe(top.Base[Stat.Str]);
    }

    /// <summary>Gear's flat bonuses go to Item.Plus and its rate columns to ItemPowerRate.Rate — and BOTH
    /// reach the total.
    ///
    /// <para>⚠️ This test previously asserted the opposite, and was wrong in a way worth recording. It read
    /// <c>armed[Stat.AC].ShouldBe(17)</c> — flat only — on the theory that an item's `ACRate` lands on
    /// `Item.Rate`, one of the five clusters `c_MakeTotal` skips. The mapping came from the shared "Item"
    /// prefix rather than from reading the equip path, so the test was encoding the bug rather than catching
    /// it: item rate bonuses did nothing at all.</para>
    ///
    /// <para>`ShinePlayer::so_RecalcEquipParam` writes `ItemPowerRate.Rate` for AC, MR, WCmin, WCmax, MAmin
    /// and MAmax, and `roe_AC` reads that cluster and never Item.Rate. `c_MakeTotal` applies
    /// ItemPowerRate.Rate as its FIRST rate step, so the bonus lands.</para></summary>
    [SkippableFact]
    public void ItemRateColumnsScaleTheTotalThroughItemPowerRate()
    {
        var dir = WorldDir();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var cleric = ClassParamTable.Load(Path.Combine(dir!, "ParamClericServer.txt"));
        var bare = CharacterParameters.Build(cleric, 20).MakeTotal();

        var armed = CharacterParameters.Build(cleric, 20, equipment:
        [
            new EquipmentPiece("mace", MinWC: 10, MaxWC: 20, AC: 5),
            new EquipmentPiece("robe", AC: 12, ACRate: 1200),
        ]).MakeTotal();

        armed[Stat.WCmax].ShouldBe(bare[Stat.WCmax] + 20);

        // 5 + 12 flat, then x1.2 from the robe's ACRate -> 20 (17 * 1200 / 1000, truncated).
        armed[Stat.AC].ShouldBe(20);

        // And it is carried on ItemPowerRate, not on Item's own rate half.
        var container = CharacterParameters.Build(cleric, 20, equipment:
            [new EquipmentPiece("robe", AC: 12, ACRate: 1200)]);
        container.Rate(StatModifier.ItemPowerRate)[Stat.AC].ShouldBe(1200);
        container.Rate(StatModifier.Item)[Stat.AC].ShouldBe(ParameterCluster.RateIdentity);
    }

    /// <summary>A buff, by contrast, DOES scale gear -- AbnormalState.Rate is one of the four rate steps.</summary>
    [SkippableFact]
    public void ABuffScalesGearBecauseItsRateLayerIsInTheTotal()
    {
        var dir = WorldDir();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var cleric = ClassParamTable.Load(Path.Combine(dir!, "ParamClericServer.txt"));
        var c = CharacterParameters.Build(cleric, 20, equipment: [new EquipmentPiece("mace", MaxWC: 100)]);
        c.Rate(StatModifier.AbnormalState)[Stat.WCmax] = 1500;

        c.MakeTotal()[Stat.WCmax].ShouldBe(150);
    }
}
