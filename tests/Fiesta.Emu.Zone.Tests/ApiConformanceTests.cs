using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Where the harness's answer has to match the live one, not merely have the right type.
///
/// <para>Every case here was a stub that ran without erroring and reversed a decision in
/// level_quest.lua: the auto-stub picks a shape from the name, the script accepts it, no error is
/// raised, and a branch of the driver is off for the whole run while the report says "0 errors".</para></summary>
public class ApiConformanceTests
{
    private static CombatSimulation WithAMobOnUs(int mobX = 40, int mobY = 0)
    {
        var sim = new CombatSimulation(seed: 1);
        sim.Player.MaxHp = sim.Player.Hp = 1000;
        var mob = sim.AddMob(10, mobX, mobY, m => m.Hp = m.MaxHp = 5000);
        mob.Name = "Orc";
        mob.SpawnX = 300;
        mob.SpawnY = 400;
        mob.Arg.Target = sim.Player;
        return sim;
    }

    // ---- castConfirmed -------------------------------------------------------------------------------

    /// <summary>An accepted cast confirms; a refused one does not. Stubbed false, the driver re-pressed
    /// skills the server had already taken.</summary>
    [Fact]
    public void AnAcceptedCastIsConfirmedAndARefusedOneIsNot()
    {
        var sim = WithAMobOnUs();
        var api = new SimBotApi(sim);

        api.castConfirmed().ShouldBeFalse("nothing has been cast yet");

        // No skill catalog loaded, so every id is unlearned: a refusal, and it must not confirm.
        api.cast(skill: 4, target: 10).ShouldBeFalse();
        sim.LastCastRefusal.ShouldBe(CombatSimulation.CastRefusal.NotLearned);
        api.castConfirmed().ShouldBeFalse();
    }

    /// <summary>The confirmation belongs to the cast in flight; Cast and the resolve own the flag.</summary>
    [Fact]
    public void TheConfirmationIsClearedWhenTheCastResolves()
    {
        var sim = WithAMobOnUs();
        sim.Player.CastServerConfirmed = true;
        sim.Player.CastingSkill = null;

        sim.Tick();
        sim.Player.CastServerConfirmed.ShouldBeTrue("a tick with nothing in flight leaves it alone");

        sim.Cast(4, 10).ShouldNotBe(CombatSimulation.CastRefusal.Accepted);
        sim.Player.CastServerConfirmed.ShouldBeFalse();
    }

    // ---- canPick -------------------------------------------------------------------------------------

    /// <summary>ZoneView.CanPick is !PickPending || sent over 2s ago, so an idle gate is OPEN. The `can*`
    /// auto-stub answered false and closed the loot branch with no error.</summary>
    [Fact]
    public void CanPickIsTrueWhenNoPickIsInFlight()
        => new SimBotApi(new CombatSimulation(seed: 1)).canPick().ShouldBeTrue();

    // ---- aggressorSpawns -----------------------------------------------------------------------------

    /// <summary>shedStep's only input. An empty table is the shape it reads as a COMPLETED escape, so the
    /// stub had it reporting success on every call without taking a step.</summary>
    [Fact]
    public void AggressorSpawnsCarriesThePackAndWhereItCameFrom()
    {
        var sim = WithAMobOnUs(mobX: 40, mobY: 30);
        var rows = new SimBotApi(sim).aggressorSpawns();
        rows.Length.ShouldBe(1);

        var row = rows.Get(1).Table;
        row.Get("handle").Number.ShouldBe(10);
        row.Get("x").Number.ShouldBe(40);
        row.Get("y").Number.ShouldBe(30);
        row.Get("anchorX").Number.ShouldBe(300);
        row.Get("anchorY").Number.ShouldBe(400);
        row.Get("fromSpawn").Number.ShouldBe(Math.Sqrt(260 * 260 + 370 * 370), 0.001);
    }

    /// <summary>Only mobs targeting us are in the pack - a shed that averaged in bystanders would run
    /// from the wrong place.</summary>
    [Fact]
    public void OnlyMobsTargetingUsAreListed()
    {
        var sim = WithAMobOnUs();
        sim.AddMob(11, 500, 500, m => m.Hp = m.MaxHp = 100).Arg.Target = null;
        new SimBotApi(sim).aggressorSpawns().Length.ShouldBe(1);
    }

    /// <summary>And a corpse cannot chase.</summary>
    [Fact]
    public void ADeadAggressorLeavesThePack()
    {
        var sim = WithAMobOnUs();
        sim.Mobs[0].Hp = 0;
        sim.Mobs[0].Mob.IsAlive = false;
        new SimBotApi(sim).aggressorSpawns().Length.ShouldBe(0);
    }
}
