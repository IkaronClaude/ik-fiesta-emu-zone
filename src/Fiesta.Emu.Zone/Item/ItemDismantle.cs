using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Item;

/// <summary>`ItemDismantle` (PDB, size 102) -- one row of `ItemDismantle.shn`, keyed by the item's CURRENT
/// upgrade level. Each class block holds five entries, one per item `Grade` 1..5.
///
/// <para>The values are QUANTITIES, not item ids. The product is always Karis: the id comes from a single
/// global, `ItemDataBox::idb_specialid + 0x3a`, which the PDB names `sii_Karis`.</para></summary>
public sealed record ItemDismantle(
    int ID,
    int Grade,
    IReadOnlyList<int> Armor,    // +0x02, Class 6
    IReadOnlyList<int> Boot,     // +0x16, Class 8
    IReadOnlyList<int> Shield,   // +0x2a, Class 7
    IReadOnlyList<int> Weapon,   // +0x3e, Class 5
    IReadOnlyList<int> Amulet);  // +0x52, Class 4

/// <summary>Why a dismantle was refused. The wire codes are the 0x168n family in
/// `sp_NC_ITEM_DISMANTLE_REQ`.</summary>
public enum ItemDismantleResult
{
    Ok,
    /// <summary>No such item, or its `ItemInfo` is missing (0x1686).</summary>
    NoSuchItem,
    /// <summary>`ItemInfo.Class` is not one of 4..8.</summary>
    NotDismantleable,
    /// <summary>`ItemInfo.UpLimit` above 12, or below the item's current level.</summary>
    BadUpgradeLimit,
    /// <summary>`ItemInfo.Grade` outside 1..5.</summary>
    BadGrade,
    /// <summary>The table yields 0 for this combination (0x168a).</summary>
    NoProduct,
}

/// <summary>`ItemDismantleProducer` / `sp_NC_ITEM_DISMANTLE_REQ` (0x5293C0) -- turning enhanced equipment
/// back into Karis.</summary>
public sealed class ItemDismantleTable
{
    /// <summary>The highest `UpLimit` a dismantleable item may declare (`cmp al, 0xc / ja`).</summary>
    public const int MaxUpgradeLimit = 12;

    public required IReadOnlyList<ItemDismantle> Rows { get; init; }

    /// <summary>`GetRecord(gItemDismantle, currentUpgradeLevel)` -- by ROW INDEX, as everywhere else.</summary>
    public ItemDismantle? this[int level] => (uint)level < (uint)Rows.Count ? Rows[level] : null;

    /// <summary>How many Karis this item yields, and why not when it yields none.
    ///
    /// <para>The class selects the column block through the jump table at 0x5298DC; the item's `Grade`
    /// selects within it. Both the level gate and the grade range are the binary's.</para></summary>
    public ItemDismantleResult Produce(ItemInfo? info, byte upgradeLevel, out int karisCount)
    {
        karisCount = 0;
        if (info is null) return ItemDismantleResult.NoSuchItem;

        var block = info.Class switch
        {
            ItemClassEnum.Amulet => Amulet(),
            ItemClassEnum.Weapon => Weapon(),
            ItemClassEnum.Armor => Armor(),
            ItemClassEnum.Shield => Shield(),
            ItemClassEnum.Boot => Boot(),
            _ => null,
        };
        if (block is null) return ItemDismantleResult.NotDismantleable;

        if (info.UpLimit > MaxUpgradeLimit) return ItemDismantleResult.BadUpgradeLimit;
        if (info.UpLimit < upgradeLevel) return ItemDismantleResult.BadUpgradeLimit;

        var row = this[upgradeLevel];
        if (row is null) return ItemDismantleResult.NoSuchItem;

        if (info.Grade < 1 || info.Grade > 5) return ItemDismantleResult.BadGrade;

        karisCount = block(row)[info.Grade - 1];
        return karisCount != 0 ? ItemDismantleResult.Ok : ItemDismantleResult.NoProduct;

        static Func<ItemDismantle, IReadOnlyList<int>> Amulet() => r => r.Amulet;
        static Func<ItemDismantle, IReadOnlyList<int>> Weapon() => r => r.Weapon;
        static Func<ItemDismantle, IReadOnlyList<int>> Armor() => r => r.Armor;
        static Func<ItemDismantle, IReadOnlyList<int>> Shield() => r => r.Shield;
        static Func<ItemDismantle, IReadOnlyList<int>> Boot() => r => r.Boot;
    }

    public static ItemDismantleTable Load(string shineDirectory)
    {
        var shn = ShnFile.Load(Path.Combine(shineDirectory, "ItemDismantle.shn"));
        var rows = new List<ItemDismantle>(shn.Rows.Count);

        // The SHN names only the FIRST column of each five-wide block; the other four arrive as
        // Undefined<n> in file order. Read them positionally, which is what the struct is.
        var names = shn.Columns.Select(c => c.Name).ToList();
        int Start(string first) => names.FindIndex(n => string.Equals(n, first, StringComparison.OrdinalIgnoreCase));

        var armor = Start("Armor");
        var boot = Start("Boot");
        var shield = Start("Shield");
        var weapon = Start("Weapon");
        var amulet = Start("Amulet");
        if (armor < 0 || boot < 0 || shield < 0 || weapon < 0 || amulet < 0)
            throw new InvalidDataException("ItemDismantle.shn is missing one of the five class columns");

        foreach (var row in shn.Rows)
        {
            int[] Block(int start)
            {
                var five = new int[5];
                for (var i = 0; i < 5; i++) five[i] = ShnFile.Int(row, names[start + i]);
                return five;
            }

            rows.Add(new ItemDismantle(
                ShnFile.Int(row, "ID"),
                ShnFile.Int(row, "Grade"),
                Block(armor), Block(boot), Block(shield), Block(weapon), Block(amulet)));
        }

        return new ItemDismantleTable { Rows = rows };
    }
}
