using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`sm_SkillExchange_*` — when a mob abandons the skill it is currently using.</summary>
public class SkillExchangeTests
{
    private static List<MobWeaponOption> Weapons(params int[] skillIds)
        => [.. skillIds.Select(id => new MobWeaponOption(id, 1000, 0, 0))];

    /// <summary>A rule only fires for the skill it names in `sc_From`, and the FIRST matching rule in
    /// traversal order wins.</summary>
    [Fact]
    public void TheFirstRuleMatchingTheCurrentSkillWins()
    {
        var weapons = Weapons(70, 71, 72, 73);
        List<SkillChange> changes =
        [
            new(From: 99, To: 73),   // not the current skill
            new(From: 71, To: 72),   // matches
            new(From: 71, To: 73),   // also matches, but later
        ];

        SkillExchange.Exchange(changes, currentSkillId: 71, weapons).ShouldBe(2);
        SkillExchange.Exchange(changes, currentSkillId: 99, weapons).ShouldBe(3);
        SkillExchange.Exchange(changes, currentSkillId: 70, weapons).ShouldBeNull();
    }

    /// <summary>⭐ `sc_To` of 0xFFFF is not a skill id — it means DROP TO WEAPON 0, the basic swing. Same
    /// sentinel as the attack sequence's terminator, and for the same reason: it keeps skill id 0
    /// usable.</summary>
    [Fact]
    public void ToFfffMeansDropToTheBasicSwing()
    {
        var weapons = Weapons(70, 71);
        List<SkillChange> changes = [new(From: 71, To: SkillExchange.SwitchToBasicSwing)];

        SkillExchange.Exchange(changes, 71, weapons).ShouldBe(0);
        SkillExchange.SwitchToBasicSwing.ShouldBe(0xFFFF);
    }

    /// <summary>A rule naming a skill the mob has no weapon for is skipped and the walk CONTINUES — the
    /// search starts at index 1, so a rule can never reach the basic swing by naming its skill, only by
    /// the 0xFFFF sentinel.</summary>
    [Fact]
    public void ARuleTargetingTheBasicSwingsSkillFindsNothing()
    {
        var weapons = Weapons(70, 71);
        List<SkillChange> changes = [new(From: 71, To: 70), new(From: 71, To: 71)];

        // The first rule names skill 70, which only weapon 0 has -- not reachable. Falls to the second.
        SkillExchange.Exchange(changes, 71, weapons).ShouldBe(1);
    }

    /// <summary>An inactive node is skipped before its content is dereferenced. `ls_IsActiv` is checked on
    /// the NODE, not on the change.</summary>
    [Fact]
    public void AnInactiveNodeIsSkipped()
    {
        var weapons = Weapons(70, 71, 72);
        List<SkillChange> changes = [new(From: 71, To: 72), new(From: 71, To: 71)];

        SkillExchange.Exchange(changes, 71, weapons, isActive: i => i != 0).ShouldBe(1);
    }

    /// <summary>⭐ `sc_Value` is a PERMILLE OF MAX HP for `HPLow`, not an absolute, and the comparison is
    /// STRICT — a mob exactly at the threshold does not switch.</summary>
    [Theory]
    [InlineData(1000, 10000, 100, false)]  // exactly 10% of max: not below it
    [InlineData(999, 10000, 100, true)]    // just below
    [InlineData(5000, 10000, 500, false)]  // exactly half
    [InlineData(1, 10000, 1000, true)]     // 100% threshold: almost anything is below
    [InlineData(5000, 10000, 0, false)]    // value 0: nothing is below 0
    public void HpLowIsAPermilleOfMaxHpAndStrict(long hp, long maxHp, uint value, bool low)
        => SkillExchange.HpIsLow(hp, maxHp, value).ShouldBe(low);

    /// <summary>The condition gates the swap: a matching `sc_From` whose HP condition does not hold is
    /// passed over and the walk continues.</summary>
    [Fact]
    public void TheHpConditionGatesTheSwap()
    {
        var weapons = Weapons(70, 71, 72, 73);
        List<SkillChange> changes =
        [
            new(From: 71, To: 72, Value: 100),   // fires below 10% hp
            new(From: 71, To: 73, Value: 900),   // fires below 90% hp
        ];

        // At 50% only the second rule's condition holds.
        SkillExchange.OnHpLow(changes, 71, weapons, hp: 5000, maxHp: 10000).ShouldBe(3);
        // At 5% the first one holds and wins on order.
        SkillExchange.OnHpLow(changes, 71, weapons, hp: 500, maxHp: 10000).ShouldBe(2);
        // At full health neither does.
        SkillExchange.OnHpLow(changes, 71, weapons, hp: 10000, maxHp: 10000).ShouldBeNull();
    }
}
