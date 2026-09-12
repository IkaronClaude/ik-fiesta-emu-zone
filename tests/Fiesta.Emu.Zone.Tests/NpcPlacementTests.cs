using Fiesta.Emu.Zone.Data;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`World/NPC.txt` - where the NPCs and the map-link GATES actually stand.
///
/// <para>A travel destination has to be a real place or the journey being scored is one nobody makes.
/// These are the server's own coordinates, and a gate is what a bot must reach to leave a zone.</para></summary>
public class NpcPlacementTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var s = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(s, "World", "NPC.txt")) ? s : null;
    }

    [SkippableFact]
    public void TheGatesOfTheBenchMapsReadOutOfTheServerFile()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present");
        var npcs = NpcPlacementCatalog.Load(shine!);

        output.WriteLine($"{npcs.Placements.Count} NPC placements");
        foreach (var map in new[] { "RouVal02", "ValDn01", "RouN" })
        {
            var gates = npcs.GatesOn(map).ToList();
            output.WriteLine($"{map}: {npcs.OnMap(map).Count()} NPCs, {gates.Count} gates");
            foreach (var g in gates.Take(6))
                output.WriteLine($"   {g.MobName,-14} ({g.X,6},{g.Y,6})  {g.Role}/{g.RoleArg0}");
        }

        npcs.Placements.ShouldNotBeEmpty("NPC.txt did not parse");

        // RouVal02 is the combat bench's field map and the server file gives it four map-link gates.
        var val = npcs.GatesOn("RouVal02").ToList();
        val.Count.ShouldBe(4, "RouVal02 has four MapLinkGate rows in NPC.txt");
        val.ShouldAllBe(g => g.MobName == "MapLinkGate");
        val.ShouldAllBe(g => g.X > 0 && g.Y > 0);

        // ...and the furthest one from the busy area is a genuine trip across the map.
        var far = npcs.FurthestGateFrom("RouVal02", 4914, 9319);
        far.ShouldNotBeNull();
        output.WriteLine($"furthest gate from (4914,9319): {far!.RoleArg0} at ({far.X},{far.Y})");
        far.RoleArg0.ShouldNotBeNullOrEmpty("a gate names the link it leads to");

        // ...and the LinkTable in the same file says where it goes. This is cross-map travel in one join:
        // the gate stands HERE and puts you THERE.
        var dest = npcs.DestinationOf(far);
        dest.ShouldNotBeNull($"no LinkTable row for {far.RoleArg0}");
        output.WriteLine($"   {far.RoleArg0} leads to {dest!.ToMap} ({dest.ToX},{dest.ToY})");
        dest.ToMap.ShouldNotBeNullOrEmpty();

        npcs.Links.Count.ShouldBeGreaterThan(100, "LinkTable did not parse");
    }

    /// <summary>⭐ The parse bug this file exists to prevent coming back. `World/NPC.txt` uses
    /// <c>#recordin &lt;TableName&gt;</c> rather than <c>#record</c>, and declares SPACE as a delimiter
    /// alongside tab. The parser knew neither, so 760 rows read as ZERO and the file looked empty --
    /// which is indistinguishable from "this map has no gates" at every call site.</summary>
    [SkippableFact]
    public void TheFileUsesRecordinAndASpaceDelimiterAndBothAreHonoured()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present");

        var tables = ShineTable.ParseFile(Path.Combine(shine!, "World", "NPC.txt"));
        foreach (var t in tables)
            output.WriteLine($"TABLE '{t.Name}': {t.Rows.Count} rows, {t.Columns.Count} columns");

        var npc = tables.First(t => t.Name.Equals("ShineNPC", StringComparison.OrdinalIgnoreCase));
        var link = tables.First(t => t.Name.Equals("LinkTable", StringComparison.OrdinalIgnoreCase));

        npc.Rows.Count.ShouldBeGreaterThan(500, "#recordin rows are being dropped");
        link.Rows.Count.ShouldBeGreaterThan(100, "the second table in the file is being dropped");

        // The three space-delimited rows at the end of the file are real rows, not one giant field.
        var ming = npc.Rows.FirstOrDefault(r => npc.Get(r, "MobName").Equals("Xiaoming", StringComparison.OrdinalIgnoreCase));
        ming.ShouldNotBeNull("the space-delimited rows at the end of the file were not split");
        npc.Get(ming!, "Map").ShouldBe("Eld");
        npc.GetInt(ming!, "Coord-X").ShouldBe(11683);
        output.WriteLine($"space-delimited row parsed: Xiaoming on {npc.Get(ming!, "Map")} "
                         + $"at ({npc.GetInt(ming!, "Coord-X")},{npc.GetInt(ming!, "Coord-Y")})");
    }
}
