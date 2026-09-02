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

    // ---- the one-subclass actor slots -----------------------------------------------------------------
    //
    // ⚠️ These all call `assa_IsHaveEffect` FIRST and return when the action is absent, so a row without
    // the action leaves the damage ALONE. That is the opposite of LastDamageRateByAttacker above, which
    // multiplies by the missing-value 0 and zeroes it. The two conventions live side by side in the same
    // family of callbacks, and which one a slot uses is not guessable -- it has to be read.

    /// <summary>`SubAbnormalStateActorShieldHPRate::sasa_Act_AllDamageAbsorb` (0x00402390) — an HP-POOL
    /// shield, as distinct from the counted absorb in <see cref="RangeIntercept"/>.
    ///
    /// <code>
    /// if (pool &lt;= damage) { damage -= pool; pool = 0; end the state; }   // overflow passes THROUGH
    /// else                 { pool -= damage; damage = 0; }
    /// </code>
    ///
    /// <para>So a big enough hit breaks the shield and still lands for the remainder, where the counted
    /// absorb would have eaten it whole. Two shields, two completely different behaviours against one
    /// large hit — worth keeping straight.</para></summary>
    /// <param name="pool">The element's remaining absorb pool (+0x54).</param>
    public static (int Damage, int Pool, bool EndsState) ShieldHpPool(int damage, int pool)
        => pool <= damage ? (damage - pool, 0, true) : (0, pool - damage, false);

    /// <summary>`SubAbnormalStateActorDamageDownRate::sasa_Act_NormalDamageDown` (0x004023D0) and
    /// `sasa_Act_DotDamageDown` (0x00402450) — the same body against two different actions,
    /// <see cref="SubAbstateAction.SAA_DMGDOWNRATE"/> (115) and
    /// <see cref="SubAbstateAction.SAA_DOTDMGDOWNRATE"/> (111).
    ///
    /// <code>
    /// if (!assa_IsHaveEffect(action)) return damage;        // absent leaves it ALONE
    /// rate = assa_FindEffect(action);
    /// if (rate &gt;= 1000) return 0;                          // fully negated, and it is &gt;=, not &gt;
    /// return damage - (uint)(damage * rate) / 1000;         // UNSIGNED divide
    /// </code>
    ///
    /// <para><b>A REDUCTION, not a scale.</b> A rate of 300 removes 30% and leaves 70% — it does not
    /// scale the damage to 30%. Getting that inverted would still look plausible on every value and be
    /// wrong on all of them.</para></summary>
    /// <param name="ratePermille">The row's argument, or null when the row does not carry the action —
    /// which is the case that leaves the damage untouched.</param>
    public static int DamageDown(int damage, int? ratePermille)
    {
        if (ratePermille is not { } rate) return damage;
        if (rate >= 1000) return 0;
        return damage - (int)((uint)unchecked(damage * rate) / 1000u);
    }

    /// <summary>`SubAbnormalStateActorMinHP::sasa_Act_MinHP` (0x004024F0) — a FLOOR on HP, raised rather
    /// than set.
    ///
    /// <code>
    /// if (!assa_IsHaveEffect(SAA_MINHP)) return floor;
    /// value = assa_FindEffect(SAA_MINHP);
    /// if (floor &lt; value) floor = value;                    // unsigned compare
    /// </code>
    ///
    /// <para>The pass runs over every element, so the HIGHEST floor among an object's states wins and the
    /// order they are visited in does not matter. This is what keeps a character at 1 HP rather than
    /// dead.</para></summary>
    public static uint MinHp(uint floor, uint? value)
        => value is { } v && floor < v ? v : floor;

    /// <summary>`SubAbnormalStateActorUseSPDown::sasa_Act_UseSPDown` (0x004021D0) — the same reduction
    /// shape as <see cref="DamageDown"/>, against <see cref="SubAbstateAction.SAA_USESPDOWN"/> (100), and
    /// applied to a skill's SP COST rather than to damage.
    ///
    /// <para>This is what the shipped `MagicDance` / `PointAttack` passives resolve to — their
    /// `StaMagicDanceUseSPDown01`..`04` abstates carry this action. So the passive path and this actor are
    /// two ends of the same mechanic.</para>
    ///
    /// <para>Absent leaves the cost alone (`assa_IsHaveEffect` first). Note there is no `&gt;= 1000` clamp
    /// here, unlike <see cref="DamageDown"/> — a rate above 1000 would make the cost NEGATIVE rather than
    /// zero. Nothing in the shipped data does that, and the difference is the binary's, not a
    /// simplification.</para></summary>
    public static uint UseSpDown(uint spCost, int? ratePermille)
    {
        if (ratePermille is not { } rate) return spCost;
        return spCost - (uint)unchecked((long)spCost * rate) / 1000u;
    }

    /// <summary>`SubAbnormalStateActorSelfRevive::sasa_Act_Killed` (0x00407910) — what a self-revive
    /// abstate does when its owner dies.
    ///
    /// <code>
    /// rate = assa_FindEffect(args, SAA_REVIVEHEALRATE /*40*/);
    /// player-&gt;so_ply_setIsRebirth(1);
    /// player-&gt;so_ply_setHealRate(rate);
    /// </code>
    ///
    /// <para>⚠️ It uses `assa_FindEffect` DIRECTLY, with no `assa_IsHaveEffect` guard — so a row without
    /// the action still flags the rebirth and sets the heal rate to <b>0</b>. The guarded and unguarded
    /// conventions really do sit side by side in this family.</para></summary>
    /// <returns>The rebirth flag and the heal rate to revive at.</returns>
    public static (bool Rebirth, int HealRatePermille) SelfRevive(int reviveHealRatePermille)
        => (true, reviveHealRatePermille);

    /// <summary>`SubAbnormalStateActorPartyRecharge::sasa_Act_Killed` (0x00407730) — the party-recharge
    /// counterpart, keyed on <see cref="SubAbstateAction.SAA_DEADHPSPRECOVRATE"/> (24).
    ///
    /// <para><b>Only partly read.</b> The action lookup is certain; what follows combines it with
    /// `SetItemAbstateEffect::siae_GetArgument_Base1000ByEffect` — a set-item bonus, the same family as
    /// the staging buffer in <see cref="Skill.MultiHit"/> — and that combination is not read yet. Stated
    /// rather than modelled, so nothing here pretends to compute it.</para></summary>
    public const SubAbstateAction PartyRechargeAction = SubAbstateAction.SAA_DEADHPSPRECOVRATE;

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
