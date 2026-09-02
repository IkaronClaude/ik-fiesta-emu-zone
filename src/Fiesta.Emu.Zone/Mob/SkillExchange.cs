namespace Fiesta.Emu.Zone.Mob;

/// <summary>`MobAttackSequence::SkillChange` (12 bytes) — one "if the mob is currently using X, switch to
/// Y" rule.</summary>
/// <param name="From">`sc_From` — the skill the mob must currently be using for this rule to apply.</param>
/// <param name="To">`sc_To` — the skill to switch to. <see cref="SkillExchange.SwitchToBasicSwing"/>
/// (0xFFFF) means "drop to weapon 0" rather than naming a skill.</param>
/// <param name="Value">`sc_Value` — the rule's threshold. For `HPLow` it is a PERMILLE OF MAX HP.
/// Defaults to 0, which is a REAL value and not "unset": nothing is strictly below a 0 threshold, so a
/// rule with `sc_Value` 0 never fires on an HP condition. Conditions that ignore the field are unaffected.</param>
/// <param name="AbstateIndex">`sc_ASIndex`. Not read by the paths ported here.</param>
public readonly record struct SkillChange(int From, int To, uint Value = 0, uint AbstateIndex = 0);

/// <summary>The `ShineMob::sm_SkillExchange_*` predicates — when a mob abandons the skill it is currently
/// using for a different one.
///
/// <para>Four of them, one per condition, each reading its own `SkillChangeList` off
/// `AttackElement4Mob`: `OutOfRange` (0x004BAEF0, pointer at +0x1244), `HPLow` (0x004BB100, +0x1248),
/// `TargetState` (0x004BB390, +0x124C) and `HPLow_ChangeOrder` (0x004BB600). Each opens by testing its
/// POINTER — not the embedded body — for null and returning immediately, so a mob with no rule of that
/// kind costs nothing.</para>
///
/// <para><b>The storage is an intrusive linked list, which is what made this hard to read.</b>
/// `l_Array` is a <c>ListStruct&lt;SkillChange&gt;[]</c> of 12-byte nodes — <c>ls_Content</c> (a
/// <c>SkillChange*</c>) at +0, <c>ls_Next</c> at +4, <c>ls_IsActiv</c> at +8 — walked from
/// <c>l_Finger.store</c> (+0x0E) and terminating when the next index reaches <c>l_MaxSize</c> (+0x04).
/// Reading the array as a flat <c>SkillChange[]</c> puts +4 on <c>sc_Value</c>'s low half and produces
/// nonsense; the entry is reached through <c>ls_Content</c>, never by indexing.</para>
///
/// <para>The shared shape, once the traversal is unpicked:</para>
/// <code>
/// for (node = l_Finger.store; node &lt; l_MaxSize; node = array[node].ls_Next)
/// {
///     if (!array[node].ls_IsActiv) continue;
///     change = array[node].ls_Content;
///     if (change-&gt;sc_From != currentSkillId) continue;
///     if (!ConditionHolds(change)) continue;          // the only per-predicate part
///     if (change-&gt;sc_To == 0xFFFF) { select weapon 0; return true; }
///     select the weapon (from index 1) whose skill is change-&gt;sc_To;
///     return true;
/// }
/// return false;
/// </code></summary>
public static class SkillExchange
{
    /// <summary>`sc_To` of 0xFFFF — switch to weapon <b>0</b>, the basic swing, rather than to a named
    /// skill. The same sentinel `MobAttackSequence` uses, and for the same reason: it keeps skill id 0
    /// usable.</summary>
    public const int SwitchToBasicSwing = 0xFFFF;

    /// <summary>The weapon index a `0xFFFF` target resolves to.</summary>
    public const int BasicSwingWeapon = 0;

    /// <summary>`sm_SkillExchange_HPLow`'s condition, the one read in full:
    ///
    /// <code>
    /// threshold = (uint)(so_GetMaxHP() * change-&gt;sc_Value) / 1000;    // UNSIGNED divide
    /// return so_GetHP() &lt; threshold;                                 // strictly below
    /// </code>
    ///
    /// <para>So `sc_Value` is a permille of MAX hp, not an absolute, and the test is strict — a mob
    /// exactly at the threshold does not switch.</para></summary>
    public static bool HpIsLow(long hp, long maxHp, uint valuePermille)
        => hp < (long)((ulong)(maxHp * valuePermille) / 1000u);

    /// <summary>The traversal and the swap. `conditionHolds` is the only part that differs between the
    /// four predicates — pass <see cref="HpIsLow"/>'s result for `HPLow`, or a constant true to model
    /// `OutOfRange` / `TargetState` once their own conditions are supplied by the caller.</summary>
    /// <param name="changes">The list in traversal order. The intrusive-list mechanics are storage; what
    /// matters here is the ORDER, because the first matching rule wins.</param>
    /// <param name="isActive">`ls_IsActiv` for the node holding each change. An inactive node is skipped
    /// before its content is even dereferenced.</param>
    /// <returns>The weapon index to switch to, or null when no rule applies.</returns>
    public static int? Exchange(IReadOnlyList<SkillChange> changes, int currentSkillId,
                                IReadOnlyList<MobWeaponOption> weapons,
                                Func<SkillChange, bool>? conditionHolds = null,
                                Func<int, bool>? isActive = null)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            if (isActive is not null && !isActive(i)) continue;

            var change = changes[i];
            if (change.From != currentSkillId) continue;
            if (conditionHolds is not null && !conditionHolds(change)) continue;

            if (change.To == SwitchToBasicSwing) return BasicSwingWeapon;

            // Same search as the attack sequence: from index 1, so a rule can never target the basic
            // swing by naming its skill -- only by the 0xFFFF sentinel.
            var found = MobAttackSequence.FindWeaponForSkill(weapons, change.To);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>`sm_SkillExchange_HPLow` end to end.</summary>
    public static int? OnHpLow(IReadOnlyList<SkillChange> changes, int currentSkillId,
                               IReadOnlyList<MobWeaponOption> weapons, long hp, long maxHp,
                               Func<int, bool>? isActive = null)
        => Exchange(changes, currentSkillId, weapons,
                    change => HpIsLow(hp, maxHp, change.Value), isActive);
}
