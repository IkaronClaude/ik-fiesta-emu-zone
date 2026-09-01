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

/// <summary>Damage over time — `SubAbnormalStateActorPoison::sasa_Routine` and the two functions under it.
///
/// <para>The tick is small, and every part of it was already half-read elsewhere in this project: the
/// `SAA_DOTDAMAGE` argument comes from the same `SubAbState` row the parameter actions do, the append
/// comes from the `DotDamagePlus` members the `SAA_ADD*DMG` family writes, and the suppression check is
/// `StaImmortal` again.</para></summary>
public class DotDamageTests
{
    /// <summary>The type byte selects the member, and the five that map were read off
    /// `smo_DotDamageAppend`'s jump table rather than guessed from the names.
    ///
    /// <para>That the names then AGREE — `SAA_ADDPOISONDMG` writes `Poison`, and type 0x21 reads it back —
    /// is the cross-check, not the derivation.</para></summary>
    [Theory]
    [InlineData(0x16, ContainerField.DotDamagePlusBlooding)]
    [InlineData(0x21, ContainerField.DotDamagePlusPoison)]
    [InlineData(0x22, ContainerField.DotDamagePlusDesease)]
    [InlineData(0x53, ContainerField.DotDamagePlusBurn)]
    [InlineData(0x54, ContainerField.DotDamagePlusPitBlooding)]
    public void EachDotTypeDrawsFromItsOwnMember(int type, ContainerField field)
        => DotDamage.MemberForSubStateType(type).ShouldBe(field);

    /// <summary>The other fifty-two types in the table's range fall through to no member at all — which is
    /// a read result, since they are enumerated in the case table.</summary>
    [Theory]
    [InlineData(0x17)]
    [InlineData(0x20)]
    [InlineData(0x3F)]
    [InlineData(0x52)]
    [InlineData(0x99)]
    public void ATypeWithNoMemberAppendsNothing(int type)
    {
        DotDamage.MemberForSubStateType(type).ShouldBeNull();
        DotDamage.Append(new ParameterContainer(), type).ShouldBe(0);
    }

    /// <summary>`damage = SAA_DOTDAMAGE arg + append`, and the append comes off the TARGET's container —
    /// so a debuff on the victim makes their own poison hit harder.</summary>
    [Fact]
    public void TheTargetsOwnDotBonusIsAddedToTheCastersDamage()
    {
        var target = new ParameterContainer();
        DotDamage.Tick(target, 0x21, dotDamageArg: 40, currentHp: 1000).ShouldBe(40);

        target[ContainerField.DotDamagePlusPoison] = 15;      // SAA_ADDPOISONDMG on the victim
        DotDamage.Tick(target, 0x21, dotDamageArg: 40, currentHp: 1000).ShouldBe(55);

        target[ContainerField.DotDamagePlusPoison] = -15;     // SAA_SUBTRACTPOISONDMG
        DotDamage.Tick(target, 0x21, dotDamageArg: 40, currentHp: 1000).ShouldBe(25);
    }

    /// <summary>The floor of 1 is applied AFTER the append, so a resistance that more than cancels the
    /// poison still leaves a tick of 1 rather than 0. `sasa_GetDamage+0xA5`.</summary>
    [Fact]
    public void TheFloorOfOneIsAppliedAfterTheAppendNotBefore()
    {
        var target = new ParameterContainer();
        target[ContainerField.DotDamagePlusPoison] = -500;

        DotDamage.Tick(target, 0x21, dotDamageArg: 40, currentHp: 1000).ShouldBe(1);
    }

    /// <summary>A DoT does not overkill — it is capped at the target's current HP.</summary>
    [Fact]
    public void ADotTickIsCappedAtCurrentHp()
    {
        var target = new ParameterContainer();
        DotDamage.Tick(target, 0x21, dotDamageArg: 400, currentHp: 37).ShouldBe(37);
        DotDamage.Tick(target, 0x21, dotDamageArg: 400, currentHp: 0).ShouldBe(0);
    }

    /// <summary>A sub-state row with no `SAA_DOTDAMAGE` deals nothing. Null is "the row has no such
    /// action", which is a different thing from an argument of 0 — that would still tick for the floor
    /// of 1.</summary>
    [Fact]
    public void NoDotActionMeansNoTickWhereasAZeroArgumentStillTicksForOne()
    {
        var target = new ParameterContainer();
        DotDamage.Tick(target, 0x21, dotDamageArg: null, currentHp: 1000).ShouldBe(0);
        DotDamage.Tick(target, 0x21, dotDamageArg: 0, currentHp: 1000).ShouldBe(1);
    }

    /// <summary>`StaImmortal` blocks the tick outright — `sasa_Routine@Poison` tests abstate 291 and 499
    /// before computing anything. Spawn invulnerability turns out to mean invulnerable to poison too.</summary>
    [Fact]
    public void SpawnInvulnerabilitySuppressesTheTickEntirely()
    {
        var states = new AbstateListInObject();
        DotDamage.IsSuppressed(states).ShouldBeFalse();

        states.Set(DotDamage.StaImmortal, strength: 1, restKeeptimeMs: 5_000, nowMs: 0);
        DotDamage.IsSuppressed(states).ShouldBeTrue();

        states.Tick(5_000);
        DotDamage.IsSuppressed(states).ShouldBeFalse();
    }

    /// <summary>The sub-state type also picks stun over entangle, which is now the list's own rule rather
    /// than a delegate the caller has to supply.</summary>
    [Theory]
    [InlineData(0x15, ContainerFlag.CannotMoveStun)]
    [InlineData(0x60, ContainerFlag.CannotMoveStun)]
    [InlineData(0x21, ContainerFlag.CannotMoveEntangle)]
    [InlineData(0x00, ContainerFlag.CannotMoveEntangle)]
    public void TheTypeByteAlsoDecidesStunVersusEntangle(int type, ContainerFlag expected)
    {
        var container = new ParameterContainer();
        var list = new AbstateListInObject();
        list.Set(2, strength: 1, restKeeptimeMs: 3_000, nowMs: 0, subStateType: type);

        list.ParameterEnchant(container, _ => [(SubAbstateAction.SAA_NOMOVE, 0)]).ShouldBeTrue();
        container.Flags.ShouldBe(expected);
    }
}

/// <summary>`so_AbnormalState_Resist` — the roll that happens before a debuff is applied at all.
///
/// <para>Mobs and players resist by completely different means, which is the point: a mob's resistance is
/// a property of the species and a player's is a property of their gear and buffs.</para></summary>
public class AbstateResistanceTests
{
    /// <summary>Twelve permille values on the mob's own record, indexed by the abstate's resist type.
    /// Strict less-than, so 0 never resists and 1000 always does.</summary>
    [Fact]
    public void AMobResistsFromItsOwnTwelveSlotTable()
    {
        int[] table = [0, 250, 500, 0, 0, 0, 0, 0, 0, 0, 0, 1000];

        AbstateResistance.MobResists(table, resistType: 2, draw: 249).ShouldBeTrue();
        AbstateResistance.MobResists(table, resistType: 2, draw: 250).ShouldBeFalse();
        AbstateResistance.MobResists(table, resistType: 1, draw: 0).ShouldBeFalse();      // 0 never resists
        AbstateResistance.MobResists(table, resistType: 12, draw: 999).ShouldBeTrue();    // 1000 always does
    }

    /// <summary>A type outside 1..12 resists nothing — including 0, which the `test edx,edx` rejects
    /// before the range check ever runs. And a mob with no resist record (`0xFFFF`) resists nothing, which
    /// is an empty table here rather than a magic number.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    public void AnOutOfRangeResistTypeResistsNothing(int type)
        => AbstateResistance.MobResists([1000, 1000, 1000], type, draw: 0).ShouldBeFalse();

    [Fact]
    public void AMobWithNoResistRecordResistsNothing()
        => AbstateResistance.MobResists([], resistType: 2, draw: 0).ShouldBeFalse();

    /// <summary>A player's resistance is `Item.Rate[slot] + AbnormalState.Rate[slot]` — SUMMED, not
    /// multiplied, which is why those slots are seeded 0 rather than 1000.</summary>
    [Fact]
    public void APlayerSumsGearAndBuffResistance()
    {
        var p = new ParameterContainer();
        AbstateResistance.PlayerResistPermille(p, Stat.ResistPoison).ShouldBe(0);

        p.Rate(StatModifier.Item)[Stat.ResistPoison] = 120;            // gear
        p.Rate(StatModifier.AbnormalState)[Stat.ResistPoison] = 200;   // a buff
        AbstateResistance.PlayerResistPermille(p, Stat.ResistPoison).ShouldBe(320);

        AbstateResistance.PlayerResists(p, Stat.ResistPoison, draw: 319).ShouldBeTrue();
        AbstateResistance.PlayerResists(p, Stat.ResistPoison, draw: 320).ShouldBeFalse();
    }

    /// <summary>The property that makes the sum work: every slot `debuffresist` can point at is inside the
    /// rate-eraser's zero run, so an unbuffed player really has 0 resistance rather than 1000.
    ///
    /// <para>This is the second independent case of "additive rate slot, zero identity", after
    /// `CriticalTB` in `roe_CriticalRate` — and it is what makes `CriDamRate`'s 1000 seed an anomaly with
    /// corroboration against it rather than a lone puzzle. See `FUTURE_TESTS.md`.</para></summary>
    [Fact]
    public void EveryResistSlotIsZeroSeededInTheRateHalf()
    {
        var p = new ParameterContainer();
        foreach (var slot in AbstateResistance.ResistSlots)
        {
            ParameterCluster.RateErasedSlots.ShouldContain(slot);
            p.Rate(StatModifier.Item)[slot].ShouldBe(0);
            p.Rate(StatModifier.AbnormalState)[slot].ShouldBe(0);
        }
    }
}

/// <summary>`SubAbstatePriority::bp_AbStateChange` — what happens when a state meets one already applied.
///
/// <para>This is the rule `asl_AbstateSet` consults before touching the list, and it is richer than the
/// "re-applying replaces" the wire suggests: a WEAKER or EQUAL re-application is declined outright, so the
/// existing element keeps its remaining keeptime.</para></summary>
public class SubAbstatePriorityTests
{
    private static (SubAbstateAction, int)[] Row(params (SubAbstateAction, int)[] a) => a;

    /// <summary>Any difference in the ACTION indices means the two are unrelated and both stay — checked
    /// before any argument comparison.</summary>
    [Fact]
    public void DifferentActionsAreUnrelated()
        => SubAbstatePriority.AbStateChange(
               Row((SubAbstateAction.SAA_WCRATE, 200)),
               Row((SubAbstateAction.SAA_ACRATE, 200)))
           .ShouldBe(StateExchange.SAP_NORELATION);

    /// <summary>Same actions, bigger argument — the existing state VANISHES and the new one lands. This is
    /// a higher rank of the same buff replacing a lower one.</summary>
    [Fact]
    public void AStrongerRankMakesTheExistingStateVanish()
        => SubAbstatePriority.AbStateChange(
               Row((SubAbstateAction.SAA_WCRATE, 251)),
               Row((SubAbstateAction.SAA_WCRATE, 203)))
           .ShouldBe(StateExchange.SAP_VANISH);

    /// <summary>Same actions, smaller argument — SUBSCRIPT: the incoming one is subordinate and does not
    /// displace what is there.</summary>
    [Fact]
    public void AWeakerRankIsSubordinate()
        => SubAbstatePriority.AbStateChange(
               Row((SubAbstateAction.SAA_WCRATE, 203)),
               Row((SubAbstateAction.SAA_WCRATE, 251)))
           .ShouldBe(StateExchange.SAP_SUBSCRIPT);

    /// <summary>⚠️ IDENTICAL rows are SUBSCRIPT, not VANISH — re-casting the same buff does NOT refresh it.
    ///
    /// <para>This is the case the wire could never show, because it is precisely the one the server
    /// re-sends constantly and declines to act on. Only an all-zero row falls through to VANISH.</para></summary>
    [Fact]
    public void RecastingTheSameRankDoesNotRefreshIt()
    {
        SubAbstatePriority.AbStateChange(
            Row((SubAbstateAction.SAA_WCRATE, 203)),
            Row((SubAbstateAction.SAA_WCRATE, 203)))
        .ShouldBe(StateExchange.SAP_SUBSCRIPT);

        SubAbstatePriority.AbStateChange(
            Row((SubAbstateAction.SAA_NOMOVE, 0)),
            Row((SubAbstateAction.SAA_NOMOVE, 0)))
        .ShouldBe(StateExchange.SAP_VANISH);
    }

    /// <summary>Rows are compared slot by slot over all four, and a row with fewer actions is padded with
    /// `SAA_NONE`/0 — which is how the server stores it.</summary>
    [Fact]
    public void ShorterRowsArePaddedNotTruncated()
        => SubAbstatePriority.AbStateChange(
               Row((SubAbstateAction.SAA_WCRATE, 200)),
               Row((SubAbstateAction.SAA_WCRATE, 200), (SubAbstateAction.SAA_ACRATE, 50)))
           .ShouldBe(StateExchange.SAP_NORELATION);

    // ---- and the list applying it ------------------------------------------------------------------

    private static AbstateListInObject ListWith(int id, params (SubAbstateAction, int)[] actions)
    {
        var l = new AbstateListInObject();
        l.SetWithPriority(id, 1, 60_000, 0, actions, _ => []);
        return l;
    }

    /// <summary>A stronger application displaces the weaker one and REPORTS it — the returned elements are
    /// `ABSTATERESET`s the server owes every client.</summary>
    [Fact]
    public void TheStrongerApplicationDisplacesAndReportsTheWeakerOne()
    {
        var weak = Row((SubAbstateAction.SAA_WCRATE, 203));
        var list = ListWith(5, weak);

        var displaced = list.SetWithPriority(5, 6, 60_000, 1_000,
            Row((SubAbstateAction.SAA_WCRATE, 251)), _ => weak);

        displaced.Count.ShouldBe(1);
        displaced[0].Strength.ShouldBe(1);
        list.Active.Single().Strength.ShouldBe(6);
    }

    /// <summary>A weaker application is DECLINED: nothing is added, nothing is displaced, and the existing
    /// element keeps the keeptime it already had rather than being refreshed.</summary>
    [Fact]
    public void AWeakerApplicationIsDeclinedAndRefreshesNothing()
    {
        var strong = Row((SubAbstateAction.SAA_WCRATE, 251));
        var list = ListWith(5, strong);

        var displaced = list.SetWithPriority(5, 1, 60_000, 30_000,
            Row((SubAbstateAction.SAA_WCRATE, 203)), _ => strong);

        displaced.ShouldBeEmpty();
        list.Active.Count.ShouldBe(1);
        list.Active.Single().Strength.ShouldBe(1);           // the ORIGINAL element
        list.Active.Single().RestTimeMs(30_000).ShouldBe(30_000);   // not refreshed
    }

    /// <summary>An unrelated state coexists — the verdict is per existing state, not per abstate id,
    /// because the rule compares resolved ACTIONS.</summary>
    [Fact]
    public void AnUnrelatedStateCoexists()
    {
        var wc = Row((SubAbstateAction.SAA_WCRATE, 203));
        var list = ListWith(5, wc);

        list.SetWithPriority(7, 1, 60_000, 0, Row((SubAbstateAction.SAA_ACMINUS, 78)), _ => wc)
            .ShouldBeEmpty();
        list.Active.Count.ShouldBe(2);
    }
}
