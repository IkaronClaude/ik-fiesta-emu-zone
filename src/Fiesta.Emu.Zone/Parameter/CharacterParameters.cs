namespace Fiesta.Emu.Zone.Parameter;

/// <summary>The stat contribution of one equipped item — the `ItemInfo.shn` columns that feed a cluster.
///
/// <para>The column names carry their own half: everything ending in <c>Plus</c> (and the flat values like
/// <c>MinWC</c>) goes to <see cref="StatModifier.Item"/>'s Plus cluster, while <c>WCRate</c>, <c>MARate</c>,
/// <c>ACRate</c> and <c>MRRate</c> are permille and go to its Rate cluster. That the data file splits its
/// columns exactly the way the container splits its clusters is the strongest available confirmation that
/// the Plus/Rate model is the real one.</para>
///
/// <para>Supplied by the caller rather than read here: this project has no SHN reader yet, and inventing
/// one to avoid an input parameter would be the wrong trade.</para></summary>
public sealed record EquipmentPiece(
    string Name = "",
    int MinWC = 0, int MaxWC = 0, int AC = 0,
    int MinMA = 0, int MaxMA = 0, int MR = 0,
    int TH = 0, int TB = 0,
    int WCRate = ParameterCluster.RateIdentity,
    int MARate = ParameterCluster.RateIdentity,
    int ACRate = ParameterCluster.RateIdentity,
    int MRRate = ParameterCluster.RateIdentity,
    int CriRate = 0, int CrlTB = 0, int ShieldAC = 0,
    int HitRatePlus = 0, int EvaRatePlus = 0, int MACriPlus = 0,
    int CriDamPlus = 0, int MagCriDamPlus = 0);

/// <summary>Points the player has spent raising a primary beyond its class-table value.
///
/// <para>`c_Storepure` adds these on top of every primary — the level row is the floor, not the answer.</para></summary>
public sealed record FreeStats(int Str = 0, int Con = 0, int Dex = 0, int Int = 0, int Men = 0);

/// <summary>Building a character's <see cref="ParameterContainer"/> from class, level and gear.
///
/// <para>This is the port of `ShinePlayer::so_allparametercalculate` (0x0054F190), which is a short function
/// that does two things: `c_Storepure` to rebuild the base cluster, then `c_MakeTotal` to combine
/// everything. The other clusters are filled as gear is equipped and buffs applied, not here.</para></summary>
public static class CharacterParameters
{
    /// <summary>The level cap `c_Storepure` enforces before indexing the class's per-level array: it
    /// compares against <c>0x96</c> and falls back to row 0 when the level exceeds it.</summary>
    public const int MaxTableLevel = 150;

    /// <summary>`Parameter::Container::c_Storepure` — fill the base cluster for a class at a level.
    ///
    /// <para>Two things are ported exactly and both are easy to get wrong:</para>
    /// <list type="bullet">
    ///   <item>Each primary is <b>table value + free stat points</b>, not the table value alone.</item>
    ///   <item>The five primaries are stored in CLUSTER order (Str, Con, Dex, Int, Men), which is not the
    ///         TABLE's column order (Strength, Constitution, Intelligence, Wizdom, Dexterity, MentalPower).
    ///         Dexterity and Intelligence swap places. The original reads row offsets +4, +8, +0x14, +0xC,
    ///         +0x18 into slots 0..4, which is what pins the crossover.</item>
    /// </list>
    ///
    /// <para>⚠️ NOT filled here: the base WC/AC/TH/TB/MA/MR/MH/MB slots. In the original those come from
    /// eight VIRTUAL methods on the class object (`CharClass::WC`, `::AC`, …), so each class computes its
    /// own — there is no shared stat-to-attack formula to write down. Those overrides have not been read,
    /// so the slots are left at zero rather than filled with a plausible guess. See docs/PARAMETERS.md.</para></summary>
    public static void StorePure(
        ParameterContainer container,
        ClassParamTable table,
        int level,
        FreeStats? freeStats = null)
    {
        var free = freeStats ?? new FreeStats();
        var row = table.At(Math.Min(level, MaxTableLevel))
                  ?? table.At(table.ByLevel.Keys.Min())
                  ?? throw new InvalidOperationException($"{table.ClassName} has no rows");

        var b = container.Base;
        b[Stat.Str] = row.Str + free.Str;
        b[Stat.Con] = row.Con + free.Con;
        b[Stat.Dex] = row.Dex + free.Dex;
        b[Stat.Int] = row.Int + free.Int;
        b[Stat.Men] = row.Men + free.Men;

        // MaxHP and MaxSP are stored columns, so there is no curve to model. A formula here would be a
        // guess competing with a number the server already has.
        b[Stat.MaxHP] = row.MaxHp;
        b[Stat.MaxSP] = row.MaxSp;
    }

    /// <summary>Fold an equipped item into the <see cref="StatModifier.Item"/> clusters.
    ///
    /// <para>Flat columns accumulate into Plus. Rate columns compound into Rate: two items each at 1100
    /// permille give 1210, not 1200, because the container's rate half is itself scaled rather than summed.
    /// Identity (1000) contributions leave the slot untouched.</para></summary>
    public static void Equip(ParameterContainer container, EquipmentPiece item)
    {
        var plus = container.Plus(StatModifier.Item);
        plus[Stat.WCmin] += item.MinWC;
        plus[Stat.WCmax] += item.MaxWC;
        plus[Stat.AC] += item.AC;
        plus[Stat.MAmin] += item.MinMA;
        plus[Stat.MAmax] += item.MaxMA;
        plus[Stat.MR] += item.MR;
        plus[Stat.TH] += item.TH;
        plus[Stat.TB] += item.TB;
        plus[Stat.Critical] += item.CriRate;
        plus[Stat.CriticalTB] += item.CrlTB;
        plus[Stat.ShieldAC] += item.ShieldAC;
        plus[Stat.HitRate] += item.HitRatePlus;
        plus[Stat.EvaRate] += item.EvaRatePlus;
        plus[Stat.MACri] += item.MACriPlus;
        plus[Stat.CriDam] += item.CriDamPlus;
        plus[Stat.MagCriDam] += item.MagCriDamPlus;

        var rate = container.Rate(StatModifier.Item);
        Compound(rate, Stat.WCmin, item.WCRate);
        Compound(rate, Stat.WCmax, item.WCRate);
        Compound(rate, Stat.MAmin, item.MARate);
        Compound(rate, Stat.MAmax, item.MARate);
        Compound(rate, Stat.AC, item.ACRate);
        Compound(rate, Stat.MR, item.MRRate);
    }

    private static void Compound(ParameterCluster rate, Stat stat, int permille)
    {
        if (permille == ParameterCluster.RateIdentity) return;
        rate[stat] = (int)((long)rate[stat] * permille / 1000);
    }

    /// <summary>Build a whole character: class table, level, spent points and worn gear.</summary>
    public static ParameterContainer Build(
        ClassParamTable table,
        int level,
        FreeStats? freeStats = null,
        IEnumerable<EquipmentPiece>? equipment = null)
    {
        var container = new ParameterContainer();
        StorePure(container, table, level, freeStats);
        foreach (var piece in equipment ?? [])
            Equip(container, piece);
        return container;
    }
}
