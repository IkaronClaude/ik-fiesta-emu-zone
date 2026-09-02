namespace Fiesta.Emu.Zone.Combat;

/// <summary>`ShinePlayer::so_ply_DecreaseDmgPassiveSkill` (0x005651E0) — a POSITIONAL damage reduction:
/// the defender takes less from a nearby monster that is not facing them.
///
/// <para>One of the five hooks a clean swing never reaches, and the reason is now concrete rather than
/// mysterious: it needs a monster attacker, a configured rate, and a geometry that a capture only
/// produces by accident. The base `ShineObject` implementation returns the damage unchanged, so
/// <b>only a player is ever protected by it</b>.</para>
///
/// <para>Four gates, all of which must pass before any arithmetic happens:</para>
/// <list type="number">
///   <item>the attacker is non-null;</item>
///   <item><c>attacker.so_GetKind() == 5</c> — a MONSTER. Player-versus-player is unaffected;</item>
///   <item><c>defender.Parameters.DMGMinusRate &gt; 0</c> (container +0x0DAC) — the passive is configured;</item>
///   <item>both objects have a position.</item>
/// </list>
///
/// <para>Then geometry, in two steps:</para>
/// <code>
/// if (DMG_MinusArea² &lt; distanceSquared)  return damage;   // too far away: no protection
/// bearing = ddt_DirectSR(defender - attacker);              // where the attacker would have to look
/// diff    = fold(|attacker.facing - bearing|)               // into 0..180
/// if (diff &lt;= 45) return damage;                           // the attacker IS facing you: full damage
/// return damage - damage * DMGMinusRate / 1000;
/// </code>
///
/// <para><b>THE UNIT IS DIRECTION UNITS OF 2° EACH</b> — the direction value is the bearing in degrees,
/// HALVED. Not a guess: <see cref="DamageByAngleTable.DegreesToUnits"/> is this repo's port of
/// `sr_degree2sr` (0x00579180), which computes <c>(deg % 360) * 180 / 360</c>, and `DamageByAngle`'s dense
/// table is `uint16[91]` covering 0–90 units = 0–180°. Two independent places, same quantum.</para>
///
/// <para>With that, every constant reads plainly:</para>
/// <list type="bullet">
///   <item>the fold's modulus of 180 units is a <b>FULL TURN</b> (360°), so a difference of 180 folding to
///         0 is simply 360° ≡ 0°;</item>
///   <item>the 45-unit band is <b>90°</b>;</item>
///   <item>so the reduction applies when the monster is facing more than <b>90° away</b> from you — you
///         are beside or behind it — and standing in its forward half gives nothing.</item>
/// </list>
///
/// <para>⚠️ Worth recording because it caught me twice in opposite directions: reading these as degrees
/// makes the fold look like a 90° axis and the rule look like a perpendicularity test, which is wrong;
/// reading the unit as HALF a degree makes the band 22.5°, which is also wrong. It is 2° per unit, and
/// only the ported `sr_degree2sr` settles it. The numeric tests were right under all three readings — the
/// arithmetic never changed — so only the prose was ever at risk.</para></summary>
public static class DecreaseDamagePassive
{
    /// <summary>`so_GetKind()` for a monster — the only attacker kind this hook responds to.</summary>
    public const int MonsterKind = 5;

    /// <summary>Half-width of the monster's forward cone: 45 direction units = <b>90°</b>. At or below it
    /// the monster counts as facing you and the reduction does NOT apply.</summary>
    public const int FacingConeUnits = 45;

    /// <summary>The fold's modulus: 180 direction units = a <b>FULL TURN</b>, 360°. `|difference|` is
    /// reduced into 0..90 units (0..180°) before the cone test, so a wrap-around bearing is not mistaken
    /// for a wide angle.</summary>
    public const int FullTurnUnits = 180;

    /// <summary>The config key `CSingleDataMap` is asked for, once, on first use — the radius inside which
    /// the passive works. Read lazily and cached in a global, which is why it cannot be treated as a
    /// per-character value.</summary>
    public const string AreaConfigKey = "DMG_MinusArea";

    /// <summary>The angle fold at +0x145: bring an absolute difference into 0..90 units (0..180°).
    ///
    /// <code>
    /// if (diff &gt; 90) { k = (diff - 91) / 180; diff = |diff + (-180 - k*180)|; }
    /// </code>
    ///
    /// <para>Written out rather than replaced with a modulo. For the byte-sized inputs this can actually
    /// receive the two agree, but the original's division is by a magic reciprocal on an UNSIGNED value
    /// and its <c>k</c> is computed from <c>diff - 91</c>, not <c>diff</c> — so an "equivalent" modulo is
    /// an assumption about the input range rather than a simplification.</para></summary>
    public static int FoldAngle(int difference)
    {
        var diff = Math.Abs(difference);
        if (diff <= 90) return diff;

        var k = (diff - 91) / FullTurnUnits;
        return Math.Abs(diff + (-FullTurnUnits - k * FullTurnUnits));
    }

    /// <summary>Whether the monster counts as facing the defender — inside its 90° forward cone — in
    /// which case there is no reduction.</summary>
    /// <param name="attackerFacing">The attacker's direction byte (+0x6A).</param>
    /// <param name="bearingToDefender">`ddt_DirectSR(defender - attacker)`.</param>
    public static bool AttackerIsFacing(int attackerFacing, int bearingToDefender)
        => FoldAngle(attackerFacing - bearingToDefender) <= FacingConeUnits;

    /// <summary>The whole hook. Returns the damage unchanged wherever a gate fails, exactly as the
    /// original does — every early exit returns the argument rather than zero.</summary>
    /// <param name="attackerKind">`so_GetKind()`. Anything but <see cref="MonsterKind"/> skips the hook.</param>
    /// <param name="dmgMinusRatePermille">The defender's `DMGMinusRate` (container +0x0DAC). Zero or below
    /// disables the passive at gate 3 — so it never reaches the arithmetic and can never scale damage
    /// to nothing.</param>
    /// <param name="distanceSquared">Squared distance between the two, in position units.</param>
    /// <param name="areaConfig">`DMG_MinusArea`. Compared SQUARED, so no square root is taken.</param>
    /// <param name="samePosition">The original checks both coordinates for equality before taking a
    /// bearing and returns unchanged when they match — a bearing from a point to itself is meaningless.</param>
    public static int Apply(int damage, int attackerKind, int dmgMinusRatePermille,
                            long distanceSquared, int areaConfig,
                            int attackerFacing, int bearingToDefender, bool samePosition = false)
    {
        if (attackerKind != MonsterKind) return damage;
        if (dmgMinusRatePermille <= 0) return damage;
        if ((long)areaConfig * areaConfig < distanceSquared) return damage;
        if (samePosition) return damage;
        if (AttackerIsFacing(attackerFacing, bearingToDefender)) return damage;

        return damage - unchecked(dmgMinusRatePermille * damage) / 1000;
    }
}
