using Fiesta.Emu.Zone.Item;
using Fiesta.Emu.Zone.Random;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The enhancement chain, checked against the real tables and branch by branch.
///
/// <para>These are DATA and BRANCH tests, not oracle differentials. `Item_Upgrade` reaches
/// `ItemDataBox`, three `CDataReader` tables and an `ItemBag` through vtables, so standing it up under
/// emulation would mean rebuilding the server's data model in emulated memory. Where the arithmetic is
/// self-contained -- the table indexing, the offset table, the per-mille split -- it is checked against
/// the shipped files, which is the same ground truth the binary reads.</para></summary>
public class ItemUpgradeTests
{
    private static string? Shine()
    {
        var d = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(d, "ItemUpgrade.shn")) ? d : null;
    }

    private sealed class Bag : IItemBag
    {
        private readonly Dictionary<byte, ShineItemStruct> _slots = new();
        public Bag Put(byte slot, ShineItemStruct item) { _slots[slot] = item; return this; }
        public ShineItemStruct? At(byte slot) => _slots.TryGetValue(slot, out var i) ? i : null;
    }

    private static cWell512Random Seeded(int seed)
    {
        var state = new uint[16];
        var r = new System.Random(seed);
        for (var i = 0; i < 16; i++) state[i] = (uint)r.Next(int.MinValue, int.MaxValue);
        return new cWell512Random(state);
    }

    /// <summary>A generator whose first draws satisfy a predicate. The port takes the concrete
    /// `cWell512Random` (as the rest of this repo does), so a branch is selected by finding a seed rather
    /// than by injecting a fake.</summary>
    private static cWell512Random RngWhere(Func<cWell512Random, bool> accepts)
    {
        for (var seed = 1; seed < 500_000; seed++)
            if (accepts(Seeded(seed)))
                return Seeded(seed);
        throw new InvalidOperationException("no seed produced the wanted draw sequence");
    }

    private static int IdOf(ItemDataBox box, string inxName)
        => box.Items.Values.First(i => string.Equals(i.InxName, inxName, StringComparison.OrdinalIgnoreCase)).ID;

    // -- tables ------------------------------------------------------------------------------------

    /// <summary>`esi = level + Grade*12 - 12`, and `GetRecord` is a row INDEX. The file's `ID` column is
    /// the grade, so every row reached for grade G must carry ID == G.</summary>
    [SkippableFact]
    public void ItemUpgradeIsIndexedByLevelPlusTwelvePerGrade()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var tables = UpgradeTables.Load(shine!);

        tables.ItemUpgradeRows.Count.ShouldBe(72);
        for (var grade = 1; grade <= 6; grade++)
            for (var level = 0; level < 12; level++)
                tables.GetItemUpgrade(grade, level)!.ID.ShouldBe(grade);
    }

    /// <summary>`LoadBRAccUpgradeData` runs only TWO prefix-sum steps, so grades 1-3 get correct start
    /// offsets. The shipped file holds exactly three grades of 10, 15 and 20 rows.</summary>
    [SkippableFact]
    public void BraceletOffsetsAreTheTwoStepPrefixSum()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var tables = UpgradeTables.Load(shine!);

        tables.BRAccUpgrade.Count.ShouldBe(45);
        tables.BraceletGradeOffset[1].ShouldBe(0);
        tables.BraceletGradeOffset[2].ShouldBe(10);
        tables.BraceletGradeOffset[3].ShouldBe(25);

        tables.GetBRAccUpgrade(1, demandLv: 0, level: 0)!.ID.ShouldBe(1);
        tables.GetBRAccUpgrade(2, demandLv: 0, level: 0)!.ID.ShouldBe(2);
        tables.GetBRAccUpgrade(3, demandLv: 0, level: 19)!.ID.ShouldBe(3);
        tables.GetBRAccUpgrade(0, 0, 0).ShouldBeNull();
    }

    /// <summary>`demandLv` is argument 2 and the original never reads it.</summary>
    [SkippableFact]
    public void BraceletLookupIgnoresDemandLevel()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var t = UpgradeTables.Load(shine!);
        t.GetBRAccUpgrade(2, 0, 3).ShouldBe(t.GetBRAccUpgrade(2, 999, 3));
    }

    // -- source validation -------------------------------------------------------------------------

    /// <summary>Karis fits any grade because its own `Grade` is 0, and the zero-grade escape in
    /// `Item_IsUpSource` skips the match. There is no Karis-specific branch in the binary.</summary>
    [SkippableFact]
    public void KarisIsAWildcardOnGradeAndCoversNineToEleven()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var karis = IdOf(box, "Karis");
        box[karis]!.Grade.ShouldBe(0);

        foreach (var grade in new[] { 1, 2, 3, 4, 5 })
        {
            var target = box.Items.Values.First(i =>
                i.Grade == grade && i.Class == ItemClassEnum.Weapon && i.UpLimit > 0);

            ItemUpgrade.Item_IsUpSource(box, target.ID, karis, 9).ShouldBeTrue();
            ItemUpgrade.Item_IsUpSource(box, target.ID, karis, 11).ShouldBeTrue();
            ItemUpgrade.Item_IsUpSource(box, target.ID, karis, 8).ShouldBeFalse();
            ItemUpgrade.Item_IsUpSource(box, target.ID, karis, 12).ShouldBeFalse();
        }
    }

    /// <summary>The tier windows are just `UpLimit`..`UpResource` on the stone, and a non-zero grade must
    /// match the item exactly.</summary>
    [SkippableTheory]
    [InlineData("El1", 1, 0, 2)]
    [InlineData("Lix1", 1, 3, 5)]
    [InlineData("Xir1", 1, 6, 8)]
    [InlineData("El5", 5, 0, 2)]
    [InlineData("Xir5", 5, 6, 8)]
    public void StoneTiersAreTheirOwnLevelWindow(string stone, int grade, int min, int max)
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var id = IdOf(box, stone);
        var target = box.Items.Values.First(i =>
            i.Grade == grade && i.Class == ItemClassEnum.Weapon && i.UpLimit > 0);
        var other = box.Items.Values.First(i =>
            i.Grade != grade && i.Grade > 0 && i.Class == ItemClassEnum.Weapon && i.UpLimit > 0);

        ItemUpgrade.Item_IsUpSource(box, target.ID, id, (byte)min).ShouldBeTrue();
        ItemUpgrade.Item_IsUpSource(box, target.ID, id, (byte)max).ShouldBeTrue();
        if (min > 0)
            ItemUpgrade.Item_IsUpSource(box, target.ID, id, (byte)(min - 1)).ShouldBeFalse();
        ItemUpgrade.Item_IsUpSource(box, target.ID, id, (byte)(max + 1)).ShouldBeFalse();

        // a graded stone does NOT wildcard
        ItemUpgrade.Item_IsUpSource(box, other.ID, id, (byte)min).ShouldBeFalse();
    }

    /// <summary>`Item_IsUpSourceLeftRight` takes no slot argument: red, blue and gold are interchangeable
    /// in all three positions as far as the server is concerned. The retail client locks the order; this
    /// records that the server does not.</summary>
    [SkippableFact]
    public void AnyGemClassIsAcceptedInAnySlot()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);

        foreach (var cls in new[]
                 {
                     ItemClassEnum.UpgradeRedGem, ItemClassEnum.UpgradeBlueGem, ItemClassEnum.UpgradeGoldGem,
                 })
        {
            var gem = box.Items.Values.First(i => i.Class == cls && i.Grade == 5);
            var target = box.Items.Values.First(i =>
                i.Grade == 5 && i.Class == ItemClassEnum.Weapon && i.UpLimit > 0);
            var level = (byte)Math.Max((int)gem.UpLimit, 1);

            ItemUpgrade.Item_IsUpSourceLeftRight(box, target.ID, gem.ID, level)
                .ShouldBeTrue($"class {cls} should validate for every slot");
        }
    }

    /// <summary>Unlike the centre stone there is no zero-grade wildcard for gems.</summary>
    [SkippableFact]
    public void GemGradeMustMatchExactly()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var gem = box.Items.Values.First(i => i.Class == ItemClassEnum.UpgradeRedGem && i.Grade == 5);
        var wrong = box.Items.Values.First(i =>
            i.Grade == 3 && i.Class == ItemClassEnum.Weapon && i.UpLimit > 0);

        ItemUpgrade.Item_IsUpSourceLeftRight(box, wrong.ID, gem.ID, 5).ShouldBeFalse();
    }

    // -- outcomes ----------------------------------------------------------------------------------

    private sealed record Fixture(
        ItemDataBox Box, UpgradeTables Tables, Bag Bag, ShineItemStruct Item, byte Target, byte Stone);

    private static Fixture Build(string shine, int grade, byte level, string stone)
    {
        var box = ItemDataBox.Load(shine);
        var tables = UpgradeTables.Load(shine);
        var target = box.Items.Values.First(i =>
            i.Grade == grade && i.Class == ItemClassEnum.Weapon && i.UpLimit >= 10);
        var item = new ShineItemStruct { ItemId = target.ID, UpgradeLevel = level };
        var stoneItem = new ShineItemStruct { ItemId = IdOf(box, stone) };
        var bag = new Bag().Put(0, item).Put(1, stoneItem);
        return new Fixture(box, tables, bag, item, 0, 1);
    }

    /// <summary>`cmp [ebp+0xf], al / ja` -- the limit must be strictly above the current level, so an item
    /// sitting at its cap reports 1 rather than rolling.</summary>
    [SkippableFact]
    public void AnItemAtItsLimitIsRefusedBeforeAnyRoll()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var f = Build(shine!, grade: 1, level: 0, stone: "El1");
        f.Item.UpgradeLevel = (byte)f.Box[f.Item.ItemId]!.UpLimit;

        ItemUpgrade.Item_Upgrade(f.Box, f.Tables, Seeded(1), f.Bag,
                f.Target, f.Stone, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot)
            .ShouldBe(ItemUpgradeResult.AlreadyAtLimit);
    }

    /// <summary>A stone outside its level window is rejected as a source, not rolled.</summary>
    [SkippableFact]
    public void AStoneOutsideItsWindowIsABadSource()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var f = Build(shine!, grade: 1, level: 7, stone: "El1");     // Elrue covers 0..2 only

        ItemUpgrade.Item_Upgrade(f.Box, f.Tables, Seeded(1), f.Bag,
                f.Target, f.Stone, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot)
            .ShouldBe(ItemUpgradeResult.BadUpgradeSource);
    }

    /// <summary>Grade 1 at +0 with Elrue: the row is 0/0/268 against a bonus of 120 + 150 - DemandLv, so
    /// the threshold lands at or below zero and every roll succeeds.</summary>
    [SkippableFact]
    public void TheFirstStepOnGradeOneIsEffectivelyCertain()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var f = Build(shine!, grade: 1, level: 0, stone: "El1");

        var successes = 0;
        for (var seed = 1; seed <= 200; seed++)
        {
            var r = ItemUpgrade.Item_Upgrade(f.Box, f.Tables, Seeded(seed), f.Bag,
                f.Target, f.Stone, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot);
            if (r is ItemUpgradeResult.Success or ItemUpgradeResult.LuckySuccess) successes++;
        }
        successes.ShouldBe(200);
    }

    /// <summary>Every failure bucket is reachable at a high level, and destruction only when the CriFail
    /// weight is non-zero.</summary>
    [SkippableFact]
    public void AllThreeFailureBucketsAreReachable()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var f = Build(shine!, grade: 1, level: 7, stone: "Xir1");   // row 8: 230 / 718 / 50

        var seen = new HashSet<ItemUpgradeResult>();
        for (var seed = 1; seed <= 4000; seed++)
            seen.Add(ItemUpgrade.Item_Upgrade(f.Box, f.Tables, Seeded(seed), f.Bag,
                f.Target, f.Stone, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot));

        seen.ShouldContain(ItemUpgradeResult.Success);
        seen.ShouldContain(ItemUpgradeResult.FailDestroyed);
        seen.ShouldContain(ItemUpgradeResult.FailDowngrade);
        seen.ShouldContain(ItemUpgradeResult.FailNoChange);
    }

    /// <summary>A `Perfect` gem in the LEFT slot has UpSucRatio 1000, so a CriFail can never destroy.</summary>
    [SkippableFact]
    public void APerfectLeftGemRemovesDestructionEntirely()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var tables = UpgradeTables.Load(shine!);

        var gem = box.Items.Values.First(i =>
            i.Class == ItemClassEnum.UpgradeRedGem && i.Grade == 1 && i.UpSucRatio == 1000);
        var target = box.Items.Values.First(i =>
            i.Grade == 1 && i.Class == ItemClassEnum.Weapon && i.UpLimit >= 10);

        var item = new ShineItemStruct { ItemId = target.ID, UpgradeLevel = 7 };
        var bag = new Bag()
            .Put(0, item)
            .Put(1, new ShineItemStruct { ItemId = IdOf(box, "Xir1") })
            .Put(2, new ShineItemStruct { ItemId = gem.ID });

        for (var seed = 1; seed <= 2000; seed++)
            ItemUpgrade.Item_Upgrade(box, tables, Seeded(seed), bag,
                    0, 1, leftSlot: 2, rightSlot: ItemUpgrade.NoSlot, middleSlot: ItemUpgrade.NoSlot)
                .ShouldNotBe(ItemUpgradeResult.FailDestroyed);
    }

    /// <summary>At +0 there is nothing to take away, so a DownFail degrades to "nothing happened".</summary>
    [SkippableFact]
    public void ADowngradeAtLevelZeroBecomesNoChange()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var f = Build(shine!, grade: 5, level: 0, stone: "El5");

        for (var seed = 1; seed <= 3000; seed++)
            ItemUpgrade.Item_Upgrade(f.Box, f.Tables, Seeded(seed), f.Bag,
                    f.Target, f.Stone, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot)
                .ShouldNotBe(ItemUpgradeResult.FailDowngrade);
    }

    /// <summary>The pity term: more prior failures lowers the threshold, so the success rate must rise
    /// monotonically with the stored fail count.</summary>
    [SkippableFact]
    public void MoreFailuresRaiseTheSuccessRate()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var f = Build(shine!, grade: 1, level: 7, stone: "Xir1");

        int RateAt(byte failCount)
        {
            var n = 0;
            for (var seed = 1; seed <= 1500; seed++)
            {
                f.Item.UpgradeLevel = 7;
                f.Item.FailCount = failCount;
                var r = ItemUpgrade.Item_Upgrade(f.Box, f.Tables, Seeded(seed), f.Bag,
                    f.Target, f.Stone, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot);
                if (r is ItemUpgradeResult.Success or ItemUpgradeResult.LuckySuccess) n++;
            }
            return n;
        }

        RateAt(0).ShouldBeLessThan(RateAt(10));
        RateAt(10).ShouldBeLessThan(RateAt(30));
    }

    /// <summary>Left and right scale the pity term by 13 and 12 then divide by 10, so both together beat
    /// either alone. Checked on the threshold's effect rather than on the intermediate.</summary>
    [SkippableFact]
    public void SideGemsAmplifyThePityTerm()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var tables = UpgradeTables.Load(shine!);

        var red = box.Items.Values.First(i => i.Class == ItemClassEnum.UpgradeRedGem && i.Grade == 1);
        var blue = box.Items.Values.First(i => i.Class == ItemClassEnum.UpgradeBlueGem && i.Grade == 1);
        var target = box.Items.Values.First(i =>
            i.Grade == 1 && i.Class == ItemClassEnum.Weapon && i.UpLimit >= 10);

        int Rate(byte left, byte right)
        {
            var n = 0;
            for (var seed = 1; seed <= 1500; seed++)
            {
                var item = new ShineItemStruct { ItemId = target.ID, UpgradeLevel = 7, FailCount = 20 };
                var bag = new Bag()
                    .Put(0, item)
                    .Put(1, new ShineItemStruct { ItemId = IdOf(box, "Xir1") })
                    .Put(2, new ShineItemStruct { ItemId = red.ID })
                    .Put(3, new ShineItemStruct { ItemId = blue.ID });
                var r = ItemUpgrade.Item_Upgrade(box, tables, Seeded(seed), bag,
                    0, 1, left, right, ItemUpgrade.NoSlot);
                if (r is ItemUpgradeResult.Success or ItemUpgradeResult.LuckySuccess) n++;
            }
            return n;
        }

        var none = Rate(ItemUpgrade.NoSlot, ItemUpgrade.NoSlot);
        var both = Rate(2, 3);
        both.ShouldBeGreaterThan(none);
    }

    // -- the +2 -------------------------------------------------------------------------------------

    /// <summary>The lucky roll is taken only after a success, and only a Lucky stone or a GoldNine carries
    /// any `UpLuckRatio` at all.</summary>
    [SkippableFact]
    public void OnlyLuckyStonesAndGoldGemsCanProduceAPlusTwo()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);

        box[IdOf(box, "El1")]!.UpLuckRatio.ShouldBe(0);
        box[IdOf(box, "BlessEl1")]!.UpLuckRatio.ShouldBe(0);
        box[IdOf(box, "LuckyEl1")]!.UpLuckRatio.ShouldBe(350);
        box.Items.Values.Where(i => i.Class == ItemClassEnum.UpgradeGoldGem)
            .ShouldAllBe(i => i.UpLuckRatio == 50);
        box.Items.Values.Where(i => i.Class == ItemClassEnum.UpgradeRedGem)
            .ShouldAllBe(i => i.UpLuckRatio == 0);
    }

    /// <summary>A Lucky stone at +0 on grade 1 produces +2 outcomes; the plain stone never does.</summary>
    [SkippableFact]
    public void ALuckyStoneProducesPlusTwo()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        HashSet<ItemUpgradeResult> Run(string stone)
        {
            var f = Build(shine!, grade: 1, level: 0, stone: stone);
            var seen = new HashSet<ItemUpgradeResult>();
            for (var seed = 1; seed <= 600; seed++)
            {
                f.Item.UpgradeLevel = 0;
                seen.Add(ItemUpgrade.Item_Upgrade(f.Box, f.Tables, Seeded(seed), f.Bag,
                    f.Target, f.Stone, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot));
            }
            return seen;
        }

        Run("LuckyEl1").ShouldContain(ItemUpgradeResult.LuckySuccess);
        Run("El1").ShouldNotContain(ItemUpgradeResult.LuckySuccess);
    }

    /// <summary>`level + 2 <= UpLimit` -- one step below the cap the lucky roll still fires but can only
    /// pay out +1.</summary>
    [SkippableFact]
    public void PlusTwoNeedsHeadroomUnderTheItemsUpLimit()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = ItemDataBox.Load(shine!);
        var tables = UpgradeTables.Load(shine!);

        // UpLimit 7 is the lowest cap on real gear, which puts cap-1 (+6) inside LuckyXir's 6..8 window
        // and cap-2 (+5) inside LuckyLix's 3..5 -- so both sides of the rule are reachable on one item.
        var target = box.Items.Values.First(i =>
            i.Grade == 5 && i.Class == ItemClassEnum.Weapon && i.UpLimit == 7);

        HashSet<ItemUpgradeResult> Run(byte level, string stone)
        {
            var seen = new HashSet<ItemUpgradeResult>();
            for (var seed = 1; seed <= 3000; seed++)
            {
                var item = new ShineItemStruct { ItemId = target.ID, UpgradeLevel = level };
                var bag = new Bag().Put(0, item).Put(1, new ShineItemStruct { ItemId = IdOf(box, stone) });
                seen.Add(ItemUpgrade.Item_Upgrade(box, tables, Seeded(seed), bag,
                    0, 1, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot, ItemUpgrade.NoSlot));
            }
            return seen;
        }

        // +6 -> +8 would overshoot UpLimit 7, so the lucky roll can only ever pay out +1.
        Run(6, "LuckyXir5").ShouldNotContain(ItemUpgradeResult.LuckySuccess);
        // +5 -> +7 fits exactly, so it can.
        Run(5, "LuckyLix5").ShouldContain(ItemUpgradeResult.LuckySuccess);
    }

    // -- applying the outcome -----------------------------------------------------------------------

    /// <summary>Success resets the fail count; both failure outcomes add one. This is what makes the
    /// `nCon * failCount` term a pity counter rather than an arbitrary multiplier.</summary>
    [Fact]
    public void SuccessResetsTheFailCountAndFailureIncrementsIt()
    {
        var item = new ShineItemStruct { ItemId = 1, UpgradeLevel = 4, FailCount = 7 };

        ItemUpgrade.Apply(item, ItemUpgradeResult.FailNoChange);
        item.FailCount.ShouldBe((byte)8);
        item.UpgradeLevel.ShouldBe((byte)4);

        ItemUpgrade.Apply(item, ItemUpgradeResult.FailDowngrade);
        item.FailCount.ShouldBe((byte)9);
        item.UpgradeLevel.ShouldBe((byte)3);

        ItemUpgrade.Apply(item, ItemUpgradeResult.Success);
        item.FailCount.ShouldBe((byte)0);
        item.UpgradeLevel.ShouldBe((byte)4);

        ItemUpgrade.Apply(item, ItemUpgradeResult.LuckySuccess);
        item.UpgradeLevel.ShouldBe((byte)6);

        ItemUpgrade.Apply(item, ItemUpgradeResult.FailDestroyed);
        item.ItemId.ShouldBe(0);
    }

    /// <summary>Keeps the enum pinned to the binary's `bl + 3`.</summary>
    [Fact]
    public void ResultCodesMatchTheBinary()
    {
        ((byte)ItemUpgradeResult.Success).ShouldBe((byte)3);
        ((byte)ItemUpgradeResult.LuckySuccess).ShouldBe((byte)4);
        ((byte)ItemUpgradeResult.FailNoChange).ShouldBe((byte)5);
        ((byte)ItemUpgradeResult.FailDowngrade).ShouldBe((byte)6);
        ((byte)ItemUpgradeResult.FailDestroyed).ShouldBe((byte)7);
    }
}
