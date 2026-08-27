using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Data;

/// <summary>One equippable item as `ItemInfo.shn` describes it.</summary>
public sealed record ItemDefinition(
    int Id, string InxName, string Name,
    int EquipSlot, int UseClass, int DemandLv, int Grade,
    int MinWc, int MaxWc, int Ac, int MinMa, int MaxMa, int Mr, int Th, int Tb,
    int WcRate, int MaRate, int AcRate, int MrRate,
    int ShieldAc, int HitRatePlus, int EvaRatePlus, int CriRate)
{
    /// <summary>Turn a catalogue entry into the shape <see cref="CharacterParameters.Equip"/> takes.</summary>
    public EquipmentPiece ToPiece() => new(
        Name: InxName,
        MinWC: MinWc, MaxWC: MaxWc, AC: Ac,
        MinMA: MinMa, MaxMA: MaxMa, MR: Mr,
        TH: Th, TB: Tb,
        WCRate: WcRate == 0 ? ParameterCluster.RateIdentity : WcRate,
        MARate: MaRate == 0 ? ParameterCluster.RateIdentity : MaRate,
        ACRate: AcRate == 0 ? ParameterCluster.RateIdentity : AcRate,
        MRRate: MrRate == 0 ? ParameterCluster.RateIdentity : MrRate,
        CriRate: CriRate, ShieldAC: ShieldAc,
        HitRatePlus: HitRatePlus, EvaRatePlus: EvaRatePlus);
}

/// <summary>Which items a class may wear, from the server's own tables.
///
/// <para>Three files join up: `ItemInfo.shn` gives each item an `Equip` slot, a `DemandLv` and a `UseClass`;
/// `UseClassTypeInfo.shn` is the <b>UseClass × class matrix</b> — 39 rows, one per UseClass, with a column
/// per class holding 1 or 0; and `ClassName.shn` names the classes.</para>
///
/// <para><b>The class columns are in ClassID order</b>, so the mapping from a class to its column is
/// positional and no list of class names is written into this repo. `ClassName.shn` supplies
/// ClassID → name, and column <c>i</c> after `UseClass` is ClassID <c>i+1</c>.</para></summary>
public sealed class EquipmentCatalog
{
    public required IReadOnlyList<ItemDefinition> Items { get; init; }

    /// <summary>Class name (as `ClassName.shn` spells it) to its 1-based ClassID.</summary>
    public required IReadOnlyDictionary<string, int> ClassIds { get; init; }

    /// <summary>`UseClass` value to the set of ClassIDs allowed to equip it.</summary>
    public required IReadOnlyDictionary<int, HashSet<int>> UseClassAllows { get; init; }

    /// <summary>Whether a class may equip an item.
    ///
    /// <para>⚠️ <c>UseClass == 1</c> is "everyone" and covers a large share of the file; <c>0</c> is nobody.
    /// Both fall out of the matrix rather than being special-cased here.</para></summary>
    public bool CanEquip(ItemDefinition item, int classId)
        => UseClassAllows.TryGetValue(item.UseClass, out var allowed) && allowed.Contains(classId);

    /// <summary>The best item this class can wear in each equip slot at this level.
    ///
    /// <para>"Best" is a simple score — weapon damage, then armour, then magic attack — because the point is
    /// to give a simulated character plausible gear, not to solve itemisation. It is a MODELLING choice and
    /// is not claimed to match what the game's own recommendation would be.</para></summary>
    public IReadOnlyList<ItemDefinition> BestLoadout(string className, int level, bool obtainableOnly = true)
    {
        if (!ClassIds.TryGetValue(className, out var classId))
            return [];

        return Items
            .Where(i => i.EquipSlot > 0 && i.DemandLv <= level && CanEquip(i, classId))
            .Where(i => !obtainableOnly || LooksObtainable(i))
            .GroupBy(i => i.EquipSlot)
            .Select(g => g.OrderByDescending(Score).ThenByDescending(i => i.DemandLv).First())
            .OrderBy(i => i.EquipSlot)
            .ToList();
    }

    /// <summary>Whether an item looks like gear a player would actually obtain.
    ///
    /// <para>⚠️ MODELLING CHOICE, not a game rule — the tables do not flag GM items. Two signals:</para>
    /// <list type="bullet">
    ///   <item>a name that did not decode from EUC-KR (it still contains <c>?</c>) is a Korean-named
    ///         test/GM entry rather than something an English client ever shows;</item>
    ///   <item>a weapon whose <c>MinWC</c> equals its <c>MaxWC</c> has no damage range, which real weapons
    ///         all have. The flat <c>1000-1000</c> "?? ?? ???? Mace" is the giveaway.</item>
    /// </list>
    ///
    /// <para>Without this the best loadout for every class is the same handful of debug weapons, which makes
    /// class differences vanish — the opposite of what the catalogue is for.</para></summary>
    public static bool LooksObtainable(ItemDefinition i)
        => !i.Name.Contains('?') && !(i.MaxWc > 0 && i.MinWc == i.MaxWc);

    private static long Score(ItemDefinition i)
        => (long)i.MaxWc * 4 + i.Ac * 3 + i.MaxMa * 2 + i.ShieldAc + i.Th + i.Tb;

    public static EquipmentCatalog Load(string shineDirectory)
    {
        var items = ShnFile.Load(Path.Combine(shineDirectory, "ItemInfo.shn"));
        var matrix = ShnFile.Load(Path.Combine(shineDirectory, "UseClassTypeInfo.shn"));
        var names = ShnFile.Load(Path.Combine(shineDirectory, "ClassName.shn"));

        static int I(IReadOnlyDictionary<string, object> r, string c) => ShnFile.Int(r, c);
        static string S(IReadOnlyDictionary<string, object> r, string c) => ShnFile.Str(r, c);

        var classIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in names.Rows)
        {
            var id = I(r, "ClassID");
            var name = S(r, "acEngName");
            if (id > 0 && name.Length > 1) classIds[name] = id;
        }

        // Column i after `UseClass` is ClassID i+1 -- the matrix is laid out in ClassID order.
        var classColumns = matrix.Columns.Where(c => !c.Name.Equals("UseClass", StringComparison.OrdinalIgnoreCase))
            .Select((c, index) => (c.Name, ClassId: index + 1))
            .ToList();

        var allows = new Dictionary<int, HashSet<int>>();
        foreach (var r in matrix.Rows)
        {
            var set = new HashSet<int>();
            foreach (var (column, classId) in classColumns)
                if (I(r, column) != 0) set.Add(classId);
            allows[I(r, "UseClass")] = set;
        }

        var defs = items.Rows
            .Select(r => new ItemDefinition(
                I(r, "ID"), S(r, "InxName"), S(r, "Name"),
                I(r, "Equip"), I(r, "UseClass"), I(r, "DemandLv"), I(r, "Grade"),
                I(r, "MinWC"), I(r, "MaxWC"), I(r, "AC"), I(r, "MinMA"), I(r, "MaxMA"), I(r, "MR"),
                I(r, "TH"), I(r, "TB"),
                I(r, "WCRate"), I(r, "MARate"), I(r, "ACRate"), I(r, "MRRate"),
                I(r, "ShieldAC"), I(r, "HitRatePlus"), I(r, "EvaRatePlus"), I(r, "CriRate")))
            .Where(i => i.InxName.Length > 0)
            .ToList();

        return new EquipmentCatalog { Items = defs, ClassIds = classIds, UseClassAllows = allows };
    }
}
