using Fiesta.Emu.Zone.Random;

namespace Fiesta.Emu.Zone.Mob;

/// <summary>What a mob is about to do to its target.</summary>
public enum MobAttackChoice
{
    /// <summary>Ordinary weapon swing.</summary>
    NormalAttack,

    /// <summary>A skill, chosen by one of the exchange rules.</summary>
    Skill,
}

/// <summary>Why a skill was chosen — the `sm_SkillExchange_*` family.
///
/// <para>Recorded on the outcome rather than discarded, because "the mob used a skill" is not a useful
/// trace when a run diverges; "the mob used a skill because its HP was low" is.</para></summary>
public enum SkillExchangeReason
{
    None,
    /// <summary>`sm_SkillExchange_HPLow` — the mob's own HP fell below its threshold.</summary>
    HpLow,
    /// <summary>`sm_SkillExchange_TargetState` — something about the target.</summary>
    TargetState,
    /// <summary>`sm_SkillExchange_OutOfRange` — the target is beyond melee reach.</summary>
    OutOfRange,
    /// <summary>`maa_SkillFromScriptClear` — a Lua script supplied the skill.</summary>
    Script,
    /// <summary>None of the rules fired; the random draw selected a skill anyway.</summary>
    Random,
}

/// <summary>The result of one attack decision.</summary>
public readonly record struct MobAttackDecision(
    MobActionBase NextState,
    MobAttackChoice Choice,
    SkillExchangeReason Reason);

/// <summary>Per-mob combat inputs the attack state consults.</summary>
public sealed class MobCombatState
{
    /// <summary>Current and maximum HP, for `sm_SkillExchange_HPLow`.</summary>
    public int Hp { get; set; } = 100;
    public int MaxHp { get; set; } = 100;

    /// <summary>The HP fraction, in permille, below which the low-HP exchange fires.</summary>
    public int HpLowThresholdPermille { get; set; }

    /// <summary>Melee reach. Compared as a SQUARE, as the server does — `so_DistanceSquar`, never rooted.</summary>
    public int AttackRange { get; set; } = 10;

    /// <summary>How far off-target the mob may face and still attack, in direction units (2 degrees each).
    /// Beyond this it reserves a turn instead.</summary>
    public int FacingToleranceUnits { get; set; } = 5;

    /// <summary>The mob's current facing, in direction units.</summary>
    public int Facing { get; set; }

    /// <summary>How fast this mob closes distance, from `MobInfo.RunSpeed` — the value
    /// `ShineMob::so_RunSpeed` returns. Orc is 127, a Mushroom 105.
    ///
    /// <para>Per-MOB, because the action states are shared singletons here: a speed stored on the chase
    /// state would be one speed for every mob in the world.</para></summary>
    public int RunSpeed { get; set; } = 50;

    /// <summary>`MobInfo.WalkSpeed` — the speed used while inside <see cref="WalkChaseDistance"/>.</summary>
    public int WalkSpeed { get; set; } = 30;

    /// <summary>`MobInfoServer.WalkChase` — the distance INSIDE which a chasing mob walks instead of runs.
    ///
    /// <para>⚠️ It is a DISTANCE, not a speed, and the difference is easy to get backwards because the
    /// column sits between two speed columns. `MobActionChase::mab_Think+0x895`:</para>
    /// <code>
    /// if (serv->WalkChase >= ddt_Distance(dx, dy)) mab_WalkTo(...);   // +0x89F, jge
    /// else                                         mab_RunTo(...);    // +0x8AB
    /// </code>
    ///
    /// <para><b>Zero means always run</b>, and 2,862 of 2,878 mobs are zero — which is why treating every
    /// chase as a run was right for almost everything. The 16 that are not: `B_SubHel01`–`08` at 400 (walk
    /// 115, run 400 — they close the last 400 units at a quarter speed), the golems at 150/300, `Anvil` at
    /// 100, and `KQ_SK_Dash` at 1.</para></summary>
    public int WalkChaseDistance { get; set; }

    /// <summary>`MobInfoServer.TurnSpeed`. <b>Zero means the mob turns instantly</b> — `mat_Reserv` returns
    /// the next action rather than entering the turning state.
    ///
    /// <para>A non-zero value is a DURATION — milliseconds per full turn — so <b>bigger is slower</b>.
    /// `MobActionTurning::mab_Think` divides by it (<see cref="MobActionTurning.UnitsTurned"/>), and a full
    /// 180-unit turn completes at <c>elapsedTenths == TurnSpeed / 100</c>. The column has four values:
    /// 100 (2,755 mobs, 0.1 s per turn), 0 (60), 300 (43), 500 (20, half a second).</para>
    ///
    /// <para>This doc said the non-zero meaning was unread for a while, on the grounds that the
    /// distribution could not settle it. It could not — but the ASM could, one function away.</para></summary>
    public int TurnSpeed { get; set; } = 100;

    /// <summary>Whether this mob skips the turning state entirely.</summary>
    public bool TurnsInstantly => TurnSpeed == 0;

    /// <summary>When the current turn began, in tenths — `mat_Reserv` stamps the clock into the turning
    /// state at +0x10 and `mab_Think` measures against it.</summary>
    public int TurnStartedTenths { get; set; }

    /// <summary>Chance in permille that a skill is used when no exchange rule fires.</summary>
    public int SkillChancePermille { get; set; }

    /// <summary>Set when a Lua script has supplied a skill — `maa_SkillFromScriptClear`. Mob behaviour is
    /// scriptable, and a scripted mob overrides the built-in rules.</summary>
    public bool SkillFromScript { get; set; }

    /// <summary>`sm_SkillExchange_TargetState` — a predicate on the target. Left to the caller because the
    /// server's version reads state this simulator does not model yet.</summary>
    public Func<IShineObject, bool> TargetStateWantsSkill { get; set; } = _ => false;

    /// <summary>What `so_mob_ChaseRangeSquar` returns for this mob: `MobInfoServer.FollowCha` squared.
    /// 0 means no limit, which is what a mob with no data row gets - it chases for ever.</summary>
    public long ChaseRangeSquar { get; set; }

    /// <summary>`MobInfoServer.Return2Regen` - whether MobAction2Region sends the mob home.</summary>
    public int Return2Regen { get; set; }

    /// <summary>`MobInfoServer.RoamingRestTime` in tenths (the server computes it as `* 10 / 1000`).
    /// 0 for every ordinary field mob.</summary>
    public int RoamingRestTenths { get; set; }
}

/// <summary>`MobTacticElement::MobActionAttack` — the attack decision.
///
/// <para>Ported from the reading in docs/COMBAT.md. The order of the checks is the point:</para>
/// <list type="number">
///   <item><b>Range</b> first — out of reach means chase, not attack.</item>
///   <item><b>Facing</b> next — a mob that is not looking at its target reserves a TURN and attacks no
///         one this tick. Turning costs ticks, which is why being approached from behind buys time.</item>
///   <item><b>Skill exchange</b> last — script, then low HP, then target state, then a random draw.</item>
/// </list>
///
/// <para>⚠️ The exchange rules' internal thresholds are not read yet; each is represented by an input on
/// <see cref="MobCombatState"/> rather than a ported condition. The <em>order</em> is from the binary; the
/// <em>predicates</em> are placeholders and are marked as such.</para></summary>
public sealed class MobActionAttack : MobActionBase
{
    /// <summary>`MobTacticElement::MobActionTurning`, reserved via `mat_Reserv` when facing is wrong.</summary>
    public static readonly MobActionBase Actor_Turning = new MobActionTurning();

    /// <summary>`MobTacticElement::MobActionChase`, entered when the target is out of range.</summary>
    public static readonly MobActionBase Actor_Chase = new MobActionChase();

    public override MobActionBase mab_Think(MobActionArgument arg)
        => Decide(arg).NextState;

    /// <summary>The decision, with its reasoning — <see cref="mab_Think"/> keeps only the next state.</summary>
    public MobAttackDecision Decide(MobActionArgument arg)
    {
        var target = arg.Target;
        if (target is null || !target.IsAlive)
            return new MobAttackDecision(Actor_Targetting, MobAttackChoice.NormalAttack, SkillExchangeReason.None);

        var combat = arg.Combat;

        // 1. Range. Squared throughout -- so_DistanceSquar never takes a root.
        var squared = MobTargetSelector.SquaredDistance(arg.Actor, target);
        if (squared > (long)combat.AttackRange * combat.AttackRange)
            return new MobAttackDecision(Actor_Chase, MobAttackChoice.NormalAttack, SkillExchangeReason.OutOfRange);

        // 2. Facing. ddt_DirectSR gives the direction to the target, ddt_ShineRadianDiff the difference;
        //    too far off and the mob reserves a turn (mat_Reserv) instead of attacking this tick.
        var toTarget = Direction.ddt_DirectSR(target.X - arg.Actor.X, target.Y - arg.Actor.Y);
        if (Direction.ddt_ShineRadianDiff(combat.Facing, toTarget) > combat.FacingToleranceUnits)
        {
            // `mat_Reserv` reads TurnSpeed and, when it is ZERO, returns the caller's next action instead of
            // the turning state -- the mob simply faces its target and carries on in the same tick.
            if (combat.TurnsInstantly)
            {
                combat.Facing = toTarget;
            }
            else
            {
                combat.TurnStartedTenths = arg.NowTenths;      // mat_Reserv stamps the clock at +0x10
                return new MobAttackDecision(Actor_Turning, MobAttackChoice.NormalAttack, SkillExchangeReason.None);
            }
        }

        // 3. Skill exchange, in the binary's order.
        var reason = ChooseSkill(arg, target, combat);
        return new MobAttackDecision(
            this,
            reason == SkillExchangeReason.None ? MobAttackChoice.NormalAttack : MobAttackChoice.Skill,
            reason);
    }

    private static SkillExchangeReason ChooseSkill(MobActionArgument arg, IShineObject target, MobCombatState combat)
    {
        if (combat.SkillFromScript)
            return SkillExchangeReason.Script;

        if (combat.MaxHp > 0 && (long)combat.Hp * 1000 / combat.MaxHp < combat.HpLowThresholdPermille)
            return SkillExchangeReason.HpLow;

        if (combat.TargetStateWantsSkill(target))
            return SkillExchangeReason.TargetState;

        // The random draw. well512_GetRandom(1000) is uniform over [0,1000), so `< chance` gives the
        // intended permille probability -- and a chance of 0 can never fire, which is correct: 0 means
        // never, not "unset".
        if (combat.SkillChancePermille > 0 && arg.Rng.well512_GetRandom(1000) < combat.SkillChancePermille)
            return SkillExchangeReason.Random;

        return SkillExchangeReason.None;
    }
}

/// <summary>`MobTacticElement::MobActionTurning` — rotate toward the target, then resume attacking.
///
/// <para>Turning is a state of its own, so it costs at least one tick. That is the mechanism behind
/// direction-dependent responsiveness: the detection radius is a circle, but a mob facing away has to
/// spend time turning before it can act.</para></summary>
public sealed class MobActionTurning : MobActionBase
{
    /// <summary>`0x2BF20` — 180,000. The numerator in the turn-progress division.</summary>
    public const int TurnScale = 180_000;

    /// <summary>How far a mob has turned since its turn began, in direction units.
    ///
    /// <para>Straight from `mab_Think`: <c>elapsed * 0x2BF20 / (TurnSpeed * 10)</c>, i.e.
    /// <c>elapsedTenths * 18000 / TurnSpeed</c>.</para>
    ///
    /// <para><b>`TurnSpeed` is a DURATION, not a rate — bigger is SLOWER.</b> Setting the result to a full
    /// turn (<see cref="Direction.UnitsPerTurn"/> = 180) and solving gives
    /// <c>elapsedTenths = TurnSpeed / 100</c>, so `TurnSpeed` is milliseconds for a complete 360°: 100 is
    /// 0.1 s, 300 is 0.3 s, 500 is 0.5 s. The name says speed and the number is a time, which is precisely
    /// why this was left unported rather than guessed — the distribution alone could not tell the
    /// direction, and getting it backwards would make the slowest mobs the nimblest.</para></summary>
    public static int UnitsTurned(int elapsedTenths, int turnSpeed)
        => turnSpeed <= 0 ? Direction.UnitsPerTurn : elapsedTenths * TurnScale / (turnSpeed * 10);

    public override MobActionBase mab_Think(MobActionArgument arg)
    {
        var target = arg.Target;
        if (target is null || !target.IsAlive)
            return Actor_Targetting;

        var combat = arg.Combat;
        var toTarget = Direction.ddt_DirectSR(target.X - arg.Actor.X, target.Y - arg.Actor.Y);
        var required = Direction.ddt_ShineRadianDiff(combat.Facing, toTarget);

        // The original does not turn a little each tick: it stamps the clock when the turn is reserved and
        // then asks, each think, whether enough time has passed to have covered the whole angle.
        var turned = UnitsTurned(arg.NowTenths - combat.TurnStartedTenths, combat.TurnSpeed);
        if (turned < required)
            return this;

        combat.Facing = toTarget;
        return MobActionAttack.Actor_Attack;
    }
}

/// <summary>`MobTacticElement::MobActionChase` — close the distance, then attack.</summary>
public sealed class MobActionChase : MobActionBase
{
    /// <summary>Distance units moved per SECOND. Per-second for the same reason as the turn rate: a
    /// mob's speed is its own property, not a function of how finely the caller ticks.</summary>
    public int SpeedPerSecond { get; set; } = 50;

    public override MobActionBase mab_Think(MobActionArgument arg)
    {
        var target = arg.Target;
        if (target is null || !target.IsAlive)
            return Actor_Targetting;

        var combat = arg.Combat;

        // mab_Think+0xC0, before the target is looked at for range:
        //     dx = self.pos.x - so_mob_LastHittedLocation()->x
        //     dy = self.pos.y - so_mob_LastHittedLocation()->y
        //     if (dx*dx + dy*dy > so_mob_ChaseRangeSquar())  ->  &Actor::toregion
        // `jbe` keeps chasing, so equality still chases -- the same boundary as the detect circle.
        if (combat.ChaseRangeSquar > 0 && arg.Actor is ShineMob self)
        {
            long ax = self.X - self.LastHittedLocation.X, ay = self.Y - self.LastHittedLocation.Y;
            if (ax * ax + ay * ay > combat.ChaseRangeSquar)
                return Actor_ToRegion;
        }

        var squared = MobTargetSelector.SquaredDistance(arg.Actor, target);
        if (squared <= (long)combat.AttackRange * combat.AttackRange)
            return MobActionAttack.Actor_Attack;

        // WALK when already inside WalkChase of the target, RUN otherwise -- `mab_Think+0x895`. The server
        // compares `ddt_Distance` against the threshold; comparing squares is the same test for
        // non-negative values and avoids introducing a second distance function whose rounding could
        // disagree with the one the rest of this file uses.
        var threshold = (long)combat.WalkChaseDistance * combat.WalkChaseDistance;
        var walking = combat.WalkChaseDistance > 0 && threshold >= squared;

        // SpeedPerSecond is only the fallback for a mob with no data at all.
        var speed = walking
            ? (combat.WalkSpeed > 0 ? combat.WalkSpeed : SpeedPerSecond)
            : (combat.RunSpeed > 0 ? combat.RunSpeed : SpeedPerSecond);

        arg.MoveToward(target, Math.Max(1, (int)(speed * arg.ElapsedMs / 1000)));
        return this;
    }
}
