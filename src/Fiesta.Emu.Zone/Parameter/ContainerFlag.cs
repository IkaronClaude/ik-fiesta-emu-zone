namespace Fiesta.Emu.Zone.Parameter;

/// <summary>`Parameter::Container::flag` (+0x0CCE) — one byte, three bits, all three named by the PDB.
///
/// <para>These are the only BEHAVIOURAL output of the whole abnormal-state system: everything else an
/// abstate does is a number. They are no-ops for the damage formula — a stunned mob takes and deals normal
/// damage — and they are the entire content of a stun for the tactic state machine.</para>
///
/// <para>Two kinds of immobilisation, deliberately separate: `SAA_NOMOVE` sets
/// <see cref="CannotMoveEntangle"/> normally, and <see cref="CannotMoveStun"/> when the sub-state's type at
/// +0x26 is 0x15 or 0x60.</para></summary>
[Flags]
public enum ContainerFlag
{
    None = 0,
    CannotMoveStun = 1,
    CannotMoveEntangle = 2,
    CannotAttack = 4,
}

/// <summary>The `Parameter::Container` fields past the clusters that an abstate action can write.
///
/// <para>Not the whole second tier — only what `aeo_ParameterEnchant` reaches, which is what
/// `tools/abstate_actions.py` found by decoding every handler's write offset against the PDB's own field
/// list. The `DotDamagePlus*` five are members of a nested ten-byte struct at +0x0CC0, and their names are
/// the reason the bleed/poison actions can be confirmed rather than assumed: `SAA_ADDPOISONDMG` writes the
/// member called <see cref="DotDamagePlusPoison"/>.</para></summary>
public enum ContainerField
{
    /// <summary>+0x0CCC, a SIGNED short. Subtracted from the hit rating by `roe_FreeStatHitRate` when the
    /// attack's range exceeds 300. Written by `SAA_EVASIONAMOUNT`.</summary>
    RangeEvasion,

    /// <summary>+0x0CD0. A fixed miss chance in permille that REPLACES the whole hit-rate computation —
    /// see <see cref="Fiesta.Emu.Zone.Combat.DamageCalculator.HitRate"/>. Written by `SAA_MISSRATE`.</summary>
    MissPercentFix,

    /// <summary>+0x0CD2. `smo_SwingDamage`'s tail multiplies the damage by this and reflects it, gated on
    /// <see cref="RangeOver"/>. Written by `SAA_REFLECTDAMAGE`.</summary>
    DamageReflection,

    /// <summary>+0x0CD4. Written by `SAA_METAABILITY`.</summary>
    ChangeAbilityInfo,

    /// <summary>+0x0CD6. ⚠️ ASSIGNED, not accumulated — `SAA_HEALRATE` is the only action in the table
    /// that uses `mov` where every neighbour uses `add`, so two sources do not stack.</summary>
    HealRate,

    /// <summary>+0x0CCA. Written by `SAA_USESPRATE`.</summary>
    SPRate,

    /// <summary>+0x0DA4 / +0x0DA6. Assigned by `SAA_ACSHIELDRATE` / `SAA_MRSHIELDRATE`.</summary>
    PhysicalImmuneRate,

    /// <inheritdoc cref="PhysicalImmuneRate"/>
    MagicalImmuneRate,

    /// <summary>+0x0DA8. The distance beyond which `smo_SwingDamage` skips damage reflection. Written by
    /// `SAA_RANGEOVER`.</summary>
    RangeOver,

    /// <summary>The five members of the nested `DotDamagePlus` struct at +0x0CC0.</summary>
    DotDamagePlusBurn,

    /// <inheritdoc cref="DotDamagePlusBurn"/>
    DotDamagePlusPoison,

    /// <inheritdoc cref="DotDamagePlusBurn"/>
    DotDamagePlusDesease,

    /// <inheritdoc cref="DotDamagePlusBurn"/>
    DotDamagePlusBlooding,

    /// <inheritdoc cref="DotDamagePlusBurn"/>
    DotDamagePlusPitBlooding,
}

