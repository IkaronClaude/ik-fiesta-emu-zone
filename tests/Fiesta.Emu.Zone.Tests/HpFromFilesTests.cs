using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Where a simulated character's HP comes from: `Param&lt;Class&gt;Server.txt`, per level, through
/// `CharClass::MaxHp`. No capture is involved and none is needed - the curve is server data, so a level-100
/// Ranger is as well-founded as a level-25 Warrior even though nobody has ever captured one.
///
/// <para>The 2,000,000 HP that appears in two old fixtures is an explicit override written into those
/// tests, a crutch so that a test about whether swings LAND is not confounded by the character dying. It
/// is not how the scenario matrix builds anything.</para></summary>
public class HpFromFilesTests(ITestOutputHelper output)
{
    private static string Shine() => Environment.GetEnvironmentVariable("SHINE_DATA")
                                     ?? @"Z:/ServerSource/9Data/Shine";

    [SkippableFact]
    public void EveryClassGetsItsHpCurveFromTheServerClassTable()
    {
        var shine = Shine();
        Skip.If(!Directory.Exists(Path.Combine(shine, "World")), "server data not present");

        string[] classes = ["Warrior", "Fighter", "Cleric", "HighCleric", "Archer", "Ranger", "Mage"];
        int[] levels = [1, 25, 60, 100];

        output.WriteLine("MaxHp straight out of Param<Class>Server.txt (no gear, no spent points):");
        output.WriteLine($"{"class",-12} {string.Join("", levels.Select(l => $"lv{l,-8}"))}");

        var seen = 0;
        foreach (var cls in classes)
        {
            var file = Path.Combine(shine, "World", $"Param{cls}Server.txt");
            if (!File.Exists(file)) continue;
            var table = ClassParamTable.Load(file);

            var cells = new List<string>();
            foreach (var lv in levels)
            {
                if (table.At(lv) is null) { cells.Add($"{"-",-10}"); continue; }
                var player = new SimPlayer();
                player.Become(table, lv);
                cells.Add($"{player.MaxHp,-10}");
                player.MaxHp.ShouldBeGreaterThan(0, $"{cls} at {lv} has no HP");
                seen++;
            }
            output.WriteLine($"{cls,-12} {string.Join("", cells)}");
        }

        seen.ShouldBeGreaterThan(0, "no class table was readable");
    }

    /// <summary>Spent Constitution is the OTHER term, and it is worth five HP a point - a separate source
    /// from the cluster, because `StorePure` never writes spent points into the base cluster. Without it a
    /// character silently loses every point of HP it bought.</summary>
    [SkippableFact]
    public void SpentConstitutionAddsFiveHpAPoint()
    {
        var shine = Shine();
        Skip.If(!File.Exists(Path.Combine(shine, "World", "ParamRangerServer.txt")), "no data");

        var table = ClassParamTable.Load(Path.Combine(shine, "World", "ParamRangerServer.txt"));
        Skip.If(table.At(100) is null, "no level 100 row");

        var bare = new SimPlayer();
        bare.Become(table, 100);
        var invested = new SimPlayer();
        invested.Become(table, 100, freeStats: new FreeStats(Con: 40));

        output.WriteLine($"Ranger lv100: base {bare.MaxHp} HP, +40 Con -> {invested.MaxHp} HP "
                         + $"(+{invested.MaxHp - bare.MaxHp})");

        (invested.MaxHp - bare.MaxHp).ShouldBe(40 * CharacterParameters.HpPerConstitutionPoint);
    }
}
