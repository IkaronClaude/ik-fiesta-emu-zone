using System.Text;

namespace Fiesta.Emu.Zone.Data;

/// <summary>A parsed server text table — the `#table` / `#columnname` / `#record` format used throughout
/// `9Data/Shine`.
///
/// <para>One file can hold several tables; `MobRegen/Urg.txt` holds two (the spawn areas and what spawns
/// in them), which is why this returns a list rather than a single table.</para>
///
/// <para>⚠️ These files are <b>EUC-KR / cp949</b>, not UTF-8. Reading them as UTF-8 mangles any Korean
/// text and can corrupt the tab structure. Nothing in this repo ships game data — the caller supplies a
/// path into their own server files.</para></summary>
public sealed class ShineTable
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    public int IndexOf(string column)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase))
                return i;
        throw new KeyNotFoundException($"table '{Name}' has no column '{column}' (has: {string.Join(", ", Columns)})");
    }

    public string Get(IReadOnlyList<string> row, string column)
    {
        var i = IndexOf(column);
        return i < row.Count ? row[i] : "";
    }

    public int GetInt(IReadOnlyList<string> row, string column)
        => int.TryParse(Get(row, column), out var v) ? v : 0;

    /// <summary>Parse every table in a file.
    ///
    /// <para>The directives that matter: `#table` starts one, `#columnname` names its columns, and a row
    /// is either `#record` (into the table being declared) or <b>`#recordin &lt;TableName&gt;`</b> (into a
    /// table named explicitly, which lets one file interleave rows for several tables).</para>
    ///
    /// <para>⚠️ <b>`#recordin` USED TO BE IGNORED ENTIRELY</b>, so `World/NPC.txt` -- 760 rows of NPC and
    /// map-link-gate placements -- parsed to a table with ZERO rows and read as "this file has no data".
    /// Every other file under `World/` uses `#record`, which is why it went unnoticed.</para>
    ///
    /// <para>⚠️ <b>THE DELIMITER IS DECLARED BY THE FILE.</b> `NPC.txt` opens with `#delimiter \x20`, so
    /// SPACE separates fields there as well as tab, and three of its rows use spaces. It also declares
    /// `#exchange # \x20`, meaning a `#` inside a value stands for a space -- which is how a name
    /// containing a space survives a space-delimited file. Both are honoured rather than assumed away;
    /// splitting a space-delimited row on tabs alone yields one enormous field and silently loses it.</para>
    ///
    /// <para>`#columntype` and `#ignore` are still not needed: values are read as text and converted by
    /// the caller, which is the only part that knows what a column means.</para>
    ///
    /// <para>A leading empty field appears on every record line (the directive is followed by two tabs),
    /// so blank leading cells are dropped rather than shifting every column by one.</para></summary>
    public static IReadOnlyList<ShineTable> ParseFile(string path)
    {
        Encoding enc;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            enc = Encoding.GetEncoding(949);
        }
        catch (Exception)
        {
            enc = Encoding.UTF8;      // best effort; ASCII table structure survives either way
        }

        // Declared by the file itself, in its opening directives.
        var spaceDelimits = false;      // #delimiter \x20
        var hashIsSpace = false;        // #exchange # \x20

        // Tables are kept BY NAME as well as in order, because `#recordin` addresses one by name and a
        // file may interleave rows for several of them.
        var order = new List<string>();
        var columnsOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var rowsOf = new Dictionary<string, List<IReadOnlyList<string>>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;

        void Declare(string tableName)
        {
            if (!rowsOf.ContainsKey(tableName))
            {
                order.Add(tableName);
                columnsOf[tableName] = [];
                rowsOf[tableName] = [];
            }
            current = tableName;
        }

        foreach (var raw in File.ReadLines(path, enc))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith(';'))
                continue;

            var fields = Fields(line, spaceDelimits, hashIsSpace);
            if (fields.Count == 0) continue;

            switch (fields[0].ToLowerInvariant())
            {
                case "#delimiter":
                    // `\x20` is a space. Anything else is left alone: tab always separates.
                    if (fields.Count > 1 && fields[1].Equals("\\x20", StringComparison.OrdinalIgnoreCase))
                        spaceDelimits = true;
                    break;

                case "#exchange":
                    // `#exchange # \x20` -- a `#` inside a value stands for a space.
                    if (fields.Count > 2 && fields[1] == "#"
                        && fields[2].Equals("\\x20", StringComparison.OrdinalIgnoreCase))
                        hashIsSpace = true;
                    break;

                case "#table":
                    Declare(fields.Count > 1 ? fields[1] : "(unnamed)");
                    break;

                case "#columnname":
                    if (current is not null) columnsOf[current] = fields.Skip(1).ToList();
                    break;

                case "#record":
                    if (current is not null) rowsOf[current].Add(fields.Skip(1).ToList());
                    break;

                case "#recordin":
                    // `#recordin <TableName> <values...>` -- the table is named, not implied.
                    if (fields.Count > 1 && rowsOf.TryGetValue(fields[1], out var into))
                        into.Add(fields.Skip(2).ToList());
                    break;
            }
        }

        return [.. order.Select(n => new ShineTable
        {
            Name = n,
            Columns = columnsOf[n],
            Rows = rowsOf[n],
        })];
    }

    private static List<string> Fields(string line, bool spaceDelimits, bool hashIsSpace)
    {
        // Tab always separates; SPACE does too when the file declared `#delimiter \x20`. Consecutive
        // separators are alignment padding rather than empty columns, so empty fields are dropped --
        // getting that wrong shifts every value one column left.
        var seps = spaceDelimits ? new[] { '\t', ' ' } : ['\t'];
        var parts = line.Split(seps, StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => f.Trim())
                        .Where(f => f.Length > 0)
                        .ToList();

        // ...and a `#` inside a VALUE stands for a space, which is how a name with a space survives a
        // space-delimited file. The leading directive keeps its own `#`.
        if (hashIsSpace)
            for (var i = 1; i < parts.Count; i++)
                parts[i] = parts[i].Replace('#', ' ').Trim();

        return parts;
    }
}
