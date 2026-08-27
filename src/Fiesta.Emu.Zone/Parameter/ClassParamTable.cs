using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Parameter;

/// <summary>One row of `Param&lt;Class&gt;Server.txt` — everything the server knows about a class at a level.</summary>
/// <param name="Level">The level this row describes.</param>
/// <param name="Str">`Strength`.</param>
/// <param name="Con">`Constitution`.</param>
/// <param name="Int">`Intelligence`.</param>
/// <param name="Dex">`Dexterity`.</param>
/// <param name="Men">`MentalPower`.</param>
/// <param name="MaxHp">`MaxHP` — a stored value, NOT a formula over Con. See <see cref="ClassParamTable"/>.</param>
/// <param name="MaxSp">`MaxSP`, likewise stored.</param>
/// <param name="SoulHp">`SoulHP` — HP restored per HP soul stone at this level.</param>
/// <param name="MaxSoulHp">`MAXSoulHP` — how many HP stones may be carried.</param>
/// <param name="PriceHpStone">`PriceHPStone` — cost of one, in cen.</param>
/// <param name="SoulSp">`SoulSP`.</param>
/// <param name="MaxSoulSp">`MAXSoulSP`.</param>
/// <param name="PriceSpStone">`PriceSPStone`.</param>
/// <param name="JobChangeDmgUp">`JobChangeDmgUp` — the job-change catch-up multiplier, in permille,
/// applied to every hit this character lands ON A MONSTER. See
/// <see cref="Combat.AttackModifiers.JobChangeDamageUpPermille"/>: it is 1000 (no change) for a base
/// class, and 2000 at level 20 for a first-job class, decaying with level until the next job change
/// resets it.</param>
public sealed record ClassParamRow(
    int Level,
    int Str, int Con, int Int, int Dex, int Men,
    int MaxHp, int MaxSp,
    int SoulHp, int MaxSoulHp, int PriceHpStone,
    int SoulSp, int MaxSoulSp, int PriceSpStone,
    int JobChangeDmgUp);

/// <summary>The per-class, per-level base stat tables — `9Data/Shine/World/Param&lt;Class&gt;Server.txt`.
///
/// <para><b>Base HP and SP are looked up, not computed.</b> `MaxHP` and `MaxSP` are columns in this table,
/// one row per level, so there is no HP curve to reverse-engineer and nothing to infer from Constitution.
/// Any "HP formula" would be a guess competing with a stored number.</para>
///
/// <para>What this table supplies is the <see cref="ParameterContainer.Base"/> cluster: the five primaries
/// plus MaxHP/MaxSP. It does NOT supply WC/AC/TH/TB and the other derived combat slots — those come from
/// equipment and from conversion rules not read yet, and are deliberately left at zero rather than filled
/// with a plausible-looking formula.</para></summary>
public sealed class ClassParamTable
{
    /// <summary>The class this table describes, e.g. "Cleric" — taken from the file name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Rows by level.</summary>
    public required IReadOnlyDictionary<int, ClassParamRow> ByLevel { get; init; }

    public int MaxLevel => ByLevel.Keys.Max();

    /// <summary>The row for a level, or null when the table does not go that high.</summary>
    public ClassParamRow? At(int level) => ByLevel.GetValueOrDefault(level);

    /// <summary>File names look like `ParamClericServer.txt`; the class is what sits between.</summary>
    private const string FilePrefix = "Param";
    private const string FileSuffix = "Server";

    /// <summary>Load one class table.</summary>
    public static ClassParamTable Load(string path)
    {
        var table = ShineTable.ParseFile(path).Single(t => t.Name == "Param");

        int Col(IReadOnlyList<string> row, string name) => table.GetInt(row, name);

        var rows = table.Rows
            .Select(r => new ClassParamRow(
                Level: Col(r, "Level"),
                Str: Col(r, "Strength"),
                Con: Col(r, "Constitution"),
                Int: Col(r, "Intelligence"),
                Dex: Col(r, "Dexterity"),
                Men: Col(r, "MentalPower"),
                MaxHp: Col(r, "MaxHP"),
                MaxSp: Col(r, "MaxSP"),
                SoulHp: Col(r, "SoulHP"),
                MaxSoulHp: Col(r, "MAXSoulHP"),
                PriceHpStone: Col(r, "PriceHPStone"),
                SoulSp: Col(r, "SoulSP"),
                MaxSoulSp: Col(r, "MAXSoulSP"),
                PriceSpStone: Col(r, "PriceSPStone"),
                JobChangeDmgUp: Col(r, "JobChangeDmgUp")))
            .ToDictionary(r => r.Level);

        var name = Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith(FilePrefix, StringComparison.Ordinal)) name = name[FilePrefix.Length..];
        if (name.EndsWith(FileSuffix, StringComparison.Ordinal)) name = name[..^FileSuffix.Length];

        return new ClassParamTable { ClassName = name, ByLevel = rows };
    }

    /// <summary>Load every class table in a `9Data/Shine/World` directory.
    ///
    /// <para>The set of classes is whatever files are THERE — discovered by glob, never a list written into
    /// this repo. A server with an extra class needs no code change to support it.</para></summary>
    public static IReadOnlyDictionary<string, ClassParamTable> LoadAll(string worldDirectory)
        => Directory.EnumerateFiles(worldDirectory, $"{FilePrefix}*{FileSuffix}.txt")
            .Select(Load)
            .ToDictionary(t => t.ClassName, StringComparer.OrdinalIgnoreCase);
}
