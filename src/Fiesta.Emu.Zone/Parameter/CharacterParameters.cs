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
    int CriDamPlus = 0, int MagCriDamPlus = 0,
    int Str = 0, int Con = 0, int Dex = 0, int Int = 0, int Men = 0);

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

        // ⭐ THE CLASS ROW ALONE. Free-stat points do NOT enter the base cluster.
        //
        // ⚠️ This used to read `row.Str + free.Str`, on the strength of `c_Storepure` computing each
        // primary as `call [vtable+0x8E0] + classRow[column]`. The call is real; reading it as "the free
        // stat for this stat" is not — it is the SAME no-argument virtual invoked five times, so it cannot
        // carry a per-stat allocation, and `MageDamageLvl60` shows it returning 0:
        //
        //     login, no gear, free stat all zero -> reported Str 43 Con 138 Dex 157 Int 273 Men 186,
        //     the level-60 Enchanter class row to the unit.
        //
        // And later, with 50 Int / 25 Men allocated, the reported Int is 288. The wire's primaries are
        // `Total` (proved by `c_compare@Cluster`, and by the pet's equip moving them), and
        // (273 + 2 pet) * 1.05 = 288. Had the base carried the 50 points it would read 341.
        //
        // Free-stat points feed the DERIVED tables instead -- `Int.MAAbsolute`, `Con.ACAbsoulte`,
        // `Men.MaxSP`, `Dex.THRate`, `Men.CriRate` -- which is where the damage path already adds them.
        var b = container.Base;
        b[Stat.Str] = row.Str;
        b[Stat.Con] = row.Con;
        b[Stat.Dex] = row.Dex;
        b[Stat.Int] = row.Int;
        b[Stat.Men] = row.Men;
        _ = free;

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
    public static int MaxHp(ClassParamTable table, int level, ParameterCluster cluster,
                            int freeStatConPoints = 0)
    {
        var row = Row(table, level);
        // Two distinct sources, and they must not be conflated. The cluster term picks up Constitution
        // from GEAR and the rate layers; the free-stat term is `Con.MaxHP = 5n` from the free-stat table.
        // Spent points do not enter the base cluster -- see `StorePure` -- so without the second term a
        // character loses every point of HP it bought.
        return row.MaxHp
               + (cluster[Stat.Con] - row.Con) * HpPerConstitutionPoint
               + freeStatConPoints * HpPerConstitutionPoint;
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
    public static int MaxSp(ClassParamTable table, int level, ParameterCluster cluster,
                            int freeStatMenPoints = 0, int passiveMaxSp = 0)
    {
        var row = Row(table, level);

        // ⭐ THREE TERMS, and the capture pins all of them. `MageDamageLvl60`'s level-60 Enchanter:
        //
        //     class row                                              1826
        //     + (Men 197 - row 186) * 5                              +  55   = 1881
        //     + WisdomMastery06's MaxSP column                       + 360   = 2241
        //     + 25 MentalPower free-stat points at Men.MaxSP = 5n    + 125   = 2366   <- reported
        //
        // The middle term comes from the passive's own row; the last from the free-stat table, which is a
        // SEPARATE contribution and not the same thing as the points already showing up in `cluster[Men]`
        // -- free-stat points do not raise the primary, they feed the derived tables.
        return row.MaxSp
               + (cluster[Stat.Men] - row.Men) * SpPerMentalPowerPoint
               + passiveMaxSp
               + freeStatMenPoints * FreeStatMenSpPerPoint;
    }

    /// <summary>`Men.MaxSP` in the free-stat table is <c>5n</c> — verified across all 181 entries by
    /// reading a live zone server.</summary>
    public const int FreeStatMenSpPerPoint = 5;

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

        // ⭐ PRIMARY STATS FROM `GradeItemOption.shn`, and they are NOT in `ItemInfo.shn` at all.
        //
        // The operator named the mechanic ("it's like an accessory but +5 on all stats") and the wire
        // confirms it exactly: in `MageDamageLvl60.pcapng`, `EQUIP slot 25 <- 30817` (`MiniPino01_7`, the
        // Mini Phino pet) is followed 35 ms later by a `CHANGEPARAM` moving Str 43->45, Con 138->140,
        // Dex 157->159, Int 273->275 and Men 186->188 -- <b>+2 on every one</b>, matching that item's
        // `GradeItemOption` row to the unit, with AC following Con (+2) and MaxHp following it by five
        // (+10).
        //
        // ⚠️ An earlier pass here dismissed the four cosmetics as "cosmetics" after checking `ItemInfo`
        // and finding no stat columns. `ItemInfo` HAS no stat columns -- for any item. Concluding "no
        // stats" from that file was reading absence in the wrong table, and it is the second time these
        // same four items have hidden something (the first was their `CriRate`).
        plus[Stat.Str] += item.Str;
        plus[Stat.Con] += item.Con;
        plus[Stat.Dex] += item.Dex;
        plus[Stat.Int] += item.Int;
        plus[Stat.Men] += item.Men;

        plus[Stat.WCmin] += item.MinWC;
        plus[Stat.WCmax] += item.MaxWC;
        plus[Stat.AC] += item.AC;
        plus[Stat.MAmin] += item.MinMA;
        plus[Stat.MAmax] += item.MaxMA;
        plus[Stat.MR] += item.MR;
        plus[Stat.TH] += item.TH;
        plus[Stat.TB] += item.TB;
        plus[Stat.CriticalTB] += item.CrlTB;
        plus[Stat.ShieldAC] += item.ShieldAC;
        plus[Stat.HitRate] += item.HitRatePlus;
        plus[Stat.EvaRate] += item.EvaRatePlus;
        plus[Stat.MACri] += item.MACriPlus;
        plus[Stat.CriDam] += item.CriDamPlus;
        plus[Stat.MagCriDam] += item.MagCriDamPlus;

        // CRIT RATE IS A SUM OF EVERY EQUIPPED SOURCE, PLUS THE MEN FREE-STAT TERM.
        // VERIFIED on a live character, 2026-09-01.
        //
        // `roe_CriticalRate` reads exactly one slot from the Item layer -- Item.Rate[CriDamRate] (+0x218)
        // -- and a live player's container was read out of zone02 to check what is in it. The character
        // (ArcherZero, Forest of Tides) was identified by matching its whole displayed stat block against
        // the container's Total: WC 89-103, MA 27, AC 83, MR 70, Aim 137, Evasion 123, primaries
        // 54/40/79/26/36. Every one matched. In that container:
        //
        //     Item.Rate[CriDamRate] = 40        <- 4%, the sum of the equipped crit sources
        //     Item.Plus[Critical]   = 0
        //     Total[Critical]       = 0         <- slot 23 is NOT the crit path at all
        //
        // So the equipped sources (weapon, jewellery, armour, costumes) all sum into CriDamRate's Item
        // rate half, `roe_FreeStatCriRate` adds the MEN term, and the `Critical` slot this port used to
        // write is dead. A displacement scan does not find the WRITER -- a loop with a computed slot index
        // does not show up in one -- but what lands there is now measured rather than inferred.
        //
        // ⚠️ THIS WAS DOCUMENTED AND NOT DONE. The paragraph above has been here since 2026-09-01 and the
        // code went on writing `Item.Plus[Critical]` -- the slot it says is dead -- and never wrote the
        // live one, so every character's critical rate was 0 whatever they wore. A second capture caught
        // it: `MageDamageLvl60`'s Enchanter crits on 15 of 67 landed skill hits (224 permille), and its
        // gear plus `FreeStatMen.CriRate` predicts 230.
        //
        // ⚠️ AND IT IS A RAW SUM, not a rate. `c_clear` seeds this cluster from `parameter_eraser_rate`,
        // whose slot 32 holds 1000 -- yet the live container read 40 and this capture needs 180. That is
        // why `ParameterContainer` zeroes `Item.Rate[CriDamRate]` and `[MagCriDamRate]` on construction
        // (see `ParameterCluster`'s per-layer zero list): the slot is a permille ACCUMULATOR living in a
        // rate cluster, and `ItemOptions` already routes `ROT_CRI` random options onto the same slot.
        // A plain `+=` is therefore right, and matches how a random option reaches it.
        container.Rate(StatModifier.Item)[Stat.CriDamRate] += item.CriRate;

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
    /// <summary>⭐ `ShinePlayer::so_RecalcLastParam` (0x004CB5F0) — the LastTune layer, and the answer to a
    /// nine-point armour gap that stood open for three sessions.
    ///
    /// <para>It seeds `LastTune.Plus` with the plus eraser and `LastTune.Rate` with the rate eraser, then
    /// does exactly one thing:</para>
    ///
    /// <code>
    /// call sdb_SpecSkillStruct@PassiveDataBox   ; the ONE special skill
    /// movzx edx, word ptr [eax]                 ; its id
    /// call cpl_IsLearn@CharacterPassiveList     ; learned?
    /// je   skip
    /// mov  eax, 0x32                            ; 50
    /// add  [LastTune.Rate + Str], eax           ; 1000 -> 1050
    /// add  [LastTune.Rate + Con], eax
    /// add  [LastTune.Rate + Dex], eax
    /// add  [LastTune.Rate + Int], eax
    /// add  [LastTune.Rate + Men], eax
    /// </code>
    ///
    /// <para><b>The special skill is named in the PDB, so this is not an inference:</b>
    /// <c>PassiveDataBox::SpecialSkill</c> is a two-byte struct with exactly one field,
    /// <c>ss_PowerOfLove</c>. "Power of Love" grants <b>+5% to all five primaries</b>, and nothing
    /// else in the game writes this layer.</para>
    ///
    /// <para>⚠️ <c>c_MakeTotal</c> applies <c>*= LastTune.Rate</c> <b>LAST</b>, so it multiplies the base,
    /// the gear and the pet together — and the truncating permille divide is why the result is
    /// <c>floor(x * 1.05)</c> rather than a rounded value. `MageDamageLvl60`'s character shows it on all
    /// five at once: 45→47, 140→147, 159→166 (166.95 truncates to 166, not 167), 275→288, 188→197. Its
    /// DEF followed Con, which is the whole of the armour discrepancy.</para>
    ///
    /// <para>The passive carries NO stat columns in `PassiveSkill.shn` — it cannot, because its effect is
    /// this hard-coded special case. Looking for its bonus in the table finds nothing and proves nothing;
    /// see the item-table coverage note for the same trap.</para></summary>
    public const int PowerOfLoveRateBonus = 50;

    /// <summary>The five slots `so_RecalcLastParam` touches, in its order.</summary>
    private static readonly Stat[] PowerOfLoveSlots =
        [Stat.Str, Stat.Con, Stat.Dex, Stat.Int, Stat.Men];

    /// <summary>Apply the LastTune layer. <paramref name="hasPowerOfLove"/> is
    /// <c>cpl_IsLearn(ss_PowerOfLove)</c>.</summary>
    public static void RecalcLastParam(ParameterContainer container, bool hasPowerOfLove)
    {
        if (!hasPowerOfLove) return;
        var rate = container.Rate(StatModifier.LastTune);
        foreach (var slot in PowerOfLoveSlots) rate[slot] += PowerOfLoveRateBonus;
    }

    public static ParameterContainer Build(
        ClassParamTable table,
        int level,
        FreeStats? freeStats = null,
        IEnumerable<EquipmentPiece>? equipment = null,
        bool hasPowerOfLove = false)
    {
        var container = new ParameterContainer();
        StorePure(container, table, level, freeStats);
        foreach (var piece in equipment ?? [])
            Equip(container, piece);
        RecalcLastParam(container, hasPowerOfLove);
        return container;
    }
}
