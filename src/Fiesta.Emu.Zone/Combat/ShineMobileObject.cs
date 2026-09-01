using Fiesta.Emu.Zone.Random;

namespace Fiesta.Emu.Zone.Combat;

/// <summary>What `ItemActionObserveManager::EventRun` (0x005D1F30) contributes to the three rolls, in
/// permille — the `roe_*ByGlobalAction` half of each.
///
/// <para>All three functions have the same shape: build an `ItemActionArgument` on the stack with an
/// action TYPE, walk the observers `HasItemActionObserves` returns and sum `GetPlusAppliValue`. The
/// types are 6 for shield block, 5 for hit and 4 for critical, and how many passes each makes differs —
/// block runs defender then attacker, hit runs both again with a second sub-flag, critical runs the
/// attacker twice.</para>
///
/// <para>Item actions are not modelled yet, so this exists to keep the roll order honest rather than to
/// compute anything: <see cref="None"/> is what a combatant with no such items produces.</para></summary>
public sealed record ItemActionRates
{
    /// <summary>No item actions on either side.</summary>
    public static ItemActionRates None { get; } = new();

    /// <summary>`roe_ShieldBlockByGlobalAction@NormalPY` (0x00505090) — the raw sum, so 0 is neutral.</summary>
    public int ShieldBlockPermille { get; init; }

    /// <summary>`roe_HitRateByGlobalAction@NormalPY` (0x005051C0) — <c>max(1000 - sum, 0)</c>.
    ///
    /// <para><b>The neutral value is 1000, not 0.</b> `smo_SwingDamage` only lets a swing through when
    /// this is at least the draw, so a port that returned 0 here would make every swing in the game miss.
    /// It is the one ByGlobalAction hook that is inverted.</para></summary>
    public int HitRatePermille { get; init; } = 1000;

    /// <summary>`roe_CriticalRateByGlobalAction@NormalPY` (0x00504B10) — the raw sum; 0 is neutral.</summary>
    public int CriticalRatePermille { get; init; }
}

/// <summary>One resolved swing. The four flags are `EngageArgument`'s own outcome bytes —
/// <c>iscritical@0x10</c>, <c>ismiss@0x11</c>, <c>isdead@0x12</c>, <c>isshieldblock@0x13</c> — which are
/// also the bit layout of <c>NC_BAT_SWING_DAMAGE_CMD.flag</c>, so a capture's flag word can be compared
/// against this field for field.</summary>
/// <param name="IsShieldBlock">The defender shield-blocked. Set inside `roe_HitRate`, which then returns a
/// hit rate of 0 — so a block nearly always produces a miss as well.</param>
/// <param name="IsMiss">`EngageArgument.ismiss`.</param>
/// <param name="IsCritical">`EngageArgument.iscritical`.</param>
/// <param name="IsStun">The critical also stunned, applying abstate
/// <see cref="DamageCalculator.CriticalStunAbstateIndex"/>. Not a swing flag — the stun reaches the client
/// as its own `ABSTATE` broadcast.</param>
/// <param name="Damage">0 on a miss; otherwise what <see cref="DamageCalculator.Resolve"/> produced.</param>
/// <param name="HitRate">What `roe_HitRate` returned, kept for explaining a surprising outcome.</param>
/// <param name="ShieldBlockRate">What `roe_ShieldBlock` returned.</param>
/// <param name="CriticalRate">What `roe_CriticalRate` returned, including `crirateadd`.</param>
public readonly record struct SwingOutcome(
    bool IsShieldBlock,
    bool IsMiss,
    bool IsCritical,
    bool IsStun,
    int Damage,
    double HitRate,
    double ShieldBlockRate,
    double CriticalRate);

/// <summary>`ShineObjectClass::ShineMobileObject` — the swing half of it.
///
/// <para>`smo_SwingDamage` (0x00432F30) is where a swing is DECIDED; `roe_CalcDamage` is only reached once
/// it has landed. Keeping them in separate types is the binary's own structure rather than a tidying
/// choice: the rule vtable has no entry for any of this, and that separation is why filtering a capture to
/// <c>flagWord == 0</c> was a sound way to validate <see cref="DamageCalculator"/> in isolation.</para>
///
/// <para><b>The order, and every draw</b> (`ShineMob` overrides `smo_SwingDamage` at 0x00433A70; this is
/// the `ShineMobileObject` version, which is what a player swings with):</para>
/// <list type="number">
///   <item>`roe_ShieldBlock`, then a draw. <c>draw &lt; rate</c> blocks and forces the hit rate to 0.</item>
///   <item>Only if the DEFENDER has item actions: `roe_ShieldBlockByGlobalAction` and a second draw.</item>
///   <item>`roe_HitRate`, then a draw. <c>draw &gt; rate</c> MISSES — so the hit comparison is the
///         non-strict one, unlike block and critical.</item>
///   <item>`roe_HitRateByGlobalAction` and a draw. <c>value &gt;= draw</c> passes.</item>
///   <item>Inside `roe_CalcDamage`: `roe_CriticalRate` + <c>crirateadd</c>, then a draw.
///         <c>draw &lt; rate</c> crits.</item>
///   <item>Only if the ATTACKER has item actions and the first critical roll failed:
///         `roe_CriticalRateByGlobalAction` and a draw.</item>
///   <item>Only on a critical: `roe_CriticalStun`, one draw against a flat
///         <see cref="DamageCalculator.CriticalStunRatePermille"/>.</item>
/// </list>
///
/// <para><b>Draws are taken only where the original takes them.</b> That is not tidiness — WELL512 is one
/// shared stream, so an extra draw on a path the server skips desynchronises every later roll.</para></summary>
public static class ShineMobileObject
{
    /// <summary>`smo_SwingDamage` — roll the swing, then compute its damage if it landed.</summary>
    /// <param name="rng">The server's generator. Combat randomness on the real server is WELL512, and
    /// this is the same stream the damage roll comes off, so one seed reproduces the whole swing.</param>
    /// <param name="itemActions">The `roe_*ByGlobalAction` contributions.
    /// <see cref="ItemActionRates.None"/> for a combatant carrying no such items.</param>
    /// <param name="rndbox">`so_ply_JobChangeDamageUp`'s 0-or-1 addition comes from `rndbox` slot 2, a
    /// different generator from WELL512 — hence a second parameter rather than a shortcut.
    /// <c>null</c> uses <see cref="System.Random.Shared"/>.</param>
    public static SwingOutcome SwingDamage(ICombatant attacker, ICombatant defender, cWell512Random rng,
                                           AttackModifiers? modifiers = null,
                                           EngagementRule rule = EngagementRule.NormalPhysical,
                                           ItemActionRates? itemActions = null,
                                           System.Random? rndbox = null)
    {
        var mods = modifiers ?? AttackModifiers.Default;
        var actions = itemActions ?? ItemActionRates.None;

        // ---- 1 and 2: shield block, inside roe_HitRate ----------------------------------------------
        var blockRate = DamageCalculator.ShieldBlockRate(defender);
        var isShieldBlock = rng.well512_GetRandom(1000) < blockRate;

        if (!isShieldBlock && defender.HasItemActionObserves)
            isShieldBlock = rng.well512_GetRandom(1000) < actions.ShieldBlockPermille;

        // A block does not report itself and stop. `roe_HitRate` sets `isshieldblock` and returns 0.0, and
        // the caller then rolls against that 0 like any other rate -- so a blocked swing resolves as a
        // miss too, except in the one draw in a thousand that comes up 0.
        var hitRate = isShieldBlock ? 0.0 : DamageCalculator.HitRate(attacker, defender);

        // ---- 3 and 4: the two hit rolls -------------------------------------------------------------
        var hit = rng.well512_GetRandom(1000) <= hitRate;
        if (hit)
            hit = actions.HitRatePermille >= rng.well512_GetRandom(1000);

        if (!hit)
        {
            // `smo_SwingDamage+0x1C9`: an attacker whose TOTAL AbsoluteAttack is at least 1 skips the miss
            // notification and the `ismiss` flag -- but the `roe_CalcDamage` call is skipped either way, so
            // the swing still lands for nothing. Ported as written. It reads more like a server bug than a
            // mechanic, and inventing a "guaranteed hit" here would be a guess dressed up as a fix.
            var absolute = attacker.Parameters.MakeTotal()[Parameter.Stat.AbsoluteAttack] >= 1;
            return new SwingOutcome(isShieldBlock, IsMiss: !absolute, IsCritical: false, IsStun: false,
                                    Damage: 0, hitRate, blockRate, CriticalRate: 0);
        }

        // ---- 5 and 6: the critical rolls, inside roe_CalcDamage --------------------------------------
        var critRate = DamageCalculator.CriticalRate(attacker, defender) + mods.CriticalRateAddPermille;
        var isCritical = rule.AlwaysCriticals() || rng.well512_GetRandom(1000) < critRate;

        if (!isCritical && attacker.HasItemActionObserves)
            isCritical = rng.well512_GetRandom(1000) < actions.CriticalRatePermille;

        // ---- 7: roe_CriticalStun --------------------------------------------------------------------
        // It returns immediately unless the swing critted, so the draw lives inside the branch. The
        // comparison is rate STRICTLY above draw -- the block convention, not the hit one.
        var isStun = isCritical
                     && rng.well512_GetRandom(1000) < DamageCalculator.CriticalStunRatePermille;

        // ---- the damage, with both random inputs already decided -------------------------------------
        var damageMods = mods with
        {
            RollPermille = mods.RollPermille ?? (int)rng.well512_GetRandom(1001),
            ForceCritical = isCritical,
        };
        var outcome = DamageCalculator.Resolve(attacker, defender, damageMods,
                                               rndbox ?? System.Random.Shared, rule);

        return new SwingOutcome(isShieldBlock, IsMiss: false, isCritical, isStun,
                                outcome.Damage, hitRate, blockRate, critRate);
    }
}
