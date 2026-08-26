using Fiesta.Emu.Zone.Mob;
using Fiesta.Emu.Zone.Random;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The attack decision, per docs/COMBAT.md.
///
/// The ORDER of the checks is ported from the binary (range, then facing, then skill exchange). The
/// exchange rules' internal thresholds are not read yet and are supplied as inputs — so these tests pin
/// the sequencing, which is what changes behaviour, and not the thresholds, which are placeholders.</summary>
public class MobActionAttackTests
{
    private static (MobActionArgument arg, ShineMob mob) Setup(
        int targetX = 5, int targetY = 0, Action<MobCombatState>? configure = null)
    {
        var mob = new ShineMob { Handle = 1, X = 0, Y = 0, so_getDetectRange = 100 };
        var combat = new MobCombatState { AttackRange = 10, Facing = 0, FacingToleranceUnits = 5 };
        configure?.Invoke(combat);
        var target = new Obj { Handle = 2, X = targetX, Y = targetY };
        var arg = new MobActionArgument
        {
            Actor = mob,
            Selector = mob.Selector,
            Combat = combat,
            Nearby = [target],
            Target = target,
        };
        return (arg, mob);
    }

    [Fact]
    public void AnOutOfRangeTargetMeansChase_NotAttack()
    {
        var (arg, _) = Setup(targetX: 50);
        var d = MobActionBase.Actor_Attack.Decide(arg);

        d.NextState.ShouldBe(MobActionAttack.Actor_Chase);
        d.Reason.ShouldBe(SkillExchangeReason.OutOfRange);
    }

    /// <summary>Facing is checked in the ATTACK state, not during acquisition. A mob looking the wrong way
    /// spends this tick turning — which is the mechanism behind direction-dependent responsiveness, with
    /// the detection radius still a circle.</summary>
    [Fact]
    public void AMobFacingTheWrongWayTurnsInsteadOfAttacking()
    {
        var (arg, _) = Setup(targetX: 5, targetY: 0, configure: c => c.Facing = 90);   // 180 degrees off
        MobActionBase.Actor_Attack.Decide(arg).NextState.ShouldBe(MobActionAttack.Actor_Turning);
    }

    [Fact]
    public void AMobAlreadyFacingTheTargetAttacksThisTick()
    {
        var (arg, _) = Setup(targetX: 5, targetY: 0, configure: c => c.Facing = 0);
        var d = MobActionBase.Actor_Attack.Decide(arg);

        d.NextState.ShouldBe(MobActionBase.Actor_Attack);
        d.Choice.ShouldBe(MobAttackChoice.NormalAttack);
    }

    [Fact]
    public void TurningTakesTicksAndThenHandsBackToAttack()
    {
        var (arg, _) = Setup(targetX: 5, targetY: 0, configure: c => c.Facing = 90);
        var turning = (MobActionTurning)MobActionAttack.Actor_Turning;

        var state = (MobActionBase)turning;
        var ticks = 0;
        while (state == turning && ticks < 50) { state = state.mab_Think(arg); ticks++; }

        ticks.ShouldBeGreaterThan(1);                       // it genuinely costs time
        state.ShouldBe(MobActionBase.Actor_Attack);
        arg.Combat.Facing.ShouldBe(0);                      // now looking at the target
    }

    [Fact]
    public void ChaseClosesTheDistanceThenAttacks()
    {
        var (arg, mob) = Setup(targetX: 50);
        var chase = MobActionAttack.Actor_Chase;

        var state = chase;
        var ticks = 0;
        while (state == chase && ticks < 100) { state = state.mab_Think(arg); ticks++; }

        state.ShouldBe(MobActionBase.Actor_Attack);
        mob.X.ShouldBeGreaterThan(0);                       // it actually moved
    }

    // ---- skill exchange, in the binary's order -------------------------------------------------------

    [Fact]
    public void AScriptSuppliedSkillWinsOverEverythingElse()
    {
        var (arg, _) = Setup(configure: c =>
        {
            c.SkillFromScript = true;
            c.Hp = 1; c.MaxHp = 100; c.HpLowThresholdPermille = 500;   // low HP would also fire
        });

        var d = MobActionBase.Actor_Attack.Decide(arg);
        d.Choice.ShouldBe(MobAttackChoice.Skill);
        d.Reason.ShouldBe(SkillExchangeReason.Script);
    }

    [Fact]
    public void LowHpTriggersASkill()
    {
        var (arg, _) = Setup(configure: c =>
        {
            c.Hp = 20; c.MaxHp = 100; c.HpLowThresholdPermille = 300;
        });

        MobActionBase.Actor_Attack.Decide(arg).Reason.ShouldBe(SkillExchangeReason.HpLow);
    }

    [Fact]
    public void HpExactlyAtTheThresholdDoesNotTrigger()
    {
        var (arg, _) = Setup(configure: c =>
        {
            c.Hp = 30; c.MaxHp = 100; c.HpLowThresholdPermille = 300;   // 300 is not < 300
        });

        MobActionBase.Actor_Attack.Decide(arg).Reason.ShouldBe(SkillExchangeReason.None);
    }

    [Fact]
    public void TargetStateCanTriggerASkill()
    {
        var (arg, _) = Setup(configure: c => c.TargetStateWantsSkill = _ => true);
        MobActionBase.Actor_Attack.Decide(arg).Reason.ShouldBe(SkillExchangeReason.TargetState);
    }

    /// <summary>A chance of 0 must never fire. Zero is a real value here, not "unset".</summary>
    [Fact]
    public void AZeroSkillChanceNeverFires()
    {
        var (arg, _) = Setup(configure: c => c.SkillChancePermille = 0);
        for (var i = 0; i < 200; i++)
            MobActionBase.Actor_Attack.Decide(arg).Reason.ShouldBe(SkillExchangeReason.None);
    }

    [Fact]
    public void AFullSkillChanceAlwaysFires()
    {
        var (arg, _) = Setup(configure: c => c.SkillChancePermille = 1000);
        for (var i = 0; i < 50; i++)
            MobActionBase.Actor_Attack.Decide(arg).Reason.ShouldBe(SkillExchangeReason.Random);
    }

    /// <summary>The whole reason the RNG was ported exactly: the same state gives the same decisions.</summary>
    [Fact]
    public void TheDecisionSequenceIsReproducibleFromTheRngState()
    {
        static List<SkillExchangeReason> Run()
        {
            var mob = new ShineMob { Handle = 1, X = 0, Y = 0 };
            var target = new Obj { Handle = 2, X = 5, Y = 0 };
            var arg = new MobActionArgument
            {
                Actor = mob,
                Selector = mob.Selector,
                Combat = new MobCombatState { AttackRange = 10, SkillChancePermille = 400 },
                Nearby = [target],
                Target = target,
                Rng = new cWell512Random(Enumerable.Range(1, 16).Select(i => (uint)i).ToArray()),
            };
            return Enumerable.Range(0, 40)
                .Select(_ => MobActionBase.Actor_Attack.Decide(arg).Reason).ToList();
        }

        var first = Run();
        Run().ShouldBe(first);
        first.ShouldContain(SkillExchangeReason.Random);      // the draw really is being consulted
        first.ShouldContain(SkillExchangeReason.None);
    }

    [Fact]
    public void ADeadOrMissingTargetSendsTheMobBackToTargetting()
    {
        var (arg, _) = Setup();
        arg.Target = null;
        MobActionBase.Actor_Attack.Decide(arg).NextState.ShouldBe(MobActionBase.Actor_Targetting);

        arg.Target = new Obj { Handle = 3, X = 5, Y = 0, IsAlive = false };
        MobActionBase.Actor_Attack.Decide(arg).NextState.ShouldBe(MobActionBase.Actor_Targetting);
    }
}
