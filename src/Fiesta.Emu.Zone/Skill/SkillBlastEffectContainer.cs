namespace Fiesta.Emu.Zone.Skill;

/// <summary>`SkillEffectApply::SkillBlastEffect` (108 bytes) — one PENDING strike, queued to land at a
/// future tick.
///
/// <para>This is the mechanism that makes a multi-hit sequence land over TIME rather than all at once:
/// each <see cref="MultiHitOneHit"/>'s `oh_HitTimeRate` becomes one of these, and the container fires it
/// when the clock reaches <see cref="BlastTick"/>.</para></summary>
/// <param name="BlastTick">`sbe_BlastTick` — when this strike is due, on the server clock.</param>
/// <param name="CasterRegistNumber">`sbe_CasterRegistNumber` — the caster's registration number,
/// re-checked at fire time. See <see cref="SkillBlastEffectContainer.CasterStillValid"/>.</param>
/// <param name="TargetRegistNumber">`sbe_TargetRegistNumber` — the same for the target.</param>
/// <param name="Empower">`sbe_Empow` — the empower allocation the cast was made with, carried forward so
/// a strike landing later still uses the allocation it was cast under.</param>
/// <param name="MultiHit">`sbe_MultiHitArgument` — embedded by VALUE at +0x2C, not referenced. Each
/// queued strike owns its own copy, which is why one sequence's strikes can carry different damage rates
/// without interfering.</param>
/// <param name="LastDamage">`sbe_LastDamage` — what the previous strike of this sequence dealt.</param>
public sealed record SkillBlastEffect(
    long BlastTick,
    uint CasterRegistNumber,
    uint TargetRegistNumber,
    SkillEmpower Empower = default,
    MultiHitArgument? MultiHit = null,
    int LastDamage = 0);

/// <summary>`SkillEffectApply::SkillBlastEffectContainer::sbec_Routine` (0x00437FC0) — the tick that fires
/// queued strikes.
///
/// <para>Two things about it decide how a simulation must drive it.</para>
///
/// <para><b>1. The queue is TICK-ORDERED and the loop BREAKS, it does not continue.</b> The first effect
/// whose `sbe_BlastTick` is past the clock stops the whole pass — so the container is sorted by tick and
/// a later-but-due effect behind an earlier-but-not-due one will NOT fire. Modelling it as "scan all,
/// fire the due ones" produces a different firing order from the server's under any out-of-order
/// insertion.</para>
///
/// <para><b>2. The caster is re-validated by REGISTRATION NUMBER, not by pointer.</b> The pointer is
/// checked for null and for a liveness flag, and then `caster-&gt;GetRegistNumber()` is compared against the
/// number stored when the effect was queued. A recycled object at the same address fails that comparison,
/// so a queued strike cannot land on behalf of whoever inherited the slot — the same recycling trap that
/// bites handle-keyed state elsewhere in this project.</para>
///
/// <para>The routine also SNAPSHOTS the 17-dword `setitemskilleffect` buffer onto its stack on entry
/// (<c>rep movsd</c> of 0x11 dwords from 0x1325EDB0), because firing an effect runs the damage path and
/// that path rebuilds the buffer for whoever is casting. Confirms what
/// <see cref="MultiHit.HitDamage"/> says about it: a global scratch area, not configuration.</para></summary>
public static class SkillBlastEffectContainer
{
    /// <summary>Whether an effect is due. The clock comparison is <c>sbe_BlastTick &gt; now</c> to STOP,
    /// so due means at or before now.</summary>
    public static bool IsDue(SkillBlastEffect effect, long nowMs) => effect.BlastTick <= nowMs;

    /// <summary>The caster re-validation, in order: the effect still names a skill, the caster object is
    /// present and alive, and its registration number still matches.</summary>
    public static bool CasterStillValid(SkillBlastEffect effect, bool hasSkill, bool casterPresent,
                                        bool casterAlive, uint? currentRegistNumber)
        => hasSkill && casterPresent && casterAlive && currentRegistNumber == effect.CasterRegistNumber;

    /// <summary>The pass: take effects from the front while they are due, stopping at the first that is
    /// not.
    ///
    /// <para>⚠️ Returns what the server would FIRE, in order. An effect that is due but fails validation
    /// is still CONSUMED — the original drops it and moves on rather than leaving it queued — so a caller
    /// must remove everything this pass reached, not only what it fired.</para></summary>
    /// <param name="queue">The container, in tick order. Not re-sorted here: if a caller inserts out of
    /// order the server's behaviour is the one modelled, not a corrected one.</param>
    /// <param name="isValid">The per-effect validation, i.e. <see cref="CasterStillValid"/> applied with
    /// whatever the caller knows about the objects.</param>
    /// <param name="reached">How many entries the pass consumed, fired or dropped.</param>
    public static IReadOnlyList<SkillBlastEffect> Fire(IReadOnlyList<SkillBlastEffect> queue, long nowMs,
                                                       Func<SkillBlastEffect, bool> isValid,
                                                       out int reached)
    {
        var fired = new List<SkillBlastEffect>();
        reached = 0;
        foreach (var effect in queue)
        {
            if (!IsDue(effect, nowMs)) break;
            reached++;
            if (isValid(effect)) fired.Add(effect);
        }
        return fired;
    }
}
