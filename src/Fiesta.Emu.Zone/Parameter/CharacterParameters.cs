namespace Fiesta.Emu.Zone.Parameter;

/// <summary>The stat contribution of one equipped item — the `ItemInfo.shn` columns that feed a cluster.
///
/// <para>The flat values (<c>MinWC</c>, <c>AC</c>, everything ending in <c>Plus</c>) go to
/// <see cref="StatModifier.Item"/>'s Plus cluster. The permille ones — <c>WCRate</c>, <c>MARate</c>,
/// <c>ACRate</c>, <c>MRRate</c> — go to <see cref="StatModifier.ItemPowerRate"/>, <b>not</b> to Item's own
/// Rate half; see <see cref="CharacterParameters.Equip"/> for why that distinction matters.</para>
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

    /// <summary>Base HP granted per point of Constitution above the class table's own value — the
    /// <c>lea ebx, [eax + eax*4]</c> in `CharClass::MaxHP`. Same figure for SP per point of MentalPower.</summary>
    public const int HpPerConstitutionPoint = 5;
    public const int SpPerMentalPowerPoint = 5;

    /// <summary>The value `c_Storepure` writes into MoveSpeed, HPRecover and SPRecover — literally
    /// <c>mov eax, 0x3E8</c>. It is 1000 because those slots are read as permille elsewhere; here it is
    /// simply the base each starts from.</summary>
    public const int BaseUnitySlotValue = 1000;

    /// <summary>`Parameter::Container::c_Storepure` — fill the base cluster for a class at a level.
    ///
    /// <para>Ported statement for statement. The parts that are easy to get wrong:</para>
    /// <list type="bullet">
    ///   <item>Each primary is <b>table value + free stat points</b>, not the table value alone.</item>
    ///   <item>The five primaries are stored in CLUSTER order (Str, Con, Dex, Int, Men), which is not the
    ///         TABLE's column order (Strength, Constitution, Intelligence, Wizdom, Dexterity, MentalPower).
    ///         Dexterity and Intelligence swap places. The original reads row offsets +4, +8, +0x14, +0xC,
    ///         +0x18 into slots 0..4, which is what pins the crossover.</item>
    ///   <item><b>The weapon and armour slots really are left at zero.</b> `c_Storepure` fills slots 5..14
    ///         from eight virtual methods — <c>CharClass::WC</c>, <c>::AC</c>, <c>::TH</c>, <c>::TB</c>,
    ///         <c>::MA</c>, <c>::MR</c>, <c>::MH</c>, <c>::MB</c> — and all eight are the SAME two-instruction
    ///         body at 0x00449600: <c>xor eax, eax; ret 8</c>. Identical Code Folding merged them because
    ///         they are identical, and no player class overrides any of them (there is exactly one symbol
    ///         each across all 32 CharClass subclasses). So a player's base weapon and armour values are
    ///         genuinely zero and every point of them comes from equipment.</item>
    ///   <item><b>MaxHP and MaxSP are NOT cluster slots that `c_Storepure` fills.</b> It stops at slot 31.
    ///         They are computed on demand by <see cref="MaxHp"/> / <see cref="MaxSp"/>.</item>
    /// </list></summary>
    public static void StorePure(
        ParameterContainer container,
        ClassParamTable table,
        int level,
        FreeStats? freeStats = null)
    {
        var free = freeStats ?? new FreeStats();
        var row = Row(table, level);

        var b = container.Base;
        b[Stat.Str] = row.Str + free.Str;
        b[Stat.Con] = row.Con + free.Con;
        b[Stat.Dex] = row.Dex + free.Dex;
        b[Stat.Int] = row.Int + free.Int;
        b[Stat.Men] = row.Men + free.Men;

        // Slots 5..14 (WCmin..MB) stay zero -- see the remark above; that is the ported behaviour, not a gap.

        // The tail of c_Storepure: three slots start at 1000, then slots 22..31 are explicitly zeroed.
        b[Stat.MoveSpeed] = BaseUnitySlotValue;
        b[Stat.HPRecover] = BaseUnitySlotValue;
        b[Stat.SPRecover] = BaseUnitySlotValue;
        foreach (var slot in new[]
                 {
                     Stat.CastingTime, Stat.Critical, Stat.PhisycalWeaponMastery, Stat.MagicalWeaponMastery,
                     Stat.ShieldAC, Stat.HitRate, Stat.EvaRate, Stat.MACri, Stat.CriDam, Stat.MagCriDam,
                 })
            b[slot] = 0;
    }

    private static ClassParamRow Row(ClassParamTable table, int level)
        => table.At(Math.Min(level, MaxTableLevel))
           ?? table.At(table.ByLevel.Keys.Min())
           ?? throw new InvalidOperationException($"{table.ClassName} has no rows");

    /// <summary>`CharClass::MaxHP` (0x00449610) — a character's maximum HP.
    ///
    /// <para><b>Not a table lookup, and not a curve either.</b> It is the class table's stored <c>MaxHP</c>
    /// column for the level, plus five per point of Constitution ABOVE what the table says that level has:</para>
    ///
    /// <code>MaxHP = row.MaxHP + (cluster.Con - row.Constitution) * 5</code>
    ///
    /// <para>Since `c_Storepure` sets <c>cluster.Con = row.Constitution + freeStatPoints</c>, the second term
    /// is exactly the player's spent Constitution points. Reading the level row alone — which is what an
    /// earlier version of this port did — silently robs every character of the HP it earned by spending
    /// points.</para>
    ///
    /// <para>The row's MaxHP is read as a <b>word</b> (<c>movzx eax, word ptr [ecx+0x74]</c>), and 0x74 is
    /// column 29, which is <c>MaxHP</c>, typed <c>Word</c> in the table header. The offset and the schema
    /// agree, which is the check that the column identification is right.</para></summary>
    public static int MaxHp(ClassParamTable table, int level, ParameterCluster cluster)
    {
        var row = Row(table, level);
        return row.MaxHp + (cluster[Stat.Con] - row.Con) * HpPerConstitutionPoint;
    }

    /// <summary>`CharClass::MaxSP` (0x00449660) — the same shape over MentalPower.
    ///
    /// <para>⚠️ TWO classes override it — <b>Sentinel and Savior</b> — both to 0x0064F610, which is
    /// <c>mov eax, 1; ret 8</c>: a flat 1 SP whatever the level or stats.</para>
    ///
    /// <para>Worth knowing how that was found, because searching the PDB for <c>?MaxSP@CharClass*</c> reports
    /// only Sentinel. Identical Code Folding merged Savior's identical body into the same address, and only
    /// one name survives there — so the symbol search UNDER-COUNTS overrides. Reading vtable slot 9 across
    /// the family (`tools/vtables.py --family CharClass --overrides`) finds both, because a slot holds an
    /// address whether or not a symbol was emitted for it.</para></summary>
    public static int MaxSp(ClassParamTable table, int level, ParameterCluster cluster)
    {
        var row = Row(table, level);
        return row.MaxSp + (cluster[Stat.Men] - row.Men) * SpPerMentalPowerPoint;
    }

    /// <summary>Fold an equipped item into the container.
    ///
    /// <para>Flat columns accumulate into <see cref="StatModifier.Item"/>'s Plus cluster.</para>
    ///
    /// <para><b>⚠️ Rate columns go to <see cref="StatModifier.ItemPowerRate"/>, NOT to Item's Rate half.</b>
    /// This port had them on Item.Rate, inferred from the shared "Item" prefix, and that cluster is one of
    /// the five `c_MakeTotal` never folds in — so item rate bonuses silently did nothing.</para>
    ///
    /// <para>`ShinePlayer::so_RecalcEquipParam` settles it: it writes `ItemPowerRate.Rate` for exactly AC,
    /// MR, WCmin, WCmax, MAmin and MAmax — which is `ACRate`, `MRRate`, `WCRate` (covering both weapon
    /// bounds) and `MARate` (covering both magic bounds). `roe_AC`, `roe_MinWC` and `roe_MR` read that same
    /// cluster and never read Item.Rate. The cluster's name means what it says: the rate half of item power.</para>
    ///
    /// <para>Rates compound: two items at 1100 permille give 1210, not 1200, because the rate half is scaled
    /// rather than summed. ⚠️ The original recalculates from the whole bag at once rather than folding items
    /// in one at a time, so the compounding ORDER here is this port's choice; with permille multiplication it
    /// is order-independent apart from truncation.</para></summary>
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

        var rate = container.Rate(StatModifier.ItemPowerRate);
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
