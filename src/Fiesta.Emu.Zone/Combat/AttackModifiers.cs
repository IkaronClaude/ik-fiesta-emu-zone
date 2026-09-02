using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Combat;

/// <summary>The per-swing inputs that are not stats: the situational multipliers, plus deterministic
/// overrides for the two random draws.
///
/// <para>All the <c>*RatePermille</c> values are permille and default to 1000 = unchanged, so
/// <see cref="Default"/> describes a plain head-on hit between equals.</para></summary>
public sealed record AttackModifiers
{
    /// <summary>A plain hit: no situational multipliers, both random draws left to chance.</summary>
    public static AttackModifiers Default { get; } = new();

    /// <summary>Where in the weapon's damage range this swing lands, in permille: 0 = minimum,
    /// 1000 = maximum. <c>null</c> draws it randomly.
    ///
    /// <para>Pin it to make a calculation reproducible, or to ask a bounding question — "what is the worst
    /// this mob can hit me for" is <c>RollPermille = 1000</c>, not a simulation.</para></summary>
    public int? RollPermille { get; init; }

    /// <summary><c>true</c> forces a critical, <c>false</c> forbids one, <c>null</c> rolls against
    /// <see cref="CriticalChancePermille"/>.</summary>
    public bool? ForceCritical { get; init; }

    /// <summary>Chance of a critical in permille, used only when <see cref="ForceCritical"/> is
    /// <c>null</c>. A critical doubles the damage before the situational rates are applied.</summary>
    public int CriticalChancePermille { get; init; }

    /// <summary>`EngageArgument.crirateadd` (+0x20) — a skill's FLAT addition to the critical rate, added
    /// to <see cref="DamageCalculator.CriticalRate"/> before the roll at `roe_CalcDamage+0x186`.
    ///
    /// <para>Written only by `MiscDataTable::mdt_ArgumentLoad`, the same function that writes
    /// <see cref="DamageRatePermille"/>; a normal swing leaves it at 0.</para></summary>
    public int CriticalRateAddPermille { get; init; }

    /// <summary>Skill / attack damage rate. The server's <c>EngageArgument.damagerate</c>.
    ///
    /// <para>Zero is a real value and produces a raw damage of 0, which the final floor turns into 1 — not
    /// an error, and not "unset". It defaults to 1000, never to 0.</para></summary>
    public int DamageRatePermille { get; init; } = 1000;

    /// <summary>Positional multiplier from <c>DamageByAngle</c>: 1000 head-on, larger from behind.
    ///
    /// <para>Index the table with <see cref="DamageCalculator.AngleDamageIndex"/> (direction units) or
    /// <see cref="DamageCalculator.AngleDamageIndexFromDegrees"/> (degrees). Behind is index 90, the top
    /// of the table — not index 0.</para>
    ///
    /// <para>Applied as a double before the integer conversion, matching the server
    /// (<c>roe_CalcDamage+0x572</c>, ahead of the <c>_ftol</c> at <c>+0x587</c>), and applied BEFORE
    /// <see cref="DamageRatePermille"/> as the binary does.</para></summary>
    public int AngleRatePermille { get; init; } = 1000;

    /// <summary>Level-difference multiplier from <c>DamageLvGap</c>: flat 1000 for monster → player, up to
    /// 1500 for player → monster.
    ///
    /// <para>The application point is exact: <see cref="DamageCalculator.Resolve"/> applies it AFTER the
    /// integer conversion as a wrapping 32-bit multiply and a truncating divide by 1000, which is what
    /// <c>roe_LevelGapDamageRevision</c> (<c>roe_CalcDamage+0x5C1</c>) does. What was missing was the rate
    /// itself, and it is now read from the game's own tables — see <see cref="LevelGapTable"/>. Supply one
    /// through <c>CombatSimulation.LevelGaps</c>; the 1000 default here means "no table", which is right
    /// for a monster attacking a player and wrong by up to 50% the other way.</para>
    ///
    /// <para>The hook that runs just before it is <see cref="JobChangeDamageUpPermille"/>. Still not
    /// modelled: <c>so_ply_DecreaseDmgPassiveSkill</c> (<c>+0x59E</c>), the DEFENDER's half.</para></summary>
    public int LevelGapRatePermille { get; init; } = 1000;

    /// <summary>`ParamXServer.txt`'s <b>JobChangeDmgUp</b> column for the attacker's class at the attacker's
    /// level — the job-change catch-up multiplier, in permille.
    ///
    /// <para><c>null</c> means the hook does not run at all, which is a different thing from a rate of
    /// 1000 and is why this is nullable. <c>roe_CalcDamage+0x5B2</c> calls the ATTACKER's vtable slot
    /// 0xD2C; <c>ShineObject</c>'s implementation is <c>return dmg</c>, so a MOB attacker never reaches
    /// the multiplier, and <c>ShinePlayer::so_ply_JobChangeDamageUp</c> returns early unless the defender
    /// is a MONSTER. Player-versus-player and monster-versus-anything take neither path. A rate of 0, by
    /// contrast, is a real table value and really does zero the damage.</para>
    ///
    /// <para><b>This is large, and it was missing.</b> A first-job class is on <c>2000</c> — double damage
    /// — the moment it changes job at level 20, decaying by level to 1190 at 59 and back to 1000 at 60
    /// when the second job change lands. Second job: 1700 at 60 down to 1055 at 99. Third: 1100 at 100
    /// down to 1025, and 1000 from 120. A base class (Fighter, Cleric, Mage, Archer, Joker) is flat 1000
    /// at every level. So the bonus is a catch-up for having just started a new class, and a character in
    /// the first half of a job band hits monsters for far more than its stats suggest.</para>
    ///
    /// <para>Applied to the INTEGER damage, after the <c>_ftol</c> and before the level gap, as a 64-bit
    /// multiply and an unsigned divide by 1000. Verified against the real function under emulation over
    /// fourteen input sets — see <c>tools/oracle_jobchange_dmgup.py</c>.</para>
    ///
    /// <para>⚠️ The server adds a 0-or-1 draw from <c>rndbox</c> slot 2 to the rate before dividing (the
    /// slot is a shuffled pool of 0s and 1s, so that is all it can be). It is worth 0.08% and
    /// <see cref="DamageCalculator.Resolve"/> draws it, so a pinned <see cref="RollPermille"/> alone does
    /// not make a swing deterministic here — pass a seeded RNG.</para></summary>
    public int? JobChangeDamageUpPermille { get; init; }

    /// <summary>`ChargedEffectContainer` — the premium/charged-effect term, in 1024ths.
    ///
    /// <para>`roe_CalcDamage+0x466` compares the ATTACKER's `cec_AttackForceRate1024` with the DEFENDER's
    /// `cec_DefendForceRate1024` and applies whichever is larger, in opposite directions:</para>
    /// <code>
    /// if (attack &gt; defend) dmg = dmg * (1024 + (attack - defend)) / 1024;
    /// if (defend &gt; attack) dmg = dmg * 1024 / (1024 + (defend - attack));
    /// </code>
    ///
    /// <para>Equal values cancel, which is why 0/0 is the neutral default. Mobs share one static container
    /// (`so_ply_ChargedEffectContainer` returns a fixed global), so this is a player-side term in BOTH
    /// directions — a charged player takes less from mobs as well as dealing more.</para></summary>
    public int AttackForceRate1024 { get; init; }

    /// <inheritdoc cref="AttackForceRate1024"/>
    public int DefendForceRate1024 { get; init; }

    /// <summary>`ItemActionObserveManager::EventRun_IncDmgRate` — the item-action results, applied to
    /// BOTH weapon bounds before the min..max roll.
    ///
    /// <para>`roe_AttackPower+0x129/+0x155` runs it on the ATTACKER's manager and then the DEFENDER's,
    /// accumulating into one `ActionResults`. The defender's manager being consulted is the surprise: a
    /// player's own gear can scale the damage they RECEIVE.</para>
    ///
    /// <para>⚠️ <b>Not a single rate.</b> `GetRateAppliValue` COMPOUNDS the results one at a time, each
    /// with its own truncating divide, so they cannot be pre-multiplied into one number — see
    /// <see cref="ItemActionResults"/>. Null (or empty) is the neutral case, and it is a genuinely
    /// different code path rather than a multiply by one: the whole block is gated on an action having
    /// fired, and skipping it also skips the truncation of the bounds.</para>
    ///
    /// <para>Verified by oracle to sit BEFORE the roll and before the mastery rate — the same place the
    /// skill-empower term lands.</para></summary>
    public ItemActionResults? ItemActions { get; init; }

    /// <summary>The CAST SKILL's `ActiveSkillInfo` row, or null for a plain swing.
    ///
    /// <para>Only the two skill rules read it — see <see cref="EngagementRuleExtensions.ReadsSkillRow"/>.
    /// It scales and shifts each weapon bound INDEPENDENTLY, so it changes the range a swing is drawn
    /// from rather than the result.</para></summary>
    public Skill.ActiveSkillInfo? Skill { get; init; }

    /// <summary>The cast's empower allocation. Only its damage nibble is read, and only when
    /// <see cref="Skill"/> carries a table.</summary>
    public Skill.SkillEmpower Empower { get; init; }

    /// <summary>`ShinePlayer::so_ply_DecreaseDmgPassiveSkill` (0x005651E0) — the one hook that can REDUCE
    /// incoming damage, run on the DEFENDER at `roe_CalcDamage+0x59E`.
    ///
    /// <para>It is identity unless every one of these holds, which is why a clean capture never reaches
    /// it:</para>
    /// <list type="number">
    ///   <item>the attacker is a MONSTER (`so_ObjectType() == 5`)</item>
    ///   <item>the defender's `Parameter::Container::DMGMinusRate` (+0x0DAC) is non-zero</item>
    ///   <item>the attacker is within <c>DMG_MinusArea</c> — a `CSingleDataMap` config value, read once
    ///         and cached, compared as squared distance</item>
    ///   <item>a FACING test: the direction from defender to attacker (`ddt_DirectSR`) against the
    ///         attacker's own facing byte, folded at 0x5A (90 direction units = 180 degrees)</item>
    /// </list>
    ///
    /// <para>⚠️ The four gates are read; the final arithmetic past the facing fold is NOT, so this is an
    /// explicit permille input rather than a computed one. 1000 is identity.</para></summary>
    public int DecreaseDamagePassivePermille { get; init; } = 1000;

    /// <summary>The server's <c>EngageArgument.nBMPDamageRate</c>, applied to attack power inside the core
    /// damage step rather than to the final figure.</summary>
    public int BaseDamageRatePermille { get; init; } = 1000;

    /// <summary>How much HP the ATTACKER is missing, in permille — the condition key for the HP-down
    /// passive that `roe_AttackPower` adds to both weapon bounds.
    ///
    /// <para>Zero means "at full health", which is also what an unconfigured passive produces, so leaving
    /// it alone is safe. Compute it with
    /// <see cref="Parameter.ChangeByConditionParam.HpMissingPermille"/> rather than by hand — the server's
    /// divide is unsigned and the rounding matters at the bucket edges.</para></summary>
    public int AttackerHpMissingPermille { get; init; }

    /// <summary>The same, for the DEFENDER, feeding `roe_DefendPower`'s AC/MR block.</summary>
    public int DefenderHpMissingPermille { get; init; }

    /// <summary>The attacker's FREE-STAT points in this school's governing stat — Str for a physical rule,
    /// Int for a magical one. Added FLAT to the damage by `roe_Damage`'s per-rule override.
    ///
    /// <para>⚠️ <b>Free-stat points only, not the total stat.</b> The base value a class and level give you
    /// feeds the multiplicative part through the container; only the points the player SPENT show up here.
    /// The operator measured this in-client on 2026-07-29 — 30 points into END produced a clean −30 on
    /// damage taken — which is also where the 1:1 scale comes from, since the accessors read level-keyed
    /// records out of runtime-allocated globals that a static read cannot see.</para></summary>
    public int AttackerFreeStat { get; init; }

    /// <summary>The defender's free-stat points in this school's defensive stat — Con physical, Men
    /// magical. SUBTRACTED flat.</summary>
    public int DefenderFreeStat { get; init; }
}

/// <summary>The result of one resolved swing — the damage plus the intermediates worth logging.
///
/// <para>The intermediates are here so a surprising number can be explained without re-running anything:
/// a hit that looks too small is usually a low <see cref="RollPermille"/> or an unexpectedly high
/// <see cref="DefendPower"/>, and those are the two figures needed to tell which.</para></summary>
public readonly record struct AttackOutcome(
    int Damage,
    bool WasCritical,
    int RollPermille,
    double AttackPower,
    double DefendPower);
