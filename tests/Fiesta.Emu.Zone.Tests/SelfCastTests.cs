using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ SELF-CASTING — heals and buffs a character puts on ITSELF.
///
/// <para>⚠️ <b>None of this worked, and it made every healer unsurvivable in the simulation.</b> A
/// level-75 HighCleric died in Trumpy Remains in 7.2 seconds with `Heal10` off cooldown on every single
/// incoming hit and 1,588 SP in the bank. Two independent bugs, both in the harness:</para>
///
/// <list type="number">
///   <item>A heal is <b>`LandsOn 3` (ALLY)</b>, not 1 (self). The driver casts it at its OWN handle — which
///         is correct, and its code says so — and the simulation looked that handle up among MOBS, found
///         nothing, and refused the cast as `NoTarget`. Every self-heal and every ally-buff was silently
///         impossible.</item>
///   <item>The heal AMOUNT was read from the magic-damage columns, and a heal has none: `Heal10` carries
///         `MinMA`/`MaxMA` of zero. Its 1,100 lives in <c>SpecialValueA</c>, behind
///         <c>SpecialIndexA == SkillSpecial.HealAmount</c>. So even once the cast landed it healed for
///         nothing.</item>
/// </list></summary>
public class SelfCastTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "ItemInfo.shn")) ? root : null;
    }

    private static string? Ressystem()
    {
        var root = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return File.Exists(Path.Combine(root, "ActiveSkillView.shn")) ? root : null;
    }

    /// <summary>⭐ THE HEAL AMOUNT IS IN `SpecialValueA`, and the rank scaling is what confirms the
    /// reading: Heal01 heals 110, Heal10 heals 1,100, GreatHeal04 heals 1,800.</summary>
    [SkippableFact]
    public void AHealsAmountComesFromItsSpecialValueNotItsDamageColumns()
    {
        var (shine, res) = (Shine(), Ressystem());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(res is null, "client data not present; set CLIENT_DATA");

        var catalog = SkillCatalog.Load(shine!, res!);
        var heal10 = catalog.Skills.First(s => s.InxName == "Heal10");

        heal10.IsHeal.ShouldBeTrue("EffectType 5 is the client's own heal marker");
        heal10.HealAmount.ShouldBe(1100);
        heal10.Magical.MaxFlat.ShouldBe(0u, "a heal has NO magic-damage columns -- that is the trap");
        heal10.LandsOn.ShouldBe(3, "ALLY, not self: the driver targets its own handle deliberately");

        catalog.Skills.First(s => s.InxName == "Heal01").HealAmount.ShouldBe(110);
        catalog.Skills.First(s => s.InxName == "GreatHeal04").HealAmount.ShouldBe(1800);
    }

    /// <summary>⭐ CASTING AT YOUR OWN HANDLE IS A SELF-CAST, whatever `LandsOn` says — and it heals.</summary>
    [SkippableFact]
    public void AHealCastAtOurOwnHandleHealsUs()
    {
        var (shine, res) = (Shine(), Ressystem());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(res is null, "client data not present; set CLIENT_DATA");

        var catalog = SkillCatalog.Load(shine!, res!);
        var table = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamHighClericServer.txt"));

        var sim = new CombatSimulation(seed: 1) { Skills = catalog };
        sim.Player.Become(table, 75, skills: catalog);
        sim.Player.Hp = 100;

        var heal = sim.Player.LearnedSkills.First(s => s.InxName == "Heal10");
        heal.LandsOn.ShouldNotBe(1, "the point of the test: it is an ALLY skill, not a self skill");

        // Exactly what the driver does: cast it at bot.selfHandle().
        sim.Cast(heal.Id, sim.Player.Handle)
            .ShouldBe(CombatSimulation.CastRefusal.Accepted, "a heal aimed at ourselves must be accepted");

        for (var i = 0; i < 40 && sim.Player.CastingSkill is not null; i++) sim.Tick();
        sim.Tick();

        output.WriteLine($"hp after {heal.InxName}: {sim.Player.Hp}/{sim.Player.MaxHp}");
        sim.Player.Hp.ShouldBe(100 + heal.HealAmount, "it heals for its SpecialValueA");
    }

    /// <summary>...and the failure it replaces: aimed at our own handle, the cast used to be refused as
    /// NoTarget because the simulation only ever looked the handle up among MOBS.</summary>
    [SkippableFact]
    public void ASelfCastIsNotRefusedForHavingNoMobTarget()
    {
        var (shine, res) = (Shine(), Ressystem());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(res is null, "client data not present; set CLIENT_DATA");

        var catalog = SkillCatalog.Load(shine!, res!);
        var table = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamHighClericServer.txt"));

        var sim = new CombatSimulation(seed: 1) { Skills = catalog };
        sim.Player.Become(table, 75, skills: catalog);
        sim.Player.Hp = 100;
        sim.Mobs.ShouldBeEmpty("no mobs at all: a self-cast must not need one");

        var heal = sim.Player.LearnedSkills.First(s => s.InxName == "Heal10");
        sim.Cast(heal.Id, sim.Player.Handle).ShouldBe(CombatSimulation.CastRefusal.Accepted);
    }
}
