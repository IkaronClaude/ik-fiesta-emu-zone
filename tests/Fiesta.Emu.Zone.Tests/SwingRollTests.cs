using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Parameter;
using Fiesta.Emu.Zone.Random;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The swing ROLLS — `roe_ShieldBlock`, `roe_HitRate`, `roe_CriticalRate` and the order
/// `smo_SwingDamage` puts them in.
///
/// <para>These pin what was read out of the binary. The one measurement they can be checked against
/// without a fixture is the flag distribution of `FighterDamageLvl60.pcapng`'s 750
/// `NC_BAT_SWING_DAMAGE_CMD` frames, counted on the flag byte's bitfield
/// (bit0 iscritical, bit2 ismissed, bit3 isshieldblock):</para>
///
/// <code>
/// 0x00  658   clean hit
/// 0x04   64   missed
/// 0x0C   15   missed AND shield-blocked
/// 0x01   13   critical
/// </code>
///
/// <para><b>Not one frame is blocked without also being missed</b>, which is exactly what the code says
/// must happen: `roe_HitRate` sets `isshieldblock` and then returns 0.0, and the caller rolls against that
/// zero like any other rate. A block that did not miss would need the draw to come up 0.</para></summary>
public class SwingRollTests
{
    /// <summary>A combatant that can answer the object-side questions the roll phase asks.</summary>
    private sealed record Fighter : ICombatant
    {
        public int Level { get; init; } = 60;
        public ParameterContainer Parameters { get; } = new();
        public int FreeStatDexTHRate { get; init; }
        public int FreeStatDexTBRate { get; init; }
        public int FreeStatMenCriRate { get; init; }
        public bool IsInMoving { get; init; }
        public int AttackRange { get; init; } = DamageCalculator.MeleeAttackRange;
        public bool HasItemActionObserves { get; init; }
    }

    private static Fighter WithDex(int dex, params (Stat Stat, int Value)[] extra)
    {
        var f = new Fighter();
        f.Parameters.Base[Stat.Dex] = dex;
        foreach (var (stat, value) in extra)
            f.Parameters.Base[stat] = value;
        return f;
    }

    private static cWell512Random Rng() => new([.. Enumerable.Range(1, 16).Select(i => (uint)i)]);

    // ---- roe_ShieldBlock ------------------------------------------------------------------------------

    /// <summary>Gear contributes a PLUS and only the abnormal-state layer a RATE, so a character with no
    /// shield can never block however many buffs they stack.</summary>
    [Fact]
    public void ShieldBlockIsZeroWithoutAShield()
    {
        var d = new Fighter();
        d.Parameters.Rate(StatModifier.AbnormalState)[Stat.ShieldAC] = 4000;

        DamageCalculator.ShieldBlockRate(d).ShouldBe(0.0);
    }

    /// <summary>`(Upgrade.Plus + Item.Plus) * AbnormalState.Rate / 1000`, all at slot 26.</summary>
    [Fact]
    public void ShieldBlockSumsGearThenScalesByTheAbnormalStateRate()
    {
        var d = new Fighter();
        d.Parameters.Plus(StatModifier.Item)[Stat.ShieldAC] = 70;      // Kaineneceshield's ShieldAC
        d.Parameters.Plus(StatModifier.Upgrade)[Stat.ShieldAC] = 30;

        DamageCalculator.ShieldBlockRate(d).ShouldBe(100.0);

        // SAA_SHIELDACRATE writes exactly this slot -- +0x9F8 is where both the abstate handler and
        // roe_ShieldBlock land, which is what cross-checks the two reads.
        d.Parameters.Rate(StatModifier.AbnormalState)[Stat.ShieldAC] = 1600;
        DamageCalculator.ShieldBlockRate(d).ShouldBe(160.0);
    }

    // ---- roe_HitRate ---------------------------------------------------------------------------------

    /// <summary>The constant at 0x006D03E0 is <b>850.0</b>, not 1000 — equal Aim and Evasion is an 85%
    /// chance, and hit rate only reaches certainty when Aim is about 18% above Evasion.</summary>
    [Fact]
    public void EqualAimAndEvasionIsEightHundredAndFiftyPermille()
    {
        DamageCalculator.HitRate(WithDex(100), WithDex(100)).ShouldBe(850.0);
        DamageCalculator.HitRate(WithDex(200), WithDex(100)).ShouldBe(1700.0);
        DamageCalculator.HitRate(WithDex(118), WithDex(100)).ShouldBe(1003.0);
    }

    /// <summary>`roe_FreeStatHitRate` truncates before the free-stat records are involved, and the records
    /// are what the wire's Aim / Evasion already include.</summary>
    [Fact]
    public void TheFreeStatDexRecordAddsToBothSides()
    {
        var attacker = WithDex(100) with { FreeStatDexTHRate = 100 };
        var defender = WithDex(100) with { FreeStatDexTBRate = 100 };

        // 200 * 850 / 100 = 1700, then 200 * 850 / 200 = 850.
        DamageCalculator.HitRate(attacker, WithDex(100)).ShouldBe(1700.0);
        DamageCalculator.HitRate(attacker, defender).ShouldBe(850.0);
    }

    /// <summary>`MissPercentFix` (+0x0CD0) replaces the entire computation, and above 1000 it returns 0
    /// rather than going negative — WITHOUT setting the block flag, unlike a real block.</summary>
    [Theory]
    [InlineData(0, 850.0)]
    [InlineData(300, 700.0)]
    [InlineData(1000, 0.0)]
    [InlineData(1001, 0.0)]
    [InlineData(60000, 0.0)]
    public void MissPercentFixShortCircuitsTheWholeHitRate(int fix, double expected)
    {
        var defender = WithDex(100);
        defender.Parameters.MissPercentFix = fix;

        DamageCalculator.HitRate(WithDex(100), defender).ShouldBe(expected);
    }

    /// <summary>`RangeEvasion` is subtracted only when the ATTACK's range is strictly over 300 — an
    /// Archer's 450 pays it, a sword's 100 does not.</summary>
    [Fact]
    public void RangeEvasionAppliesOnlyBeyondThreeHundred()
    {
        var defender = WithDex(100);
        defender.Parameters.RangeEvasion = 40;

        DamageCalculator.HitRate(WithDex(100), defender).ShouldBe(850.0);
        DamageCalculator.HitRate(WithDex(100) with { AttackRange = 300 }, defender).ShouldBe(850.0);
        DamageCalculator.HitRate(WithDex(100) with { AttackRange = 450 }, defender).ShouldBe(810.0);
    }

    /// <summary>`PassiveMovingTBPlus` (+0x0D88) is the only movement-dependent term in the engine, and it
    /// is read by INDEX (`cbcp_GetValue_Index(0)`), not by the HP-missing key the other blocks use.</summary>
    [Fact]
    public void MovingAddsThePassiveMovingBlockBonusAndOnlyWhenMoving()
    {
        var still = WithDex(100);
        still.Parameters.PassiveMovingTbPlus = new ChangeByConditionParam(1, [70]);
        var moving = still with { IsInMoving = true };

        DamageCalculator.HitRate(WithDex(100), still).ShouldBe(850.0);
        DamageCalculator.HitRate(WithDex(100), moving).ShouldBe(500.0);   // 100 * 850 / 170
    }

    // ---- roe_CriticalRate ----------------------------------------------------------------------------

    /// <summary>A character with no crit gear crits once in a thousand — the floor — because the three
    /// rate slots `roe_CriticalRate` sums start at ZERO in a live container.
    ///
    /// <para>This was the session's longest-running open question and it is now settled by measurement,
    /// not argument. The rate ERASER really does hold 1000 at <see cref="Stat.CriDamRate"/> — read out of
    /// zone02's live memory at 0x0DA3FA78, all 51 slots, and `so_RecalcEquipParam` re-seeds from that same
    /// eraser. But a live container read out of zone00 holds <b>0</b> at that slot in `Item.Rate`,
    /// `WeaponTitle.Rate` and `AbnormalState.Rate` — and in no other rate cluster. Something clears them
    /// after the erase, and those three are exactly the ones the crit formula ADDS.</para>
    ///
    /// <para>Without this a bare container summed to 3000 and every swing critted, against a measured
    /// 55.6 permille on the wire.</para></summary>
    [Fact]
    public void ACharacterWithNoCritGearCritsAtTheFloor()
    {
        DamageCalculator.CriticalRate(new Fighter(), new Fighter()).ShouldBe(1.0);
    }

    /// <summary>Which slots are zeroed, per cluster, exactly as the live container holds them. Every other
    /// rate cluster keeps the eraser's 1000 at those slots, so this is not "zero the crit slots
    /// everywhere".</summary>
    [Fact]
    public void OnlyTheClustersTheCritFormulaReadsAreZeroed()
    {
        var p = new ParameterContainer();

        p.Rate(StatModifier.Item)[Stat.CriDamRate].ShouldBe(0);
        p.Rate(StatModifier.Item)[Stat.MagCriDamRate].ShouldBe(0);
        p.Rate(StatModifier.WeaponTitle)[Stat.CriDamRate].ShouldBe(0);
        p.Rate(StatModifier.WeaponTitle)[Stat.MagCriDamRate].ShouldBe(0);
        p.Rate(StatModifier.AbnormalState)[Stat.CriDamRate].ShouldBe(0);

        // AbnormalState keeps MagCriDamRate, and the other four clusters keep both.
        p.Rate(StatModifier.AbnormalState)[Stat.MagCriDamRate].ShouldBe(1000);
        foreach (var src in new[] { StatModifier.ItemPowerRate, StatModifier.Upgrade,
                                    StatModifier.PassiveSkill, StatModifier.LastTune })
        {
            p.Rate(src)[Stat.CriDamRate].ShouldBe(1000);
            p.Rate(src)[Stat.MagCriDamRate].ShouldBe(1000);
        }
    }

    /// <summary>Whatever sits in those three rate slots IS the crit chance, in PERMILLE — the result is
    /// compared against <c>well512_GetRandom(1000)</c>, so 50 is 5% and the floor of 1 is a literal
    /// 1-in-1000.
    ///
    /// <para>⚠️ <b>What PUTS a number there is an open question, and this test no longer claims it is a
    /// weapon's `ItemInfo.CriRate`.</b> An earlier version asserted exactly that, because the capture's
    /// weapons (Splitter 30, Kaineneceflight 70, Kainenecefury 90) predicted close to the measured 55.6
    /// permille. The numbers do fit — and so does a model with no weapon term at all: over 234 landed
    /// swings the MEN-only prediction is 11.7 criticals and the weapon-only prediction 11.9, against 13
    /// observed. 234 swings cannot separate them, and a scan for direct writes to those displacements
    /// finds only `c_clear` — no equipment code at all.</para>
    ///
    /// <para>Operator's play figures, for whoever settles it: weapons carry roughly 3–9% (30–90 permille,
    /// which is exactly the range `ItemInfo.CriRate` holds), 25 MEN adds 5%, and a normal player without
    /// high-level jewellery or premium items is <b>5–10% in TOTAL</b> — which a straight sum of a weapon
    /// and 25 MEN would already exceed. So the two terms are probably not both feeding this
    /// function.</para></summary>
    [Theory]
    [InlineData(30)]
    [InlineData(70)]
    [InlineData(90)]
    public void WhateverIsInTheCritSlotsIsTheChanceInPermille(int permille)
    {
        var attacker = new Fighter();
        attacker.Parameters.Rate(StatModifier.Item)[Stat.CriDamRate] = permille;

        DamageCalculator.CriticalRate(attacker, new Fighter()).ShouldBe(permille);
    }

    /// <summary>The one crit term pinned end to end: `roe_FreeStatCriRate` adds `FreeStatMen.CriRate`, and
    /// the live table gives 25 points exactly 50 permille — the operator's 5%, to the point.
    ///
    /// <para>On its own that predicts 11.7 criticals over the capture's 234 landed swings against 13
    /// observed, so MEN alone already accounts for what was measured.</para></summary>
    [Fact]
    public void TheMenTermIsPinnedEndToEnd()
    {
        FreeStatTables.MenCriRate(25).ShouldBe(50);

        var attacker = new Fighter { FreeStatMenCriRate = FreeStatTables.MenCriRate(25) };
        DamageCalculator.CriticalRate(attacker, new Fighter()).ShouldBe(50);
    }

    /// <summary>Three attacker layers add at <see cref="Stat.CriDamRate"/>, two defender layers subtract at
    /// <see cref="Stat.CriticalTB"/>, and `roe_FreeStatCriRate` adds the attacker's Men record last.</summary>
    [Fact]
    public void CriticalRateSumsThreeAttackerLayersAgainstTwoDefenderOnes()
    {
        var attacker = new Fighter { FreeStatMenCriRate = 19 };
        attacker.Parameters.Rate(StatModifier.Item)[Stat.CriDamRate] = 40;
        attacker.Parameters.Rate(StatModifier.WeaponTitle)[Stat.CriDamRate] = 10;
        attacker.Parameters.Rate(StatModifier.AbnormalState)[Stat.CriDamRate] = 5;

        var defender = new Fighter();
        defender.Parameters.Rate(StatModifier.AbnormalState)[Stat.CriticalTB] = 12;
        defender.Parameters.Rate(StatModifier.Item)[Stat.CriticalTB] = 3;

        DamageCalculator.CriticalRate(attacker, defender).ShouldBe(40 + 10 + 5 - 12 - 3 + 19);
    }

    /// <summary>A critical is <c>2*damage + damage * PassiveCriDamageRatePlus / 1000</c>, not simply
    /// double. At the default 0 it IS double, which is why the missing term went unnoticed.</summary>
    [Fact]
    public void CriticalDamageAddsThePassiveCriDamageRate()
    {
        var attacker = Combatant.FromBaseStats(60,
        [
            new(Stat.Str, 300), new(Stat.WCmin, 400), new(Stat.WCmax, 400),
        ]);
        var defender = Combatant.FromBaseStats(60, [new(Stat.Con, 150), new(Stat.AC, 120)]);
        var mods = new AttackModifiers { RollPermille = 500 };

        var plain = DamageCalculator.ResolveDamage(attacker, defender, mods with { ForceCritical = false });
        var doubled = DamageCalculator.ResolveDamage(attacker, defender, mods with { ForceCritical = true });
        doubled.ShouldBe(plain * 2);

        attacker.Parameters.PassiveCriDamageRatePlus = 500;
        var boosted = DamageCalculator.ResolveDamage(attacker, defender, mods with { ForceCritical = true });
        (boosted - (int)(plain * 2.5)).ShouldBeInRange(-1, 1);
    }

    // ---- SwingDamage -----------------------------------------------------------------------------

    /// <summary>The coupling the capture shows in every one of its 15 blocked frames: a block forces the
    /// hit rate to 0, so it misses too. Only a draw of exactly 0 escapes, which is why this asserts
    /// "almost always" rather than "always" — inventing an absolute here would be wrong about the
    /// server.</summary>
    [Fact]
    public void AShieldBlockAlsoMisses()
    {
        var attacker = WithDex(100);
        var defender = WithDex(100);
        defender.Parameters.Plus(StatModifier.Item)[Stat.ShieldAC] = 5000;   // blocks every draw
        var rng = Rng();

        var outcomes = Enumerable.Range(0, 400)
            .Select(_ => ShineMobileObject.SwingDamage(attacker, defender, rng))
            .ToList();

        outcomes.ShouldAllBe(o => o.IsShieldBlock);
        outcomes.Count(o => o.IsMiss).ShouldBeGreaterThanOrEqualTo(398);
        outcomes.Where(o => o.IsMiss).ShouldAllBe(o => o.Damage == 0);
    }

    /// <summary>No shield, Aim far above Evasion, and the neutral 1000 from
    /// <see cref="ItemActionRates.None"/> — every swing lands.
    ///
    /// <para>The second hit roll is the trap: `roe_HitRateByGlobalAction` returns <c>1000 - sum</c>, so
    /// the neutral value is 1000 and a port that defaulted it to 0 would miss every swing here.</para></summary>
    [Fact]
    public void AnUnblockableSwingWithHighAimAlwaysLands()
    {
        var attacker = WithDex(500, (Stat.WCmin, 400), (Stat.WCmax, 400), (Stat.Str, 300));
        var defender = WithDex(100, (Stat.Con, 150), (Stat.AC, 120));
        var rng = Rng();

        for (var i = 0; i < 200; i++)
        {
            var o = ShineMobileObject.SwingDamage(attacker, defender, rng);
            o.IsMiss.ShouldBeFalse();
            o.IsShieldBlock.ShouldBeFalse();
            o.Damage.ShouldBeGreaterThan(0);
        }
    }

    /// <summary>`RulesOfEngagementAlwaysCritical` overrides the rate with a flat 1000, and a critical then
    /// rolls once more for the stun at a flat 200 permille (`roe_CriticalStunRate`, 0x00500170).</summary>
    [Fact]
    public void AlwaysCriticalCritsAndStunsAboutOneTimeInFive()
    {
        var attacker = WithDex(500, (Stat.WCmin, 400), (Stat.WCmax, 400), (Stat.Str, 300));
        var defender = WithDex(100, (Stat.Con, 150), (Stat.AC, 120));
        var rng = Rng();

        var outcomes = Enumerable.Range(0, 2000)
            .Select(_ => ShineMobileObject.SwingDamage(attacker, defender, rng,
                                                           rule: EngagementRule.AlwaysCritical))
            .ToList();

        outcomes.ShouldAllBe(o => o.IsCritical);
        var stuns = outcomes.Count(o => o.IsStun);
        stuns.ShouldBeInRange(340, 460);          // 20% of 2000, with room for the generator
    }

    /// <summary>The hit comparison is <c>draw &gt; rate</c> misses, so a rate of 500 hits on draws 0..500 —
    /// 501 of the 1000 outcomes, not 500. The block and critical comparisons are strict the other way;
    /// the asymmetry is in the binary and is worth one test.</summary>
    [Fact]
    public void TheHitComparisonIsNonStrictInTheHitsFavour()
    {
        var attacker = WithDex(100, (Stat.WCmin, 400), (Stat.WCmax, 400), (Stat.Str, 300));
        var defender = WithDex(170, (Stat.Con, 150), (Stat.AC, 120));   // 100*850/170 = 500
        DamageCalculator.HitRate(attacker, defender).ShouldBe(500.0);

        var rng = Rng();
        var landed = Enumerable.Range(0, 20000)
            .Count(_ => !ShineMobileObject.SwingDamage(attacker, defender, rng).IsMiss);

        // 501/1000 expected; ±1.5% of 20000 covers the sampling noise.
        landed.ShouldBeInRange(9720, 10320);
    }
}
