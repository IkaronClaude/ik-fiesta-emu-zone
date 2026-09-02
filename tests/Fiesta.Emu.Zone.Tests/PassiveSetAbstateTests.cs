using Fiesta.Emu.Zone.Abstate;
using Fiesta.Emu.Zone.Skill;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`CharacterPassiveList::cpl_SetAbstate` (0x00446A10) — the passive half of abstate
/// application.</summary>
public class PassiveSetAbstateTests
{
    private static PSkillSetAbstate Row(PassiveSetAbstateCondition condition, int rate = 1000)
        => new("PsCbowKnockback", condition, rate, "StaCommonStun02", Strength: 1);

    /// <summary>Exactly four conditions exist. `cpl_SetAbstate` rejects anything ≥ 4 before it looks at a
    /// single row, and dispatches the rest through a four-entry jump table — so this is the complete set
    /// of hooks passives have into the abstate system, not a sample.</summary>
    [Fact]
    public void ThereAreExactlyFourConditions()
    {
        PassiveSetAbstate.ConditionCount.ShouldBe(4);
        Enum.GetValues<PassiveSetAbstateCondition>().Length.ShouldBe(4);
    }

    /// <summary>⭐ Only ONE condition has a weapon gate, and it is the one whose name says so:
    /// `PS_CBOWATKRATEKNOCKBACK` checks the source's `ItemInfo.WeaponType` against `WT_CROSSBOW` (10).
    /// The name and the check agreeing is what confirmed the reading.</summary>
    [Theory]
    [InlineData(10, true)]    // WT_CROSSBOW
    [InlineData(2, false)]    // WT_BOW -- close, and not it
    [InlineData(1, false)]    // WT_SWORD
    [InlineData(null, false)] // unarmed or unknown: the original reaches the same place via a null lookup
    public void TheCrossbowConditionChecksTheWeapon(int? weaponType, bool opens)
        => PassiveSetAbstate.GateOpens(Row(PassiveSetAbstateCondition.PS_CBOWATKRATEKNOCKBACK),
                PassiveSetAbstateCondition.PS_CBOWATKRATEKNOCKBACK, flag: true, weaponType)
            .ShouldBe(opens);

    /// <summary>Two of the four conditions require the caller's boolean and two ignore it entirely —
    /// worth pinning rather than treating as a uniform precondition.</summary>
    [Theory]
    [InlineData(PassiveSetAbstateCondition.PS_CBOWATKRATEKNOCKBACK, true)]
    [InlineData(PassiveSetAbstateCondition.PS_MEDMGMISSCRIUPRATE, true)]
    [InlineData(PassiveSetAbstateCondition.PS_SKILLSPUSEDOWN, false)]
    [InlineData(PassiveSetAbstateCondition.PS_AREAENEMYSPDOWN, false)]
    public void OnlyTwoConditionsConsultTheCallersFlag(PassiveSetAbstateCondition condition,
                                                       bool needsFlag)
    {
        var withFlag = PassiveSetAbstate.GateOpens(Row(condition), condition, true,
                                                   PassiveSetAbstate.CrossbowWeaponType);
        var without = PassiveSetAbstate.GateOpens(Row(condition), condition, false,
                                                  PassiveSetAbstate.CrossbowWeaponType);
        withFlag.ShouldBeTrue();
        without.ShouldBe(!needsFlag);
    }

    /// <summary>A row only fires on its OWN condition — the loop tests `row.PS_Condition != condition`
    /// first and skips.</summary>
    [Fact]
    public void ARowIgnoresEveryConditionButItsOwn()
        => PassiveSetAbstate.GateOpens(Row(PassiveSetAbstateCondition.PS_SKILLSPUSEDOWN),
                PassiveSetAbstateCondition.PS_AREAENEMYSPDOWN, true, null)
            .ShouldBeFalse();

    /// <summary>`cmp word [row+0x24], ax; jbe skip` — STRICTLY greater than the draw. So a rate of 0 can
    /// never fire however low the draw goes, and a rate of 1000 always does.</summary>
    [Theory]
    [InlineData(0, 0, false)]      // 0 is a real rate meaning never, not "unset"
    [InlineData(1, 0, true)]
    [InlineData(500, 499, true)]
    [InlineData(500, 500, false)]  // equal does NOT fire
    [InlineData(1000, 999, true)]
    public void TheRateMustBeatTheDrawStrictly(int rate, int draw, bool fires)
        => PassiveSetAbstate.RatePasses(Row(PassiveSetAbstateCondition.PS_SKILLSPUSEDOWN, rate), draw)
            .ShouldBe(fires);

    /// <summary>⭐ The rate is per ROW, not per skill, and the loop does not stop at the first match — so
    /// two rows on the same condition take two independent rolls and both can fire.</summary>
    [Fact]
    public void EveryMatchingRowRollsIndependently()
    {
        const PassiveSetAbstateCondition c = PassiveSetAbstateCondition.PS_SKILLSPUSEDOWN;
        List<PSkillSetAbstate> list =
        [
            Row(c, rate: 600) with { InxName = "first" },
            Row(PassiveSetAbstateCondition.PS_AREAENEMYSPDOWN, rate: 1000) with { InxName = "other" },
            Row(c, rate: 300) with { InxName = "second" },
        ];

        // Draws are consumed only by rows that pass the gate, so "other" takes none.
        var draws = new List<int> { 100, 999 }.GetEnumerator();
        PassiveSetAbstate.Firing(list, c, flag: false, sourceWeaponType: null, draws)
            .Select(r => r.InxName).ShouldBe(["first"]);

        var both = new List<int> { 100, 200 }.GetEnumerator();
        PassiveSetAbstate.Firing(list, c, flag: false, sourceWeaponType: null, both)
            .Select(r => r.InxName).ShouldBe(["first", "second"]);
    }

    /// <summary>⚠️ The crossbow arm is reachable code with NO DATA behind it: all 16 rows of the shipped
    /// `PSkillSetAbstate.shn` use conditions 1, 2 and 3. The gate is modelled because the binary has it,
    /// and this test exists so nobody later reports it as a live mechanic.
    ///
    /// <para>The shipped rows, for reference: condition 1 = `MagicDance01`..`04` + `PointAttack01`..`04`;
    /// condition 2 = `DeepFear01`..`04`; condition 3 = `Shame01`..`04`. Every rate is 1000, which with the
    /// strict-greater rule always fires.</para></summary>
    [Fact]
    public void NoShippedRowUsesTheCrossbowCondition()
    {
        // The shipped table, by condition. Kept as the counts rather than the whole table: the point is
        // which conditions have data, not what each row applies.
        var shipped = new Dictionary<PassiveSetAbstateCondition, int>
        {
            [PassiveSetAbstateCondition.PS_CBOWATKRATEKNOCKBACK] = 0,
            [PassiveSetAbstateCondition.PS_SKILLSPUSEDOWN] = 8,
            [PassiveSetAbstateCondition.PS_AREAENEMYSPDOWN] = 4,
            [PassiveSetAbstateCondition.PS_MEDMGMISSCRIUPRATE] = 4,
        };

        shipped.Values.Sum().ShouldBe(16);
        shipped[PassiveSetAbstateCondition.PS_CBOWATKRATEKNOCKBACK]
            .ShouldBe(0, "the weapon gate never runs on this data set");
    }

    /// <summary>`mdt_ArgumentLoad`'s abstate half. The row carries no strength field, so a skill's
    /// misc-data abstate is always rank 1 — and the bound on the index is `jge 0x318`, so 0x318 itself
    /// applies nothing.</summary>
    [Theory]
    [InlineData(0x100, 0x100)]
    [InlineData(0x317, 0x317)]
    [InlineData(0x318, null)]
    [InlineData(0x400, null)]
    public void TheMiscDataRowAppliesItsStateOnlyBelowTheLimit(int newState, int? expected)
        => MiscDataTable.StateToApply(new MiscDataVarifyByAbstate(
                Skill: 1, Condition: AbnormalStateAttr.NONE, DamageRate: 1000,
                NewState: newState, CriRate: 0))
            .ShouldBe(expected);

    /// <summary>Always rank 1, hard-coded in the call rather than taken from the row.</summary>
    [Fact]
    public void TheMiscDataAbstateIsAlwaysRankOne()
        => MiscDataTable.AppliedStrength.ShouldBe(1);
}
