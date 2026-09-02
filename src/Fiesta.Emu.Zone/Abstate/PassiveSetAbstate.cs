namespace Fiesta.Emu.Zone.Abstate;

/// <summary>`PS_ConditionEnum` — the four moments at which a passive skill can apply an abstate.
///
/// <para>Four, and only four. `cpl_SetAbstate` rejects anything `>= 4` before it looks at a single row
/// (<c>cmp [ebp+8], 4; jge</c>), and dispatches the rest through a four-entry jump table. So this is the
/// complete set of hooks passives have into the abstate system — not a sample of it.</para></summary>
public enum PassiveSetAbstateCondition
{
    /// <summary>0 — a crossbow attack knocking back. The one condition with a WEAPON gate: the source's
    /// equipped weapon must be `WT_CROSSBOW` (10). The condition's name and the check agree, which is how
    /// the reading was confirmed.
    ///
    /// <para>⚠️ <b>NO SHIPPED ROW USES IT.</b> All 16 rows of `PSkillSetAbstate.shn` use conditions 1, 2
    /// and 3, so on this data set the weapon gate never runs and this arm is reachable code with nothing
    /// behind it. Modelled because the binary has it; do not describe it as a live mechanic.</para></summary>
    PS_CBOWATKRATEKNOCKBACK = 0,

    /// <summary>1 — a skill's SP cost reduction. No gate beyond the rate roll.</summary>
    PS_SKILLSPUSEDOWN = 1,

    /// <summary>2 — an area SP drain on enemies. No gate beyond the rate roll.</summary>
    PS_AREAENEMYSPDOWN = 2,

    /// <summary>3 — melee damage / miss / critical rate. Gated on the caller's boolean only.</summary>
    PS_MEDMGMISSCRIUPRATE = 3,
}

/// <summary>`PSkillSetAbstate` (71 bytes) — one row of `PSkillSetAbstate.shn`: a passive skill, the moment
/// it fires, how often, and what it applies.</summary>
/// <param name="InxName">`PS_InxName` — the passive skill this row belongs to.</param>
/// <param name="Condition">`PS_Condition` — which of the four moments.</param>
/// <param name="ConditionRatePermille">`PS_ConditioRate` (the server's spelling) — the chance, in 1/1000,
/// checked against `RandomBox::rb_1000`.</param>
/// <param name="AbStateInxName">`PS_AbStateInx` — the abstate to apply, BY NAME. Resolved through
/// `as_FromName`, not by index, so a row naming an abstate the dictionary does not hold silently applies
/// nothing.</param>
/// <param name="Strength">`Strength` — the rank passed to the application.</param>
public sealed record PSkillSetAbstate(
    string InxName,
    PassiveSetAbstateCondition Condition,
    int ConditionRatePermille,
    string AbStateInxName,
    byte Strength);

/// <summary>`CharacterPassiveList::cpl_SetAbstate` (0x00446A10) — the passive half of abstate
/// application: which passives apply an abstate, and on what condition.
///
/// <para>Reached through `so_ply_PassiveSetAbstate`, whose base `ShineObject` implementation (0x00402370)
/// does nothing — so <b>only a player can apply an abstate this way</b>; a mob never does.</para>
///
/// <para>The whole function, with the four arms folded back in:</para>
/// <code>
/// if (source == null || target == null || list.empty || condition >= 4) return;
/// foreach (row in passiveList)
/// {
///     if (row.PS_Condition != condition)                              continue;
///     if (condition == PS_CBOWATKRATEKNOCKBACK &amp;&amp;
///         (!flag || source.weapon.WeaponType != WT_CROSSBOW))         continue;
///     if (condition == PS_MEDMGMISSCRIUPRATE &amp;&amp; !flag)             continue;
///     if (row.PS_ConditioRate &lt;= rb_1000())                          continue;   // apply when rate &gt; draw
///     abstate = as_FromName(row.PS_AbStateInx);
///     if (abstate == null || abstate-&gt;head == null)                   continue;
///     if (target.so_AbnormalState_Resist(abstate) == 1)               continue;
///     target.so_AbnormalState_Set(source, abstate.index, row.Strength, 1);
/// }
/// </code>
///
/// <para><b>Note which object is which.</b> The abstate lands on the SECOND argument and the first is
/// only the source passed along to the application — both the resistance check and the set are called on
/// `[ebp+0x10]`. Getting that backwards would put every passive debuff on the wrong character, and the
/// two arguments have the same type, so nothing would complain.</para>
///
/// <para><b>The rate is per ROW, not per skill.</b> The loop does not stop at the first match: every row
/// whose condition matches takes its own independent roll, so a character holding two passives on the
/// same condition gets two attempts, and one passive with two rows gets two.</para>
///
/// <para><b>What the shipped table actually holds</b> (all 16 rows of `PSkillSetAbstate.shn`), because the
/// code alone gives a misleading picture of which of this is live:</para>
/// <list type="bullet">
///   <item>condition 1 — `MagicDance01`..`04` and `PointAttack01`..`04` -&gt; `StaMagicDanceUseSPDown01`..`04`</item>
///   <item>condition 2 — `DeepFear01`..`04` -&gt; `StaDeepFearMenDownRate01` at strengths 1..4</item>
///   <item>condition 3 — `Shame01`..`04` -&gt; `StaShameCRIUp01`..`04`</item>
///   <item>condition 0 — <b>nothing</b></item>
/// </list>
///
/// <para>Two consequences. <b>Every rate is 1000</b>, which with the strict-greater rule always fires — so
/// the roll is real machinery running at full throughput rather than a chance anything declines. And
/// <b>rank is expressed two different ways</b>: `DeepFear` uses one abstate at four strengths while the
/// others use four abstates at strength 1, so rank does not reliably live in `Strength`.</para></summary>
public static class PassiveSetAbstate
{
    /// <summary>`WeaponTypeEnum::WT_CROSSBOW`. The only weapon type the passive abstate path names.</summary>
    public const int CrossbowWeaponType = 10;

    /// <summary>The number of conditions `cpl_SetAbstate` accepts. Anything at or above this returns
    /// before the list is walked — <c>cmp [ebp+8], 4; jge</c>.</summary>
    public const int ConditionCount = 4;

    /// <summary>Whether a row's gate opens, before its rate is rolled.
    ///
    /// <para><paramref name="flag"/> is the caller's boolean (`cpl_SetAbstate`'s fourth argument). Two of
    /// the four conditions require it and two ignore it entirely, which is worth stating rather than
    /// treating as a uniform precondition.</para></summary>
    /// <param name="sourceWeaponType">The source's equipped `ItemInfo.WeaponType`, or null when unknown
    /// or unarmed. Null fails the crossbow gate — the original reaches the same place by way of a null
    /// item lookup.</param>
    public static bool GateOpens(PSkillSetAbstate row, PassiveSetAbstateCondition condition, bool flag,
                                 int? sourceWeaponType)
    {
        if (row.Condition != condition) return false;
        if ((int)condition >= ConditionCount) return false;

        return condition switch
        {
            PassiveSetAbstateCondition.PS_CBOWATKRATEKNOCKBACK
                => flag && sourceWeaponType == CrossbowWeaponType,
            PassiveSetAbstateCondition.PS_MEDMGMISSCRIUPRATE => flag,
            _ => true,
        };
    }

    /// <summary>The rate roll. <c>cmp word [row+0x24], ax; jbe skip</c> — so the row applies when its rate
    /// is STRICTLY GREATER than the draw, and a rate of 0 can never fire however low the draw goes.</summary>
    /// <param name="draw">`RandomBox::rb_1000()`, 0..999.</param>
    public static bool RatePasses(PSkillSetAbstate row, int draw) => row.ConditionRatePermille > draw;

    /// <summary>Every row that fires, in list order — the gate, then the rate roll, for each row
    /// independently.
    ///
    /// <para>The caller still owes the two steps this cannot do without a target: `as_FromName` (a row
    /// naming an unknown abstate applies nothing) and the target's resistance roll.</para></summary>
    /// <param name="draws">One draw per row that passes its gate, in order. Supplying them rather than
    /// generating them keeps the shared WELL512 stream's position the caller's business — the same reason
    /// the swing rolls take theirs.</param>
    public static IEnumerable<PSkillSetAbstate> Firing(
        IEnumerable<PSkillSetAbstate> passiveList, PassiveSetAbstateCondition condition, bool flag,
        int? sourceWeaponType, IEnumerator<int> draws)
    {
        foreach (var row in passiveList)
        {
            if (!GateOpens(row, condition, flag, sourceWeaponType)) continue;
            if (!draws.MoveNext())
                yield break;
            if (RatePasses(row, draws.Current))
                yield return row;
        }
    }
}
