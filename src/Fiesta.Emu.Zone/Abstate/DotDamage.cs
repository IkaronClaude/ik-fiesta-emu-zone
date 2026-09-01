using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Abstate;

/// <summary>Damage over time — `SubAbnormalStateActorPoison::sasa_Routine` (0x0040D4C0) and the two
/// functions it leans on.
///
/// <para>The whole mechanic is small once the pieces are lined up, and every piece was already half-read
/// somewhere else in this project:</para>
///
/// <code>
/// if (target holds abstate 291 or 499) return;              // no tick at all
/// if (!subState has SAA_DOTDAMAGE) return 0;
/// damage = subState's SAA_DOTDAMAGE arg + DotDamageAppend(target, subState.Type)
/// if (damage &lt; 1) damage = 1
/// damage = min(damage, target's current HP)
/// </code>
///
/// <para><b>291 is `StaImmortal`</b> — the spawn invulnerability this project confirmed on the wire. So it
/// blocks poison ticks as well as swings, which is one more thing that "invulnerable" turns out to mean
/// literally.</para></summary>
public static class DotDamage
{
    /// <summary>Abstate ids that suppress a DoT tick entirely. `sasa_Routine@Poison+0xDB` and `+0xF0` test
    /// exactly these two, by calling the object's abstate-is-set predicate.</summary>
    public const int StaImmortal = 0x123;    // 291 — spawn invulnerability

    /// <inheritdoc cref="StaImmortal"/>
    public const int SuppressesDotOther = 0x1F3;   // 499 — not yet identified by name

    /// <summary>Which `DotDamagePlus` member a sub-state's TYPE draws from.
    ///
    /// <para>`smo_DotDamageAppend` (0x00408A60) switches on the sub-state's type byte at +0x26, offset by
    /// -0x16 and bounded at 0x3E, through a case table at 0x408B9C into bodies at 0x408B84. Five of the
    /// fifty-seven types map to a member and the rest fall through to zero:</para>
    ///
    /// <code>
    /// 0x16 -&gt; Blooding    0x21 -&gt; Poison    0x22 -&gt; Desease    0x53 -&gt; Burn    0x54 -&gt; PitBlooding
    /// </code>
    ///
    /// <para>This closes a loop: `SAA_ADDPOISONDMG` writes `DotDamagePlus.Poison` and a sub-state of type
    /// 0x21 is what reads it back. The action names and the member names agree, which is not something a
    /// mis-decoded offset would produce.</para></summary>
    public static ContainerField? MemberForSubStateType(int subStateType) => subStateType switch
    {
        0x16 => ContainerField.DotDamagePlusBlooding,
        0x21 => ContainerField.DotDamagePlusPoison,
        0x22 => ContainerField.DotDamagePlusDesease,
        0x53 => ContainerField.DotDamagePlusBurn,
        0x54 => ContainerField.DotDamagePlusPitBlooding,
        _ => null,
    };

    /// <summary>`ShineMobileObject::smo_DotDamageAppend` — the TARGET's own bonus to this kind of DoT.
    ///
    /// <para>Read off the target's container, not the caster's: the fields are written by the
    /// `SAA_ADD*DMG` / `SAA_SUBTRACT*DMG` family, so a debuff on the victim makes their poison hit harder
    /// and a buff makes it hit softer. A type with no member contributes 0.</para></summary>
    public static int Append(ParameterContainer target, int subStateType)
        => MemberForSubStateType(subStateType) is { } field ? target[field] : 0;

    /// <summary>One tick's damage, or 0 when the state does not deal any.
    ///
    /// <para><paramref name="dotDamageArg"/> is the sub-state row's argument for
    /// <see cref="SubAbstateAction.SAA_DOTDAMAGE"/>; pass <c>null</c> when the row has no such action, and
    /// the tick deals nothing. The floor of 1 is the server's (`sasa_GetDamage+0xA5`), and it applies
    /// AFTER the append — so a target whose `SubtractPoisonDmg` buff more than cancels the poison still
    /// takes 1, not 0.</para>
    ///
    /// <para>The result is capped at <paramref name="currentHp"/>: a DoT does not overkill, it finishes
    /// the target exactly.</para></summary>
    public static int Tick(ParameterContainer target, int subStateType, int? dotDamageArg, int currentHp)
    {
        if (dotDamageArg is not { } baseDamage) return 0;

        var damage = baseDamage + Append(target, subStateType);
        if (damage < 1) damage = 1;
        return Math.Min(damage, Math.Max(0, currentHp));
    }

    /// <summary>Whether any state on the target suppresses DoT ticks outright.</summary>
    public static bool IsSuppressed(AbstateListInObject states)
        => states.IsSet(StaImmortal) || states.IsSet(SuppressesDotOther);
}
