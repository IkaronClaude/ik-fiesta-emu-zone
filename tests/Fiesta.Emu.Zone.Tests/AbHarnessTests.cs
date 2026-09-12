using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The combat-script A/B bench. Point it at two Lua files and it tells you which one is better,
/// on the axes that matter, over the same worlds and the same seeds.
///
/// <para>Set LEVEL_QUEST_LUA_B to a candidate script to compare it against the live one. With only one
/// script present it still runs, and then it is a BASELINE: the same scorecard for the current driver,
/// which is what any change has to beat.</para></summary>
[Collection(HeavySimulationCollection.Name)]
public class AbHarnessTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return Directory.Exists(Path.Combine(root, "MobRegen")) ? root : null;
    }

    private static string? Ressystem()
    {
        var root = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return File.Exists(Path.Combine(root, "ActiveSkillView.shn")) ? root : null;
    }

    private static string? DriverPath()
    {
        var p = Environment.GetEnvironmentVariable("LEVEL_QUEST_LUA")
                ?? @"C:/Projects/ik-fiesta-bots/scripts/level_quest.lua";
        return File.Exists(p) ? p : null;
    }

    /// <summary>One melee, one caster, one ranged: the three shapes a combat script has to serve. A
    /// change that helps a Warrior and starves the Mage is not an improvement, and a single-class bench
    /// would not show it.</summary>
    private static readonly AbCell[] Cells =
    [
        new("Warrior", 25, Dungeon: false),
        new("Warrior", 25, Dungeon: true),
        new("HighCleric", 25, Dungeon: false),
        new("Ranger", 25, Dungeon: false),
    ];

    private static List<ScriptVariant> Variants(string driver)
    {
        var list = new List<ScriptVariant> { ScriptVariant.FromFile("A:current", driver) };
        var b = Environment.GetEnvironmentVariable("LEVEL_QUEST_LUA_B");
        if (!string.IsNullOrWhiteSpace(b) && File.Exists(b))
            list.Add(ScriptVariant.FromFile("B:candidate", b));
        return list;
    }

    /// <summary>The bench. Prints the scorecard; asserts only that it produced runs, because WHICH axis
    /// matters is a judgement the operator makes from the numbers, not one a test should encode.</summary>
    [SkippableFact]
    public void CombatScriptBench()
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var variants = Variants(driver!);
        output.WriteLine($"variants: {string.Join(", ", variants.Select(v => v.Name))}");
        output.WriteLine($"cells:    {string.Join(" | ", Cells.Select(c => c.ToString()))}");
        output.WriteLine($"seeds:    {string.Join(", ", AbHarness.DefaultSeeds)}");
        output.WriteLine("");

        var runs = AbHarness.RunMatrix(shine!, ressystem!, variants, Cells).ToList();
        runs.ShouldNotBeEmpty("the bench produced no runs - check the class names against ClassName.shn");

        output.WriteLine(AbHarness.Scorecard(runs));
    }

    /// <summary>⭐ THE BENCH HAS TO BE DETERMINISTIC OR IT MEASURES NOTHING. Same script, same cell, same
    /// seed, twice: every axis must match exactly. If this ever fails, no A/B result from this harness
    /// means anything until it is fixed - a same-seed run whose numbers move is the tell that something
    /// is reading a wall clock or leaking state between runs.</summary>
    [SkippableFact]
    public void TheSameScriptOnTheSameSeedScoresIdentically()
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var v = ScriptVariant.FromFile("A", driver!);
        var cell = new AbCell("Warrior", 25, Dungeon: false);

        var first = AbHarness.Run(shine!, ressystem!, v, cell, seed: 42);
        var second = AbHarness.Run(shine!, ressystem!, v, cell, seed: 42);
        first.ShouldNotBeNull();
        second.ShouldNotBeNull();

        output.WriteLine($"run 1: kills={first!.Result.Kills} dmg={first.Metrics.DamageDealt} "
                         + $"dead%={first.Metrics.DeadTimePercent:F2}");
        output.WriteLine($"run 2: kills={second!.Result.Kills} dmg={second.Metrics.DamageDealt} "
                         + $"dead%={second.Metrics.DeadTimePercent:F2}");

        second.Result.Kills.ShouldBe(first.Result.Kills);
        second.Result.Experience.ShouldBe(first.Result.Experience);
        second.Metrics.DamageDealt.ShouldBe(first.Metrics.DamageDealt);
        second.Metrics.DamageTaken.ShouldBe(first.Metrics.DamageTaken);
        second.Metrics.TicksWithWastedSkill.ShouldBe(first.Metrics.TicksWithWastedSkill);
    }
}
