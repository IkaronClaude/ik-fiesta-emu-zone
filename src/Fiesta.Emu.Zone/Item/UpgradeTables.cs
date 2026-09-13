using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Item;

/// <summary>`AccUpgrade` (PDB, size 14) -- one row of upgrade odds. The same 14-byte shape backs all three
/// tables: `ItemUpgrade.shn`, `AccUpgrade.shn` and `BRAccUpgrade.shn`.
///
/// <para>The three fail weights are per-mille but routinely sum ABOVE 1000 at high levels, so the bucket
/// split renormalises them; see <see cref="ItemUpgrade"/>.</para></summary>
public sealed record AccUpgrade(
    int ID, int CriFail, int DownFail, int NormalFail, int nCon, int LuckySuc);

/// <summary>The odds tables. `LuckySuc` is present in every file and read by NONE of the upgrade code in
/// this build -- the lucky roll uses `ItemInfo.UpLuckRatio` instead.</summary>
public sealed class UpgradeTables
{
    /// <summary>How many from-levels each grade block holds in `ItemUpgrade.shn` / `AccUpgrade.shn`.
    /// From `Item_Upgrade`+0x30D: `esi = level + Grade*12 - 12`.</summary>
    public const int LevelsPerGrade = 12;

    /// <summary>`ItemUpgrade.shn`, 72 rows, read by ROW INDEX -- `CDataReader::GetRecord` (0x62A0F0) is
    /// `ptrArray[i]` with a bounds check, not a key lookup. The file's `ID` column is the grade.</summary>
    public required IReadOnlyList<AccUpgrade> ItemUpgradeRows { get; init; }

    /// <summary>`AccUpgrade.shn`, 60 rows. `Item_Upgrade` substitutes this for Class 4 via
    /// `AccUpGradeTable::GetByIndex` at the SAME index.</summary>
    public required IReadOnlyList<AccUpgrade> AccUpgradeRows { get; init; }

    /// <summary>`BRAccUpgrade.shn`, 45 rows, indexed by <see cref="GetBRAccUpgrade"/>.</summary>
    public required IReadOnlyList<AccUpgrade> BRAccUpgrade { get; init; }

    /// <summary>`BRAccUpgradeDataBox` +0x54.. -- the per-grade start offsets `GetBRAccUpgrade` adds the
    /// level to. Indexed by grade directly, so entry 0 is unused.</summary>
    public required IReadOnlyList<int> BraceletGradeOffset { get; init; }

    /// <summary>`ItemUpgrade`/`AccUpGradeTable` index: `level + 12*(Grade-1)`.</summary>
    public static int IndexOf(int grade, int level) => level + LevelsPerGrade * (grade - 1);

    /// <summary>`ItemUpgrade` by grade and from-level, null when out of range.</summary>
    public AccUpgrade? GetItemUpgrade(int grade, int level) => At(ItemUpgradeRows, IndexOf(grade, level));

    /// <summary>`AccUpGradeTable::GetByIndex` (0x53C740).</summary>
    public AccUpgrade? GetByIndex(int index) => At(AccUpgradeRows, index);

    /// <summary>`BRAccUpgradeDataBox::GetBRAccUpgrade` (0x43B850), verbatim:
    /// <code>
    /// if (grade &lt;= 0) return null
    /// i = offset[grade] + level            // demandLv (arg2) is NOT used
    /// if (i &lt; 0) return null
    /// return rowPtr[i]
    /// </code>
    /// <para>`demandLv` is kept in the signature because the caller passes it and the argument is dead in
    /// the original -- dropping it would hide that.</para></summary>
    public AccUpgrade? GetBRAccUpgrade(int grade, int demandLv, int level)
    {
        _ = demandLv;
        if (grade <= 0 || grade >= BraceletGradeOffset.Count) return null;
        var i = BraceletGradeOffset[grade] + level;
        return i < 0 ? null : At(BRAccUpgrade, i);
    }

    private static AccUpgrade? At(IReadOnlyList<AccUpgrade> rows, int i)
        => (uint)i < (uint)rows.Count ? rows[i] : null;

    public static UpgradeTables Load(string shineDirectory)
    {
        var item = Rows(Path.Combine(shineDirectory, "ItemUpgrade.shn"));
        var acc = Rows(Path.Combine(shineDirectory, "AccUpgrade.shn"));
        var br = Rows(Path.Combine(shineDirectory, "BRAccUpgrade.shn"));

        return new UpgradeTables
        {
            ItemUpgradeRows = item,
            AccUpgradeRows = acc,
            BRAccUpgrade = br,
            BraceletGradeOffset = BraceletOffsets(br),
        };
    }

    /// <summary>`LoadBRAccUpgradeData` (0x43BB00): count rows per grade into slots at +0x58+4g, then run
    /// TWO prefix-sum steps (+0x5c += +0x58, +0x60 += +0x5c). `GetBRAccUpgrade` reads at +0x54+4g, one slot
    /// BELOW where the counts were written, which turns the counts into exclusive start offsets.
    ///
    /// <para>Only two prefix steps run, so only grades 1-3 get a correct offset. The shipped file has
    /// exactly three grades (10/15/20 rows), so nothing is out of reach -- but a fourth grade added to the
    /// data would index wrongly, and this reproduces that rather than fixing it.</para></summary>
    private static int[] BraceletOffsets(IReadOnlyList<AccUpgrade> rows)
    {
        var slot = new int[8];                       // slot[g] == the binary's [this+0x58+4g]
        foreach (var r in rows)
            if (r.ID != 0 && r.ID < slot.Length)
                slot[r.ID]++;

        slot[1] += slot[0];
        slot[2] += slot[1];

        // offset[g] == [this+0x54+4g] == slot[g-1]
        var offset = new int[slot.Length + 1];
        for (var g = 1; g < offset.Length && g - 1 < slot.Length; g++)
            offset[g] = slot[g - 1];
        return offset;
    }

    private static List<AccUpgrade> Rows(string path)
    {
        var shn = ShnFile.Load(path);
        var list = new List<AccUpgrade>(shn.Rows.Count);
        foreach (var row in shn.Rows)
            list.Add(new AccUpgrade(
                ShnFile.Int(row, "ID"),
                ShnFile.Int(row, "CriFail"),
                ShnFile.Int(row, "DownFail"),
                ShnFile.Int(row, "NormalFail"),
                ShnFile.Int(row, "nCon"),
                ShnFile.Int(row, "LuckySuc")));
        return list;
    }
}
