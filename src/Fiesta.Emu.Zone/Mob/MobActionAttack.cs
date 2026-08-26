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

    /// <summary>`MobInfoServer.TurnSpeed`. <b>Zero means the mob turns instantly</b> — `mat_Reserv` returns
    /// the next action rather than entering the turning state.
    ///
    /// <para>⚠️ The meaning of a NON-zero value is NOT read. The column has only four values across the
    /// whole table — 100 (2,755 mobs), 0 (60), 300 (43), 500 (20) — and whether a larger number turns the
    /// mob faster or slower is undetermined; the distribution alone cannot say. Until `mab_Think`'s turn
    /// progress is ported, non-zero mobs use <see cref="MobActionTurning.TurnRateUnitsPerSecond"/> as
    /// before. Only the zero branch is acted on, because only that one was read.</para></summary>
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
        var squared = MobTargetSelector.SquaredDistance(arg.Actor, target);
        if (squared <= (long)combat.AttackRange * combat.AttackRange)
            return MobActionAttack.Actor_Attack;

        // Per-mob speed from MobInfo.RunSpeed; SpeedPerSecond is only the fallback for mobs with no data.
        var speed = combat.RunSpeed > 0 ? combat.RunSpeed : SpeedPerSecond;
        arg.MoveToward(target, Math.Max(1, (int)(speed * arg.ElapsedMs / 1000)));
        return this;
    }
}
