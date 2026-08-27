using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Mob;

/// <summary>What one mob attack costs in time, in the server's own units.
///
/// <para>All three are in <b>tenths of a second</b>, because that is the resolution the zone's timed logic
/// runs at — the main loop maintains a tenths counter and `mab_Think` converts every weapon timing into it.
/// Keeping the port in tenths rather than milliseconds preserves the truncation.</para></summary>
/// <param name="SwingTenths">How long the swing itself takes.</param>
/// <param name="HitTenths">How long after the swing starts the damage lands.</param>
/// <param name="DelayTenths">Extra delay added before the NEXT attack may begin — `AtkDly`.</param>
/// <param name="CastTenths">The cast time of the SKILL on this weapon row, from
/// `ShineMob::sm_GetWeaponCastTime`. Zero for an ordinary swing.</param>
public readonly record struct MobAttackTiming(int SwingTenths, int HitTenths, int DelayTenths, int CastTenths)
{
    /// <summary>Time from the start of one attack to the start of the next — the COMPLETE sum.
    ///
    /// <para>Read off `mab_Think+0x8B5`, which is the whole of it:</para>
    /// <code>
    /// if (swing &lt; 0) swing = 0;  if (hit &lt; 0) hit = 0;  if (delay &lt; 0) delay = 0;   // +0x8A3
    /// nextAttackAt = delay + swing + weaponCastTime + clockwatchNow;                  // +0x8B8..+0x8C3
    /// </code>
    ///
    /// <para>The third term was open for a while and is `sm_GetWeaponCastTime()`. Two details it is worth
    /// not losing: the cast time is added <b>after</b> the three clamps, so it is not clamped itself; and
    /// it is <b>not</b> scaled by the attack-speed rate, which the other three are. Haste does not shorten
    /// a cast.</para>
    ///
    /// <para>⚠️ It is zero for every attack this simulation currently makes, and that is a fact about the
    /// data rather than a simplification: `MobWeapon.Skill` is `-` on <b>weapon row 0 of all 2,834 mobs</b>,
    /// and row 0 is the row `mab_Think` forces when the target is a player.</para></summary>
    public int IntervalTenths => DelayTenths + SwingTenths + CastTenths;

    public uint SwingMs => (uint)(SwingTenths * 100);
    public uint HitMs => (uint)(HitTenths * 100);
    public uint IntervalMs => (uint)(IntervalTenths * 100);
}

/// <summary>Computing a mob's attack timings the way `MobActionAttack::mab_Think` (0x004BBA00) does.
///
/// <para><b>Why this is a ported function rather than three field reads.</b> The four timing columns do not
/// mean what their names suggest, and an earlier version of this project guessed twice and was wrong twice:
/// first taking `AtkDly` for the swing interval, then taking `SwingTime` for it. Reading `mab_Think`
/// settles all four.</para></summary>
public static class MobAttackTimingCalculator
{
    /// <summary>The fixed-point scale in the normalisation — <c>shl eax, 7</c>, twice.</summary>
    private const int Scale = 128;

    /// <summary>The permille identity for the attack-speed modifier.</summary>
    private const int RateIdentity = 1000;

    /// <summary>`ms * 10 / 1000`, i.e. milliseconds to tenths of a second, truncating toward zero exactly as
    /// the original's <c>lea/add</c> then <c>0x10624DD3</c> magic divide does.</summary>
    private static int ToTenths(int milliseconds) => milliseconds * 10 / 1000;

    /// <summary>Port of the timing block of `mab_Think`.
    ///
    /// <para>The shape, in order:</para>
    /// <code>
    /// normalised = SwingTime * 128 / max(AtkSpd, 1)      // floored at 1 if it truncates to 0
    /// swing      = (SwingTime/100) * 128 / normalised
    /// swing      = swing * attackSpeedRate / 1000
    /// hit        = (HitTime/100) * attackSpeedRate / 1000
    /// delay      = (AtkDly/100)  * attackSpeedRate / 1000
    /// cast       = sm_GetWeaponCastTime() / 100          // NOT scaled by attackSpeedRate
    /// </code>
    ///
    /// <para><b>The two `128`s cancel algebraically</b>, which is the key to reading this: substituting the
    /// first line into the second gives <c>swing ≈ AtkSpd / 100</c>. So <b>`AtkSpd` is the real swing
    /// duration</b> and `SwingTime` is only the reference it is normalised against — which is exactly why
    /// the two are equal for most mobs and differ only for ones deliberately quickened or slowed.</para>
    ///
    /// <para>They are NOT collapsed to <c>AtkSpd / 100</c> here. Each step truncates, and the intermediate
    /// floor-at-1 is observable, so the two-step form is kept.</para>
    ///
    /// <para><paramref name="attackSpeedRate"/> is <see cref="Stat.AttSpeed"/> from the mob's
    /// <see cref="StatModifier.AbnormalState"/> RATE cluster — container offset 0xA18 — so a haste or slow
    /// abnormal state scales all three timings together.</para></summary>
    public static MobAttackTiming Compute(MobWeapon weapon, int attackSpeedRate = RateIdentity,
                                          int castTimeMs = 0)
    {
        // AtkSpd of zero would divide by zero; the original substitutes 1.
        var atkSpd = weapon.AtkSpd != 0 ? weapon.AtkSpd : 1;

        var normalised = weapon.SwingTime * Scale / atkSpd;
        if (normalised == 0) normalised = 1;                       // `test eax,eax; jne; mov eax,1`

        var swing = ToTenths(weapon.SwingTime) * Scale / normalised;
        swing = swing * attackSpeedRate / 1000;

        var hit = ToTenths(weapon.HitTime) * attackSpeedRate / 1000;
        var delay = ToTenths(weapon.AtkDly) * attackSpeedRate / 1000;

        // Each of the three is clamped at zero before use (`test/jns/xor`), so a negative rate cannot make
        // an attack land in the past. The cast term is added AFTER those clamps in the original and is not
        // one of them, so it is not clamped here either.
        return new MobAttackTiming(Math.Max(0, swing), Math.Max(0, hit), Math.Max(0, delay),
                                   ToTenthsUnsigned(castTimeMs));
    }

    /// <summary>The same `ms * 10 / 1000` as <see cref="ToTenths"/>, but UNSIGNED — `sm_GetWeaponCastTime`
    /// ends in <c>mul</c> / <c>shr edx, 6</c> where the timing block uses <c>imul</c> / <c>sar</c>.
    ///
    /// <para>They agree for every value a cast time can hold. Kept separate anyway: the two idioms are
    /// visibly different in the binary and collapsing them would quietly assert that the distinction does
    /// not matter, which is a claim rather than a reading.</para></summary>
    private static int ToTenthsUnsigned(int milliseconds)
        => milliseconds <= 0 ? 0 : (int)((uint)milliseconds * 10u / 1000u);

    /// <summary>The attack-speed rate a mob's stat container carries — `[container + 0xA18]`, which is
    /// <see cref="Stat.AttSpeed"/> of the AbnormalState rate cluster.</summary>
    public static int AttackSpeedRate(ParameterContainer container)
        => container.Rate(StatModifier.AbnormalState)[Stat.AttSpeed];
}
