namespace Fiesta.Emu.Zone.Skill;

/// <summary>`SkillSpecial` — `ActiveSkill.shn`'s `SpecialIndexA`..`SpecialIndexE`, the tag half of the five
/// (index, value) pairs every skill row carries.
///
/// <para>`sdb_Load+0x58E` reads each pair straight off the row and hands it to
/// `SkillDataIndex::sdi_SetArgument` (0x00584800), which is a 73-entry jump table writing
/// <c>{exist = 1; value = v}</c> into one `EnumStruct` slot of the `SkillDataIndex`. So a skill's
/// "specials" are how the damage engine, the targeting and the effect code all get their per-skill
/// parameters, and <see cref="SkillBlastCascade"/> reads three of them.</para>
///
/// <para><b>⚠️ The index is NOT the slot's array position, and computing one from the other is wrong.</b>
/// The obvious arithmetic — <c>offset = 0x78 + (index - 1) * 8</c> — holds for the first nine entries and
/// then breaks: <see cref="ThHpUp"/> (10) writes +0x0F0 where the formula says +0x0C0, and
/// <see cref="DispelRandom"/> (26) writes +0x0C0 where it says +0x1A0. The order of the fields in
/// `SkillDataIndex` and the order of this enum are simply different, and only the jump table relates
/// them. Every mapping in this file was read out of that table.</para>
///
/// <para><b>Ten indices store nothing at all.</b> `sdi_SetArgument`'s cases for them fall straight to the
/// epilogue, so the skill's `EnumStruct` is never marked present and anything reading it sees "not set" —
/// those specials are consumed somewhere else entirely. They are listed here because a loader that wrote
/// them anyway would invent state the server does not have. See <see cref="Stores"/>.</para>
///
/// <para><b>One slot has two names.</b> <see cref="Dash"/> (36) writes the same `sdi_WARPING` slot as
/// <see cref="Warping"/> (25), so a row carrying both keeps only the later one — which is why this store
/// is keyed by the slot rather than by the index. See <see cref="SkillSpecialArguments.Canonical"/>.</para>
///
/// <para>Values are the PDB's, including its spelling (`SS_Silience`, `SS_POSIONTIMEADD`).</para></summary>
public enum SkillSpecial
{
    None = 0,
    HealAmount = 1,
    Cure = 2,
    Dispel = 3,
    Teleport = 4,
    WholeHealAmount = 5,
    ManaBurn = 6,

    /// <summary>Extra damage against an <see cref="Data.MobType.Undead"/> target, in 1/1000. Read by
    /// <see cref="SkillBlastCascade"/>.</summary>
    UndeadToDmg = 7,

    DispelAll = 8,
    DispelOne = 9,
    ThHpUp = 10,
    DieHpUp = 11,
    Revival = 12,
    MagicFieldTick = 13,
    MagicFieldKeep = 14,
    StealEnchant = 15,
    HpConvertSp = 16,
    HpUpRate = 17,
    FlyDuringCast = 18,
    SilverWing = 19,
    DispelDebuff = 20,
    DispelCurse = 21,
    DispelPoison = 22,
    DispelDisease = 23,
    WholeAggroRate = 24,
    Warping = 25,
    DispelRandom = 26,
    Detect = 27,
    WholeAggroRange = 28,

    /// <summary>Stores nothing — `sdi_SetArgument` has no case body for it.</summary>
    Hide = 29,
    /// <summary>Stores nothing. The PDB's spelling.</summary>
    Silience = 30,
    /// <summary>Stores nothing.</summary>
    Mesmerize = 31,
    /// <summary>Stores nothing.</summary>
    Summon = 32,
    /// <summary>Stores nothing.</summary>
    Metamorphosis = 33,

    CrossCounter = 34,
    DispelDeeper = 35,

    /// <summary>Writes the SAME slot as <see cref="Warping"/>.</summary>
    Dash = 36,

    /// <summary>Stores nothing.</summary>
    DispelBuff = 37,

    Dash2 = 38,

    /// <summary>Stores nothing.</summary>
    HpRate = 39,

    CurseTimeAdd = 40,
    FireTimeAdd = 41,
    HoldMe = 42,
    JWalk = 43,
    /// <summary>The PDB's spelling.</summary>
    PosionTimeAdd = 44,
    ShootMe = 45,
    DmgCollTimeDown = 46,
    AreaType = 47,
    BombDispelAll = 48,
    MagicFieldParty = 49,
    HealFriendRate = 50,
    SetAbstateFriend = 51,
    RandomTargetNumber = 52,
    NextTargetArea = 53,

    /// <summary>Per-step damage REDUCTION, in 1/1000, multiplied by the blast's own wave counter and
    /// capped by <see cref="MaxDmgDownRate"/>. Read by <see cref="SkillBlastCascade"/>.</summary>
    DmgDownRate = 54,

    /// <summary>The cap on <see cref="DmgDownRate"/>'s scaled value. Read by
    /// <see cref="SkillBlastCascade"/>.</summary>
    MaxDmgDownRate = 55,

    ShotSpeed = 56,

    /// <summary>Damage bonus proportional to how much HP the TARGET is missing. Read by
    /// <see cref="SkillBlastCascade"/>.</summary>
    TargetHpDownDmgUpRate = 57,

    Jump = 58,
    SetAbstateMe = 59,
    NotTarget = 60,
    AbKeepTime = 61,
    TopAggroNo = 62,
    RandomTarget = 63,
    MagicFieldDelayStartTime = 64,
    DispelAbstate = 65,
    BmpMagicFieldRotationSpeedRight = 66,

    /// <summary>Stores nothing.</summary>
    MoveChr = 67,
    /// <summary>Stores nothing.</summary>
    HideChrStart = 68,
    /// <summary>Stores nothing.</summary>
    HideChrEnd = 69,

    Recall = 70,
    DmgShare = 71,
    SuckHp = 72,
    RandomTargetSpot = 73,
}

/// <summary>A skill's `SpecialIndex`/`SpecialValue` pairs, resolved the way `sdi_SetArgument` resolves
/// them — the `EnumStruct` block of a `SkillDataIndex`, as far as this port models it.
///
/// <para><b>Absent is not zero.</b> Every consumer of these reads `exist` before `value`, and a rate of 0
/// is a real, different thing from "this skill has no such special": <see cref="SkillSpecial.DmgDownRate"/>
/// at 0 reduces nothing, while absent skips a whole branch that would otherwise divide. So this returns
/// <c>null</c> for absent, never a default.</para></summary>
public sealed class SkillSpecialArguments
{
    /// <summary>The ten indices whose `sdi_SetArgument` case stores nothing, so a skill carrying one has
    /// no `EnumStruct` marked present for it.</summary>
    private static readonly HashSet<SkillSpecial> NoOp =
    [
        SkillSpecial.Hide, SkillSpecial.Silience, SkillSpecial.Mesmerize, SkillSpecial.Summon,
        SkillSpecial.Metamorphosis, SkillSpecial.DispelBuff, SkillSpecial.HpRate,
        SkillSpecial.MoveChr, SkillSpecial.HideChrStart, SkillSpecial.HideChrEnd,
    ];

    private readonly Dictionary<SkillSpecial, int> _values = [];

    /// <summary>A skill with no specials — every `EnumStruct` absent.</summary>
    public static SkillSpecialArguments None { get; } = new();

    /// <summary>Whether `sdi_SetArgument` stores this index at all. <see cref="SkillSpecial.None"/> and
    /// anything above <see cref="SkillSpecial.RandomTargetSpot"/> fall out of the jump table's range
    /// check (<c>index - 1 &gt; 0x48</c>, unsigned, so 0 wraps and is rejected).</summary>
    public static bool Stores(SkillSpecial index)
        => index is > SkillSpecial.None and <= SkillSpecial.RandomTargetSpot && !NoOp.Contains(index);

    /// <summary>The slot an index writes. Only <see cref="SkillSpecial.Dash"/> differs from itself: it
    /// shares `sdi_WARPING` with <see cref="SkillSpecial.Warping"/>, so both are stored under the
    /// latter and a row carrying both keeps whichever came second, exactly as the field would.</summary>
    public static SkillSpecial Canonical(SkillSpecial index)
        => index == SkillSpecial.Dash ? SkillSpecial.Warping : index;

    /// <summary>The value stored in this slot, or <c>null</c> when `exist` is 0.</summary>
    public int? this[SkillSpecial index]
        => _values.TryGetValue(Canonical(index), out var v) ? v : null;

    /// <summary>Apply one `SpecialIndex`/`SpecialValue` pair, as `sdi_SetArgument` does — ignoring the
    /// indices it has no case for, and letting a later pair overwrite an earlier one that shares a
    /// slot.</summary>
    public void Set(SkillSpecial index, int value)
    {
        if (!Stores(index)) return;
        _values[Canonical(index)] = value;
    }

    /// <summary>Build from a row's five pairs, in file order — A through E, because order decides which
    /// of two aliased indices survives.</summary>
    public static SkillSpecialArguments From(params (SkillSpecial Index, int Value)[] pairs)
    {
        var a = new SkillSpecialArguments();
        foreach (var (index, value) in pairs) a.Set(index, value);
        return a;
    }

    /// <summary>Read one `ActiveSkill.shn` row's five pairs, the way `sdb_Load+0x58E` does — A, B, C, D, E
    /// in that order, each through <see cref="Set"/>.</summary>
    /// <param name="row">A row from `ActiveSkill.shn`.</param>
    /// <param name="value">How to read an integer column; pass `ShnFile.Int`. Taken as a delegate so this
    /// stays in the Skill namespace without a Data dependency for one lookup.</param>
    public static SkillSpecialArguments FromRow(IReadOnlyDictionary<string, object> row,
                                                Func<IReadOnlyDictionary<string, object>, string, int> value)
    {
        var a = new SkillSpecialArguments();
        foreach (var slot in "ABCDE")
            a.Set((SkillSpecial)value(row, "SpecialIndex" + slot), value(row, "SpecialValue" + slot));
        return a;
    }
}
