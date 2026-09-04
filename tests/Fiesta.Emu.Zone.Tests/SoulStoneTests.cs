using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>HP soul stones — the first healing the simulation models, and the thing that makes survival
/// evaluable at all.
///
/// <para>⚠️ Before this, `BotSimIntegrationTests` had to hand its character <b>2,000,000 HP</b> to keep it
/// standing, which makes every flee, heal and stone decision in `level_quest.lua` untestable and
/// `sustainableHealDps` a permanent -1. A bot cannot be judged on staying alive in a world where it cannot
/// die and cannot heal.</para></summary>
public class SoulStoneTests
{
    private static CombatSimulation Stocked(int charges = 10, int restore = 300)
    {
        var sim = new CombatSimulation();
        sim.Player.MaxHp = 1000;
        sim.Player.Hp = 400;
        sim.Player.HpStones = charges;
        sim.Player.MaxHpStones = 10;
        sim.Player.HpStoneRestore = restore;
        return sim;
    }

    [Fact]
    public void AChargeHealsAndIsSpent()
    {
        var sim = Stocked();

        sim.Api.soulstoneHp().ShouldBeTrue();
        sim.Player.Hp.ShouldBe(700);
        sim.Api.hpStones().ShouldBe(9);
    }

    /// <summary>Healing never overshoots the maximum.</summary>
    [Fact]
    public void AChargeDoesNotHealPastFull()
    {
        var sim = Stocked(restore: 900);
        sim.Api.soulstoneHp();

        sim.Player.Hp.ShouldBe(1000);
    }

    /// <summary>The cooldown is real: a second charge in the same instant is refused, and refusing is NOT
    /// the same as being empty.</summary>
    [Fact]
    public void ASecondChargeWaitsForTheCooldown()
    {
        var sim = Stocked();
        sim.Api.soulstoneHp().ShouldBeTrue();

        sim.Api.soulstoneHp().ShouldBeFalse();
        sim.Api.hpStones().ShouldBe(9, "a refused use must not consume a charge");
        sim.Api.hpStoneDepleted().ShouldBeFalse("on cooldown is not empty");
        sim.Api.hpStoneReadyInMs().ShouldBeGreaterThan(0);
    }

    /// <summary>⭐ AN EMPTY RESERVE SETS THE FLAG THE DRIVER GATES ON. Live, a failed use is learned from a
    /// `USEFAIL` or from no acknowledgement arriving at all — the absence IS the signal. Returning true
    /// and doing nothing would tell the bot it had healed.</summary>
    [Fact]
    public void AnEmptyReserveFailsAndSetsDepleted()
    {
        var sim = Stocked(charges: 0);

        sim.Api.soulstoneHp().ShouldBeFalse();
        sim.Api.hpStoneDepleted().ShouldBeTrue();
        sim.Player.Hp.ShouldBe(400, "a failed use heals nothing");
    }

    /// <summary>⚠️ -1 IS "NOT LEARNED YET", AND IT IS LOAD-BEARING. `level_quest.lua`'s `outmatched()`
    /// says so in as many words — "Both must be MEASURED. -1 is 'not learned yet' (never 0-as-sentinel);
    /// an unknown must not fake a verdict." A 0 here tells it healing keeps up with nothing, and it never
    /// flees.</summary>
    [Fact]
    public void SustainableHealDpsIsMinusOneUntilTheRestoreAmountIsKnown()
    {
        var sim = Stocked(restore: 0);

        sim.Api.sustainableHealDps().ShouldBe(-1);
    }

    [Fact]
    public void SustainableHealDpsIsOneChargePerCooldown()
    {
        var sim = Stocked(restore: 300);
        sim.Player.HpStoneCooldownMs = 6000;

        sim.Api.sustainableHealDps().ShouldBe(50, 0.001);   // 300 hp / 6 s
    }

    /// <summary>...and an empty reserve is also -1 rather than 0: "I have no healing" and "I have not
    /// learned my healing" are different states, and only the second may stop the driver judging.</summary>
    [Fact]
    public void SustainableHealDpsIsMinusOneWhenTheReserveIsEmpty()
    {
        var sim = Stocked(charges: 0);

        sim.Api.sustainableHealDps().ShouldBe(-1);
    }

    /// <summary>⭐ THE POINT OF ALL OF IT: with stones modelled, a character can be given its REAL maximum
    /// HP and the question "does it survive" becomes answerable.
    ///
    /// <para>Ten charges of 300 against 1,000 max HP is 3,000 HP of healing on a 5-second cooldown — a
    /// sustained 60 dps, which is what `outmatched()` needs on the other side of `incomingDps` before it
    /// can decide anything at all.</para></summary>
    [Fact]
    public void AStockedCharacterHasARealHealDpsToWeighAgainstIncomingDamage()
    {
        var sim = Stocked();

        sim.Api.sustainableHealDps().ShouldBe(60, 0.001);
        sim.Api.incomingDps(5000).ShouldBe(-1, "nothing has hit us yet, and that is not zero damage");
    }

    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "World", "ParamEnchanterServer.txt")) ? root : null;
    }

    /// <summary>⭐ THE RESERVE IS GAME DATA, NOT A FIXTURE CONSTANT. `SoulHP` / `MAXSoulHP` and their SP
    /// twins are columns of the class parameter table, per class per level, so `Become` fills them the
    /// same way it fills MaxHp.
    ///
    /// <para>Which column is the restore and which the capacity is read off the data rather than assumed:
    /// at level 1 an Enchanter has <c>SoulHP 29</c> against a <c>MaxHP</c> of 42, at level 60
    /// <c>SoulHP 872</c> against 1245. The first tracks the HP pool across 60 levels and `MAXSoulHP`
    /// (12 → 170) plainly does not.</para></summary>
    [SkippableTheory]
    [InlineData(1, 29, 12, 37, 15)]
    [InlineData(60, 872, 170, 1460, 249)]
    public void StoneReservesComeFromTheClassTable(int level, int hpRestore, int hpMax, int spRestore, int spMax)
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var enchanter = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamEnchanterServer.txt"));
        var sim = new CombatSimulation();
        sim.Player.Become(enchanter, level);

        sim.Api.hpStoneRestore().ShouldBe(hpRestore);
        sim.Api.maxHpStones().ShouldBe(hpMax);
        sim.Api.spStoneRestore().ShouldBe(spRestore);
        sim.Api.maxSpStones().ShouldBe(spMax);

        sim.Api.hpStones().ShouldBe(hpMax, "a character built by Become starts restocked");
        sim.Api.hpStoneDepleted().ShouldBeFalse();
        sim.Api.sustainableHealDps().ShouldBeGreaterThan(0, "a stocked character has measurable healing");
    }

    /// <summary>⚠️ AND THE RESTORE IS A REAL FRACTION OF THE POOL — a soul stone is close to a full heal,
    /// which is exactly why a bot that does not model it cannot be judged on survival.</summary>
    [SkippableFact]
    public void OneChargeIsMostOfTheHpPool()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var enchanter = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamEnchanterServer.txt"));
        var sim = new CombatSimulation();
        sim.Player.Become(enchanter, 60);

        // 872 of a 1245 pool.
        ((double)sim.Player.HpStoneRestore / sim.Player.MaxHp).ShouldBeInRange(0.6, 0.8);
    }
}
