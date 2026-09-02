using Fiesta.Emu.Zone.Abstate;

namespace Fiesta.Emu.Zone.Skill;

/// <summary>`MultiHitData::MultiHitElement::OneHit` (16 bytes) — one strike in a multi-hit sequence.
///
/// <para>A multi-hit skill is not one damage event with a multiplier: it is a SEQUENCE, each entry landing
/// at its own moment with its own damage rate and its own abstate roll. So the same skill can open with a
/// weak tick that applies a debuff and close with the strike that carries the damage.</para></summary>
/// <param name="HitTimeRate">`oh_HitTimeRate` — when this strike lands, as a rate of the skill's own
/// `HitTime`. Timing, not damage; carried so a simulation can order the sequence.</param>
/// <param name="DamageRate">`oh_DamageRate` — this strike's share of the skill's damage, in 1/1000. See
/// <see cref="MultiHit.HitDamage"/>: it is applied to the FINISHED damage figure, after everything
/// `roe_CalcDamage` does.</param>
/// <param name="AbState">`oh_AbState` — the abstate this strike may apply, as an `ABSTATEINDEX`.</param>
/// <param name="AbStateRate">`oh_AbStateRate` — the chance it lands, in 1/1000.</param>
/// <param name="AbStrength">`oh_AbStrength` — the rank applied.</param>
/// <param name="AreaStep">`oh_AreaStep` — the area ring this strike covers.</param>
public readonly record struct MultiHitOneHit(
    ushort HitTimeRate,
    ushort DamageRate,
    int AbState = 0,
    ushort AbStateRate = 0,
    byte AbStrength = 0,
    byte AreaStep = 0);

/// <summary>`MultiHitData::MultiHitElement` (168 bytes) — a whole sequence, keyed by `mhe_ID`.
///
/// <para>The array is a fixed <c>OneHit[160]</c> with a separate <c>mhe_ArrayCnt</c> saying how many are
/// live, so the tail of the array is real memory holding stale rows. <b>Reading past the count reads
/// garbage that looks like data</b> — hence the count is the boundary here, not the array length.</para>
///
/// <para>Loaded by `MultiHitTable::mht_Load` from `9Data/Shine/MultiHitType.shn` into the global
/// `_MultiHitTable` (0x0DA29384), and looked up by id through `MultiHitTable::operator[]`.</para></summary>
public sealed record MultiHitElement(ushort Id, IReadOnlyList<MultiHitOneHit> Hits)
{
    /// <summary>`mhe_ArrayCnt`. The number of live rows — never <c>Hits.Count</c> on the raw 160-entry
    /// array.</summary>
    public int Count => Hits.Count;

    /// <summary>The fixed capacity of `mhe_Array`. Present so a loader can assert against it.</summary>
    public const int Capacity = 160;
}

/// <summary>`MultiHitArgument` (60 bytes) — the per-strike argument the damage engine actually sees,
/// hanging off `EngageArgument.pMultiHitArg` (+0x24).
///
/// <para>This is the resolved form of one <see cref="MultiHitOneHit"/>: the sequence is data, this is the
/// strike currently being dealt. <c>null</c> on an ordinary swing, and the engine branches on that.</para></summary>
/// <param name="HitStep">`mha_HitStep` — which strike of the sequence this is.</param>
/// <param name="DamageRate">`mha_DamageRate` — the strike's damage rate in 1/1000. Read in TWO places, for
/// two different purposes: it scales the damage (<see cref="MultiHit.HitDamage"/>) and it gates the
/// critical stun (<see cref="MultiHit.AppliesCriticalStun"/>).</param>
/// <param name="AreaStep">`mha_AraeStep` — the server's spelling, kept.</param>
/// <param name="AbStates">`mha_AbState[4]` — four (rate, index, strength) triples, filled by
/// `mha_SetAbState`. 48 bytes of 12, so four slots exactly.</param>
public sealed record MultiHitArgument(
    int HitStep,
    int DamageRate,
    int AreaStep = 0,
    IReadOnlyList<MultiHitAbState>? AbStates = null)
{
    /// <summary>How many (rate, index, strength) triples `mha_AbState` holds — 48 bytes of 12.</summary>
    public const int AbStateSlots = 4;
}

/// <summary>One slot of `MultiHitArgument::mha_AbState` — an abstate this strike may apply.</summary>
/// <param name="Rate">`mha_AbStateRate`, in 1/1000.</param>
/// <param name="Index">`mha_AbStateIndex`, an `ABSTATEINDEX`.</param>
/// <param name="Strength">`mha_AbStateStrength`.</param>
public readonly record struct MultiHitAbState(int Rate, int Index, int Strength);

/// <summary>The two places a multi-hit strike changes what the damage engine does.
///
/// <para>Both were read out of the binary rather than inferred, and neither is where you would guess:
/// <b>`roe_CalcDamage` does not scale damage by the multi-hit rate at all.</b> It reads
/// `pMultiHitArg` exactly once, to decide whether the critical stun fires. The scaling happens in the
/// CALLER, `smo_SkillBlast`, on the finished integer the rules returned.</para></summary>
public static class MultiHit
{
    /// <summary>`smo_SkillBlast+0x927` — the damage one strike of a sequence actually deals.
    ///
    /// <code>
    /// dmg = sdi_DamageRule-&gt;roe_CalcDamage(arg);          // the whole engine, per strike
    /// dmg = (uint)(dmg * serverRate) / 1000;               // UNSIGNED divide
    /// dmg = (dmg * mha_DamageRate) / 1000;                 // SIGNED, truncating toward zero
    /// if (scaled &gt; 0 &amp;&amp; dmg == 0 &amp;&amp; mha_DamageRate &gt; 0) dmg = 1;
    /// </code>
    ///
    /// <para><b>The two divides are not the same divide.</b> The first is the unsigned reciprocal
    /// sequence (<c>mul 0x10624DD3; shr edx,6</c>), the second the signed one (<c>imul; sar edx,6</c> plus
    /// the sign-bit fixup that makes it truncate toward zero rather than toward negative infinity). A
    /// negative product therefore behaves differently in the two steps, which is the kind of asymmetry a
    /// tidy rewrite silently removes.</para>
    ///
    /// <para><b>The floor is the interesting part.</b> A strike that scales down to nothing still lands
    /// for 1 — but only if the damage before scaling was positive AND the strike carries a positive rate.
    /// So a low-rate tick of a sequence can never be rounded away to zero, while a zero-rate tick stays at
    /// zero. That is the same distinction the critical-stun gate makes, from the same field.</para></summary>
    /// <param name="calculatedDamage">What `roe_CalcDamage` returned for this strike.</param>
    /// <param name="multiHitDamageRatePermille">`mha_DamageRate`.</param>
    /// <param name="serverRatePermille">The server-wide damage multiplier at 0x1325EDB8. ⚠️ Its identity
    /// is NOT established — the PDB gives it no name and it resolves only to a neighbouring symbol. It
    /// behaves as a permille rate and 1000 leaves the damage alone, which is why that is the default; do
    /// not describe it as a configured rate until it has been read out of a live zone.</param>
    public static int HitDamage(int calculatedDamage, int multiHitDamageRatePermille,
                                int serverRatePermille = 1000)
    {
        var scaled = (int)((uint)unchecked(calculatedDamage * serverRatePermille) / 1000u);
        var damage = unchecked(scaled * multiHitDamageRatePermille) / 1000;

        if (scaled > 0 && damage == 0 && multiHitDamageRatePermille > 0)
            return 1;
        return damage;
    }

    /// <summary>`roe_CalcDamage+0x1AE` — whether a critical also attempts its stun.
    ///
    /// <code>
    /// arg-&gt;iscritical = 1;
    /// if (arg-&gt;pMultiHitArg == null)          roe_CriticalStun(arg);   // an ordinary swing always tries
    /// else if (pMultiHitArg-&gt;mha_DamageRate &gt; 0) roe_CriticalStun(arg);
    /// // otherwise the stun is skipped entirely
    /// </code>
    ///
    /// <para>So a filler strike carrying no damage rate can still be flagged critical and will never
    /// stun, while a plain swing always makes the attempt. It is a property of the individual STRIKE,
    /// not of the skill — the same sequence can have ticks on both sides of this.</para>
    ///
    /// <para>Note the flag is set BEFORE the branch: <c>iscritical</c> is true either way, so anything
    /// reading it downstream (critical damage, the client's display) is unaffected. Only the stun is
    /// gated.</para></summary>
    public static bool AppliesCriticalStun(MultiHitArgument? multiHit)
        => multiHit is null || multiHit.DamageRate > 0;
}
