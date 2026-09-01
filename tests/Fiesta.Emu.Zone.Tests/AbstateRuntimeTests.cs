using Fiesta.Emu.Zone.Abstate;
using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Mob;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`AbstateListInObject` — abnormal states at runtime, rather than reconstructed inside a test.
///
/// <para>The effect table these run against is GENERATED from the binary
/// (`tools/abstate_actions.py --csharp`), so what is under test here is the list's behaviour — expiry,
/// replacement, cancel, and rebuilding a container — not the table's contents.</para></summary>
public class AbstateRuntimeTests
{
    /// <summary>A resolver standing in for `AbState.shn` -> `SubAbState.shn`: abstate id to its actions.</summary>
    private static Func<AbstateElementInObject, IReadOnlyList<(SubAbstateAction, int)>> Rows(
        Dictionary<int, (SubAbstateAction, int)[]> table)
        => e => table.TryGetValue(e.AbstateId, out var rows) ? rows : [];

    // ---- the list -------------------------------------------------------------------------------------

    /// <summary>`restKeeptime` is a duration in milliseconds, and a state that runs out is gone.
    ///
    /// <para>This is the hole `damage_buckets.py` still has: it removes a state only on an explicit
    /// `ABSTATERESET`, so one that lapses on its own timer is held forever. Harmless for `StaImmortal`,
    /// which carries no actions; wrong for `SubStaMoraleDecreaseWC`, which has 15 seconds and a real
    /// weapon-damage effect.</para></summary>
    [Fact]
    public void AStateExpiresOnItsOwnKeeptime()
    {
        var list = new AbstateListInObject();
        list.Set(abstateId: 5, strength: 4, restKeeptimeMs: 15_000, nowMs: 1_000);

        list.Active.Single().RestTimeMs(1_000).ShouldBe(15_000);
        list.Active.Single().RestTimeMs(9_000).ShouldBe(7_000);
        list.Tick(15_999).ShouldBeEmpty();
        list.IsSet(5).ShouldBeTrue();

        list.Tick(16_000).Single().AbstateId.ShouldBe(5);
        list.IsSet(5).ShouldBeFalse();
    }

    /// <summary>A `restKeeptime` of 0 expires immediately. Nothing in the data uses 0 as an eternal
    /// marker — `SubStaKeepTime_Eternal` carries a real 5000 — so treating it as infinite would be
    /// inventing a mechanic.</summary>
    [Fact]
    public void ZeroKeeptimeIsExpiredNotEternal()
    {
        var list = new AbstateListInObject();
        list.Set(abstateId: 291, strength: 1, restKeeptimeMs: 0, nowMs: 500);

        list.Tick(500).Single().AbstateId.ShouldBe(291);
        list.Active.ShouldBeEmpty();
    }

    /// <summary>Re-applying an id replaces the element. The server re-sends a combat debuff constantly and
    /// the effect does not accumulate; a list that stacked them would drift further from the wire the
    /// longer a fight ran.</summary>
    [Fact]
    public void ReapplyingAnIdReplacesRatherThanStacks()
    {
        var list = new AbstateListInObject();
        list.Set(5, strength: 4, restKeeptimeMs: 15_000, nowMs: 0);
        list.Set(5, strength: 6, restKeeptimeMs: 15_000, nowMs: 8_000);

        list.Active.Count.ShouldBe(1);
        list.Active.Single().Strength.ShouldBe(6);
        list.Active.Single().RestTimeMs(8_000).ShouldBe(15_000);   // the timer restarts
    }

    /// <summary>`aeo_Attack` — a mob that opens fire drops its own spawn invulnerability. That is why the
    /// capture shows two swings from an attacker still holding `StaImmortal` (291): the `ABSTATERESET`
    /// broadcast follows the swing by two to four frames rather than preceding it.</summary>
    [Fact]
    public void AttackingCancelsTheStatesThatSaySoAndLeavesTheRest()
    {
        var list = new AbstateListInObject();
        list.Set(291, strength: 1, restKeeptimeMs: 5_000, nowMs: 0);   // StaImmortal
        list.Set(5, strength: 4, restKeeptimeMs: 15_000, nowMs: 0);    // a plain debuff

        var ended = list.OnOwnerAttacked(e => e.AbstateId == 291);

        ended.Single().AbstateId.ShouldBe(291);
        list.IsSet(291).ShouldBeFalse();
        list.IsSet(5).ShouldBeTrue();
    }

    // ---- aeo_ParameterEnchant -------------------------------------------------------------------------

    /// <summary>The AbnormalState layer is REBUILT, not adjusted — which is the only way removing a state
    /// can take its bonus with it.
    ///
    /// <para>The rebuild has to restore each half's own identity: 0 for the plus half, 1000 for the rate
    /// half, and <b>0 for the seven rate slots the eraser zeroes</b> (`CriticalTB` through `ResistGTI`).
    /// Getting that last group wrong would leave a defender resisting criticals by 1000 permille.</para></summary>
    [Fact]
    public void EnchantRebuildsTheLayerSoARemovedStateLeavesNothingBehind()
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        var rows = Rows(new() { [5] = [(SubAbstateAction.SAA_WCMINUS, 203)] });

        list.Set(5, strength: 4, restKeeptimeMs: 15_000, nowMs: 0);
        list.ParameterEnchant(container, rows).ShouldBeTrue();
        container.Plus(StatModifier.AbnormalState)[Stat.WCmin].ShouldBe(-203);
        container.Plus(StatModifier.AbnormalState)[Stat.WCmax].ShouldBe(-203);

        list.Tick(15_000);
        list.ParameterEnchant(container, rows).ShouldBeTrue();
        container.Plus(StatModifier.AbnormalState)[Stat.WCmin].ShouldBe(0);
        container.Rate(StatModifier.AbnormalState)[Stat.WCmin].ShouldBe(1000);
        container.Rate(StatModifier.AbnormalState)[Stat.CriticalTB].ShouldBe(0);
    }

    /// <summary>`SAA_WCMINUS` and `SAA_WCRATE` touch BOTH bounds, which is why a weapon debuff shifts the
    /// whole damage range instead of squashing it.</summary>
    [Fact]
    public void WeaponActionsMoveBothBounds()
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        list.Set(4, strength: 5, restKeeptimeMs: 60_000, nowMs: 0);

        list.ParameterEnchant(container, Rows(new()
        {
            [4] = [(SubAbstateAction.SAA_WCRATE, 251)],
        })).ShouldBeTrue();

        container.Rate(StatModifier.AbnormalState)[Stat.WCmin].ShouldBe(1251);
        container.Rate(StatModifier.AbnormalState)[Stat.WCmax].ShouldBe(1251);
    }

    /// <summary>`SAA_NOMOVE` and `SAA_NOATTACK` write no stat at all. They are no-ops FOR DAMAGE, which is
    /// all the damage tests need, and they are the entire content of a stun for the tactic machine.</summary>
    [Fact]
    public void StunActionsSetBehaviourBitsAndNoStats()
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        list.Set(2, strength: 1, restKeeptimeMs: 3_000, nowMs: 0);   // StaBattleBlowStun

        list.ParameterEnchant(container, Rows(new()
        {
            [2] = [(SubAbstateAction.SAA_NOMOVE, 0), (SubAbstateAction.SAA_NOATTACK, 0)],
        })).ShouldBeTrue();

        container.Flags.ShouldBe(ContainerFlag.CannotMoveEntangle | ContainerFlag.CannotAttack);
        container.MakeTotal()[Stat.WCmin].ShouldBe(0);
    }

    /// <summary>The two immobilisations are distinguished by the sub-state's type, not by the action:
    /// `SAA_NOMOVE` branches into `SAA_AWAY`'s body and sets `cannotmove_stun` when the type at +0x26 is
    /// 0x15 or 0x60. The PDB names the bits separately, so the distinction is deliberate.</summary>
    [Fact]
    public void NoMoveSetsStunInsteadOfEntangleOnTheAlternativeBranch()
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        list.Set(307, strength: 1, restKeeptimeMs: 3_000, nowMs: 0);   // StaCommonStun02
        var rows = Rows(new() { [307] = [(SubAbstateAction.SAA_NOMOVE, 0)] });

        list.ParameterEnchant(container, rows, altCondition: _ => true).ShouldBeTrue();
        container.Flags.ShouldBe(ContainerFlag.CannotMoveStun);

        list.ParameterEnchant(container, rows, altCondition: _ => false).ShouldBeTrue();
        container.Flags.ShouldBe(ContainerFlag.CannotMoveEntangle);
    }

    /// <summary>An action that dispatches but writes nothing is a READ RESULT, so it must not make a
    /// prediction refuse. 49 of the 120 are exactly that.</summary>
    [Fact]
    public void AnActionWithNoContainerEffectStillSucceeds()
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        list.Set(1, strength: 1, restKeeptimeMs: 1_000, nowMs: 0);

        list.ParameterEnchant(container, Rows(new()
        {
            [1] = [(SubAbstateAction.SAA_DOTDAMAGE, 40), (SubAbstateAction.SAA_FEAR, 1)],
        })).ShouldBeTrue();
        container.Flags.ShouldBe(ContainerFlag.None);
    }

    /// <summary>An action index outside the dispatched range makes it refuse instead of silently
    /// predicting. `aeo_ParameterEnchant` bounds the switch at <c>cmp ebx, 0x77</c> after subtracting 1.</summary>
    [Fact]
    public void AnUndispatchedActionIndexRefuses()
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        list.Set(1, strength: 1, restKeeptimeMs: 1_000, nowMs: 0);

        list.ParameterEnchant(container, Rows(new()
        {
            [1] = [((SubAbstateAction)121, 5)],
        })).ShouldBeFalse();
    }

    // ---- the effects reach the formula ----------------------------------------------------------------

    /// <summary>The whole point of the layer: an abstate's write lands where the damage engine reads it.
    /// `SAA_MISSRATE` writes `MissPercentFix`, which short-circuits `roe_HitRate` entirely.</summary>
    [Fact]
    public void MissRateReachesTheHitRateShortCircuit()
    {
        var defender = Combatant.FromBaseStats(60, [new(Stat.Dex, 100)]);
        var attacker = Combatant.FromBaseStats(60, [new(Stat.Dex, 100)]);
        DamageCalculator.HitRate(attacker, defender).ShouldBe(850.0);

        var list = new AbstateListInObject();
        list.Set(60, strength: 1, restKeeptimeMs: 10_000, nowMs: 0);
        list.ParameterEnchant(defender.Parameters, Rows(new()
        {
            [60] = [(SubAbstateAction.SAA_MISSRATE, 300)],
        })).ShouldBeTrue();

        defender.Parameters.MissPercentFix.ShouldBe(300);
        DamageCalculator.HitRate(attacker, defender).ShouldBe(700.0);
    }

    /// <summary>`SAA_SHIELDACRATE` writes `AbnormalState.Rate[ShieldAC]`, the one rate term in
    /// `roe_ShieldBlock` — the abstate decode and the block formula meeting at +0x9F8 is what cross-checks
    /// both readings.</summary>
    [Fact]
    public void ShieldAcRateReachesTheBlockFormula()
    {
        var defender = Combatant.FromBaseStats(60, []);
        defender.Parameters.Plus(StatModifier.Item)[Stat.ShieldAC] = 70;
        DamageCalculator.ShieldBlockRate(defender).ShouldBe(70.0);

        var list = new AbstateListInObject();
        list.Set(6, strength: 1, restKeeptimeMs: 60_000, nowMs: 0);
        list.ParameterEnchant(defender.Parameters, Rows(new()
        {
            [6] = [(SubAbstateAction.SAA_SHIELDACRATE, 160)],
        })).ShouldBeTrue();

        DamageCalculator.ShieldBlockRate(defender).ShouldBe(81.2);   // 70 * 1160 / 1000
    }

    /// <summary>`SAA_HEALRATE` is the only action that ASSIGNS where its neighbours accumulate, so two
    /// sources of it do not stack. Zero as a sign means "mov", which is a real operation.</summary>
    [Fact]
    public void HealRateAssignsWhereItsNeighboursAccumulate()
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        list.Set(1, strength: 1, restKeeptimeMs: 60_000, nowMs: 0);
        list.Set(2, strength: 1, restKeeptimeMs: 60_000, nowMs: 0);

        list.ParameterEnchant(container, Rows(new()
        {
            [1] = [(SubAbstateAction.SAA_HEALRATE, 120)],
            [2] = [(SubAbstateAction.SAA_HEALRATE, 150)],
        })).ShouldBeTrue();

        container[ContainerField.HealRate].ShouldBe(150);

        // The accumulating neighbour, for contrast.
        list.ParameterEnchant(container, Rows(new()
        {
            [1] = [(SubAbstateAction.SAA_MISSRATE, 120)],
            [2] = [(SubAbstateAction.SAA_MISSRATE, 150)],
        })).ShouldBeTrue();
        container[ContainerField.MissPercentFix].ShouldBe(270);
    }

    /// <summary>The generated table is the binary's, so its shape is worth pinning: 120 dispatched
    /// actions, 71 with a container effect. If a regeneration changed either number, something moved.</summary>
    [Fact]
    public void TheGeneratedTableHasTheShapeTheBinaryHas()
    {
        AbstateEffects.All.Count.ShouldBe(71);
        Enumerable.Range(1, 120).ShouldAllBe(i => AbstateEffects.IsDispatched((SubAbstateAction)i));
        AbstateEffects.IsDispatched(SubAbstateAction.SAA_NONE).ShouldBeFalse();
        AbstateEffects.IsDispatched(SubAbstateAction.MAX_SUBABSTATEACTION).ShouldBeFalse();
    }
}

/// <summary>Where the behaviour bits are actually ENFORCED.
///
/// <para>The readers were found by scanning the image for the flag byte at both displacements it can be
/// reached through — <c>+0xCCE</c> from the container, and <c>+0x1C8E</c> from the object, since
/// `so_parameter@ShineMobileObject` is <c>lea eax,[ecx+0xFC0]</c>. There are exactly two in the whole
/// binary, and neither is in the tactic states this project expected them to be in.</para></summary>
public class ContainerFlagEnforcementTests
{
    /// <summary>`so_ReinforceMove@ShineMobileObject+0x90`: <c>test byte [edi+0x1C8E], 2</c> then return.
    /// The gate is on the MOVE function, so it applies to every caller at once — which is why porting it
    /// into `MobActionChase` would have been both more work and wrong.</summary>
    [Fact]
    public void AnEntangledMobDoesNotMove()
    {
        var mob = new ShineMob { Handle = 1, X = 0, Y = 0 };
        var target = new ShineMob { Handle = 2, X = 1000, Y = 0 };
        var arg = new MobActionArgument { Actor = mob, Selector = new MobTargetSelector() };

        arg.MoveToward(target, 100);
        mob.X.ShouldBe(100);

        mob.Flags = ContainerFlag.CannotMoveEntangle;
        arg.MoveToward(target, 100);
        mob.X.ShouldBe(100);

        mob.Flags = ContainerFlag.None;
        arg.MoveToward(target, 100);
        mob.X.ShouldBe(200);
    }

    /// <summary>⚠️ <b>`cannotmove_stun` is written by two actions and read by nothing.</b>
    ///
    /// <para>`SAA_NOMOVE`'s alternative branch and `SAA_AWAY` both set bit 1, and no instruction in the
    /// image tests it — at either displacement. So the two immobilisations the PDB names separately are
    /// not both enforced server-side, and however a stunned MOB is stopped, it is not through this bit.
    /// This test exists to pin that as a finding rather than leave it as an assumption; if a reader turns
    /// up later, it should fail and be rewritten.</para>
    ///
    /// <para>The port therefore does NOT gate movement on it. Making <c>CannotMoveStun</c> stop a mob
    /// would be inventing a mechanic the server does not have, which is exactly the kind of plausible
    /// fabrication that is hardest to find later.</para></summary>
    [Fact]
    public void TheStunBitStopsNothing_BecauseNothingReadsIt()
    {
        var mob = new ShineMob { Handle = 1, X = 0, Y = 0, Flags = ContainerFlag.CannotMoveStun };
        var target = new ShineMob { Handle = 2, X = 1000, Y = 0 };
        var arg = new MobActionArgument { Actor = mob, Selector = new MobTargetSelector() };

        arg.MoveToward(target, 100);
        mob.X.ShouldBe(100);
    }

    /// <summary>An abstate that sets the bit reaches the mob through the container, which is the whole
    /// path: `SubAbState.shn` row -> `SAA_NOMOVE` -> `Container.flag` -> the move gate.</summary>
    [Fact]
    public void AStunAbstateReachesTheMoveGate()
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        list.Set(2, strength: 1, restKeeptimeMs: 3_000, nowMs: 0);
        list.ParameterEnchant(container,
            _ => [(SubAbstateAction.SAA_NOMOVE, 0)]).ShouldBeTrue();

        var mob = new ShineMob { Handle = 1, X = 0, Y = 0, Flags = container.Flags };
        var target = new ShineMob { Handle = 2, X = 1000, Y = 0 };
        new MobActionArgument { Actor = mob, Selector = new MobTargetSelector() }.MoveToward(target, 100);
        mob.X.ShouldBe(0);

        // ...and it lets go when the state times out.
        list.Tick(3_000);
        list.ParameterEnchant(container, _ => []).ShouldBeTrue();
        container.Flags.ShouldBe(ContainerFlag.None);
    }
}
