namespace Fiesta.Emu.Zone.Mob;

/// <summary>One of a mob's weapons, as the selector sees it.</summary>
/// <param name="SkillId">The skill this weapon casts — `u16` at the weapon record's +0x04, looked up in
/// `SkillDataBox`. A weapon whose skill is not in the box is skipped.</param>
/// <param name="BlastRatePermille">`MobWeapon.BlastRate` (+0x47) — the chance this weapon is chosen once
/// it is otherwise eligible. Overridable per mob instance; see
/// <see cref="MobWeaponSelection.UseWeaponRate"/>.</param>
/// <param name="SpCost">`ActiveSkillInfo.SP` (+0xA0) for that skill. The mob must have at least this
/// much or the weapon is skipped.</param>
/// <param name="CooldownUntilMs">When this weapon comes off cooldown, on the server clock. Strictly
/// greater than "now" means unavailable.</param>
public readonly record struct MobWeaponOption(
    int SkillId,
    int BlastRatePermille,
    long SpCost,
    long CooldownUntilMs);

/// <summary>`ShineMob::so_mob_SelectWeapon` (0x004AB720) — which of a mob's weapons it attacks with.
///
/// <para>⭐ <b>The list is walked from the HIGHEST index DOWN to 0, and the first weapon that passes takes
/// the attack.</b> So within this function index 0 is the LAST RESORT, not the default: higher indices
/// are the special attacks and they get first refusal.</para>
///
/// <para>⚠️ <b>But against a PLAYER none of that runs.</b> `mab_Think` dynamic-casts the target to
/// `ShinePlayer` and ZEROES the index when the cast succeeds — see
/// `MobWeaponSelectionTests.TheAttackAgainstAPlayerIsWeaponIndexZero`, which predates this port. So this
/// selection governs mob-versus-NON-player attacks; for a player target weapon 0 really is forced, and
/// "the simulation only ever uses index 0" is CORRECT for the player case and wrong only for the
/// rest.</para>
///
/// <para>Index 0 is also the skill-less row for every mob in the shipped data — a coincidence pinned by a
/// neighbouring test — so the forced-to-zero path is the mob's basic swing.</para>
///
/// <para>Each candidate must clear three gates, in this order:</para>
/// <list type="number">
///   <item>its skill resolves in `SkillDataBox`;</item>
///   <item>the mob's current SP is at least the skill's `SP` cost;</item>
///   <item>the weapon is off cooldown (`[this+0x1F98][i] &lt;= clockwatch`);</item>
/// </list>
/// <para>and only then does it roll <c>well512_GetRandom(1000) &lt;= UseWeaponRate(i)</c>.</para>
///
/// <para>Before any of that, `smo_SkillBlastOption() == 2` short-circuits the whole function: the mob is
/// blocked from casting (silenced) and attacks with weapon <b>0</b>. That is the one place index 0 really
/// is a default.</para></summary>
public static class MobWeaponSelection
{
    /// <summary>`smo_SkillBlastOption`'s value that forces the plain attack. Anything else runs the
    /// selection.</summary>
    public const int BlastOptionBlocked = 2;

    /// <summary>What the function returns when no weapon passes: <b>-1</b>, not 0. The distinction
    /// matters — 0 is a real weapon index and "nothing was selected" has to be distinguishable from
    /// "weapon 0 was selected", which is exactly what a caller reading 0 as "none" would destroy.</summary>
    public const int NoWeapon = -1;

    /// <summary>`ShineMob::sm_GetUseWeaponRate` (0x004AA060) — the rate for one weapon index.
    ///
    /// <code>
    /// if (index &lt; instanceOverrides.Count) return instanceOverrides[index];   // a per-mob vector&lt;u16&gt;
    /// if (index &lt; weapons.Count)           return weapons[index].BlastRate;   // MobWeapon +0x47
    /// return 0;
    /// </code>
    ///
    /// <para>The override vector comes FIRST and is per instance, so two mobs of the same kind can
    /// disagree — a scripted encounter can retune its own weapon weights without touching the data.</para></summary>
    public static int UseWeaponRate(IReadOnlyList<MobWeaponOption> weapons, int index,
                                    IReadOnlyList<int>? instanceOverrides = null)
    {
        if (instanceOverrides is not null && index < instanceOverrides.Count)
            return instanceOverrides[index];
        return index < weapons.Count ? weapons[index].BlastRatePermille : 0;
    }

    /// <summary>The rate roll. <c>cmp draw, rate; jle select</c> — <b>inclusive</b>.
    ///
    /// <para>⚠️ So a <c>BlastRate</c> of 0 is NOT "never": the draw is 0..999 and a draw of 0 still
    /// selects, one time in a thousand. Reading 0 as "disabled" is the recurring
    /// zero-as-sentinel mistake, and here the binary genuinely gives zero a meaning.</para></summary>
    public static bool RatePasses(int draw, int ratePermille) => draw <= ratePermille;

    /// <summary>The selection, in the original's order.</summary>
    /// <param name="skillResolves">Whether the weapon's skill is present in `SkillDataBox`. A weapon whose
    /// skill is missing is skipped before SP or cooldown are even looked at.</param>
    /// <param name="draws">One `well512_GetRandom(1000)` per candidate that clears the three gates —
    /// supplied rather than generated so the shared stream's position stays the caller's business.</param>
    /// <returns>The chosen index, or <see cref="NoWeapon"/>.</returns>
    public static int SelectWeapon(IReadOnlyList<MobWeaponOption> weapons, long currentSp, long nowMs,
                                   IEnumerator<int> draws,
                                   IReadOnlyList<int>? instanceOverrides = null,
                                   Func<int, bool>? skillResolves = null,
                                   int blastOption = 0)
    {
        if (blastOption == BlastOptionBlocked) return 0;

        for (var i = weapons.Count - 1; i >= 0; i--)
        {
            var w = weapons[i];
            if (skillResolves is not null && !skillResolves(w.SkillId)) continue;
            if (currentSp < w.SpCost) continue;
            if (w.CooldownUntilMs > nowMs) continue;

            if (!draws.MoveNext()) return NoWeapon;
            if (RatePasses(draws.Current, UseWeaponRate(weapons, i, instanceOverrides)))
                return i;
        }
        return NoWeapon;
    }
}
