using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Do the mobs a scenario actually spawns carry their own defences, or the placeholder?
///
/// <para>A mob has no equipment in this game - `c_StoreMob` writes AC, TB, MR and MB straight from its
/// `MobInfoServer` row, which is the mob's armour. What this checks is that the mobs the BENCH fights
/// get that row rather than falling back to <see cref="MobStats.Default"/>, because a fallback mob is
/// defenceless and every damage number measured against it is inflated.</para></summary>
public class MobArmourTests(ITestOutputHelper output)
{
    private static string Shine() => Environment.GetEnvironmentVariable("SHINE_DATA")
                                     ?? @"Z:/ServerSource/9Data/Shine";

    [SkippableTheory]
    [InlineData("RouVal02")]
    [InlineData("ValDn01")]
    public void EveryMobTheBenchFightsCarriesItsOwnDefences(string mapName)
    {
        var shine = Shine();
        Skip.If(!Directory.Exists(Path.Combine(shine, "MobRegen")), "server data not present");

        var data = MobDataBox.Load(shine);
        var map = MobRegenData.Load(Path.Combine(shine, "MobRegen", $"{mapName}.txt"));
        var sim = new CombatSimulation(seed: 42);
        var spawned = sim.SpawnFightable(map, data, spawnSeed: 7, maxRank: MapSpawner.NormalMobMaxRank);

        var armoured = 0;
        var defenceless = new List<string>();
        foreach (var m in spawned)
        {
            var ac = m.Parameters.Base[Stat.AC];
            if (ac > 0) armoured++;
            else defenceless.Add($"{m.Name} ac={ac}");
        }

        output.WriteLine($"{mapName}: {spawned.Count} mobs, {armoured} carrying AC > 0");
        foreach (var g in spawned.GroupBy(m => m.Name).OrderBy(g => g.Key).Take(12))
        {
            var p = g.First().Parameters.Base;
            output.WriteLine($"  {g.Key,-24} x{g.Count(),-3} ac={p[Stat.AC],-6} tb={p[Stat.TB],-5} "
                             + $"mr={p[Stat.MR],-6} mb={p[Stat.MB],-5} str={p[Stat.Str]} con={p[Stat.Con]}");
        }
        if (defenceless.Count > 0)
            output.WriteLine($"  DEFENCELESS ({defenceless.Count}): {string.Join(", ", defenceless.Distinct().Take(10))}");

        spawned.ShouldNotBeEmpty();
        // A defenceless mob is one the tables did not know; the bench's damage against it is not real.
        defenceless.ShouldBeEmpty($"{defenceless.Count} of {spawned.Count} mobs on {mapName} spawned with no AC");
    }
}
