using Fiesta.Emu.Zone.Abstate;
using Fiesta.Emu.Zone.Skill;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`SKILL_EMPOWER` and the per-skill table it indexes.</summary>
public class SkillEmpowerTests
{
    /// <summary>Four 4-bit fields in one word, in the PDB's declared order.</summary>
    [Fact]
    public void TheWordUnpacksIntoFourNibbles()
    {
        var e = new SkillEmpower(0x4321);
        e.Damage.ShouldBe(1);
        e.Sp.ShouldBe(2);
        e.KeepTime.ShouldBe(3);
        e.CoolTime.ShouldBe(4);

        SkillEmpower.From(damage: 15, sp: 1, keepTime: 0, coolTime: 9).Raw.ShouldBe((ushort)0x901F);
        SkillEmpower.None.Damage.ShouldBe(0);
    }

    /// <summary>`nT0`..`nT3` are four contiguous <c>ulong[5]</c> and the engine walks them as ONE flat run
    /// of twenty — <c>[sdi_Activ + level*4 + 0x1BB]</c> is <c>nT0[level-1]</c> at level 1 and keeps going
    /// straight across the array boundaries.
    ///
    /// <para>Reading them as four separate five-entry tables would give the wrong value for every level
    /// above 5, which is the whole reason this is a test rather than a comment.</para></summary>
    [Fact]
    public void TheEmpowerLevelWalksAcrossAllFourArrays()
    {
        var table = SkillEmpowerTable.FromArrays(
            nT0: [10, 11, 12, 13, 14],
            nT1: [20, 21, 22, 23, 24],
            nT2: [30, 31, 32, 33, 34],
            nT3: [40, 41, 42, 43, 44]);

        table.DamageTerm(SkillEmpower.From(damage: 1)).ShouldBe(10u);    // nT0[0]
        table.DamageTerm(SkillEmpower.From(damage: 5)).ShouldBe(14u);    // nT0[4]
        table.DamageTerm(SkillEmpower.From(damage: 6)).ShouldBe(20u);    // nT1[0] -- across the boundary
        table.DamageTerm(SkillEmpower.From(damage: 15)).ShouldBe(34u);   // nT2[4]
    }

    /// <summary>A damage allocation of 0 short-circuits before the lookup — the original tests it
    /// explicitly. Zero is a real allocation, not a marker.</summary>
    [Fact]
    public void NoDamagePointsMeansNoTermAtAll()
    {
        var table = SkillEmpowerTable.FromArrays([999, 0, 0, 0, 0], [], [], []);
        table.DamageTerm(SkillEmpower.None).ShouldBe(0u);
        table.DamageTerm(SkillEmpower.From(damage: 0, sp: 15, keepTime: 15)).ShouldBe(0u);
    }
}

/// <summary>`MiscDataTable::mdt_ArgumentLoad` — and the thing about it that matters: the skill damage rate
/// is CONDITIONAL on the defender's state, not a property of the skill.</summary>
public class MiscDataTableTests
{
    private static SubStateFacts Facts(int type, params SubAbstateAction[] actions)
        => new(type, actions);

    private static AbstateListInObject WithOne(int id = 1)
    {
        var l = new AbstateListInObject();
        l.Set(id, strength: 1, restKeeptimeMs: 10_000, nowMs: 0);
        return l;
    }

    /// <summary>The row fires only when the DEFENDER satisfies its condition. Against an unafflicted
    /// target a skill leaves `damagerate` and `crirateadd` at their 1000/0 defaults, exactly like a normal
    /// swing.</summary>
    [Fact]
    public void TheRowOnlyAppliesWhenTheDefenderSatisfiesTheCondition()
    {
        var table = new MiscDataTable([new(Skill: 42, AbnormalStateAttr.SLOW,
                                           DamageRate: 1500, NewState: 0x400, CriRate: 200)]);
        var states = WithOne();

        table.ArgumentLoad(42, states, _ => Facts(0)).ShouldBeNull();

        var row = table.ArgumentLoad(42, states, _ => Facts(0, SubAbstateAction.SAA_SPEEDDOWNRATE));
        row.ShouldNotBeNull();
        row!.DamageRate.ShouldBe(1500);
        row.CriRate.ShouldBe(200);
    }

    /// <summary>A skill with no row is not a skill with a neutral row — null keeps `damagerate` at 1000,
    /// whereas a row carrying 0 would set it to 0 and zero the damage. Both are real.</summary>
    [Fact]
    public void ASkillWithNoRowIsNotTheSameAsARowOfZero()
    {
        var table = new MiscDataTable([new(7, AbnormalStateAttr.STUN, DamageRate: 0, NewState: 0, CriRate: 0)]);
        var states = WithOne();

        table.ArgumentLoad(99, states, _ => Facts(0x15)).ShouldBeNull();
        table.ArgumentLoad(7, states, _ => Facts(0x15))!.DamageRate.ShouldBe(0);
    }

    /// <summary>STUN is the sub-state TYPE being 0x15 — the same value that makes `SAA_NOMOVE` set
    /// `cannotmove_stun` rather than entangle. Not an action, and not the presence of `SAA_NOATTACK`.</summary>
    [Theory]
    [InlineData(0x15, true)]
    [InlineData(0x60, false)]
    [InlineData(0x00, false)]
    public void StunIsTheTypeByteAndOnlyThatOneValue(int type, bool expected)
        => MiscDataTable.Satisfies(AbnormalStateAttr.STUN, WithOne(),
                                   _ => Facts(type, SubAbstateAction.SAA_NOATTACK)).ShouldBe(expected);

    /// <summary>ACMRMINUS covers armour OR magic resistance, by plus OR by rate — four actions behind one
    /// attribute.</summary>
    [Theory]
    [InlineData(SubAbstateAction.SAA_ACMINUS)]
    [InlineData(SubAbstateAction.SAA_ACDOWNRATE)]
    [InlineData(SubAbstateAction.SAA_MRMINUS)]
    [InlineData(SubAbstateAction.SAA_MRDOWNRATE)]
    public void AcMrMinusMatchesAnyOfItsFourActions(SubAbstateAction action)
        => MiscDataTable.Satisfies(AbnormalStateAttr.ACMRMINUS, WithOne(), _ => Facts(0, action))
                        .ShouldBeTrue();

    /// <summary>A near miss: `SAA_ACPLUS` is a BUFF and must not satisfy the debuff condition.</summary>
    [Fact]
    public void AnArmourBuffDoesNotSatisfyTheArmourDebuffCondition()
        => MiscDataTable.Satisfies(AbnormalStateAttr.ACMRMINUS, WithOne(),
                                   _ => Facts(0, SubAbstateAction.SAA_ACPLUS)).ShouldBeFalse();

    /// <summary>NONE satisfies nothing, so a row keyed on it never fires.</summary>
    [Fact]
    public void TheNoneAttributeNeverMatches()
        => MiscDataTable.Satisfies(AbnormalStateAttr.NONE, WithOne(),
                                   _ => Facts(0x15, SubAbstateAction.SAA_SPEEDDOWNRATE)).ShouldBeFalse();

    /// <summary>`mdvba_NewState` is applied only below 0x318 — the bound at `mdt_ArgumentLoad+0xFE`.</summary>
    [Theory]
    [InlineData(0x317, 0x317)]
    [InlineData(0x318, null)]
    [InlineData(0x999, null)]
    public void TheAppliedStateIsBoundedAtSevenNinetyTwo(int newState, int? expected)
        => MiscDataTable.StateToApply(new(1, AbnormalStateAttr.STUN, 1000, newState, 0)).ShouldBe(expected);
}
