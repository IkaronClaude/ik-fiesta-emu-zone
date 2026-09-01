using System;
using static Fiesta.Emu.Zone.Combat.ServerArithmetic;

using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Combat;

/// <summary>Fiesta's damage engine, ported 1:1 from <c>RulesOfEngagement</c> in <c>Zone.exe</c>.
///
/// <para>Everything here was read out of the server binary and is verified against it by differential
/// fuzzing: <c>tools/fuzz_extreme.py</c> drives this code and the real code (under emulation) on identical
/// inputs and requires <b>exact bitwise agreement</b>. Deterministic edge cases live in
/// <c>tests/Fiesta.Bot.Tests/DamageCalculatorTests.cs</c>.</para>
///
/// <para>⚠️ <b>Do not simplify the arithmetic.</b> Operation order, the floors, and the truncation points
/// are all observable in the output. Several of the comments below mark places where the mathematically
/// equivalent rewrite produces a different number.</para>
///
/// <para>Typical use — "how hard does this mob hit me, worst case":</para>
/// <code>
/// var worst = DamageCalculator.Resolve(mob, me, new AttackModifiers
/// {
///     RollPermille = 1000, ForceCritical = true
/// });
/// </code>
///
/// See docs/DAMAGE_FORMULA.md.</summary>
public static class DamageCalculator
{
    // ---- the shared stat chain -------------------------------------------------------------------------

    /// <summary>One stat's value across every modifier layer:
    /// <c>(Base + Item.plus) x four rate halves / 1e12 + five plus halves</c>.
    ///
    /// <para>The rate halves are multiplied in the order AbnormalState, PassiveSkill, ItemPowerRate,
    /// LastTune — the order of the <c>fild</c> sequence at the head of every accessor — before a single
    /// divide. Floating-point multiplication is not associative, so a different order lands an ULP away,
    /// which is invisible until one of the truncating accessors turns it into a whole point.</para></summary>
    private static double Chain(ParameterContainer s, Stat stat)
    {
        var value = (double)s.Base[stat] + s.Plus(StatModifier.Item)[stat];

        value = value * s.Rate(StatModifier.AbnormalState)[stat]
                      * s.Rate(StatModifier.PassiveSkill)[stat]
                      * s.Rate(StatModifier.ItemPowerRate)[stat]
                      * s.Rate(StatModifier.LastTune)[stat] / RateDivisor;

        // Five SEPARATE additions, in this order, exactly as the binary's fild/fadd pairs do. Folding them
        // into one sum is wrong twice over: with `int` operands it overflows int32 before promoting, and
        // even in double it re-associates — above 2^53, where the gap between representable doubles is 2,
        // pre-summing lands one ULP out (1841195746 against the server's 1841195744).
        value += s.Plus(StatModifier.Upgrade)[stat];
        value += s.Plus(StatModifier.WeaponTitle)[stat];
        value += s.Plus(StatModifier.PassiveSkill)[stat];
        value += s.Plus(StatModifier.AbnormalState)[stat];
        value += s.Plus(StatModifier.LastTune)[stat];
        return value;
    }

    /// <summary>The chain over the stat that GOVERNS an accessor — Str for weapon damage, Con for armour,
    /// Dex for hit and block, Men for magic resistance — floored at 1.
    ///
    /// <para>The floor belongs here and not on the accessor's own half: with an empty container every
    /// accessor returns exactly 1, and flooring both halves would return 2.</para></summary>
    private static double GoverningChain(ParameterContainer s, Stat governing) => FloorAtOne(Chain(s, governing));

    // ---- defensive accessors ---------------------------------------------------------------------------

    /// <summary>Armour class — physical damage reduction. The server's <c>roe_AC</c>.
    ///
    /// <para>Its first trailing rate is applied INSIDE the truncation and the second outside, which is why
    /// the result is not always a whole number: at (AbnormalState, ItemPowerRate) = (500, 7) it returns
    /// 3.5. Verified at four rate pairs.</para></summary>
    public static double ArmourClass(ICombatant defender)
    {
        var s = defender.Parameters;
        var sum = GoverningChain(s, Stat.Con) + Chain(s, Stat.AC);
        var truncated = Ftol32(ApplyRate(sum, s.Rate(StatModifier.AbnormalState)[Stat.AC]));
        return FloorAtOne(ApplyRate(truncated, s.Rate(StatModifier.ItemPowerRate)[Stat.AC]));
    }

    /// <summary>Magic resistance. The server's <c>roe_MR</c>.
    ///
    /// <para>Unlike <see cref="ArmourClass"/> its rate is applied OUTSIDE the truncation — with a sum of
    /// 1000.6 and a rate of 3000, AC gives 3001 and MR gives 3000. Asymmetric, but measured.</para></summary>
    public static double MagicResistance(ICombatant defender)
    {
        var s = defender.Parameters;
        var truncated = Ftol32(GoverningChain(s, Stat.Men) + Chain(s, Stat.MR));
        return FloorAtOne(ApplyRate(truncated, s.Rate(StatModifier.ItemPowerRate)[Stat.MR]));
    }

    /// <summary>Block rating. The server's <c>roe_TB</c>. Truncates its sum; <see cref="ToHitRating"/> does not.</summary>
    public static double ToBlockRating(ICombatant defender)
    {
        var s = defender.Parameters;
        return FloorAtOne(Ftol32(GoverningChain(s, Stat.Dex) + Chain(s, Stat.TB)));
    }

    // ---- offensive accessors ---------------------------------------------------------------------------

    /// <summary>Hit rating. The server's <c>roe_TH</c>. Applies no trailing rate and does NOT truncate,
    /// which makes it the cleanest probe of the shared chain.</summary>
    public static double ToHitRating(ICombatant attacker)
    {
        var s = attacker.Parameters;
        return FloorAtOne(GoverningChain(s, Stat.Dex) + Chain(s, Stat.TH));
    }

    // ---- the swing rolls: block, hit, critical ---------------------------------------------------------

    /// <summary>An ordinary melee `AttackRange` — 100. Anything at or below
    /// <see cref="RangedAttackThreshold"/> skips the ranged-evasion subtraction.</summary>
    public const int MeleeAttackRange = 100;

    /// <summary>`roe_FreeStatHitRate+0x1E` compares the attack's range against <b>300</b>, STRICTLY
    /// greater. An Archer's 450 is over it; every other class's 100 is not.</summary>
    public const int RangedAttackThreshold = 300;

    /// <summary>`RulesOfEngagement::roe_CriticalStunRate` (0x00500170) is a one-line
    /// <c>fld 200.0</c> — a flat 20% chance that a critical also stuns, with no stat feeding it.
    /// `NormalPY` does not override vtable slot 0, so this is the physical rule's value.</summary>
    public const int CriticalStunRatePermille = 200;

    /// <summary>The abstate a critical stun applies: <c>ABSTATEINDEX 0x133</c> = <b>307</b>, which
    /// `AbState.shn` names `StaCommonStun02`.
    ///
    /// <para>`roe_CriticalStun` (0x00500070) hard-codes it — <c>push 0x133; call
    /// AbState::as_FromIndex</c> — so it is not a table lookup that could vary. 307 is also one of the
    /// abstates observed applied to mobs in `FighterDamageLvl60.pcapng`, which makes this the one part of
    /// the roll phase with ground truth already on the wire.</para></summary>
    public const int CriticalStunAbstateIndex = 307;

    /// <summary>`roe_ShieldBlock@NormalPY` (0x004FF860) — the DEFENDER's chance to shield-block, in
    /// permille.
    ///
    /// <code>
    /// block = (Upgrade.Plus[ShieldAC] + Item.Plus[ShieldAC]) * AbnormalState.Rate[ShieldAC] / 1000
    ///           +0x464                   +0x134                 +0x9F8
    /// </code>
    ///
    /// <para>All three are <see cref="Stat.ShieldAC"/> (slot 26) in their own halves, and <c>+0x9F8</c> is
    /// exactly the address `SAA_SHIELDACRATE` writes — the abstate decode and this formula meet at the
    /// same offset, which checks both.</para>
    ///
    /// <para>Note the shape: gear contributes a PLUS and only the abnormal-state layer contributes a RATE.
    /// A non-positive result returns 0 (the original branches out at +0x76), so a character with no shield
    /// can never block.</para></summary>
    public static double ShieldBlockRate(ICombatant defender)
    {
        var s = defender.Parameters;
        var plus = (double)s.Plus(StatModifier.Upgrade)[Stat.ShieldAC] + s.Plus(StatModifier.Item)[Stat.ShieldAC];
        var value = plus * s.Rate(StatModifier.AbnormalState)[Stat.ShieldAC] / 1000.0;
        return value > 0 ? value : 0.0;
    }

    /// <summary>`roe_HitRate@NormalPY` (0x005011F0) MINUS its two shield-block rolls — the chance to hit,
    /// in permille, given that nothing blocked.
    ///
    /// <para>The block rolls are deliberately not here. They consume random draws and they set
    /// `EngageArgument.isshieldblock`, which makes them part of RESOLVING a swing rather than part of
    /// computing a rate; <see cref="ShineMobileObject.SwingDamage"/> (`smo_SwingDamage`) does them in the original's order. What is left is:</para>
    ///
    /// <code>
    /// if (MissPercentFix != 0)                       // defender, +0x0CD0
    ///     return MissPercentFix &gt; 1000 ? 0 : 1000 - MissPercentFix;
    /// th = roe_TH(attacker) + attacker.FreeStatDex.THRate
    /// tb = roe_TB(defender) + defender.FreeStatDex.TBRate
    /// if (defender.IsMoving) tb += defender.PassiveMovingTBPlus[0]
    /// hit = (int)(th * 850.0 / tb)
    /// if (attackRange &gt; 300) hit -= defender.RangeEvasion
    /// return hit
    /// </code>
    ///
    /// <para><b>850, not 1000.</b> `roe_HitRate+0x392` multiplies by the constant at 0x006D03E0, which is
    /// 850.0 — so equal Aim and Evasion is an 85% chance, not a certainty, and hit rate only reaches 1000
    /// when Aim is about 18% above Evasion.</para>
    ///
    /// <para>The truncation is a real <c>_ftol</c> and happens BEFORE the ranged-evasion subtraction,
    /// because `roe_FreeStatHitRate` takes an <c>int</c>. The MissPercentFix path skips
    /// `roe_FreeStatHitRate` entirely, so a fixed miss chance also ignores ranged evasion.</para></summary>
    public static double HitRate(ICombatant attacker, ICombatant defender)
    {
        var d = defender.Parameters;

        // The short-circuit is on the DEFENDER and replaces everything below it. Above 1000 the original
        // returns zero rather than a negative rate -- and, unlike a real block, leaves `isshieldblock` unset.
        if (d.MissPercentFix != 0)
            return d.MissPercentFix > 1000 ? 0.0 : 1000.0 - d.MissPercentFix;

        var th = ToHitRating(attacker) + attacker.FreeStatDexTHRate;
        var tb = ToBlockRating(defender) + defender.FreeStatDexTBRate;

        // `so_mobile_IsInMoving` gates the ONLY movement-dependent term in the whole damage engine. Note
        // the index accessor: `cbcp_GetValue_Index(0)`, not the HP-keyed `cbcp_GetValue`.
        if (defender.IsInMoving)
            tb += d.PassiveMovingTbPlus.ValueAtIndex(0);

        var hit = Ftol32(th * 850.0 / tb);

        // `roe_FreeStatHitRate` (0x00500010). The threshold is on the ATTACK's range, so a bow's 450 pays
        // the defender's RangeEvasion and a sword's 100 does not.
        if (attacker.AttackRange > RangedAttackThreshold)
            hit -= d.RangeEvasion;
        return hit;
    }

    /// <summary>`roe_CriticalRate@NormalPY` (0x00501700) — the chance of a critical, in permille.
    ///
    /// <code>
    /// crit = Item.Rate[CriDamRate](att)          +0x218
    ///      + WeaponTitle.Rate[CriDamRate](att)   +0x6E0
    ///      + AbnormalState.Rate[CriDamRate](att) +0xA10
    ///      - AbnormalState.Rate[CriticalTB](def) +0xA38
    ///      - Item.Rate[CriticalTB](def)          +0x240
    /// crit += attacker.FreeStatMen.CriRate                  // roe_FreeStatCriRate
    /// return max(crit, 1.0)
    /// </code>
    ///
    /// <para>⚠️ <b>The slot the attacker's three terms read is <see cref="Stat.CriDamRate"/>, not
    /// <see cref="Stat.Critical"/>.</b> That is what the offsets say — 0x218 is Item.Rate + 0x80 and
    /// `Parameter::Cluster+0x80` is `CriDamRate` in the PDB — and the name is simply misleading: this slot
    /// carries the crit CHANCE, and `Critical` (slot 23) is not read by this function at all. Renaming the
    /// slot would be inventing a fact; reading a different slot because its name reads better would be
    /// worse.</para>
    ///
    /// <para>The defender resists with <see cref="Stat.CriticalTB"/> out of two layers only — its own
    /// buffs and its gear — with no base or upgrade term.</para>
    ///
    /// <para>The floor is 1.0, so there is always a 1-in-1000 critical. The rate is compared against a
    /// draw with STRICT less-than, which is why the floor produces exactly one outcome in a thousand and
    /// not two.</para></summary>
    public static double CriticalRate(ICombatant attacker, ICombatant defender)
    {
        var a = attacker.Parameters;
        var d = defender.Parameters;

        // The original's association: ((WeaponTitle + Item + AbnormalState) - defenderAbnormal) - defenderItem.
        var sum = (double)a.Rate(StatModifier.WeaponTitle)[Stat.CriDamRate];
        sum += a.Rate(StatModifier.Item)[Stat.CriDamRate];
        sum += a.Rate(StatModifier.AbnormalState)[Stat.CriDamRate];
        sum -= d.Rate(StatModifier.AbnormalState)[Stat.CriticalTB];
        sum -= d.Rate(StatModifier.Item)[Stat.CriticalTB];

        var crit = Ftol32(sum) + attacker.FreeStatMenCriRate;
        return crit >= 1.0 ? crit : 1.0;
    }

    /// <summary>Bottom of the weapon damage range. The server's <c>roe_MinWC</c>.</summary>
    public static double MinWeaponDamage(ICombatant attacker) => WeaponDamage(attacker.Parameters, Stat.WCmin);

    /// <summary>Top of the weapon damage range. The server's <c>roe_MaxWC</c>.</summary>
    public static double MaxWeaponDamage(ICombatant attacker) => WeaponDamage(attacker.Parameters, Stat.WCmax);

    /// <summary>The weapon-damage accessors, which share a shape but differ only in which slot they read.</summary>
    private static double WeaponDamage(ParameterContainer s, Stat bound)
    {
        // The Str chain and the five own terms accumulate in ONE running total. The six additions are a
        // single fld plus five fadds in the binary and that association is observable: computing the own
        // half separately and adding the Str chain last gives 7732.317384936001 where the server gives
        // 7732.317384936. One ULP -- which then feeds attack power and an integer damage, where it became
        // a 256-point difference.
        //
        // Upgrade.plus and AbnormalState.plus are read at WCmax even when computing WCmin, and that is
        // not a quirk of the code -- it is what the stat MEANS. An enhanced weapon reads in the client as
        //
        //     1000~2000 (+3000)
        //
        // one flat bonus that shifts BOTH ends of the range, not a separate bump to each end. So the
        // enhancement and buff layers store that single figure once, in the WCmax slot, and both bounds
        // read it from there. Only the weapon's own base range is per-bound.
        var value = GoverningChain(s, Stat.Str);
        value += s.Plus(StatModifier.Upgrade)[Stat.WCmax];
        value += s.Plus(StatModifier.AbnormalState)[Stat.WCmax];
        value += s.Base[bound];
        value += ScaledWeaponItemBonus(s, bound);
        value += s.Plus(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery];

        // Three trailing rates, multiplying and dividing at each step rather than taking the product
        // first. Scored 397/400 exact against the server where the alternative groupings scored 221 and 208.
        value = ApplyRate(value, s.Rate(StatModifier.AbnormalState)[Stat.WCmax]);
        value = ApplyRate(value, s.Rate(StatModifier.ItemPowerRate)[bound]);
        value = ApplyRate(value, s.Rate(StatModifier.PassiveSkill)[bound]);
        return FloorAtOne(value);
    }

    /// <summary>The weapon's own damage bonus is NOT added raw: it is scaled by the weapon title's rate for
    /// the slot and then by physical weapon mastery.</summary>
    private static double ScaledWeaponItemBonus(ParameterContainer s, Stat bound)
    {
        //     fld WeaponTitle.rate.WCmin; fmul Item.plus.WCmin; fdiv 1000; fmul PassiveSkill.rate.Mastery
        // Written out rather than via ApplyRate because the roles are reversed here -- the weapon-title
        // RATE is the multiplicand and the item PLUS is the multiplier -- and expressing that as
        // "apply a rate" would read as the opposite of what it does.
        var scaled = (double)s.Rate(StatModifier.WeaponTitle)[bound] * s.Plus(StatModifier.Item)[bound] / 1000.0;
        return ApplyRate(scaled, s.Rate(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery]);
    }

    /// <summary>Bottom of the magic attack range. The server's <c>roe_MinMA</c>.</summary>
    public static double MinMagicAttack(ICombatant attacker) => MagicAttack(attacker.Parameters, Stat.MAmin);

    /// <summary>Top of the magic attack range. The server's <c>roe_MaxMA</c>.</summary>
    public static double MaxMagicAttack(ICombatant attacker) => MagicAttack(attacker.Parameters, Stat.MAmax);

    /// <summary>The magic attack accessors.
    ///
    /// <para>⚠️ These are NOT the mirror image of <see cref="WeaponDamage"/>, which is the natural
    /// assumption and is wrong. Probing which fields each accessor actually reads shows two structural
    /// differences: the magic pair takes a FULL <see cref="Chain"/> over its own bound — including
    /// <c>WeaponTitle.plus</c> and <c>LastTune.plus</c>, which the weapon pair does not read at all — and
    /// it reads <c>Upgrade.plus</c> / <c>AbnormalState.plus</c> at its OWN bound rather than at the max
    /// slot the way the weapon pair does. The weapon pair's flat-bonus-at-WCmax behaviour is specific to
    /// enhancement; magic does not share it.</para>
    ///
    /// <para>What does carry over: the item bonus is scaled rather than added raw, and the scale is taken
    /// from the MAX slot's weapon-title rate even when computing the minimum.</para></summary>
    private static double MagicAttack(ParameterContainer s, Stat bound)
    {
        // ONE divide by 1e6, not two by 1000 -- the physical equivalent divides after each multiply and
        // magic does not. Mathematically identical, one ULP apart in doubles, and the ULP shows: scored
        // 300/300 exact against the server where each two-divide grouping scored 295-296.
        var scaledItem = (double)s.Rate(StatModifier.WeaponTitle)[Stat.MAmax]
                         * s.Plus(StatModifier.Item)[bound]
                         * s.Rate(StatModifier.PassiveSkill)[Stat.MagicalWeaponMastery] / 1_000_000.0;

        var value = GoverningChain(s, Stat.Int);
        value += Chain(s, bound);
        value += scaledItem;
        value += s.Plus(StatModifier.PassiveSkill)[Stat.MagicalWeaponMastery];

        // ONE trailing rate, where the weapon pair has three. Isolated by flooring the governing chain to
        // 1 with an empty own half and setting each rate alone: only ItemPowerRate.rate on the bound moves
        // the result, while the weapon pair also answers to PassiveSkill.rate[bound] and
        // AbnormalState.rate[WCmax].
        return FloorAtOne(ApplyRate(value, s.Rate(StatModifier.ItemPowerRate)[bound]));
    }

    // ---- attack / defend power -------------------------------------------------------------------------

    /// <summary>Physical attack power: a point in the weapon's damage range, scaled by weapon mastery.
    ///
    /// <para><paramref name="rollPermille"/> selects the point: 0 = <see cref="MinWeaponDamage"/>,
    /// 1000 = <see cref="MaxWeaponDamage"/>. The server draws an INTEGER over the truncated range, so the
    /// roll is quantised, not continuous.</para>
    ///
    /// <para><b>The mastery multiplier is not floored.</b> With a physical-weapon-mastery rate of 0 this
    /// returns exactly 0 even though both bounds are floored at 1 — no mastery means no damage, and the
    /// floors on the bounds do not rescue it.</para></summary>
    public static double AttackPower(ICombatant attacker, int rollPermille,
                                    EngagementRule rule = EngagementRule.NormalPhysical,
                                    int hpMissingPermille = 0)
    {
        var s = attacker.Parameters;
        var magical = rule.School() == DamageSchool.Magical;
        var low = magical ? MinMagicAttack(attacker) : MinWeaponDamage(attacker);
        var high = magical ? MaxMagicAttack(attacker) : MaxWeaponDamage(attacker);

        // THE HP-DOWN PASSIVE. `roe_AttackPower+0xCF` and `+0xEA` add a ChangeByConditionParam bonus to
        // each bound, keyed on how much HP the attacker is missing -- all four rules do it, physical
        // against the WC pair and magical against the MA pair. Added to the BOUNDS, before the roll, so it
        // shifts the whole range rather than the result.
        //
        // Zero unless the character actually has the passive: an unconfigured block returns 0 for every
        // key. See ChangeByConditionParam.
        low += (magical ? s.PassiveHpDownMaMin : s.PassiveHpDownWcMin).Value(hpMissingPermille);
        high += (magical ? s.PassiveHpDownMaMax : s.PassiveHpDownWcMax).Value(hpMissingPermille);

        // The range goes through _ftol because the server's RNG takes an int.
        var range = (long)Ftol32(high - low);
        var draw = range * rollPermille / 1000;

        var value = low + draw;
        if (!rule.AppliesWeaponMastery())
            return value;
        return ApplyRate(value, s.Rate(StatModifier.PassiveSkill)[
            magical ? Stat.MagicalWeaponMastery : Stat.PhisycalWeaponMastery]);
    }

    /// <summary>Defend power. <c>NormalPY::roe_DefendPower</c> IS <c>roe_AC</c>; the magical rules use
    /// <c>roe_MR</c> instead.</summary>
    /// <param name="hpMissingPermille">How much HP the DEFENDER is missing, in permille — the same
    /// condition key the attack side uses, for the defensive half of the HP-down passive.</param>
    public static double DefendPower(ICombatant defender, EngagementRule rule = EngagementRule.NormalPhysical,
                                     int hpMissingPermille = 0)
    {
        var magical = rule.School() == DamageSchool.Magical;
        var value = magical ? MagicResistance(defender) : ArmourClass(defender);

        // `roe_DefendPower+0xCA` on NormalPY, NormalMA and MagicalSkill. ⚠️ It is the DEFEND POWER that
        // reads this, not `roe_AC` / `roe_MR` — those two have no cbcp call at all, so a caller reading
        // armour directly gets a number that is correct as armour and incomplete as defence.
        var s = defender.Parameters;
        return value + (magical ? s.PassiveHpDownMr : s.PassiveHpDownAc).Value(hpMissingPermille);
    }

    // ---- the damage pipeline ---------------------------------------------------------------------------

    /// <summary>The core damage step, <c>roe_Damage</c>, byte for byte:
    /// <code>
    /// v = (nBMPDamageRate * attack) / 1000.0;
    /// if (v &lt;= 0) v = 1.0;
    /// return ((attackerLevel + 1) * v) / defend;
    /// </code>
    /// The only literals in the real function are 1000.0 and 1.0 — there is no magic constant anywhere in
    /// it, and any fitted one (an earlier attempt produced <c>K / (DEF - 141)</c>) is an artefact of
    /// fitting rather than reading.
    ///
    /// <para>Note this floor really is <c>&lt;= 0</c>, unlike the accessors' <c>&lt; 1</c>.</para></summary>
    public static double CoreDamage(double attackPower, double defendPower, int attackerLevel,
                                    int baseDamageRatePermille = 1000)
    {
        var v = baseDamageRatePermille * attackPower / 1000.0;
        if (v <= 0) v = 1.0;
        return (attackerLevel + 1) * v / defendPower;
    }

    /// <summary>Resolve one physical swing to an integer damage figure.
    ///
    /// <para>Both random draws — where in the weapon range the swing lands, and whether it crits — are
    /// taken from <paramref name="rng"/> unless <paramref name="modifiers"/> pins them. Pass a seeded
    /// <see cref="System.Random"/> for a reproducible simulation, or pin both for a bounding question.</para></summary>
    public static AttackOutcome Resolve(ICombatant attacker, ICombatant defender,
                                        AttackModifiers? modifiers = null, System.Random? rng = null,
                                        EngagementRule rule = EngagementRule.NormalPhysical)
    {
        var mods = modifiers ?? AttackModifiers.Default;
        rng ??= System.Random.Shared;

        var rollPermille = mods.RollPermille ?? rng.Next(0, 1001);
        var isCritical = rule.AlwaysCriticals()
                         || (mods.ForceCritical ?? rng.Next(0, 1000) < mods.CriticalChancePermille);

        var attackPower = AttackPower(attacker, rollPermille, rule, mods.AttackerHpMissingPermille);
        var defendPower = DefendPower(defender, rule, mods.DefenderHpMissingPermille);

        var damage = CoreDamage(attackPower, defendPower, attacker.Level, mods.BaseDamageRatePermille);

        // THE PER-RULE `roe_Damage` OVERRIDE. Slot 4 of the rule vtable is not the base function for five
        // of the eight rules: NormalPY, PhisycalSkill and AlwaysCritical add
        //     + attacker.FreeStatStr - defender.FreeStatCon
        // and NormalMA / MagicalSkill the Int/Men pair, on top of the base result. Verified by running the
        // real function under emulation over six input sets -- see tools/oracle_free_stat_damage.py.
        //
        // A rule with no free-stat school (CureSkill, AlwaysHit, HealAttack) keeps the base untouched.
        if (rule.FreeStatSchool() is not null)
            damage += mods.AttackerFreeStat - mods.DefenderFreeStat;

        // A CRITICAL IS NOT SIMPLY DOUBLE. `roe_CalcDamage+0x4C2` reads the ATTACKER's
        // `PassiveCriDamageRatePlus` (container +0x0CDC, an unsigned short) and computes
        //     damage = 2*damage + damage * plus / 1000
        // The port doubled and stopped for a long time, which is invisible at the default 0 and wrong by
        // exactly that passive's permille for anyone who has it.
        if (isCritical)
        {
            var critPlus = attacker.Parameters.PassiveCriDamageRatePlus;
            damage = damage * critPlus / 1000.0 + (damage + damage);
        }

        // ANGLE FIRST, THEN DAMAGE RATE -- the order the binary uses at roe_CalcDamage+0x572..+0x585:
        //     fild angleRate; fmul damage; fdiv 1000; fild damagerate; fmulp; fdivp
        // Both are doubles applied BEFORE the integer conversion, so this ordering is the only thing that
        // separates the port from the server here, and it is at most an ULP -- but ULPs on the wrong side
        // of the conversion have already cost a whole point of armour once in this file.
        damage = ApplyRate(damage, mods.AngleRatePermille);
        damage = ApplyRate(damage, mods.DamageRatePermille);

        // The LEVEL GAP is deliberately NOT applied here: on the server it runs AFTER the integer
        // conversion, as an integer function. See below.

        // The final conversion is the same wrapping _ftol as the accessors use, NOT a saturating cast: a
        // damage of 8.5e12 comes back as its low 32 bits, which is negative and so floors to 1. A plain
        // (int)Math.Floor gave 2147483647 -- "maximum possible hit" where the server deals the minimum.
        var final = (int)Ftol32(damage);
        final = ApplyJobChangeDamageUp(final, mods.JobChangeDamageUpPermille, rng);
        final = ApplyLevelGap(final, mods.LevelGapRatePermille);
        return new AttackOutcome(final > 0 ? final : 1, isCritical, rollPermille, attackPower, defendPower);
    }

    /// <summary>`ShinePlayer::so_ply_JobChangeDamageUp` (0x00560E80) — the job-change catch-up multiplier,
    /// run at `roe_CalcDamage+0x5B2` on the ATTACKER, one call before the level gap.
    ///
    /// <para><paramref name="ratePermille"/> is <c>null</c> when the hook does not run: the base
    /// `ShineObject` implementation is <c>return dmg</c>, so a mob attacker never reaches it, and the
    /// player override returns early unless the defender is a MONSTER. That is why "does not apply" is a
    /// null and not a rate of 1000 — 0 is a real rate here and really does zero the damage.</para>
    ///
    /// <para>The arithmetic is the binary's: the damage is sign-extended (<c>cdq</c>), multiplied by the
    /// rate with <c>__allmul</c>, and divided by 1000 with <c>__aulldiv</c> — an <b>unsigned</b> 64-bit
    /// divide. A plain <c>damage * rate / 1000</c> in int32 overflows above about 1.7M damage, which the
    /// server does not.</para>
    ///
    /// <para>The rate has a 0-or-1 random added to it first, read from `rndbox` slot 2 — a shuffled pool
    /// whose only values are 0 and 1, because `RandomBox`'s constructor fills slot <c>b</c> with
    /// <c>floor(i * b / 16384)</c>. Worth 0.08%, and modelled because leaving it out would make this the
    /// one step of the pipeline that is deterministic when the server's is not.</para></summary>
    private static int ApplyJobChangeDamageUp(int damage, int? ratePermille, System.Random rng)
    {
        if (ratePermille is not { } rate) return damage;
        var product = unchecked((long)damage * (rate + rng.Next(0, 2)));
        return unchecked((int)((ulong)product / 1000UL));
    }

    /// <summary>`roe_LevelGapDamageRevision` — the level-difference multiplier, applied to the INTEGER
    /// damage after the conversion, not to the double before it.
    ///
    /// <para>The server does <c>imul ecx, damage</c> (a 32-bit multiply, so it WRAPS) followed by a signed
    /// divide by 1000 truncating toward zero — the <c>0x10624DD3</c> / <c>sar 6</c> magic sequence at
    /// <c>roe_LevelGapDamageRevision+0x62</c>. C#'s <c>int * int</c> wraps and <c>/</c> truncates toward
    /// zero, so this is the same operation, not an approximation of it.</para>
    ///
    /// <para>The server only reaches this for specific combatant-type pairs (player attacking a monster
    /// selects <c>LevelGap_Player_to_Monster</c>); other pairings leave the damage untouched, which is a
    /// rate of 1000 here.</para></summary>
    private static int ApplyLevelGap(int damage, int ratePermille) => unchecked(ratePermille * damage) / 1000;

    /// <summary>Convenience for the common question: how much damage, ignoring the breakdown.</summary>
    public static int ResolveDamage(ICombatant attacker, ICombatant defender,
                                    AttackModifiers? modifiers = null, System.Random? rng = null,
                                    EngagementRule rule = EngagementRule.NormalPhysical)
        => Resolve(attacker, defender, modifiers, rng, rule).Damage;

    /// <summary>Two degrees per direction unit. The server never works in degrees: <c>ddt_Initialize</c>
    /// builds its direction table with <c>atan(...) * 90 / PI</c>, and degrees would be <c>* 180 / PI</c>,
    /// so one unit is half a degree's worth — a full turn is <see cref="DirectionUnitsPerTurn"/>.</summary>
    public const int DegreesPerDirectionUnit = 2;

    /// <summary>A full turn in direction units (360 degrees / 2).</summary>
    public const int DirectionUnitsPerTurn = 180;

    /// <summary>Fold a facing difference, <b>in direction units</b>, into the 0..90 index that
    /// <c>DamageByAngle</c>'s 91-entry table expects.
    ///
    /// <para>The server computes the argument as <c>defenderFacing - directionFromDefenderToAttacker</c>,
    /// both direction bytes, then indexes the table with it. So the index is a measure of how far round
    /// the defender the attacker is:</para>
    ///
    /// <list type="table">
    ///   <item><term>index 0</term><description>0 degrees — attacked from the FRONT</description></item>
    ///   <item><term>index 45</term><description>90 degrees — from the SIDE</description></item>
    ///   <item><term>index 90</term><description>180 degrees — from BEHIND, the largest multiplier</description></item>
    /// </list>
    ///
    /// <para>⚠️ This takes DIRECTION UNITS, not degrees. Passing degrees folds 180 to index 0 and makes a
    /// backstab read as a frontal hit — which is the inverse of the truth, and is what this method did
    /// while its parameter was misnamed <c>angleDegrees</c>. Use
    /// <see cref="AngleDamageIndexFromDegrees"/> if you are holding degrees.</para></summary>
    public static int AngleDamageIndex(int directionUnitDelta)
    {
        var folded = Math.Abs(directionUnitDelta) % DirectionUnitsPerTurn;
        return Math.Abs(((folded + 90) % DirectionUnitsPerTurn) - 90);
    }

    /// <summary>As <see cref="AngleDamageIndex"/>, for a caller holding real degrees (0 = front,
    /// 180 = behind).</summary>
    public static int AngleDamageIndexFromDegrees(int degrees)
        => AngleDamageIndex(degrees / DegreesPerDirectionUnit);
}
