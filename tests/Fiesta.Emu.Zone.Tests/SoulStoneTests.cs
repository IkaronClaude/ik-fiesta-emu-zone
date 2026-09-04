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

    /// <summary>⭐ SEVEN SECONDS, FROM THE BINARY. `ShinePlayer::sp_HPStoneUse` (0x00589180) does
    /// <c>add eax, 0x46</c> — 70 — onto the clockwatch counter at `[0x14D41A70]`, which counts TENTHS of a
    /// second (docs/TICKRATE.md). `sp_SPStoneUse` is the same function shape with the same constants.
    ///
    /// <para>⚠️ It was 5,000 ms, invented, with a doc line that admitted the value had never been read.
    /// That is a 40% overstatement of sustained healing — 91 dps against the true 65 — which is exactly the
    /// margin that decides whether a fight can be won standing still, and it made a driver that never
    /// kites look closer to viable than it is.</para></summary>
    [Fact]
    public void TheCooldownIsSevenSecondsBecauseTheBinarySaysSeventyTenths()
    {
        var sim = Stocked();

        sim.Player.HpStoneCooldownMs.ShouldBe(7000u);
        sim.Player.SpStoneCooldownMs.ShouldBe(7000u);
        sim.Player.StoneQueueWindowMs.ShouldBe(5000u);
    }

    /// <summary>⭐ THE QUEUE WINDOW — a press between 5 and 7 seconds is ACCEPTED and fires when the
    /// cooldown expires; before 5 seconds it is refused.
    ///
    /// <para>`sp_NC_SOULSTONE_HP_USE_REQ` reads both timestamps: <c>[+0x173B4] &lt;= now</c> uses it,
    /// <c>[+0x173B8] &gt; now</c> rejects, and anything between parks a deferred action at
    /// <c>[+0x173BC]</c>. Modelling one cooldown would have missed this entirely — and it matters, because
    /// a bot may pre-press and get its charge at the earliest legal instant.</para></summary>
    [Fact]
    public void APressInsideTheQueueWindowIsAcceptedAndFiresLater()
    {
        var sim = Stocked(restore: 300);
        sim.Player.Hp = 100;
        sim.Api.soulstoneHp().ShouldBeTrue();
        var afterFirst = sim.Player.Hp;
        sim.Player.HpStones.ShouldBe(9);

        // Too early: refused outright, nothing queued.
        while (sim.Now < 4000) sim.Tick();
        sim.Api.soulstoneHp().ShouldBeFalse("inside the first 5 seconds the server rejects the request");
        sim.Player.HpStoneUseQueued.ShouldBeFalse();

        // Inside the window: accepted, but nothing has happened yet.
        while (sim.Now < 5500) sim.Tick();
        sim.Api.soulstoneHp().ShouldBeTrue("between 5 and 7 seconds the press is queued");
        sim.Player.HpStoneUseQueued.ShouldBeTrue();
        sim.Player.Hp.ShouldBe(afterFirst, "queued is not yet spent");
        sim.Player.HpStones.ShouldBe(9);

        // ...and it fires by itself the moment the cooldown expires.
        while (sim.Now < 7500) sim.Tick();
        sim.Player.HpStoneUseQueued.ShouldBeFalse();
        sim.Player.HpStones.ShouldBe(8);
        sim.Player.Hp.ShouldBeGreaterThan(afterFirst);
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
    /// <para>Ten charges of 300 against 1,000 max HP is 3,000 HP of healing on the server's 7-second
    /// cooldown — a sustained 43 dps, which is what `outmatched()` needs on the other side of
    /// `incomingDps` before it can decide anything at all.</para></summary>
    [Fact]
    public void AStockedCharacterHasARealHealDpsToWeighAgainstIncomingDamage()
    {
        var sim = Stocked();

        // 300 HP per 7-SECOND cooldown. This read 60 while the cooldown was the invented 5,000 ms -- the
        // wrong constant had been baked into an expectation, which is how an invented number survives.
        sim.Api.sustainableHealDps().ShouldBe(300 / 7.0, 0.01);
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
