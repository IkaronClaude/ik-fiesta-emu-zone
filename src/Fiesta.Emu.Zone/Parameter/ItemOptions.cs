namespace Fiesta.Emu.Zone.Parameter;

/// <summary>`RandomOptionType` — what an item's option slot can grant.
///
/// <para>This is the vocabulary shared by SOCKETED GEMS and random/rolled item options: both end up as
/// `ItemOptionStorage::Element` pairs of <c>{ itemoption_type, itemoption_value }</c> on the item, read
/// back with `iac_FindOption(item, type)`.</para>
///
/// <para>⭐ Two of the fifteen are the crit pair, and they map straight onto the terms
/// `roe_CriticalRate` already reads: <see cref="ROT_CRI"/> onto `Item.Rate[CriDamRate]` and
/// <see cref="ROT_CRITICAL_TB"/> onto `Item.Rate[CriticalTB]`, the defender's subtrahend. So a socketed
/// crit gem is not a separate mechanism — it is one more contributor to the same `Item` cluster sum that
/// a live container read already measured as weapon 90 + costume 70 + earrings 40 = 200.</para></summary>
public enum RandomOptionType
{
    ROT_STR = 0,
    ROT_CON = 1,
    ROT_DEX = 2,
    ROT_INT = 3,
    ROT_MEN = 4,
    ROT_TH = 5,
    /// <summary>Critical rate — reaches `Item.Rate[CriDamRate]`.</summary>
    ROT_CRI = 6,
    ROT_WC = 7,
    ROT_AC = 8,
    ROT_MA = 9,
    ROT_MR = 10,
    ROT_TB = 11,
    /// <summary>The defender-side critical block — `Item.Rate[CriticalTB]`, which `roe_CriticalRate`
    /// SUBTRACTS.</summary>
    ROT_CRITICAL_TB = 12,
    ROT_DEMANDLVDOWN = 13,
    ROT_MAXHP = 14,
}

/// <summary>`ItemOptionStorage` — an item's option list, which is where a socketed gem's bonus lives.
///
/// <para>The weapon attribute (`ShineItemAttr_Weapon`) carries the whole picture in one place, and it is
/// worth reading as a unit because it shows four of the operator's "modifiers we aren't processing"
/// sitting side by side:</para>
/// <code>
/// +0x00 upgrade              u8      <- the +N ENCHANT level      -> Upgrade cluster
/// +0x01 strengthen           u8
/// +0x07 mobkills[18]                 <- the LICENSE kill counts   -> WeaponTitle cluster
/// +0x19 CharacterTitleMobID  u16
/// +0x30 gemSockets[9]                <- { elementalGemID, restCount }
/// +0x39 maxSocketCount       u8
/// +0x3A createdSocketCount   u8
/// +0x40 option              ItemOptionStorage   <- 24 { type, value } pairs -> Item cluster
/// </code>
///
/// <para>⚠️ <b>A socket is not its own stat channel.</b> `gemSockets` records WHICH gem is in each slot
/// and how many uses remain; the resulting stat is an ordinary option pair, and options land in the
/// `Item` cluster like every other piece of gear. That is why the live crit measurement summed cleanly:
/// there is one path, not one per source.</para>
///
/// <para>`EnchantSocketRateTable` is about the enchanting PROCESS — the chance of adding socket 0, 1 or 2
/// by item grade — not about what a socket grants.</para></summary>
public sealed class ItemOptions
{
    /// <summary>`ItemOptionStorage::Element[24]`.</summary>
    public const int MaxOptions = 24;

    /// <summary>`ShineItemAttr_Weapon::gemSockets[9]`.</summary>
    public const int MaxSockets = 9;

    private readonly List<(RandomOptionType Type, int Value)> _options = [];

    public IReadOnlyList<(RandomOptionType Type, int Value)> Options => _options;

    /// <summary>Add one option pair. Nothing dedupes: a type may appear more than once and the values
    /// accumulate at the cluster, which is how several sockets of the same kind stack.</summary>
    public ItemOptions With(RandomOptionType type, int value)
    {
        _options.Add((type, value));
        return this;
    }

    /// <summary>`iac_FindOption(item, type)` — the total for one option type, or 0 when absent.
    ///
    /// <para>0 is the real absent-value, and it is also a legitimate stored value; the original makes no
    /// distinction either.</para></summary>
    public int Find(RandomOptionType type)
        => _options.Where(o => o.Type == type).Sum(o => o.Value);

    /// <summary>Which container slot an option type contributes to. Only the entries the damage engine
    /// reads are mapped; the rest are real options that this simulation has no use for yet, and are
    /// returned as <c>null</c> rather than guessed at.</summary>
    public static Stat? StatFor(RandomOptionType type) => type switch
    {
        RandomOptionType.ROT_STR => Stat.Str,
        RandomOptionType.ROT_CON => Stat.Con,
        RandomOptionType.ROT_DEX => Stat.Dex,
        RandomOptionType.ROT_INT => Stat.Int,
        RandomOptionType.ROT_MEN => Stat.Men,
        RandomOptionType.ROT_TH => Stat.TH,
        RandomOptionType.ROT_TB => Stat.TB,
        RandomOptionType.ROT_AC => Stat.AC,
        RandomOptionType.ROT_MR => Stat.MR,
        RandomOptionType.ROT_CRI => Stat.CriDamRate,
        RandomOptionType.ROT_CRITICAL_TB => Stat.CriticalTB,
        RandomOptionType.ROT_MAXHP => Stat.MaxHP,
        // ROT_WC and ROT_MA name a PAIR of bounds rather than one slot, and ROT_DEMANDLVDOWN is a
        // requirement reduction with no combat effect at all. Left unmapped on purpose.
        _ => null,
    };
}
