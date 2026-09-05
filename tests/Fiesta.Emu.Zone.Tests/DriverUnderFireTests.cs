using Fiesta.Emu.Zone.Lua;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>One scenario cell, run with the CombatLog on, so a run can be explained rather than scored.
///
/// <para>Prints; asserts nothing. It is the instrument for driver work, and the numbers it produces are
/// what the assertions in the bots-lua repo get written against.</para></summary>
[Collection(HeavySimulationCollection.Name)]
public class DriverUnderFireTests(ITestOutputHelper output)
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

    private void Cell(string className, int level, bool dungeon)
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var log = new CombatLog();
        var simLog = new List<string>();
        var driverLog = new List<string>();
        var r = ScenarioRunner.Run(shine!, ressystem!, File.ReadAllText(driver!),
                                   className, level, dungeon,
                                   combatLog: log, simLog: simLog, driverLog: driverLog,
                                   logRouteDetours: true,
                                   inspect: s => output.WriteLine(
                                       $"  walkTo: {s.WalkToCalls} calls, {s.WalkToRouted} routed, "
                                       + $"{s.WalkToNoPath} NO PATH, {s.WalkToAlreadyThere} already there"));
        if (r is null) { output.WriteLine("no loadout for this cell"); return; }

        output.WriteLine($"{r.ClassName} L{r.Level} {r.AreaName} ({r.Map}){(r.IsDungeon ? " [dungeon]" : "")}");
        output.WriteLine($"  kills={r.Kills} exp={r.Experience} casts={r.Casts} died={r.Died} "
                         + $"survived={r.SurvivedSeconds}s of {r.SimulatedSeconds}s hp={r.HpLeftPercent}% "
                         + $"sp={r.SpLeftPercent}% errors={r.Errors} {r.FirstError}");
        output.WriteLine("");
        output.WriteLine(log.Summarise());
        output.WriteLine("");
        output.WriteLine("last 25 incoming hits:");
        foreach (var line in log.Tail(25)) output.WriteLine("  " + line);

        var detours = simLog.Where(l => l.Contains("detour x")).ToList();
        output.WriteLine("");
        output.WriteLine($"routed walkTo calls with straight-line vs route ({detours.Count}), first 20:");
        foreach (var line in detours.Take(20)) output.WriteLine("  " + line);

        output.WriteLine("");
        output.WriteLine("driver log, first 90 lines in order:");
        foreach (var line in driverLog.Where(l => !l.StartsWith("CRUTCH[OK] no active quests")).Take(90))
            output.WriteLine("  " + line);

        output.WriteLine("");
        output.WriteLine($"driver said {driverLog.Count} things; distinct lines by count:");
        foreach (var g in driverLog.GroupBy(Collapse).OrderByDescending(g => g.Count()).Take(30))
            output.WriteLine($"  {g.Count(),6}  {g.Key}");

        var cut = simLog.Where(l => l.Contains("aggro list cut") || l.Contains("past FollowCha")).ToList();
        output.WriteLine("");
        output.WriteLine($"chase-range / aggro-cut events: {cut.Count}");
        foreach (var line in cut.Take(15)) output.WriteLine("  " + line);
    }

    /// <summary>Walk up and swing at the nearest mob. No quests, no phases, no kiting, no rotation.
    ///
    /// <para>The control for every claim about level_quest.lua: if this cannot get around the map either,
    /// the fault is in the harness's movement and not in the driver's choices.</para></summary>
    private const string KillNearest = """
        function on_tick()
          local mobs = bot.nearbyMobs()
          local best, bestDist = nil, 1e9
          for i = 1, #mobs do
            local m = mobs[i]
            if m.isHuntable and m.hp > 0 and m.dist < bestDist then best, bestDist = m, m.dist end
          end
          if best == nil then return end
          if not bot.swing(best.handle) then
            if not bot.walking() then bot.walkTo(best.x, best.y) end
          end
        end
        """;

    [SkippableFact]
    public void ControlWalkUpAndSwingInTheSameDungeon()
    {
        var (shine, ressystem) = (Shine(), Ressystem());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");

        foreach (var dungeon in new[] { true, false })
        {
            var log = new CombatLog();
            var r = ScenarioRunner.Run(shine!, ressystem!, KillNearest, "Warrior", 25, dungeon,
                                       combatLog: log);
            output.WriteLine($"{r!.AreaName} ({r.Map}): kills={r.Kills} died={r.Died} "
                             + $"survived={r.SurvivedSeconds}s errors={r.Errors} {r.FirstError}");
            output.WriteLine(log.Summarise());
            output.WriteLine("");
        }
    }

    /// <summary>Collapse numbers out of a driver line so repeats of one decision group together.</summary>
    private static string Collapse(string line)
        => System.Text.RegularExpressions.Regex.Replace(line, @"-?\d+(\.\d+)?", "#");

    /// <summary>The operator's own example of a map where kiting is mandatory.</summary>
    [SkippableFact]
    public void WarriorLevel25InMarloneClansHideout() => Cell("Warrior", 25, dungeon: true);

    /// <summary>The same character on the band's field map, which should be winnable with little or no
    /// kiting - the control for the dungeon run.</summary>
    [SkippableFact]
    public void WarriorLevel25OnBurningHill() => Cell("Warrior", 25, dungeon: false);
}
