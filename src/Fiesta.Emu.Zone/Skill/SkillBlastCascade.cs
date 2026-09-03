using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Skill;

/// <summary>What one skill strike does to a target, once `roe_CalcDamage` has returned.</summary>
/// <param name="Damage">The figure that reaches the `SkillDamage` record.</param>
/// <param name="IsDamage">The record's `isdamage` bit. <b>False is not "zero damage" — it is a different
/// outcome.</b> `smo_SkillBlast+0x998` clears the flag in the one case below where a strike scales away
/// to nothing and carries no rate to floor it at 1, and the client renders that as no hit rather than as
/// a hit for 0.</param>
public readonly record struct SkillBlastOutcome(int Damage, bool IsDamage);

/// <summary>The inputs `ShineMobileObject::smo_SkillBlast` has that `roe_CalcDamage` does not.
///
/// <para>Every default here is the neutral the server uses when nothing is configured, so an unbuffed
/// caster hitting an ordinary mob with an ordinary skill passes <see cref="SkillBlastCascade.Apply"/>
/// through unchanged.</para>
///
/// <para>⚠️ <b>A record CLASS, deliberately.</b> As a <c>record struct</c> this compiled and every default
/// silently vanished: <c>new SkillBlastInputs()</c> on a struct zero-initialises rather than running the
/// primary constructor, so the two permille rates arrived as 0 and a neutral hit came out at 0 damage. The
/// same shape as <see cref="Combat.AttackModifiers"/> for the same reason.</para></summary>
public sealed record SkillBlastInputs
{
    /// <summary>A neutral cast: no set bonus, a single strike, no specials, no override.</summary>
    public static SkillBlastInputs Neutral { get; } = new();

    /// <summary>`setitemskilleffect.se_Argument[SET_DAMEGERATE]` (0x1325EDB8) — the caster's
    /// matched-equipment-SET damage bonus, staged per cast by `smo_ply_SetItemEffect(skillId)`. See
    /// <see cref="SetItemSkillEffect"/>; 1000 is `se_Clear`'s neutral.</summary>
    public int SetItemDamageRatePermille { get; init; } = SetItemSkillEffect.Neutral;

    /// <summary>`MultiHitArgument.mha_DamageRate` — this strike's share of the sequence. 1000 for a
    /// single-hit skill.</summary>
    public int MultiHitDamageRatePermille { get; init; } = 1000;

    /// <summary>The cast skill's `SpecialIndex`/`SpecialValue` arguments, resolved into `SkillDataIndex`'s
    /// `EnumStruct` slots. Null is the same as <see cref="SkillSpecialArguments.None"/>.</summary>
    public SkillSpecialArguments? Special { get; init; }

    /// <summary>The target's `so_MaxHP`, needed only by <see cref="SkillSpecial.TargetHpDownDmgUpRate"/>.
    /// Zero skips that step, as the server's own null check does.</summary>
    public int TargetMaxHp { get; init; }

    /// <summary>The target's `so_HP` at the moment of the strike.</summary>
    public int TargetHp { get; init; }

    /// <summary>The target's `MobInfo.Type`, for <see cref="SkillSpecial.UndeadToDmg"/>. Null for a target
    /// with no `MobDataBox` — a player — which the server treats as "no bonus".</summary>
    public MobType? TargetType { get; init; }

    /// <summary>`SkillBlastEffect.sbe_nLightningWaveCnt`, the blast's own step counter, which multiplies
    /// <see cref="SkillSpecial.DmgDownRate"/>. `sbe_BlastObject` passes it as the 6th argument;
    /// `smo_SkillBash_Blast_Trap` passes a literal 1.</summary>
    public int WaveCount { get; init; } = 1;

    /// <summary>The caster's `so_smo_StaticDamage` slot (`ShineMobileObject+0x1E7C`). The constructor sets
    /// it to -1 and only the GM commands `&amp;staticdamage` / `&amp;godmode` and the Lua `cStaticDamage`
    /// ever write it, so it is normally inert — but when positive it REPLACES the damage outright, after
    /// everything else.</summary>
    public int StaticDamage { get; init; } = -1;
}

/// <summary>⭐ `ShineMobileObject::smo_SkillBlast` (0x00581160) — <b>what happens to a skill's damage AFTER
/// `roe_CalcDamage` returns.</b>
///
/// <para><see cref="Combat.DamageCalculator"/> ends at `roe_CalcDamage`, which is the rules engine. It is
/// not the end of the skill damage path: `smo_SkillBlast` takes the integer it gets back and puts it
/// through six more steps before it reaches the `SkillDamage` record on the wire. Every one of them is
/// neutral for an unbuffed caster with an ordinary skill, which is exactly why it went unnoticed — and
/// none of them is neutral in general.</para>
///
/// <para>In the order the function applies them:</para>
///
/// <list type="number">
///   <item><b>+0x93B</b> — <c>dmg = (uint)(dmg * setItemRate) / 1000</c>, an UNSIGNED divide.</item>
///   <item><b>+0x94D</b> — <c>dmg = (dmg * mha_DamageRate) / 1000</c>, a SIGNED one, truncating toward
///         zero. The two are not the same divide and a negative product behaves differently in each.</item>
///   <item><b>+0x97B</b> — the floor: a strike that scales to nothing still lands for 1, but only if it
///         had positive damage AND a positive rate. A ZERO rate instead clears the record's `isdamage`
///         bit (`mov byte [rec+8], 0`), which is a different outcome from dealing 0.</item>
///   <item><b>+0x9A9</b> — <see cref="SkillSpecial.TargetHpDownDmgUpRate"/>: an execute bonus keyed on how
///         much HP the target is MISSING.</item>
///   <item><b>+0xA3C</b> — <see cref="SkillSpecial.DmgDownRate"/>, scaled by the blast's wave counter and
///         capped by <see cref="SkillSpecial.MaxDmgDownRate"/>. It SUBTRACTS.</item>
///   <item><b>+0xAAF</b> — <see cref="SkillSpecial.UndeadToDmg"/> against an
///         <see cref="MobType.Undead"/> target.</item>
///   <item><b>+0xC98</b> — the caster's static-damage override, which replaces the result outright.</item>
/// </list>
///
/// <para>Steps 1-3 were already modelled as <see cref="MultiHit.HitDamage"/> and are called through it
/// here rather than duplicated; steps 4-7 are new. The damage REFLECT at +0xAF3 and the HP drain at
/// +0xBC0 are deliberately absent: both read the running damage and act on the CASTER, and neither
/// changes what the target takes.</para>
///
/// <para><b>Steps 4, 5 and 6 COMPOUND, in that order.</b> Each takes its percentage of what the one
/// before it produced, so a skill carrying two of them is not the sum of the two applied separately.</para>
///
/// <para>⚠️ A note for anyone reading the disassembly alongside this: step 4 sources its multiplicand from
/// <c>[ebp-0xB4]</c> while steps 5 and 6 use the running <c>[ebp-0xB0]</c>. That looks like an asymmetry
/// worth preserving and is not observable — nothing sits between step 3 and step 4, so the two slots hold
/// the same value there. It is written the compounding way here because that is what the instructions
/// compute, not because the distinction was tested.</para></summary>
public static class SkillBlastCascade
{
    /// <summary>Run the whole cascade over a `roe_CalcDamage` result.</summary>
    /// <param name="calculatedDamage">What <see cref="Combat.DamageCalculator"/> returned.</param>
    /// <param name="inputs">Everything `smo_SkillBlast` knows that the rules engine does not.</param>
    public static SkillBlastOutcome Apply(int calculatedDamage, SkillBlastInputs inputs)
    {
        // Steps 1-3. `HitDamage` is the set-item multiply, the multi-hit multiply and the floor; the one
        // thing it cannot report is WHICH of the two zero cases it hit, so the flag is recomputed here
        // from the same two conditions the server branches on.
        var scaled = (int)((uint)unchecked(calculatedDamage * inputs.SetItemDamageRatePermille) / 1000u);
        var damage = MultiHit.HitDamage(calculatedDamage, inputs.MultiHitDamageRatePermille,
                                        inputs.SetItemDamageRatePermille);
        var isDamage = !(scaled > 0 && damage == 0 && inputs.MultiHitDamageRatePermille <= 0);

        // ⚠️ Steps 4-6 are skipped WHOLE when the skill carries no such special. Passing a rate of 0
        // instead is not equivalent: step 4 divides by the rate.
        var special = inputs.Special ?? SkillSpecialArguments.None;
        var afterFloor = damage;

        // 4. TARGETHPDOWNDMGUPRATE.
        if (special[SkillSpecial.TargetHpDownDmgUpRate] is { } hpDownRate && inputs.TargetMaxHp != 0)
            damage = afterFloor + TargetHpDownBonus(afterFloor, hpDownRate,
                                                    inputs.TargetMaxHp, inputs.TargetHp);

        // 5. DMGDOWNRATE, capped by MAXDMGDOWNRATE.
        if (special[SkillSpecial.DmgDownRate] is { } downRate)
        {
            var effective = unchecked(downRate * inputs.WaveCount);
            if (special[SkillSpecial.MaxDmgDownRate] is { } cap && cap < effective) effective = cap;

            // The divisor really is NEGATIVE one thousand: the magic constant at +0xA5D is 0xEF9DB22D,
            // which is -0x10624DD3 — the /1000 magic negated — with the same shift 6 and the same
            // truncate-toward-zero fixup. So the step subtracts, which is what "damage DOWN rate" says.
            damage += unchecked(effective * damage) / -1000;
        }

        // 6. UNDEADTODMG. The gate is the target's `MobInfo.Type`, reached through `so_mob_DataBox` — so a
        // target with no data box (a player) never qualifies, whatever it is.
        if (special[SkillSpecial.UndeadToDmg] is { } undeadRate && inputs.TargetType == MobType.Undead)
            damage += unchecked(undeadRate * damage) / 1000;

        // 7. The GM override, last and unconditional.
        if (inputs.StaticDamage > 0) damage = inputs.StaticDamage;

        return new SkillBlastOutcome(damage, isDamage);
    }

    /// <summary>`smo_SkillBlast+0x9E5` — the execute bonus, in full.
    ///
    /// <code>
    /// missing = (ulong)((long)maxHp - (long)hp) * 1000 / (uint)maxHp;   // 64-bit, UNSIGNED divide
    /// bonus   = ((missing * rate) / 1000 * damage) / rate;              // rate cancels, and does not
    /// </code>
    ///
    /// <para><b>The rate cancels out of the arithmetic and is still load-bearing.</b> Algebraically
    /// <c>((m*r)/1000 * d) / r</c> is <c>m*d/1000</c>, so the configured rate changes nothing — except
    /// through the two truncations it sits between, and except that a rate of 0 would divide by zero.
    /// That looks like a bug in the original and it is what the instructions do; the shape is preserved
    /// here so the off-by-one cases match rather than being rounded away.</para>
    ///
    /// <para>The HP difference is taken as an UNSIGNED 32-bit subtraction sign-extended by its borrow
    /// (<c>sub edx,eax; sbb eax,ecx</c>) and then divided by <c>__aulldiv</c>, so a target whose current
    /// HP exceeds its maximum does not produce a small negative bonus — it produces an enormous positive
    /// one. Modelled, not corrected.</para></summary>
    public static int TargetHpDownBonus(int damage, int rate, int maxHp, int hp)
    {
        var missing = (int)(unchecked((ulong)(((long)(uint)maxHp - (uint)hp) * 1000L)) / (uint)maxHp);
        var scaled = unchecked(missing * rate) / 1000;
        return unchecked(scaled * damage) / rate;
    }
}
