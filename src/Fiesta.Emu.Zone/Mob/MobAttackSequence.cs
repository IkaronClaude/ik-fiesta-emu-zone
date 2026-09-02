namespace Fiesta.Emu.Zone.Mob;

/// <summary>`MobAttackSequence::AttackElement4Mob::ae4m_NextSkill` (0x004A9730) — which skill a mob
/// proposes next, before <see cref="MobWeaponSelection"/>'s descending walk gets a say.
///
/// <para>The two are not alternatives. `so_mob_SelectWeapon` calls this FIRST; the sequence proposes a
/// weapon index and the descending walk is what happens when it does not.</para>
///
/// <para>The "500-entry sequence" is a flat <c>u16[]</c> of SKILL IDS at <c>this+0x04</c>, and the mob
/// does not own its own position in it — the caller passes an <c>int*</c> step and the function reads
/// <c>sequence[step]</c>. `0xFFFF` is the terminator, and it is a real terminator rather than a skill id,
/// which is why a skill id of 0 is still usable.</para>
///
/// <para>Resolving a step to a weapon is a SEARCH, not an index: the mob's weapon list is scanned for the
/// entry naming that skill, <b>starting at index 1</b>. Index 0 is excluded — it is the basic swing, and
/// a sequence never proposes it.</para>
///
/// <para>There is also a one-shot override. When the caller's flag is set, `sm_GetNextSkillID` is
/// consulted; anything but `0xFFFF` is CONSUMED (`sm_SetNextSkillID(0xFFFF)` immediately after) and
/// searched for the same way. That is the hook a script uses to make a boss cast a specific thing
/// next.</para>
///
/// <para>The whole function returns -1 when the target is not a `ShineMob` — it opens with a type walk
/// against a specific RTTI pointer, so a non-mob target has no sequence at all.</para></summary>
public static class MobAttackSequence
{
    /// <summary>The sequence terminator, and the "nothing queued" value of `sm_GetNextSkillID`. NOT a
    /// skill id — which is what leaves 0 free to be a real one.</summary>
    public const int NoSkill = 0xFFFF;

    /// <summary>Returned when the target is not a `ShineMob` and therefore has no sequence.</summary>
    public const int NotAMob = -1;

    /// <summary>The first weapon index a sequence may propose. Index 0 is the basic swing and is skipped
    /// by both search loops.</summary>
    public const int FirstSequencedWeapon = 1;

    /// <summary>The weapon-list search both paths use: the first entry at or after
    /// <see cref="FirstSequencedWeapon"/> whose record is live and whose skill matches.</summary>
    /// <param name="isLive">The record's leading dword being non-zero — the original tests
    /// <c>cmp [entry], 0</c> before comparing the skill, so a blank row is skipped rather than matched.</param>
    /// <returns>The weapon index, or <see cref="NotAMob"/>-style absence expressed as <c>null</c>.</returns>
    public static int? FindWeaponForSkill(IReadOnlyList<MobWeaponOption> weapons, int skillId,
                                          Func<int, bool>? isLive = null)
    {
        for (var i = FirstSequencedWeapon; i < weapons.Count; i++)
        {
            if (isLive is not null && !isLive(i)) continue;
            if (weapons[i].SkillId == skillId) return i;
        }
        return null;
    }

    /// <summary>The queued one-shot, when the caller's flag is set.
    ///
    /// <para>⚠️ It is CONSUMED whether or not a weapon is found for it — `sm_SetNextSkillID(0xFFFF)` runs
    /// immediately after the read and before the search. So a script that queues a skill the mob has no
    /// weapon for loses it silently, rather than having it retried next swing.</para></summary>
    /// <returns>The proposed weapon index, or null when nothing was queued or nothing matched. The caller
    /// must clear the queue in either case.</returns>
    public static int? TakeQueuedSkill(IReadOnlyList<MobWeaponOption> weapons, int queuedSkillId,
                                       Func<int, bool>? isLive = null)
        => queuedSkillId == NoSkill ? null : FindWeaponForSkill(weapons, queuedSkillId, isLive);

    /// <summary>The sequence step: read `sequence[step]` and resolve it.</summary>
    /// <param name="step">The caller's position. The function reads it and does NOT advance it — the
    /// step lives in the caller's <c>int*</c>, which is why two mobs can share a sequence.</param>
    public static int? SkillAtStep(IReadOnlyList<int> sequence, int step)
    {
        if (step < 0 || step >= sequence.Count) return null;
        var id = sequence[step];
        return id == NoSkill ? null : id;
    }

    /// <summary>Both paths together, in the original's order: the queued one-shot first when the flag is
    /// set, then the sequence step.</summary>
    public static int? NextWeapon(IReadOnlyList<MobWeaponOption> weapons, IReadOnlyList<int> sequence,
                                  int step, bool useQueued, int queuedSkillId = NoSkill,
                                  Func<int, bool>? isLive = null)
    {
        if (useQueued)
        {
            var queued = TakeQueuedSkill(weapons, queuedSkillId, isLive);
            if (queued is not null) return queued;
        }

        var skill = SkillAtStep(sequence, step);
        return skill is null ? null : FindWeaponForSkill(weapons, skill.Value, isLive);
    }
}
