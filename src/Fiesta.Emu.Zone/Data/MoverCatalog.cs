

namespace Fiesta.Emu.Zone.Data;

/// <summary>One MOVER -- what the game calls a mount, and the word the binary uses (`NC_MOVER_*`,
/// `MoverMain`, `MoverItem`). Joined from `MoverMain` to the `ItemInfo` row you actually carry.</summary>
/// <param name="Idx">`MoverIDX` -- the mover's own name, e.g. `Coll`, `TigerColl01_2`.</param>
/// <param name="Id">`MoverID`.</param>
/// <param name="CastingTimeMs">The SUMMON windup. 1000 for most, 3000 for the Wooden Horse.</param>
/// <param name="CoolTimeMs">How long before it can be summoned again.</param>
/// <param name="RunSpeed">Run speed in PERMILLE of the character's own -- 1100 to 1500 in this data,
/// against the 1000 the cluster carries for an unmounted character.</param>
/// <param name="WalkSpeed">The same for walking.</param>
/// <param name="DurationHour">0 = permanent; anything else is a timed item.</param>
/// <param name="ItemId">`ItemInfo.ID` of the bag item that summons it.</param>
/// <param name="DemandLv">The level the item needs.</param>
public sealed record MoverDefinition(
    string Idx, int Id, int CastingTimeMs, int CoolTimeMs,
    int RunSpeed, int WalkSpeed, int DurationHour,
    int ItemId, int DemandLv, int BuyPrice, int DurationHourItem = 0)
{
    /// <summary>Speed as a multiplier of the character's own, which is what a caller wants.</summary>
    public double RunSpeedFactor => RunSpeed / 1000.0;
}

/// <summary>Every mover in the game, from `MoverMain` + `MoverItem` + `ItemInfo`.
///
/// <para>The three files are needed together: `MoverMain` has the speeds and timings keyed by `MoverIDX`,
/// `MoverItem` maps `MoverIDX` to an `ItemIDX`, and only `ItemInfo` turns that into the numeric item id a
/// bag slot actually holds -- which is what `level_quest.lua` scans for (`Type` 1, `Class` 23).</para></summary>
public sealed class MoverCatalog
{
    /// <summary>`ItemInfo.Type` and `.Class` of a mover item. The driver's own `mountSlot()` looks for
    /// exactly this pair, so it is stated once here rather than guessed at each call site.</summary>
    public const int MoverItemType = 1;
    public const int MoverItemClass = 23;

    public required IReadOnlyList<MoverDefinition> Movers { get; init; }

    /// <summary>The fastest mover a character of this level could actually GO AND BUY AND KEEP.
    ///
    /// <para>⚠️ THE TWO FILTERS ARE THE WHOLE POINT, and each was added after watching the unfiltered
    /// answer. With neither, the fastest mover at level 1 is `Dog_Black00` at 2900 permille -- nearly
    /// three times running speed. With only a price filter it is `M_Bunny_1` at 2800, because the cash
    /// shop prices its rentals at <b>1</b>. Both are <c>DemandLv</c> 1, and a bench character handed
    /// either is measuring a journey no levelling bot will ever make.</para>
    ///
    /// <para>So: <b>PERMANENT</b> (<c>DurationHour</c> 0 -- something you own rather than rent by the
    /// day) and <b>priced in real cen</b> (above the cash shop's token 1). What is left is what a
    /// merchant sells: Hobby at 1100 for 500 cen, Coll at 1300 for 100,000, TigerColl at 1500.</para></summary>
    public MoverDefinition? BestFor(int level)
        => Movers.Where(m => m.DemandLv <= level && m.BuyPrice > 1 && m.DurationHourItem == 0)
                 .OrderByDescending(m => m.RunSpeed)
                 .ThenBy(m => m.CastingTimeMs)
                 .FirstOrDefault();

    public MoverDefinition? ByItemId(int itemId) => Movers.FirstOrDefault(m => m.ItemId == itemId);

    public static MoverCatalog Load(string shineDirectory)
    {
        var main = ShnFile.Load(Path.Combine(shineDirectory, "MoverMain.shn"));
        var items = ShnFile.Load(Path.Combine(shineDirectory, "MoverItem.shn"));
        var info = ShnFile.Load(Path.Combine(shineDirectory, "ItemInfo.shn"));

        static int I(IReadOnlyDictionary<string, object> r, string c)
            => r.TryGetValue(c, out var v) && v is not null && int.TryParse(v.ToString(), out var n) ? n : 0;
        static string S(IReadOnlyDictionary<string, object> r, string c)
            => r.TryGetValue(c, out var v) ? v?.ToString() ?? "" : "";

        // MoverIDX -> ItemIDX, then ItemIDX -> the numeric id a bag slot holds.
        var itemIdxOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in items.Rows) itemIdxOf.TryAdd(S(r, "MoverIDX"), S(r, "ItemIDX"));

        var byInxName = new Dictionary<string, (int Id, int DemandLv, int BuyPrice, int Duration)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var r in info.Rows)
            byInxName.TryAdd(S(r, "InxName"),
                (I(r, "ID"), I(r, "DemandLv"), I(r, "BuyPrice"), I(r, "DurationHour")));

        var list = new List<MoverDefinition>();
        foreach (var r in main.Rows)
        {
            var idx = S(r, "MoverIDX");
            if (idx.Length == 0) continue;
            if (!itemIdxOf.TryGetValue(idx, out var itemIdx)) continue;
            if (!byInxName.TryGetValue(itemIdx, out var item)) continue;

            list.Add(new MoverDefinition(
                Idx: idx,
                Id: I(r, "MoverID"),
                CastingTimeMs: I(r, "CastingTime"),
                CoolTimeMs: I(r, "CoolTime"),
                RunSpeed: I(r, "RunSpeed"),
                WalkSpeed: I(r, "WalkSpeed"),
                DurationHour: I(r, "DurationHour"),
                ItemId: item.Id,
                DemandLv: item.DemandLv,
                BuyPrice: item.BuyPrice,
                DurationHourItem: item.Duration));
        }

        return new MoverCatalog { Movers = list };
    }
}
