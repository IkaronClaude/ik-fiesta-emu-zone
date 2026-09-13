using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Item;

/// <summary>`ItemInfo` -- the columns the upgrade and dismantle paths read, at the PDB's own names.
/// Offsets in the comments are into the 378-byte struct the binary uses.</summary>
public sealed record ItemInfo(
    int ID,
    string InxName,
    int Type,           // +0x62
    int Class,          // +0x66  ItemClassEnum
    int ItemGradeType,  // +0x76
    int DemandLv,       // +0x7f
    int Grade,          // +0x83
    int UpLimit,        // +0xf0  on a TARGET: max upgrade level. on a SOURCE: min level it accepts.
    int BasicUpInx,     // +0xf1
    int UpSucRatio,     // +0xf3
    int UpLuckRatio,    // +0xf5
    int UpResource);    // +0xf7  on a SOURCE: max level it accepts.

/// <summary>`ItemDataBox` -- id to <see cref="ItemInfo"/>, plus the `SpecialItemIdent` entries the
/// upgrade paths reach for by name.</summary>
public sealed class ItemDataBox
{
    public required IReadOnlyDictionary<int, ItemInfo> Items { get; init; }

    /// <summary>`ItemDataBox::idb_specialid` (+0x8f8). Only the members this port needs.</summary>
    public required SpecialItemIdent SpecialId { get; init; }

    /// <summary>`ItemDataBox::operator[]` (0x419020). Absent id yields null, which every caller treats as
    /// a rejection.</summary>
    public ItemInfo? this[int itemId] => Items.TryGetValue(itemId, out var i) ? i : null;

    public static ItemDataBox Load(string shineDirectory)
    {
        var shn = ShnFile.Load(Path.Combine(shineDirectory, "ItemInfo.shn"));
        var byId = new Dictionary<int, ItemInfo>();
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in shn.Rows)
        {
            var name = ShnFile.Str(row, "InxName");
            var info = new ItemInfo(
                ID: ShnFile.Int(row, "ID"),
                InxName: name,
                Type: ShnFile.Int(row, "Type"),
                Class: ShnFile.Int(row, "Class"),
                ItemGradeType: ShnFile.Int(row, "ItemGradeType"),
                DemandLv: ShnFile.Int(row, "DemandLv"),
                Grade: ShnFile.Int(row, "Grade"),
                UpLimit: ShnFile.Int(row, "UpLimit"),
                BasicUpInx: ShnFile.Int(row, "BasicUpInx"),
                UpSucRatio: ShnFile.Int(row, "UpSucRatio"),
                UpLuckRatio: ShnFile.Int(row, "UpLuckRatio"),
                UpResource: ShnFile.Int(row, "UpResource"));
            byId[info.ID] = info;
            byName[name] = info.ID;
        }

        int Find(string n) => byName.TryGetValue(n, out var v) ? v : 0;

        return new ItemDataBox
        {
            Items = byId,
            SpecialId = new SpecialItemIdent
            {
                // sii_Karis is at idb_specialid+0x3a; the dismantle path reads it as the ONLY product it
                // ever makes. Resolved by InxName because this port has no server config to seed it from.
                Karis = Find("Karis"),
                BraceletUpSource =
                [
                    Find("BR_UPSORCE01"), Find("BR_UPSORCE02"),
                    Find("BR_UPSORCE03"), Find("BR_UPSORCE04"),
                ],
            },
        };
    }
}

/// <summary>`SpecialItemIdent` -- the members the upgrade/dismantle code uses.</summary>
public sealed class SpecialItemIdent
{
    /// <summary>`sii_Karis` (+0x3a).</summary>
    public required int Karis { get; init; }

    /// <summary>The four `BR_UPSORCE0n` ids `Item_IsUpSource` compares a bracelet's source against.
    /// The binary caches them from `CSingleDataMap::GetValue` on first use.</summary>
    public required IReadOnlyList<int> BraceletUpSource { get; init; }
}

/// <summary>`ItemClassEnum`, the values these two paths branch on.</summary>
public static class ItemClassEnum
{
    public const int Amulet = 4;
    public const int Weapon = 5;
    public const int Armor = 6;
    public const int Shield = 7;
    public const int Boot = 8;
    public const int UpgradeSource = 0x0E;   // the Elrue/Lix/Xir/Karis stones
    public const int UpgradeRedGem = 0x13;   // RedEye
    public const int UpgradeBlueGem = 0x14;  // BuleMile
    public const int UpgradeGoldGem = 0x19;  // GoldNine
    public const int Bracelet = 0x26;
}
