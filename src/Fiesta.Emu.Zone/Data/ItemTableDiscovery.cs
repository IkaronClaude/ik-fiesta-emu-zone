namespace Fiesta.Emu.Zone.Data;


/// <summary>Which server tables are keyed by an ITEM — discovered from the data, not from a list somebody
/// remembered to keep up to date.
///
/// <para>⚠️ <b>THIS EXISTS BECAUSE "I READ ALL THE EQUIPMENT" WAS TRUE AND USELESS AT THE SAME TIME.</b>
/// Rebuilding a capture's character, this port dumped every non-zero column of `ItemInfo.shn` for all nine
/// equipped items, found no STR/CON/DEX/INT/MEN, and concluded that items cannot grant primary stats. The
/// conclusion was about the wrong thing: `ItemInfo.shn` has no such column <i>for any item in the game</i>
/// — the primary stats live in `GradeItemOption.shn`, keyed by `InxName` rather than by id. A Mini Phino
/// pet was silently contributing +2 to every stat, and the operator had to supply the mechanic from
/// memory.</para>
///
/// <para><b>An absent COLUMN is evidence about a schema. Only an absent VALUE, in the right table, is
/// evidence about the entity.</b> The same mistake had already been made twice on the same four cosmetic
/// items — once dismissing them for having no `MinMA`, when they carry `CriRate`.</para>
///
/// <para>So the question "what describes an item?" is answered by scanning, and
/// <c>ItemTableCoverageTests</c> fails when a table turns up that nothing has accounted for. Being wrong
/// about one table is a bug; not knowing the table exists is the failure this closes.</para></summary>
public static class ItemTableDiscovery
{
    /// <summary>A table that references items, and the column that does the referencing.</summary>
    /// <param name="Table">File name, e.g. `GradeItemOption.shn`.</param>
    /// <param name="Column">The column holding item `InxName`s or ids.</param>
    /// <param name="Matched">How many of that column's values resolved to a real item.</param>
    /// <param name="Total">How many non-empty values the column holds.</param>
    public sealed record Reference(string Table, string Column, int Matched, int Total);

    /// <summary>A column counts as an item reference when at least this share of its values resolve to
    /// real items. Half is deliberately generous: `ActiveSkill.InxName` matches 1,767 of 2,791 because
    /// skills and items share a naming scheme, and a threshold tuned to exclude that would also exclude
    /// `LCReward.Item_Inx`, which is a genuine item column that misses 4 rows. False positives are cheap —
    /// they get one line in the coverage table saying why they do not matter. A false NEGATIVE is the
    /// thing that cost this project a session.</summary>
    public const int MatchPercentThreshold = 50;

    /// <summary>Integer columns are far easier to match by accident than names, so they must both look
    /// like an item column and resolve almost perfectly.</summary>
    public const int IdMatchPercentThreshold = 95;

    /// <summary>The smallest column worth judging; below this, a handful of coincidental matches would
    /// clear any threshold.</summary>
    public const int MinimumColumnSize = 8;

    public static IReadOnlyList<Reference> Scan(string shineDirectory)
    {
        var items = ShnFile.Load(Path.Combine(shineDirectory, "ItemInfo.shn"));
        var names = items.Rows.Select(r => ShnFile.Str(r, "InxName"))
                               .Where(n => n.Length > 1)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = items.Rows.Select(r => ShnFile.Int(r, "ID")).ToHashSet();

        var found = new List<Reference>();
        foreach (var file in Directory.GetFiles(shineDirectory, "*.shn").OrderBy(f => f))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("ItemInfo.shn", StringComparison.OrdinalIgnoreCase)) continue;

            ShnFile table;
            try { table = ShnFile.Load(file); }
            catch { continue; }                       // an unreadable table is not an item table
            if (table.Rows.Count == 0) continue;

            foreach (var column in table.Rows[0].Keys)
            {
                var strings = table.Rows.Select(r => r[column]).OfType<string>()
                                        .Where(v => v.Length > 1).ToList();
                if (strings.Count > 0)
                {
                    var hit = strings.Count(v => names.Contains(v));
                    if (hit > 0 && hit * 100 / strings.Count >= MatchPercentThreshold)
                        found.Add(new Reference(name, column, hit, strings.Count));
                    continue;
                }

                var numbers = table.Rows.Select(r => ShnFile.Int(r, column)).Where(v => v > 0).ToList();
                if (numbers.Count < MinimumColumnSize) continue;
                if (!column.Contains("item", StringComparison.OrdinalIgnoreCase)) continue;

                var matched = numbers.Count(v => ids.Contains(v));
                if (matched * 100 / numbers.Count >= IdMatchPercentThreshold)
                    found.Add(new Reference(name, column, matched, numbers.Count));
            }
        }
        return found;
    }

    /// <summary>Just the distinct table names, which is what coverage is tracked against.</summary>
    public static IReadOnlySet<string> Tables(string shineDirectory)
        => Scan(shineDirectory).Select(r => r.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
