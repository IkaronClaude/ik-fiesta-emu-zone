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

    [SkippableFact]
    public void FreeStatPointsAddOnTopOfTheTableValue()
    {
        var dir = WorldDir();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var cleric = ClassParamTable.Load(Path.Combine(dir!, "ParamClericServer.txt"));

        var plain = CharacterParameters.Build(cleric, level: 20);
        var spent = CharacterParameters.Build(cleric, level: 20, new FreeStats(Str: 10, Men: 5));

        spent.Base[Stat.Str].ShouldBe(plain.Base[Stat.Str] + 10);
        spent.Base[Stat.Men].ShouldBe(plain.Base[Stat.Men] + 5);
        spent.Base[Stat.Dex].ShouldBe(plain.Base[Stat.Dex]);
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

    /// <summary>Gear folds into the Item layer.
    ///
    /// <para>⚠️ NOTE WHAT DOES NOT HAPPEN: the robe's <c>ACRate</c> of 1200 does NOT scale the result. It is
    /// stored on the Item RATE cluster, and `c_MakeTotal` never folds that cluster into the total — the only
    /// rate steps it runs are ItemPowerRate, AbnormalState, PassiveSkill and LastTune.</para>
    ///
    /// <para>This test asserted 20 first, on the assumption that an item's rate column must obviously apply.
    /// The port said 17 and the port was right — the assumption had not been read. The value of writing the
    /// combining order out as a ported sequence rather than a tidy sum is exactly that it makes this kind of
    /// assumption fail loudly. Whether the game nonetheless applies ACRate somewhere else is an open
    /// question, recorded in docs/PARAMETERS.md; it is not answered by pretending the total applies it.</para></summary>
    [SkippableFact]
    public void EquipmentAddsThroughTheItemPlusLayerAndItsRateColumnsDoNotReachTheTotal()
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
        armed[Stat.AC].ShouldBe(17);          // 5 + 12 flat; the 1.2x never runs

        // It IS recorded on the item rate cluster -- it is carried, just not folded into the total.
        var container = CharacterParameters.Build(cleric, 20, equipment:
            [new EquipmentPiece("robe", AC: 12, ACRate: 1200)]);
        container.Rate(StatModifier.Item)[Stat.AC].ShouldBe(1200);
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
