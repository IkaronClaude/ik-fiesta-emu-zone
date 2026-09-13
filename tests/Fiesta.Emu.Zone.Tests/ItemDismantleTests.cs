using Fiesta.Emu.Zone.Item;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Dismantling enhanced equipment into Karis, from `sp_NC_ITEM_DISMANTLE_REQ` (0x5293C0).</summary>
public class ItemDismantleTests
{
    private static string? Shine()
    {
        var d = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(d, "ItemDismantle.shn")) ? d : null;
    }

    /// <summary>13 rows, one per upgrade level 0..12, read by row index.</summary>
    [SkippableFact]
    public void TheTableIsOneRowPerUpgradeLevel()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var table = ItemDismantleTable.Load(shine!);

        table.Rows.Count.ShouldBe(13);
        for (var level = 0; level < 13; level++)
            table[level]!.ID.ShouldBe(level);
        table[13].ShouldBeNull();
    }

    /// <summary>Each class block is five wide, one entry per item `Grade` 1..5 -- the `unsigned long[5]`
    /// arrays the PDB declares at +0x02, +0x16, +0x2a, +0x3e and +0x52.</summary>
    [SkippableFact]
    public void EachClassBlockHoldsFiveGrades()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var table = ItemDismantleTable.Load(shine!);

        foreach (var row in table.Rows)
        {
            row.Armor.Count.ShouldBe(5);
            row.Boot.Count.ShouldBe(5);
            row.Shield.Count.ShouldBe(5);
            row.Weapon.Count.ShouldBe(5);
            row.Amulet.Count.ShouldBe(5);
        }
    }

    /// <summary>The yield rises with both the upgrade level and the item grade, and a +0 item yields
    /// nothing at any grade.</summary>
    [SkippableFact]
    public void TheYieldRisesWithLevelAndGrade()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var table = ItemDismantleTable.Load(shine!);

        table[0]!.Weapon.ShouldAllBe(v => v == 0);
        table[12]!.Weapon[4].ShouldBeGreaterThan(table[9]!.Weapon[4]);
        table[12]!.Weapon[4].ShouldBeGreaterThan(table[12]!.Weapon[2]);
    }

    /// <summary>`sii_Karis` is the only product the dismantle path ever makes; the table supplies the
    /// count. Resolved here by `InxName` since this port has no server config to seed the id.</summary>
    [SkippableFact]
    public void TheProductIsAlwaysKaris()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);

        box.SpecialId.Karis.ShouldBeGreaterThan(0);
        box[box.SpecialId.Karis]!.InxName.ShouldBe("Karis");
        box[box.SpecialId.Karis]!.Grade.ShouldBe(0);
    }

    /// <summary>The jump table at 0x5298DC maps Class 4,5,6,7,8 onto Amulet, Weapon, Armor, Shield, Boot.
    /// Checked exhaustively over every level and grade, since the mapping is the whole content of that
    /// table and a swapped pair would still "work" on any single sample.</summary>
    [SkippableTheory]
    [InlineData(ItemClassEnum.Amulet)]
    [InlineData(ItemClassEnum.Weapon)]
    [InlineData(ItemClassEnum.Armor)]
    [InlineData(ItemClassEnum.Shield)]
    [InlineData(ItemClassEnum.Boot)]
    public void EveryEquipmentClassResolvesToItsOwnBlock(int itemClass)
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var table = ItemDismantleTable.Load(shine!);

        // UpLimit is pinned to 12 so the level gate passes at every level -- that gate has its own test,
        // and only a handful of classes ship an item that reaches 12.
        var found = box.Items.Values.FirstOrDefault(i => i.Class == itemClass && i.UpLimit > 0);
        Skip.If(found is null, $"no class-{itemClass} item in ItemInfo");
        var template = found! with { UpLimit = 12 };

        for (var grade = 1; grade <= 5; grade++)
            for (byte level = 0; level <= 12; level++)
            {
                var row = table[level]!;
                var block = itemClass switch
                {
                    ItemClassEnum.Amulet => row.Amulet,
                    ItemClassEnum.Weapon => row.Weapon,
                    ItemClassEnum.Armor => row.Armor,
                    ItemClassEnum.Shield => row.Shield,
                    _ => row.Boot,
                };
                var expected = block[grade - 1];

                table.Produce(template with { Grade = grade }, level, out var karis);
                karis.ShouldBe(expected, $"class {itemClass} grade {grade} at +{level}");
            }
    }

    /// <summary>Boots are zero above +9 across every grade, so a +10 or better boot dismantles to nothing.
    /// Recorded because it looks like a port bug until you read the column.</summary>
    [SkippableFact]
    public void BootsYieldNothingAbovePlusNine()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var table = ItemDismantleTable.Load(shine!);

        for (var level = 10; level <= 12; level++)
            table[level]!.Boot.ShouldAllBe(v => v == 0);
        table[9]!.Boot.ShouldContain(v => v > 0);
    }

    /// <summary>A stone is not equipment and has no block.</summary>
    [SkippableFact]
    public void ANonEquipmentClassIsRefused()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var table = ItemDismantleTable.Load(shine!);

        var stone = box.Items.Values.First(i => i.Class == ItemClassEnum.UpgradeSource);
        table.Produce(stone, 0, out _).ShouldBe(ItemDismantleResult.NotDismantleable);
        table.Produce(null, 0, out _).ShouldBe(ItemDismantleResult.NoSuchItem);
    }

    /// <summary>`cmp al, 0xc / ja` then `cmp al, cl / jb`: `UpLimit` must be at most 12 and not below the
    /// item's current level.</summary>
    [SkippableFact]
    public void TheUpgradeLimitGatesBothWays()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var table = ItemDismantleTable.Load(shine!);

        var info = box.Items.Values.First(i =>
            i.Class == ItemClassEnum.Weapon && i.Grade == 5 && i.UpLimit == 12);

        table.Produce(info, upgradeLevel: 13, out _).ShouldBe(ItemDismantleResult.BadUpgradeLimit);

        var tooHigh = info with { UpLimit = 13 };
        table.Produce(tooHigh, upgradeLevel: 0, out _).ShouldBe(ItemDismantleResult.BadUpgradeLimit);
    }

    /// <summary>Grade must be 1..5; the table has no column for anything else, including the grade-6
    /// items the upgrade path does recognise.</summary>
    [SkippableFact]
    public void GradeOutsideOneToFiveIsRefused()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var table = ItemDismantleTable.Load(shine!);

        var info = box.Items.Values.First(i =>
            i.Class == ItemClassEnum.Weapon && i.Grade == 5 && i.UpLimit == 12);

        table.Produce(info with { Grade = 6 }, 5, out _).ShouldBe(ItemDismantleResult.BadGrade);
        table.Produce(info with { Grade = 0 }, 5, out _).ShouldBe(ItemDismantleResult.BadGrade);
    }

    /// <summary>A combination the table zeroes yields nothing -- error 0x168a on the wire.</summary>
    [SkippableFact]
    public void AZeroTableEntryYieldsNoProduct()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var table = ItemDismantleTable.Load(shine!);

        var info = box.Items.Values.First(i =>
            i.Class == ItemClassEnum.Weapon && i.Grade == 5 && i.UpLimit == 12);

        table.Produce(info, upgradeLevel: 0, out var karis).ShouldBe(ItemDismantleResult.NoProduct);
        karis.ShouldBe(0);
    }
}
