using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>MOVERS - what the game calls mounts, and the word the binary uses (`MoverMain`, `NC_MOVER_*`).
///
/// <para>None of this existed: `bot.mounted` was a hardcoded `false` in the harness registry, the bag was
/// empty so `mountSlot()` always answered "no mount item", and movement ignored any speed but the
/// character's own. Every mount decision in `level_quest.lua` was therefore dead code, which is why the
/// travel half of the driver could not be benched at all.</para></summary>
public class MoverTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var s = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(s, "MoverMain.shn")) ? s : null;
    }

    [SkippableFact]
    public void TheMoverTableReadsWithRealSpeedsAndWindups()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present");
        var cat = MoverCatalog.Load(shine!);

        output.WriteLine($"{cat.Movers.Count} movers");
        foreach (var m in cat.Movers.OrderBy(m => m.RunSpeed).Take(6))
            output.WriteLine($"  {m.Idx,-16} run {m.RunSpeed,5} cast {m.CastingTimeMs,5}ms "
                             + $"cool {m.CoolTimeMs,6}ms item {m.ItemId,6} lv{m.DemandLv}");

        cat.Movers.ShouldNotBeEmpty("MoverMain/MoverItem/ItemInfo did not join");
        // Every mover is FASTER than being on foot, and none is instant.
        cat.Movers.ShouldAllBe(m => m.RunSpeed > 1000);
        cat.Movers.ShouldAllBe(m => m.CastingTimeMs > 0);
        cat.Movers.ShouldAllBe(m => m.ItemId > 0);

        // The speeds genuinely DIFFER, which is the operator's "mounts with different speeds" - a table
        // where they were all equal would read as fine and make the choice meaningless.
        cat.Movers.Select(m => m.RunSpeed).Distinct().Count()
           .ShouldBeGreaterThan(1, "every mover has the same speed; the column is not being read");

        var low = cat.BestFor(1);
        var high = cat.BestFor(60);
        output.WriteLine($"best at  1: {low?.Idx} ({low?.RunSpeed})");
        output.WriteLine($"best at 60: {high?.Idx} ({high?.RunSpeed})");
        low.ShouldNotBeNull();
        // ⚠️ OWNABLE ONLY, and both filters earned their place by watching the answer. Unfiltered, the
        // fastest at level 1 is Dog_Black00 at 2900 permille; filtered only on price it is M_Bunny_1 at
        // 2800, because the cash shop prices its 24-hour rentals at 1. A shop-bought Hobby is 1100.
        low!.BuyPrice.ShouldBeGreaterThan(1, "a cash-shop token price is not a purchase a bot makes");
        low.DurationHourItem.ShouldBe(0, "a rental is not a mover the character owns");
        low.RunSpeed.ShouldBeLessThan(2000, "a level-1 character should not be riding a cash-shop mover");
        high!.RunSpeed.ShouldBeGreaterThanOrEqualTo(low!.RunSpeed, "a higher level should not ride slower");
    }

    /// <summary>Summoning takes its windup, and then the character actually moves faster.</summary>
    [SkippableFact]
    public void SummoningTakesItsWindupAndThenTheCharacterMovesFaster()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present");
        var mover = MoverCatalog.Load(shine!).BestFor(60);
        Skip.If(mover is null, "no mover at level 60");

        var sim = new CombatSimulation(seed: 42);
        sim.Player.MoveSpeed = 130;
        sim.Player.CarriedMover = mover;
        sim.Player.MoverSlot = 0;
        sim.Player.X = sim.Player.Y = 0;

        sim.SummonMover().ShouldBeTrue("nothing is attacking, so the summon must be allowed");
        sim.Player.RidingMover.ShouldBeNull("it is not instant - there is a windup");

        // Step through the windup.
        for (var i = 0; i < mover!.CastingTimeMs / 100 + 2; i++) sim.Step();
        sim.Player.RidingMover.ShouldNotBeNull($"{mover.CastingTimeMs}ms should have been enough");

        // Now walk, and compare against the same walk on foot.
        int Travel(bool mounted)
        {
            var s = new CombatSimulation(seed: 42);
            s.Player.MoveSpeed = 130;
            s.Player.X = s.Player.Y = 0;
            if (mounted) { s.Player.CarriedMover = mover; s.Player.MoverSlot = 0; s.Player.RidingMover = mover; }
            s.Player.WalkTarget = (100_000, 0);
            for (var i = 0; i < 100; i++) s.Step();
            return s.Player.X;
        }

        var onFoot = Travel(false);
        var riding = Travel(true);
        output.WriteLine($"10s of walking: on foot {onFoot}u, riding {mover.Idx} {riding}u "
                         + $"(x{(double)riding / Math.Max(1, onFoot):F2}, table says x{mover.RunSpeedFactor:F2})");

        riding.ShouldBeGreaterThan(onFoot, "riding a mover must actually be faster");
    }

    /// <summary>⭐ TAKING DAMAGE KILLS A SUMMON, and this is not a detail. The operator measured a summon
    /// REFUSED 0 of 21 times while being hit, and the attempt still cancelled the walk in progress - which
    /// made it the cause of every death in one earlier session.</summary>
    [SkippableFact]
    public void ASummonIsRefusedWhileSomethingIsHittingUs()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present");
        var mover = MoverCatalog.Load(shine!).BestFor(60);
        Skip.If(mover is null, "no mover at level 60");

        var sim = new CombatSimulation(seed: 42);
        sim.Player.CarriedMover = mover;
        sim.Player.MoverSlot = 0;
        sim.Player.MaxHp = sim.Player.Hp = 100_000;
        sim.Player.X = sim.Player.Y = 0;

        var mob = sim.AddMob(10, 20, 0, m => m.Hp = m.MaxHp = 100_000);
        mob.Arg.Target = sim.Player;                       // it is on us

        sim.IsUnderFire.ShouldBeTrue();
        sim.SummonMover().ShouldBeFalse("a summon is refused while something is attacking");
        sim.Player.RidingMover.ShouldBeNull();

        // Once nothing is on us, the summon works. The mob is moved out of its own detect range first:
        // just clearing its target is not enough now that a summon is INTERRUPTED by damage as well as
        // refused -- an aggressive mob standing 20u away re-acquires us mid-windup, which is exactly what
        // it should do and exactly why a bot must gain distance before it tries to ride.
        mob.Mob.X = 100_000;
        mob.Mob.Y = 100_000;
        mob.Arg.Target = null;
        sim.SummonMover().ShouldBeTrue();
        for (var i = 0; i < mover!.CastingTimeMs / 100 + 2; i++) sim.Step();
        sim.Player.RidingMover.ShouldNotBeNull();

        output.WriteLine($"refused under fire, allowed once clear -> riding {sim.Player.RidingMover?.Idx}");
    }

    /// <summary>⭐ DAMAGE INTERRUPTS THE SUMMON AND NOTHING ELSE. Operator, 2026-09-12: "damage doesnt
    /// dismount it just interrupts the summon."
    ///
    /// <para>Both halves matter and they pull opposite ways. The windup being fragile is why a bot must
    /// not try to mount with something on it. Riding being DURABLE is why riding straight through a pack
    /// is a real option at all -- and that is half of the travel brief. Getting the second half wrong
    /// would quietly make every "run through them" decision look suicidal.</para></summary>
    [SkippableFact]
    public void DamageInterruptsTheSummonButNeverThrowsYouOff()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present");
        var mover = MoverCatalog.Load(shine!).BestFor(60);
        Skip.If(mover is null, "no mover at level 60");

        // --- the windup is fragile ---
        var sim = new CombatSimulation(seed: 42);
        sim.Player.CarriedMover = mover;
        sim.Player.MoverSlot = 0;
        sim.Player.MaxHp = sim.Player.Hp = 100_000;
        sim.Player.X = sim.Player.Y = 0;
        var mob = sim.AddMob(10, 20, 0, m => m.Hp = m.MaxHp = 100_000);

        sim.SummonMover().ShouldBeTrue("nothing on us yet");
        mob.Arg.Target = sim.Player;                       // hit during the windup
        sim.Step();
        sim.Player.MoverSummonEndsAt.ShouldBe(0u, "a summon taking damage mid-windup is lost");
        sim.Player.RidingMover.ShouldBeNull();

        // --- but riding is not ---
        sim.Player.RidingMover = mover;                    // already up and running
        var hpBefore = sim.Player.Hp;
        for (var i = 0; i < 40; i++) sim.Step();           // take a beating

        sim.Player.RidingMover.ShouldNotBeNull(
            "DAMAGE MUST NOT DISMOUNT. Riding through a pack is a real option and the travel script "
            + "depends on it; throwing the rider off would make every run-through decision look fatal");
        output.WriteLine($"took {hpBefore - sim.Player.Hp} damage while riding and stayed on "
                         + $"{sim.Player.RidingMover?.Idx}");
    }
}
