using Fiesta.Emu.Zone.Mob;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>What a combat script is actually judged on, sampled once per tick.
///
/// <para>Kills alone cannot rank two scripts: a script that kills 40 while dying twice and standing idle
/// half the run is not better than one that kills 35 cleanly. These are the axes the operator named -
/// no ability dead time, fewest deaths, best resource management, and kites that are short bursts rather
/// than tours into the next pack.</para>
///
/// <para>Every counter here is a COUNT OF TICKS, converted to a percentage against the ticks it could
/// apply to. A rate over "ticks where the question was even askable" is comparable between a run that
/// died at 30s and one that lasted 400s; a raw count is not.</para></summary>
public sealed class CombatMetrics
{
    /// <summary>Ticks sampled while the character was alive.</summary>
    public int Ticks { get; private set; }

    /// <summary>Ticks with a living mob inside the character's own attack range.</summary>
    public int TicksWithTargetInReach { get; private set; }

    /// <summary>THE DEAD-TIME NUMERATOR: ticks where a damaging skill was ready, affordable AND in range,
    /// and the character was neither casting nor had just cast.
    ///
    /// <para>This is the operator's first goal made measurable. A script that stands next to a mob with
    /// four skills off cooldown is wasting the only resource that does not regenerate - time.</para>
    ///
    /// <para>Counted only while ENGAGED - see <see cref="TicksEngaged"/>. It used to be counted on every
    /// tick, and that made the number uncomparable ACROSS CLASSES rather than merely generous: a Ranger
    /// reaching 450u catches a mob inside skill range on most travel ticks, a Warrior reaching 100u almost
    /// never does, so the Ranger scored 32.1% against the Warrior's 5.7% with 13.8 points of that being
    /// nothing but walking somewhere past a mob it had correctly decided not to pull.</para></summary>
    public int TicksWithWastedSkill { get; private set; }

    /// <summary>Ticks where the script was committed to a fight: locked on to something, or something was
    /// on us. The denominator dead time is charged against.</summary>
    public int TicksEngaged { get; private set; }

    /// <summary>Ticks where NOTHING offensive was possible: no skill ready and affordable, and nothing in
    /// weapon reach. Not dead time - there was nothing to do.</summary>
    public int TicksWithNothingPossible { get; private set; }

    // ---- WHY a tick was wasted --------------------------------------------------------------------
    //
    // A dead-time percentage says a script is idle; it does not say what it was doing instead, and the
    // fix is different for each. These partition TicksWithWastedSkill exactly, in this order.

    /// <summary>Wasted while a walk was in progress: the script chose to MOVE rather than cast something
    /// it could have cast. Legitimate for a kite, a defect for an approach that never ends.</summary>
    public int WastedWhileWalking { get; private set; }

    /// <summary>Of those, the ones walking TOWARD the mob - the walk ends closer than we stand now. The
    /// skill already reached, so the approach was the thing that should have stopped.</summary>
    public int WastedWalkingCloser { get; private set; }

    /// <summary>Of those, the ones walking AWAY WHILE SOMETHING IS ON US. A kite with a ready shot: the
    /// distinction that matters for a ranged class, because walking past an unaggroed mob with a skill up
    /// is not a waste, it is not starting a fight we did not want.</summary>
    public int WastedKitingWithAShot { get; private set; }

    /// <summary>Wasted with nothing locked on: the script had a usable skill and a reachable mob but was
    /// not engaged with anything. Target selection, not the rotation.</summary>
    public int WastedNotEngaged { get; private set; }

    /// <summary>Wasted with only a RANGED skill usable - the mob was outside weapon reach, so the skill
    /// was the only thing that could touch it and it was not cast. The ranged-class failure mode.</summary>
    public int WastedOutOfWeaponReach { get; private set; }

    /// <summary>Wasted standing still, engaged, in weapon reach. Nothing explains this one: the rotation
    /// simply did not fire. The residue after the three above.</summary>
    public int WastedIdle { get; private set; }

    /// <summary>Ticks spent moving while at least one mob was targeting us. The kite budget.</summary>
    public int TicksKiting { get; private set; }

    /// <summary>Kite ticks whose destination was MORE crowded than where we stood. The operator's
    /// complaint - "kites into other groups of enemies" - as a number.</summary>
    public int TicksKitingIntoACrowd { get; private set; }

    // ---- how the kiting is SHAPED -----------------------------------------------------------------
    //
    // "Short little kite bursts until a key skill or an HP stone is off cooldown" is the operator's
    // brief, and a kite PERCENTAGE cannot tell a burst from a tour: 30% of engaged time is the same
    // number whether it is thirty one-second breaks or three ten-second runs. Bursts are counted so the
    // two are distinguishable.

    /// <summary>Number of separate kites - transitions from not-kiting to kiting.</summary>
    public int KiteBursts { get; private set; }

    /// <summary>Total and longest kite, in simulated milliseconds.</summary>
    public uint KiteMsTotal { get; private set; }
    public uint KiteMsLongest { get; private set; }

    private uint _kiteStartedAt;
    private bool _kiting;

    /// <summary>Ticks at full SP. Regeneration past the cap is thrown away, and a script that never
    /// spends is not managing a resource, it is hoarding one.</summary>
    public int TicksSpCapped { get; private set; }

    /// <summary>Ticks where no learned damaging skill was affordable. The opposite failure.</summary>
    public int TicksSpStarved { get; private set; }

    /// <summary>Lowest HP fraction the character reached, in percent. How close the run came to dying.</summary>
    public int LowWaterHpPercent { get; private set; } = 100;

    /// <summary>Ticks below a quarter HP - time spent in the band where one bad pull ends the run.</summary>
    public int TicksBelowQuarterHp { get; private set; }

    /// <summary>THE DANGER BAND. Min HP is not a linear score: bottoming out near the heal threshold is
    /// the system working, and bottoming out near zero is a death that happened to be survived. These
    /// separate the two so a scorecard cannot praise a run that nearly died.</summary>
    public const int HealBandPercent = 40;     // the driver heals at ~this; touching it is NOT a problem
    public const int NearDeathPercent = 15;

    /// <summary>Ticks below <see cref="NearDeathPercent"/> - how long the run spent one hit from over.</summary>
    public int TicksNearDeath { get; private set; }

    /// <summary>Whether the run ever dropped into the near-death band at all.</summary>
    public bool EverNearDeath => TicksNearDeath > 0;

    /// <summary>Damage dealt and taken over the run, copied off the simulation at each sample so a run
    /// that died still carries its totals.</summary>
    public long DamageDealt { get; private set; }
    public long DamageTaken { get; private set; }

    /// <summary>Simulated milliseconds the character was alive for, from the last sample.</summary>
    public uint AliveMs { get; private set; }

    /// <summary>HP and SP soul-stone charges consumed.</summary>
    public int HpStonesUsed { get; private set; }
    public int SpStonesUsed { get; private set; }

    /// <summary>Aggro entries the server's own CutInterval/CutNonAT purge removed - a kite that actually
    /// worked. Counted from the simulation rather than inferred from movement.</summary>
    public int AggroCuts { get; set; }

    private int _lastHpStones = int.MinValue;
    private int _lastSpStones = int.MinValue;

    /// <summary>What counts as "crowded" around a point. Same radius the CombatLog uses so the two
    /// agree.</summary>
    public const int CrowdRadius = CombatLogEntry.CrowdRadius;

    /// <summary>Sample one tick. Called by <see cref="CombatSimulation.Step"/> AFTER the script has had
    /// its turn, so "wasted" means the script saw this state and did nothing with it.</summary>
    public void Sample(CombatSimulation sim)
    {
        var p = sim.Player;
        if (!p.IsAlive) return;
        Ticks++;

        if (p.MaxHp > 0)
        {
            var pct = 100 * p.Hp / p.MaxHp;
            if (pct < LowWaterHpPercent) LowWaterHpPercent = pct;
            if (pct < 25) TicksBelowQuarterHp++;
            if (pct < NearDeathPercent) TicksNearDeath++;
        }

        // Stone charges are counted by WATCHING THE RESERVE FALL rather than by hooking every use path,
        // so a charge spent by the queued-use timer counts the same as an immediate one.
        if (_lastHpStones != int.MinValue && p.HpStones < _lastHpStones)
            HpStonesUsed += _lastHpStones - p.HpStones;
        if (_lastSpStones != int.MinValue && p.SpStones < _lastSpStones)
            SpStonesUsed += _lastSpStones - p.SpStones;
        _lastHpStones = p.HpStones;
        _lastSpStones = p.SpStones;

        if (p.MaxSp > 0 && p.Sp >= p.MaxSp) TicksSpCapped++;

        DamageDealt = sim.DamageDealt;
        DamageTaken = sim.DamageTaken;
        AliveMs = sim.Now;

        var aggressors = sim.Mobs.Count(m => m.Mob.IsAlive && m.Arg.Target is SimPlayer);
        var kitingNow = aggressors > 0 && p.WalkTarget is not null;
        if (kitingNow && !_kiting) { KiteBursts++; _kiteStartedAt = sim.Now; }
        else if (!kitingNow && _kiting) EndKite(sim.Now);
        _kiting = kitingNow;

        if (kitingNow)
        {
            TicksKiting++;
            if (p.FinalWalkTarget is { } dest)
            {
                var here = sim.Mobs.Count(m => m.Mob.IsAlive && Near(m, p.X, p.Y));
                var there = sim.Mobs.Count(m => m.Mob.IsAlive && Near(m, dest.X, dest.Y));
                if (there > here) TicksKitingIntoACrowd++;
            }
        }

        // ---- the offensive-opportunity sample --------------------------------------------------------
        var nearest = sim.Mobs.Where(m => m.Mob.IsAlive)
            .Select(m => (m, d2: MobTargetSelector.SquaredDistance(p, m.Mob)))
            .OrderBy(t => t.d2)
            .FirstOrDefault();
        if (nearest.m is null) return;

        var inWeaponReach = nearest.d2 <= (long)p.AttackRange * p.AttackRange;
        if (inWeaponReach) TicksWithTargetInReach++;

        var anyAffordable = false;
        var anyUsable = false;
        foreach (var s in p.LearnedSkills)
        {
            if (s.Physical.MaxFlat <= 0 && s.Magical.MaxFlat <= 0) continue;   // damage skills only
            if (p.Sp < s.Sp) continue;
            anyAffordable = true;
            if (p.SkillReadyAt.TryGetValue(s.Id, out var ready) && sim.Now < ready) continue;
            // Range 0 on an offensive skill is melee reach, never unlimited -- see CombatSimulation.Cast.
            var reach = (long)(s.Range > 0 ? s.Range : p.AttackRange);
            if (nearest.d2 > reach * reach) continue;
            anyUsable = true;
            break;
        }

        if (!anyAffordable) TicksSpStarved++;

        // NOT ENGAGED IS NOT DEAD TIME. Travelling past a mob with a shot up is a decision not to pull it.
        var engaged = p.AutoAttackTarget is not null || aggressors > 0;
        if (!engaged) return;
        TicksEngaged++;

        // Casting counts as using the opportunity; so does being mid-cast.
        if (anyUsable && p.CastingSkill is null)
            Wasted(p, inWeaponReach, nearest.d2, nearest.m.Mob.X, nearest.m.Mob.Y, aggressors > 0);
        else if (!anyUsable && !inWeaponReach) TicksWithNothingPossible++;
    }

    /// <summary>Charge one wasted tick, and to exactly one reason. First match wins, so the categories
    /// partition the total and the scorecard's columns sum back to it.</summary>
    private void Wasted(SimPlayer p, bool inWeaponReach, long nearestD2, int mobX, int mobY,
                        bool underFire)
    {
        TicksWithWastedSkill++;
        if (p.WalkTarget is not null)
        {
            WastedWhileWalking++;
            if (nearestD2 >= 0 && p.FinalWalkTarget is { } dest)
            {
                long dx = mobX - dest.X, dy = mobY - dest.Y;
                if (dx * dx + dy * dy < nearestD2) WastedWalkingCloser++;
                else if (underFire) WastedKitingWithAShot++;
            }
        }
        else if (p.AutoAttackTarget is null) WastedNotEngaged++;
        else if (!inWeaponReach) WastedOutOfWeaponReach++;
        else WastedIdle++;
    }

    /// <summary>Close the current kite and bank its length.</summary>
    private void EndKite(uint now)
    {
        var len = now - _kiteStartedAt;
        KiteMsTotal += len;
        if (len > KiteMsLongest) KiteMsLongest = len;
    }

    private static bool Near(SimMob m, int x, int y)
    {
        long dx = m.Mob.X - x, dy = m.Mob.Y - y;
        return dx * dx + dy * dy <= (long)CrowdRadius * CrowdRadius;
    }

    // ---- derived rates, which are what a comparison actually reads --------------------------------

    /// <summary>Percent of sampled ticks where a usable damaging skill went uncast. LOWER IS BETTER.</summary>
    public double DeadTimePercent => TicksEngaged == 0 ? 0 : 100.0 * TicksWithWastedSkill / TicksEngaged;

    /// <summary>The wasted-tick breakdown as percentages of ENGAGED ticks, in the order they are
    /// charged. They sum to <see cref="DeadTimePercent"/>.</summary>
    public double WastedWalkingPercent => TicksEngaged == 0 ? 0 : 100.0 * WastedWhileWalking / TicksEngaged;
    public double WastedWalkingCloserPercent => TicksEngaged == 0 ? 0 : 100.0 * WastedWalkingCloser / TicksEngaged;
    public double WastedKitingWithAShotPercent => TicksEngaged == 0 ? 0 : 100.0 * WastedKitingWithAShot / TicksEngaged;
    public double WastedNotEngagedPercent => TicksEngaged == 0 ? 0 : 100.0 * WastedNotEngaged / TicksEngaged;
    public double WastedOutOfReachPercent => TicksEngaged == 0 ? 0 : 100.0 * WastedOutOfWeaponReach / TicksEngaged;
    public double WastedIdlePercent => TicksEngaged == 0 ? 0 : 100.0 * WastedIdle / TicksEngaged;

    /// <summary>Percent of kite ticks aimed somewhere MORE crowded. LOWER IS BETTER.</summary>
    public double BadKitePercent => TicksKiting == 0 ? 0 : 100.0 * TicksKitingIntoACrowd / TicksKiting;

    /// <summary>Percent of ticks spent moving under fire. Some is correct; a run that is mostly this is
    /// a tour, not a fight.</summary>
    public double KitePercent => Ticks == 0 ? 0 : 100.0 * TicksKiting / Ticks;

    /// <summary>Percent of ticks at the SP cap - regeneration thrown away.</summary>
    public double SpCappedPercent => Ticks == 0 ? 0 : 100.0 * TicksSpCapped / Ticks;

    /// <summary>Percent of ticks with no affordable damaging skill.</summary>
    public double SpStarvedPercent => Ticks == 0 ? 0 : 100.0 * TicksSpStarved / Ticks;

    private double AliveSeconds => AliveMs == 0 ? 0 : AliveMs / 1000.0;

    /// <summary>Damage dealt per second ALIVE. Higher is better, and unlike kills it still scores a fight
    /// that was lost.</summary>
    public double DpsOut => AliveSeconds == 0 ? 0 : DamageDealt / AliveSeconds;

    /// <summary>Damage taken per second alive. Lower is better - it is what the kiting and the pulling
    /// are for.</summary>
    public double DpsIn => AliveSeconds == 0 ? 0 : DamageTaken / AliveSeconds;

    /// <summary>Damage dealt for each point taken. The single number that says whether the fighting was
    /// worth it; a script that doubles its output while tripling its intake has got worse.</summary>
    public double DamageRatio => DamageTaken == 0 ? DamageDealt : (double)DamageDealt / DamageTaken;

    /// <summary>Mean kite length in seconds. A burst is a second or two; anything longer is a tour, and
    /// a tour is what walks the character into the next pack.</summary>
    public double KiteBurstSeconds => KiteBursts == 0 ? 0 : KiteMsTotal / 1000.0 / KiteBursts;

    /// <summary>Longest single kite, in seconds.</summary>
    public double KiteLongestSeconds => KiteMsLongest / 1000.0;

    /// <summary>Percent of ticks spent one hit from death.</summary>
    public double NearDeathPercentOfRun => Ticks == 0 ? 0 : 100.0 * TicksNearDeath / Ticks;
}
