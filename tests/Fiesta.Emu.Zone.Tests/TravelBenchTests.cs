using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ THE TRAVEL BENCH: a journey to a real place, scored against just walking at it.
///
/// <para>Start is the map's busiest spawn area; the destination is the map's furthest MAP-LINK GATE, read
/// from `World/NPC.txt` -- somewhere a bot genuinely has to reach to leave the zone, not a point the
/// harness made up. The character carries a mover it could have bought at its level.</para>
///
/// <para>The control is five lines: walk at the goal, nothing else. Anything clever has to beat that on
/// TIME or on DAMAGE TAKEN, and a script that arrives slower while taking less is a real answer, not a
/// loss -- which is why both are printed rather than combined.</para></summary>
[Collection(HeavySimulationCollection.Name)]
public class TravelBenchTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var s = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return Directory.Exists(Path.Combine(s, "MobRegen")) ? s : null;
    }

    private static string? Ressystem()
    {
        var r = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return File.Exists(Path.Combine(r, "ActiveSkillView.shn")) ? r : null;
    }

    private static string ScriptDir()
        => Environment.GetEnvironmentVariable("TRAVEL_SCRIPTS")
           ?? @"C:/Users/Claude/AppData/Local/Temp/claude/C--Projects/5464e9b5-d658-4875-ab07-b2bb72cfffb6/scratchpad";

    private static string Line(string what, TravelResult r)
        => $"  {what,-8} arrived {(r.Arrived ? "YES" : "no "),-4} {r.Seconds,6:F1}s  "
           + $"short {r.Remaining,6}u  taken {r.DamageTaken,7}  lowHp {r.LowWaterHpPercent,3}%  "
           + $"mounted {r.MountedPercent,5:F1}%  kills {r.Kills,3}  died {r.Died}  err {r.Errors}";

    /// <summary>Both scripts, same map, same seeds. Prints; asserts only that the journey is a real one
    /// and that neither script errors -- which side WINS is a judgement from the numbers.</summary>
    [SkippableFact]
    public void TravelToTheFurthestGate()
    {
        var (shine, res) = (Shine(), Ressystem());
        Skip.If(shine is null || res is null, "game data not present");

        var naivePath = Path.Combine(ScriptDir(), "naive_travel.lua");
        var cleverPath = Path.Combine(ScriptDir(), "travel.lua");
        Skip.If(!File.Exists(naivePath) || !File.Exists(cleverPath), "travel scripts not present");

        var naive = File.ReadAllText(naivePath);
        var clever = File.ReadAllText(cleverPath);

        // Level 25 on the band-20 field map, as the class a level-25 archer actually is.
        const string map = "RouVal02";
        const string cls = "HawkArcher";
        const int level = 25;

        var any = false;
        foreach (var seed in new uint[] { 42, 1337, 9001 })
        {
            var log = new List<string>();
            var a = TravelRunner.Run(shine!, res!, naive, cls, level, map, seed: seed, driverLog: log);
            var b = TravelRunner.Run(shine!, res!, clever, cls, level, map, seed: seed);
            if (a is null || b is null) continue;
            any = true;

            if (!any || seed == 42) output.WriteLine(log.FirstOrDefault(l => l.StartsWith("travel:")) ?? "");
            output.WriteLine($"seed {seed}");
            output.WriteLine(Line("naive", a));
            output.WriteLine(Line("clever", b));

            a.Errors.ShouldBe(0, $"naive script errored: {a.FirstError}");
            b.Errors.ShouldBe(0, $"travel script errored: {b.FirstError}");
        }

        any.ShouldBeTrue("no travel run was produced -- check the class name and the map");
    }
}
