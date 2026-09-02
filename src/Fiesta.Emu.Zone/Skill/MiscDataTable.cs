using Fiesta.Emu.Zone.Abstate;

namespace Fiesta.Emu.Zone.Skill;

/// <summary>`MiscData_VarifyByAbstate::AbnormalStateAttr` — the four conditions a skill can be keyed on.
///
/// <para>Read off `so_smo_AbnormalStateAttribute@ShineMobileObject` (0x004A8010), which walks the object's
/// abstate list and asks whether any active state satisfies the attribute. What each one MEANS is the
/// function's own test, not an inference from the name:</para>
///
/// <list type="table">
///   <item><term><see cref="STUN"/></term><description>a sub-state whose TYPE byte at +0x26 is <b>0x15</b> —
///         the same value that makes `SAA_NOMOVE` set `cannotmove_stun` rather than entangle</description></item>
///   <item><term><see cref="SLOW"/></term><description>any state carrying `SAA_SPEEDDOWNRATE` (88)</description></item>
///   <item><term><see cref="ACMRMINUS"/></term><description>any state carrying `SAA_ACMINUS` (73),
///         `SAA_ACDOWNRATE` (74), `SAA_MRMINUS` (86) or `SAA_MRDOWNRATE` (87) — armour OR magic
///         resistance, by plus OR by rate, which is why one attribute covers four actions</description></item>
/// </list></summary>
public enum AbnormalStateAttr
{
    NONE = 0,
    STUN = 1,
    SLOW = 2,
    ACMRMINUS = 3,
}

/// <summary>One row of `MiscDataTable`'s verify-by-abstate list, 20 bytes, keyed by skill id.
///
/// <para>Field names and offsets are the PDB's.</para></summary>
/// <param name="Skill">`mdvba_Skill` @0 — the skill id this row applies to.</param>
/// <param name="Condition">`mdvba_Condition` @4 — the attribute the DEFENDER must satisfy.</param>
/// <param name="DamageRate">`mdvba_DamageRate` @8, a SIGNED short — written into
/// `EngageArgument.damagerate`, replacing the default 1000.</param>
/// <param name="NewState">`mdvba_NewState` @0xC — an `ABSTATEINDEX` to apply, ignored at or above 0x318.</param>
/// <param name="CriRate">`mdvba_Crirate` @0x10, a SIGNED short — written into
/// `EngageArgument.crirateadd`.</param>
public sealed record MiscDataVarifyByAbstate(
    int Skill, AbnormalStateAttr Condition, int DamageRate, int NewState, int CriRate);

/// <summary>`MiscDataTable::mdt_ArgumentLoad` (0x004A6110) — the ONLY writer of
/// `EngageArgument.damagerate` and `crirateadd`.
///
/// <para><b>And it is CONDITIONAL, which is the part worth knowing.</b> The rate a skill applies is not a
/// property of the skill alone: the row is keyed by skill id, and it only fires when the DEFENDER
/// currently satisfies the row's <see cref="AbnormalStateAttr"/>. So these are "hits harder against a
/// stunned / slowed / armour-broken target" bonuses, and against an unafflicted target a skill leaves the
/// argument at its 1000/0 defaults exactly like a normal swing does.</para>
///
/// <para>The sequence, with every early-out the original has:</para>
/// <code>
/// if (!arg || !arg-&gt;sklinfo || !arg-&gt;sklinfo-&gt;sdi_Activ) return;   // not a skill: nothing to load
/// if (!arg-&gt;att || !arg-&gt;def) return;
/// row = bsearch(sklinfo-&gt;sdi_Activ-&gt;id, table, 20-byte rows);
/// if (!row) return;
/// if (!def-&gt;so_smo_AbnormalStateAttribute(row-&gt;mdvba_Condition)) return;
/// arg-&gt;damagerate = row-&gt;mdvba_DamageRate;
/// arg-&gt;crirateadd = row-&gt;mdvba_Crirate;
/// if (row-&gt;mdvba_NewState &lt; 0x318) apply AbState::as_FromIndex(mdvba_NewState);
/// </code>
///
/// <para>Loaded from `9Data/Shine/World/MiscDataTable.txt`.</para></summary>
public sealed class MiscDataTable(IEnumerable<MiscDataVarifyByAbstate> rows)
{
    /// <summary>`ABSTATEINDEX` values at or above this are not applied — `mdt_ArgumentLoad+0xFE`.</summary>
    public const int AbstateIndexLimit = 0x318;

    private readonly Dictionary<int, MiscDataVarifyByAbstate> _bySkill =
        rows.ToDictionary(r => r.Skill);

    /// <summary>An empty table: every skill leaves the argument at its defaults.</summary>
    public static MiscDataTable Empty { get; } = new([]);

    /// <summary>What this skill does to the engage argument against this defender, or <c>null</c> when
    /// nothing applies — no row for the skill, or the defender does not satisfy its condition.
    ///
    /// <para>Returning null rather than a neutral row keeps the distinction the server makes: "no
    /// modification" leaves `damagerate` at 1000, and a row with `mdvba_DamageRate` of 0 would set it to
    /// 0 and zero the damage. Both are real, and they are not the same.</para></summary>
    public MiscDataVarifyByAbstate? ArgumentLoad(int skillId, AbstateListInObject defenderStates,
                                                 Func<AbstateElementInObject, SubStateFacts> facts)
    {
        if (!_bySkill.TryGetValue(skillId, out var row)) return null;
        return Satisfies(row.Condition, defenderStates, facts) ? row : null;
    }

    /// <summary>The `ABSTATEINDEX` this row applies, or <c>null</c> when it is out of range.
    ///
    /// <para>The bound is a `jge` on 0x318, so 0x318 itself does NOT apply. Out of range is how a row
    /// says "modify the damage but apply nothing" -- it is not an error value.</para></summary>
    public static int? StateToApply(MiscDataVarifyByAbstate row)
        => row.NewState < AbstateIndexLimit ? row.NewState : null;

    /// <summary>How `mdt_ArgumentLoad` applies that state, once <see cref="StateToApply"/> has given one
    /// (+0x109..+0x14E).
    ///
    /// <code>
    /// abstate = as_FromIndex(row.mdvba_NewState);      // by INDEX here, unlike the passive path's name
    /// if (abstate == null) return;
    /// if (defender-&gt;so_AbnormalState_Set(attacker, row.mdvba_NewState, 1, abstate,
    ///                                    0, -1, 0, 6, 0))
    ///     defender-&gt;so_AbnormalState_BitSet(abstate.index);
    /// </code>
    ///
    /// <para>Three things worth having as constants rather than as literals buried in a call:</para>
    /// <list type="bullet">
    ///   <item>it lands on the <b>DEFENDER</b>, with the attacker only as the source;</item>
    ///   <item><b>strength is a hard-coded 1</b> — the row carries no strength field, so a skill's
    ///         misc-data abstate is always rank 1 whatever the skill;</item>
    ///   <item>it uses the FULL `so_AbnormalState_Set` (vtable +0x638), not the `_Simple` overload
    ///         (+0x644) that the passive path uses — different entry point, different argument shape.</item>
    /// </list>
    ///
    /// <para>⚠️ Note this path does NOT roll resistance first. The passive path checks
    /// `so_AbnormalState_Resist` before applying and this one does not: it hands the whole decision to
    /// `so_AbnormalState_Set` and only bit-sets when that reports success.</para></summary>
    public const int AppliedStrength = 1;

    /// <summary>The literal `6` passed as `so_AbnormalState_Set`'s eighth argument on this path. Named
    /// rather than explained, because what the parameter MEANS is not read yet — only that it is
    /// constant here.</summary>
    public const int AppliedSetArgument = 6;

    /// <summary>`so_smo_AbnormalStateAttribute@ShineMobileObject` (0x004A8010) — does any state currently
    /// on the object satisfy this attribute?</summary>
    public static bool Satisfies(AbnormalStateAttr attr, AbstateListInObject states,
                                 Func<AbstateElementInObject, SubStateFacts> facts)
    {
        if (attr == AbnormalStateAttr.NONE) return false;

        foreach (var element in states.Active)
        {
            var f = facts(element);
            var hit = attr switch
            {
                // The type byte, not an action -- the same 0x15 that means "stun" rather than "entangle".
                AbnormalStateAttr.STUN => f.SubStateType == 0x15,
                AbnormalStateAttr.SLOW => f.Has(SubAbstateAction.SAA_SPEEDDOWNRATE),
                AbnormalStateAttr.ACMRMINUS => f.Has(SubAbstateAction.SAA_ACMINUS)
                                            || f.Has(SubAbstateAction.SAA_ACDOWNRATE)
                                            || f.Has(SubAbstateAction.SAA_MRMINUS)
                                            || f.Has(SubAbstateAction.SAA_MRDOWNRATE),
                _ => false,
            };
            if (hit) return true;
        }
        return false;
    }
}

/// <summary>What the engine asks about one active state's `SubAbState` row — the type byte and which
/// actions it carries. `assa_IsHaveEffect` is the server's own predicate for the second half.</summary>
/// <param name="SubStateType">The row's type byte at +0x26.</param>
/// <param name="Actions">The actions the row carries, whatever their arguments.</param>
public readonly record struct SubStateFacts(int SubStateType, IReadOnlyCollection<SubAbstateAction> Actions)
{
    /// <summary>`assa_IsHaveEffect`.</summary>
    public bool Has(SubAbstateAction action) => Actions.Contains(action);
}
