using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ THE COMBAT LOG — one row per incoming hit, carrying every cooldown the character had.
///
/// <para>Asked for directly by the operator, and it earned itself immediately: a level-25 Warrior dying in
/// Marlone Clan's Hideout with <b>80 HP stones unused</b> reads as a bot that forgot to heal. The log shows
/// it moving on 3 of 48 hits at a median attacker range of <b>0 units</b> — it never kited, in an area
/// where kiting is the intended answer.</para></summary>
public class CombatLogTests
{
    private static CombatSimulation Fighting()
    {
        var sim = new CombatSimulation(seed: 1) { CombatLog = new CombatLog() };
        sim.Player.MaxHp = sim.Player.Hp = 1000;
        sim.Player.HpStones = 5;
        sim.Player.HpStoneRestore = 100;
        var mob = sim.AddMob(10, 0, 0, m => m.Hp = m.MaxHp = 100_000);
        mob.AttackDamage = 25;
        mob.Arg.Target = sim.Player;
        return sim;
    }

    /// <summary>A hit produces a row, and the row carries the state at the moment it landed.</summary>
    [Fact]
    public void EveryIncomingHitBecomesARow()
    {
        var sim = Fighting();
        for (var i = 0; i < 200 && sim.CombatLog!.Entries.Count == 0; i++) sim.Tick();

        sim.CombatLog!.Entries.ShouldNotBeEmpty("the mob should have connected");
        var hit = sim.CombatLog.Entries[0];
        hit.Damage.ShouldBeGreaterThan(0);
        hit.MaxHp.ShouldBe(1000);
        hit.HpStones.ShouldBe(5);
        hit.Format().ShouldContain("hpStone");
    }

    /// <summary>⚠️ -1 IS "NOT WALKING", AND IT IS NOT A BAD KITE. Conflating "did not kite" with "kited
    /// somewhere worse" would make a bot that stands still look like a bot that flees into a pack — the
    /// two need opposite fixes.</summary>
    [Fact]
    public void NotWalkingIsNotAKiteIntoACrowd()
    {
        var standing = new CombatLogEntry(0, "m", 1, 10, 900, 1000, 0, 5, false, false, true, 1,
                                          5, 0, 5, 0, MobsNearby: 3, MobsNearWalkTarget: -1, []);
        standing.KitingIntoACrowd.ShouldBeFalse();
        standing.Format().ShouldContain("crowd  3");

        var away = standing with { Walking = true, MobsNearWalkTarget = 1 };
        away.KitingIntoACrowd.ShouldBeFalse("fewer mobs at the destination is a GOOD kite");

        var into = standing with { Walking = true, MobsNearWalkTarget = 7 };
        into.KitingIntoACrowd.ShouldBeTrue();
        into.Format().ShouldContain("3->7");
    }

    /// <summary>A skill counts as usable only when it is BOTH off cooldown and affordable — "every skill
    /// was ready" means nothing if the character had no SP for any of them.</summary>
    [Fact]
    public void ASkillIsUsableOnlyWhenItIsReadyAndAffordable()
    {
        new SkillReadiness(1, "A", 0, true).Usable.ShouldBeTrue();
        new SkillReadiness(1, "A", 500, true).Usable.ShouldBeFalse("still cooling");
        new SkillReadiness(1, "A", 0, false).Usable.ShouldBeFalse("cannot afford it");
    }

    /// <summary>The summary is the thing that gets read; it must survive an empty log rather than throw.</summary>
    [Fact]
    public void AnEmptyLogSummarisesRatherThanThrowing()
        => new CombatLog().Summarise().ShouldBe("no incoming hits");

    /// <summary>Oldest rows are dropped, because a death is explained by the hits just before it.</summary>
    [Fact]
    public void TheLogKeepsTheMostRecentHits()
    {
        var log = new CombatLog { Capacity = 3 };
        for (uint i = 0; i < 10; i++)
            log.Add(new CombatLogEntry(i, "m", 1, 1, 1, 1, 0, 0, false, false, false, 0, 0, 0, 0, 0, 0, -1, []));

        log.Entries.Count.ShouldBe(3);
        log.Entries[^1].At.ShouldBe(9u, "the newest hit must survive");
    }
}
