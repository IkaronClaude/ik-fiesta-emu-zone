namespace Fiesta.Emu.Zone.Abstate;

/// <summary>The abstate hooks in `roe_CalcDamage`'s tail — the two that do something, and the one that
/// does not.
///
/// <para>Each pass walks one side's `so_mobile_AbstateList` (object vtable +0x52C) and calls a single
/// `SubAbnormalStateActor` slot on every element:</para>
///
/// <list type="table">
///   <item><term>+0x60A</term><description>DEFENDER, slot 0x28, `sasa_Act_DamegeIntercept` —
///     <see cref="RangeIntercept"/></description></item>
///   <item><term>+0x76F</term><description>ATTACKER, slot 0x2C, `sasa_Act_LastDamegeInterceptByAtk` —
///     <see cref="LastDamageRateByAttacker"/></description></item>
///   <item><term>+0x8D0</term><description>DEFENDER, slot 0x50 — <b>nobody implements it.</b> The base is
///     `ret 0x10` and no subclass in the image overrides it, so the whole pass calls nothing. Worth
///     knowing before anyone hunts for its effect.</description></item>
/// </list></summary>
public static class AbstateDamageCallbacks
{
    /// <summary>The attack range above which the DEFENDER's intercept pass runs at all — strictly greater
    /// than 300, the same test `roe_FreeStatHitRate` uses. Which is what "RangeIntercept" means: it only
    /// ever sees ranged hits, so a melee attacker walks past a shield that would have stopped an arrow.</summary>
    public const int RangedAttackThreshold = 300;

    /// <summary>`SubAbnormalStateActorRangeIntercept::sasa_Act_DamegeIntercept` (0x004074F0).
    ///
    /// <code>
    /// charges = element[0x54];
    /// if (charges &gt; 0) { element[0x54] = charges - 1; damage = 0; }   // absorbs the hit ENTIRELY
    /// else             { element[0x20] = clockwatch; }                 // out of charges: end the state
    /// </code>
    ///
    /// <para><b>A counted absorb, not a percentage.</b> N ranged hits taken to zero regardless of size,
    /// and the state expires itself on the first hit AFTER the last charge — so a shield with one charge
    /// stops one hit and then costs a second hit to clear, rather than vanishing the moment it is spent.</para></summary>
    /// <param name="charges">The element's remaining charges (+0x54).</param>
    /// <returns>The damage after the pass, and the element's new charge count. When
    /// <paramref name="charges"/> is already zero the state should be ended by the caller — that is what
    /// the original's write to +0x20 does.</returns>
    public static (int Damage, int Charges, bool EndsState) RangeIntercept(int damage, int charges)
        => charges > 0 ? (0, charges - 1, false) : (damage, charges, true);

    /// <summary>Whether the defender's intercept pass runs for this attack. Strictly greater than 300 —
    /// an Archer's 450 is over it, every other class's 100 is not, and a skill substitutes its own range
    /// (`sklinfo-&gt;[4]-&gt;[0xB4]`).</summary>
    public static bool RangeInterceptApplies(int attackRange) => attackRange > RangedAttackThreshold;

    /// <summary>`sasa_Act_LastDamegeInterceptByAtk` (0x00407570), shared verbatim by the `LastDmgRatio`
    /// and `HideDamage` actors — one body, two subclasses.
    ///
    /// <code>
    /// rate   = assa_FindEffect(element.row[strength].args, SAA_TOTALDAMAGERATE);
    /// damage = damage * rate / 1000;          // signed, truncating toward zero
    /// </code>
    ///
    /// <para>It runs on the ATTACKER's states, not the defender's — a state that scales down everything
    /// its owner deals.</para>
    ///
    /// <para>⚠️ <b>`assa_FindEffect` returns 0 when the action is absent</b> (it scans the row's four
    /// action slots and falls out of the loop with <c>xor eax, eax</c>). So an element reaching this
    /// callback WITHOUT a <see cref="SubAbstateAction.SAA_TOTALDAMAGERATE"/> slot zeroes the damage
    /// outright rather than leaving it alone. That is the binary's behaviour, not a guard to add: it is
    /// only survivable because the two actors are bound to rows that carry the action. Modelled honestly
    /// so a mis-built row in a simulation fails loudly the same way.</para></summary>
    /// <param name="totalDamageRatePermille">The row's `SAA_TOTALDAMAGERATE` argument, or 0 if the row
    /// does not carry that action — 0 is the real absent-value here and must not be replaced by 1000.</param>
    public static int LastDamageRateByAttacker(int damage, int totalDamageRatePermille)
        => unchecked(damage * totalDamageRatePermille) / 1000;

    /// <summary>`assa_FindEffect` (0x00416160) — an `AbStateStrArgument` holds FOUR (action, argument)
    /// slots and this is a linear scan of them.
    ///
    /// <code>
    /// for (i = 0; i &lt; 4; i++) if (slot[i].action == wanted) return slot[i].arg;
    /// return 0;
    /// </code>
    ///
    /// <para>Four, fixed — the same four <see cref="SubAbstatePriority.ActionSlots"/> the rank rule
    /// compares. <b>Absent returns 0, which is indistinguishable from an argument of 0</b>; every caller
    /// therefore treats "no such action" and "this action, valued zero" as the same thing, and so does
    /// this port.</para></summary>
    public static int FindEffect(IReadOnlyList<(SubAbstateAction Action, int Arg)> slots,
                                 SubAbstateAction wanted)
    {
        for (var i = 0; i < SubAbstatePriority.ActionSlots && i < slots.Count; i++)
            if (slots[i].Action == wanted)
                return slots[i].Arg;
        return 0;
    }
}
