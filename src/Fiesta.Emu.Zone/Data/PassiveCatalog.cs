namespace Fiesta.Emu.Zone.Data;

/// <summary>One row of `PassiveSkill.shn`, restricted to the columns that reach a character's stats.
///
/// <para>⚠️ <b>A passive's effect is not always in its row.</b> `PowerofLove01` grants +5% to every
/// primary and carries no stat column at all — that bonus is hard-coded in
/// `ShinePlayer::so_RecalcLastParam` and reached through `PassiveDataBox::SpecialSkill.ss_PowerOfLove`.
/// This type describes what the TABLE says; see <see cref="Parameter.CharacterParameters.RecalcLastParam"/>
/// for the part that it does not.</para></summary>
/// <param name="Id">`ID`.</param>
/// <param name="InxName">e.g. `WisdomMastery06`. The trailing digits are the rank.</param>
/// <param name="Name">The displayed name.</param>
/// <param name="MaxSp">`MaxSP` — flat SP the passive adds to the pool.</param>
public sealed record PassiveDefinition(int Id, string InxName, string Name, int MaxSp)
{
    /// <summary>`WisdomMastery06` → `WisdomMastery`. Ranks of one line share this.</summary>
    public string Line
    {
        get
        {
            var n = 0;
            while (n < InxName.Length && char.IsAsciiDigit(InxName[^(n + 1)])) n++;
            return n == 0 ? InxName : InxName[..^n];
        }
    }
}

/// <summary>`PassiveSkill.shn`, for the columns that change a character's stats.</summary>
public sealed class PassiveCatalog
{
    public required IReadOnlyList<PassiveDefinition> Passives { get; init; }

    private Dictionary<int, PassiveDefinition>? _byId;
    public PassiveDefinition? Find(int id)
        => (_byId ??= Passives.ToDictionary(p => p.Id)).GetValueOrDefault(id);

    /// <summary>⭐ SP GRANTED BY A SET OF LEARNED PASSIVES — <b>the highest rank of each line, not the
    /// sum of the ranks.</b>
    ///
    /// <para>The ranks are cumulative VALUES, not cumulative bonuses: `WisdomMastery` runs 55, 110, 170,
    /// 230, 295, 360, so a character holding all six gets <b>360</b>, not 1,220. This is the same
    /// "<c>mov</c>, not <c>add</c> — last non-zero wins" rule the weapon-mastery lookup follows, and it is
    /// checkable rather than assumed: `MageDamageLvl60`'s Enchanter knows ranks 01-06 and its MaxSp is
    /// 2,366, which needs exactly 360 from this term.</para></summary>
    public int MaxSpFrom(IEnumerable<int> learnedPassiveIds)
    {
        var best = new Dictionary<string, int>();
        foreach (var id in learnedPassiveIds)
        {
            if (Find(id) is not { MaxSp: > 0 } p) continue;
            best[p.Line] = Math.Max(best.GetValueOrDefault(p.Line), p.MaxSp);
        }
        return best.Values.Sum();
    }

    public static PassiveCatalog Load(string shineDirectory)
    {
        var table = ShnFile.Load(Path.Combine(shineDirectory, "PassiveSkill.shn"));
        var rows = new List<PassiveDefinition>(table.Rows.Count);
        foreach (var r in table.Rows)
            rows.Add(new PassiveDefinition(
                ShnFile.Int(r, "ID"), ShnFile.Str(r, "InxName"), ShnFile.Str(r, "Name"),
                ShnFile.Int(r, "MaxSP")));
        return new PassiveCatalog { Passives = rows };
    }
}
