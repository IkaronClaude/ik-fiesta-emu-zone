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
    /// <para>The directives that matter: `#table` starts one, `#columnname` names its columns, `#record`
    /// is a row. `#columntype`, `#ignore` and `#exchange` are declarations this parser does not need —
    /// the values are read as text and converted by the caller, which is the only part that knows what a
    /// column means.</para>
    ///
    /// <para>A leading empty field appears on every `#record` line (the directive is followed by two
    /// tabs), so blank leading cells are dropped rather than shifting every column by one.</para></summary>
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

        var tables = new List<ShineTable>();
        string? name = null;
        List<string>? columns = null;
        List<IReadOnlyList<string>>? rows = null;

        void Flush()
        {
            if (name is not null && columns is not null && rows is not null)
                tables.Add(new ShineTable { Name = name, Columns = columns, Rows = rows });
        }

        foreach (var raw in File.ReadLines(path, enc))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.TrimStart().StartsWith(';'))
                continue;

            var fields = Fields(line);
            if (fields.Count == 0) continue;

            switch (fields[0].ToLowerInvariant())
            {
                case "#table":
                    Flush();
                    name = fields.Count > 1 ? fields[1] : "(unnamed)";
                    columns = null;
                    rows = new List<IReadOnlyList<string>>();
                    break;

                case "#columnname":
                    columns = fields.Skip(1).ToList();
                    break;

                case "#record":
                    rows?.Add(fields.Skip(1).ToList());
                    break;
            }
        }
        Flush();
        return tables;
    }

    private static List<string> Fields(string line)
    {
        // Tab-separated, but consecutive tabs are used as alignment padding rather than empty columns,
        // so empty fields are dropped. Getting this wrong shifts every value one column left.
        return line.Split('\t').Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
    }
}
