using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Data;

/// <summary>`WeaponTitleData.shn` — every weapon LICENSE level, loaded from the server's own table.
///
/// <para>1,971 rows keyed by (`MobID`, `Level`): a license is per MONSTER, and levels up as its
/// `MobKillCount` threshold is passed. Feeds <see cref="WeaponTitleLicense.Stage"/>, which the swing
/// calls with the defender's mob id.</para>
///
/// <para>The `SP1..SP3` triples are loaded verbatim rather than filtered, because which of them the
/// server honours is `sp_WeaponTitleOption`'s business (only Reference 1 / Type 1 today) and a loader
/// that dropped the rest would hide a data change.</para></summary>
public sealed class WeaponTitleCatalog
{
    private readonly Dictionary<int, List<WeaponTitleData>> _byMob;

    private WeaponTitleCatalog(Dictionary<int, List<WeaponTitleData>> byMob) => _byMob = byMob;

    /// <summary>Every level defined for a mob, ascending. Empty when no license exists for it — the
    /// common case, and the one <see cref="WeaponTitleLicense.Stage"/> takes as null.</summary>
    public IReadOnlyList<WeaponTitleData> LevelsFor(int mobId)
        => _byMob.TryGetValue(mobId, out var rows) ? rows : [];

    /// <summary>The level a given kill count has earned against that mob, or <c>null</c> for none.
    ///
    /// <para>The highest row whose `MobKillCount` the player has reached — so a partially-progressed
    /// license grants its last completed level, not its next one.</para></summary>
    public WeaponTitleData? EarnedLevel(int mobId, uint kills)
    {
        WeaponTitleData? best = null;
        foreach (var row in LevelsFor(mobId))
            if (kills >= row.MobKillCount && (best is null || row.Level > best.Level))
                best = row;
        return best;
    }

    /// <summary>How many distinct mobs have a license defined.</summary>
    public int MobCount => _byMob.Count;

    public static WeaponTitleCatalog Load(string shineDirectory)
    {
        var table = ShnFile.Load(Path.Combine(shineDirectory, "WeaponTitleData.shn"));
        var byMob = new Dictionary<int, List<WeaponTitleData>>();

        foreach (var r in table.Rows)
        {
            var options = new List<(int, int, uint)>();
            foreach (var n in new[] { "SP1", "SP2", "SP3" })
            {
                var reference = ShnFile.Int(r, n + "_Reference");
                var type = ShnFile.Int(r, n + "_Type");
                var value = (uint)ShnFile.Int(r, n + "_Value");
                // Reference 0 is the row's own "no option here" -- sp_WeaponTitleOption returns
                // immediately on it. Kept OUT of the list so a consumer counting options sees the real
                // number rather than three every time.
                if (reference != 0) options.Add((reference, type, value));
            }

            var row = new WeaponTitleData(
                MobId: ShnFile.Int(r, "MobID"),
                Level: ShnFile.Int(r, "Level"),
                MobKillCount: (uint)ShnFile.Int(r, "MobKillCount"),
                MinAdd: ShnFile.Int(r, "MinAdd"),
                MaxAdd: ShnFile.Int(r, "MaxAdd"),
                Options: options.Count > 0 ? options : null);

            if (!byMob.TryGetValue(row.MobId, out var list))
                byMob[row.MobId] = list = [];
            list.Add(row);
        }

        foreach (var list in byMob.Values)
            list.Sort((a, b) => a.Level.CompareTo(b.Level));

        return new WeaponTitleCatalog(byMob);
    }
}
