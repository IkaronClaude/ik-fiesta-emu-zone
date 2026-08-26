namespace Fiesta.Emu.Zone.Parameter;

/// <summary>One slot of a <see cref="ParameterCluster"/>.
///
/// <para><b>These are wire and memory indices, not labels.</b> A cluster is 51 consecutive int32s in the
/// server's memory — <c>Parameter::Container::c_clear</c> copies exactly <c>0x33</c> (51) dwords into each
/// one — and every slot below is that array's index. Reordering or renumbering them silently reinterprets
/// every stat.</para>
///
/// <para>The count is the check: 51 names, 51 dwords.</para></summary>
public enum Stat
{
    // The five primaries. c_MakeTotal floors exactly these at 1 after combining, which is how we know
    // they are the first five and not merely conventionally listed first.
    Str = 0, Con, Dex, Int, Men,

    WCmin, WCmax, AC, TH, TB,
    MAmin, MAmax, MR, MH, MB,
    AbsoluteAttack, AbsoluteDefend, AbsoluteHit, AbsoluteBlock,
    MoveSpeed, HPRecover, SPRecover, CastingTime, Critical,
    PhisycalWeaponMastery, MagicalWeaponMastery, ShieldAC,
    HitRate, EvaRate, MACri, CriDam, MagCriDam, CriDamRate, MagCriDamRate,
    AttSpeed, MaxHP, MaxHP_2, MaxSP,
    HPAbsorption_Hitted, SPAbsorption_Hitted, HPAbsorption_Hit, SPAbsorption_Hit,
    CriticalTB, RegistNone, ResistPoison, ResistDeaseas, ResistCurse,
    ResistMoveSpdDown, ResistGTI, MaxLP, LPRecover,
}

/// <summary>Where a stat modification came from.
///
/// <para>A character's stats are not one flat table. The server keeps a <see cref="ParameterCluster"/> per
/// source, each split into a flat <c>Plus</c> half and a permille <c>Rate</c> half, and the damage formula
/// reads several of those halves INDIVIDUALLY rather than reading a combined total. That is why the layers
/// survive into <see cref="ParameterContainer"/> instead of collapsing into one dictionary of effective
/// values: they are inputs to the formula, not a presentation detail.</para>
///
/// <para>The order is the container's memory order — see <see cref="ParameterContainer"/> for the proof.</para></summary>
public enum StatModifier
{
    /// <summary>Equipped gear.</summary>
    Item,
    /// <summary>Gear "power rate" — a second item layer the server tracks apart from <see cref="Item"/>.</summary>
    ItemPowerRate,
    /// <summary>Enhancement / +N upgrade levels.</summary>
    Upgrade,
    /// <summary>Weapon title (the prefix/suffix affix on a named weapon).</summary>
    WeaponTitle,
    /// <summary>Learned passive skills.</summary>
    PassiveSkill,
    /// <summary>Buffs and debuffs currently applied.</summary>
    AbnormalState,
    /// <summary>"Last tune" — the final adjustment layer.</summary>
    LastTune,
}

/// <summary>Which half of a source's contribution — the flat one or the permille one.</summary>
public enum StatHalf
{
    /// <summary>Added. Identity 0.</summary>
    Plus,
    /// <summary>Scaled, in permille. Identity 1000.</summary>
    Rate,
}
