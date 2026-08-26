namespace Fiesta.Emu.Zone.Data;

/// <summary>A spawn area — one row of the `MobRegenGroup` table.
///
/// <para><b><c>RangeDegree</c> is overloaded</b>, which the file's own header comment admits by calling
/// the column "Range/Degree". When <see cref="Width"/> and <see cref="Height"/> are both zero the area is
/// a CIRCLE and the value is its radius; otherwise the area is a RECTANGLE and the value is its rotation
/// in degrees. That matches the binary exactly: `MobRegenLoc_Circular::mrlc_Get` versus
/// `MobRegenLoc_Rectangle::mrlr_Get`.</para></summary>
public sealed record MobRegenGroup(
    string GroupIndex,
    bool IsFamily,
    int CenterX,
    int CenterY,
    int Width,
    int Height,
    int RangeDegree)
{
    /// <summary>Circular when it has no extent — the discriminator the server uses.</summary>
    public bool IsCircular => Width == 0 && Height == 0;

    public int Radius => IsCircular ? RangeDegree : 0;
    public int RotationDegrees => IsCircular ? 0 : RangeDegree;
}

/// <summary>What spawns in a group, and how fast it comes back — one row of the `MobRegen` table.
///
/// <para><see cref="RegStandard"/> / <see cref="RegMin"/> / <see cref="RegMax"/> are the respawn delay in
/// seconds and its bounds (Uruga's ordinary mobs are 25, 23, 27).</para>
///
/// <para>⚠️ The <c>Delta</c>/<c>Sec</c> pairs are <b>not yet understood</b>. They look like a
/// load-adaptive schedule — respawn faster when a group is being farmed, slower when idle — because the
/// deltas run negative-to-positive against increasing second thresholds (-2@8, -1@15, 0@60, 1@120, 2).
/// That is a guess from the shape of the numbers, NOT a reading of `mb_Setregentime`, and it is carried
/// as data without being acted on until somebody reads that function.</para></summary>
public sealed record MobRegenEntry(
    string RegenIndex,
    string MobIndex,
    int MobNum,
    int KillNum,
    int RegStandard,
    int RegMin,
    int RegMax,
    IReadOnlyList<(int Delta, int Sec)> Schedule);

/// <summary>One map's spawn tables, as loaded from `9Data/Shine/MobRegen/&lt;Map&gt;.txt`.</summary>
public sealed class MobRegenData
{
    /// <summary>The server's fixed spawn-group capacity.
    ///
    /// <para>Kept verbatim so the model matches the original. <b>Nothing iterates or bounds-checks
    /// against this constant</b> — loops use the actual collection count — so raising or lowering the
    /// limit is a one-line change here rather than a hunt through the code.</para></summary>
    public const int MaxSpawnGroups = 4096;

    public required string MapName { get; init; }
    public required IReadOnlyList<MobRegenGroup> Groups { get; init; }
    public required IReadOnlyList<MobRegenEntry> Entries { get; init; }

    /// <summary>Entries belonging to a group, by its index name.</summary>
    public IEnumerable<MobRegenEntry> EntriesFor(string groupIndex)
        => Entries.Where(e => string.Equals(e.RegenIndex, groupIndex, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every mob this map spawns, and how many of each at full population.</summary>
    public IReadOnlyDictionary<string, int> Population()
    {
        var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries)
            total[e.MobIndex] = total.GetValueOrDefault(e.MobIndex) + e.MobNum;
        return total;
    }

    public static MobRegenData Load(string path)
    {
        var tables = ShineTable.ParseFile(path);

        var groupTable = tables.FirstOrDefault(t => t.Name.Equals("MobRegenGroup", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{path}: no MobRegenGroup table");
        var regenTable = tables.FirstOrDefault(t => t.Name.Equals("MobRegen", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{path}: no MobRegen table");

        var groups = groupTable.Rows.Select(r => new MobRegenGroup(
            groupTable.Get(r, "GroupIndex"),
            groupTable.Get(r, "IsFamily").StartsWith("Y", StringComparison.OrdinalIgnoreCase),
            groupTable.GetInt(r, "CenterX"),
            groupTable.GetInt(r, "CenterY"),
            groupTable.GetInt(r, "Width"),
            groupTable.GetInt(r, "Height"),
            groupTable.GetInt(r, "RangeDegree"))).ToList();

        if (groups.Count > MaxSpawnGroups)
            throw new InvalidDataException(
                $"{path}: {groups.Count} spawn groups exceeds the server's capacity of {MaxSpawnGroups}");

        var entries = regenTable.Rows.Select(r =>
        {
            var schedule = new List<(int, int)>();
            for (var i = 0; ; i++)
            {
                var d = $"RegDelta{i}";
                var s = $"RegSec{i}";
                if (!regenTable.Columns.Contains(d, StringComparer.OrdinalIgnoreCase)) break;
                var sec = regenTable.Columns.Contains(s, StringComparer.OrdinalIgnoreCase)
                    ? regenTable.GetInt(r, s) : 0;
                schedule.Add((regenTable.GetInt(r, d), sec));
            }
            return new MobRegenEntry(
                regenTable.Get(r, "RegenIndex"),
                regenTable.Get(r, "MobIndex"),
                regenTable.GetInt(r, "MobNum"),
                regenTable.GetInt(r, "KillNum"),
                regenTable.GetInt(r, "RegStandard"),
                regenTable.GetInt(r, "RegMin"),
                regenTable.GetInt(r, "RegMax"),
                schedule);
        }).ToList();

        return new MobRegenData
        {
            MapName = Path.GetFileNameWithoutExtension(path),
            Groups = groups,
            Entries = entries,
        };
    }
}
