using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Why the bench's 24.6% dead time is spent kiting: which branch of the driver is walking, and
/// what it says while it does it. A percentage names the defect, not its address.</summary>
[Collection(HeavySimulationCollection.Name)]
public class KiteDiagnosticsTests(ITestOutputHelper output)
{
    private static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) ?? d;

    [SkippableTheory]
    [InlineData("Ranger", false)]
    [InlineData("Warrior", false)]
    [InlineData("HighCleric", false)]
    [InlineData("Warrior", true)]
    public void WhatTheDriverSaysWhileItKites(string className, bool dungeon)
    {
        var shine = Env("SHINE_DATA", @"Z:/ServerSource/9Data/Shine");
        var res = Env("CLIENT_DATA", @"Z:/ClientProd2/ressystem");
        var lua = Env("LEVEL_QUEST_LUA_B", Env("LEVEL_QUEST_LUA", @"C:/Projects/ik-fiesta-bots/scripts/level_quest.lua"));
        Skip.If(!Directory.Exists(Path.Combine(shine, "MobRegen")) || !File.Exists(lua), "data not present");

        var driverLog = new List<string>();
        var metrics = new CombatMetrics();
        var result = ScenarioRunner.Run(shine, res, File.ReadAllText(lua), className, 25, dungeon,
                                        seed: 42, driverLog: driverLog, metrics: metrics);
        result.ShouldNotBeNull();

        output.WriteLine($"{className}{(dungeon ? " DUNGEON" : "")}: kills={result!.Kills} dead%={metrics.DeadTimePercent:F1} "
                         + $"kite%={metrics.KitePercent:F1} bursts={metrics.KiteBursts} "
                         + $"mean={metrics.KiteBurstSeconds:F1}s max={metrics.KiteLongestSeconds:F1}s "
                         + $"lowHp={metrics.LowWaterHpPercent} died={result.Died} "
                         + $"dealt={metrics.DamageDealt} taken={metrics.DamageTaken} "
                         + $"hpStones={metrics.HpStonesUsed}");
        output.WriteLine($"wasted: walking={metrics.WastedWhileWalking} (closing={metrics.WastedWalkingCloser} "
                         + $"kiting={metrics.WastedKitingWithAShot}) notEngaged={metrics.WastedNotEngaged} "
                         + $"outOfReach={metrics.WastedOutOfWeaponReach} idle={metrics.WastedIdle} "
                         + $"pastAnother={metrics.WastedChasingPastAReachableMob}");
        output.WriteLine($"  of the walking ticks: inReach={metrics.WastedWalkingWhileInReach}  "
                         + $"mean dest->target={metrics.MeanWalkDestDistance:F0}u  "
                         + $"mean self->target={metrics.MeanSelfDistance:F0}u  "
                         + $"attackRange={metrics.AttackRangeSeen}u");
        if (result.Errors > 0)
            output.WriteLine($"ERRORS {result.Errors}: {result.FirstError}");
        output.WriteLine("");

        // Collapse the numbers out of each line so repeats group, then show what dominates.
        static string Collapse(string l) => System.Text.RegularExpressions.Regex.Replace(l, @"-?\d+", "#");
        output.WriteLine($"driver said {driverLog.Count} things; top lines by count:");
        foreach (var g in driverLog.GroupBy(Collapse).OrderByDescending(g => g.Count()).Take(25))
            output.WriteLine($"  {g.Count(),5}x {g.Key[..Math.Min(150, g.Key.Length)]}");
    }
}
