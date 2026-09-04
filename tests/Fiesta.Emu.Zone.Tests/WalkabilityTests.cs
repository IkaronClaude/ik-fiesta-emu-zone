using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`.shbd` walls and the routing over them.
///
/// <para>⚠️ These are OPT-IN in the scenario matrix and default OFF, because the simulation is not yet at
/// parity with walls on: a level-25 Warrior on Burning Hill scores 19 kills without them and 1 with, and
/// that gap is the harness's, not the driver's. The pieces below are correct on their own terms; what is
/// not yet true is that a run with walls measures the bot rather than the harness.</para></summary>
public class WalkabilityTests(ITestOutputHelper output)
{
    private static string? BlockInfo()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        var dir = Path.Combine(root, "BlockInfo");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>A missing `.shbd` is a map without geometry, not a crash — the simulation then behaves as
    /// it always did, on open ground.</summary>
    [Fact]
    public void AMissingGridLoadsAsNull()
        => WalkabilityGrid.Load(Path.GetTempPath(), "NoSuchMapAnywhere").ShouldBeNull();

    [SkippableFact]
    public void ARealMapLoadsAndItsGrindAreaIsMostlyOpen()
    {
        var dir = BlockInfo();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var grid = WalkabilityGrid.Load(dir!, "RouVal02");
        grid.ShouldNotBeNull();
        grid.WidthTiles.ShouldBe(2048);
        grid.HeightTiles.ShouldBe(2048);

        // Burning Hill's grind area, measured: every direction clear at 300 units.
        const int x = 6223, y = 7280;
        grid.IsWalkable(x, y).ShouldBeTrue("the spot the matrix starts characters on must be walkable");

        var clear = 0;
        for (var a = 0; a < 16; a++)
        {
            var th = a * Math.PI / 8;
            if (grid.ReachableFraction(x, y, x + (int)(Math.Cos(th) * 300), y + (int)(Math.Sin(th) * 300)) >= 0.99)
                clear++;
        }
        output.WriteLine($"directions fully clear at 300u from the grind spot: {clear}/16");
        clear.ShouldBeGreaterThanOrEqualTo(14, "an open field map should be open around its spawn");
    }

    /// <summary>⚠️ OFF THE EDGE IS NOT WALKABLE. Treating out-of-bounds as open would let a kite escape
    /// into nothing, which is worse than a wall.</summary>
    [SkippableFact]
    public void OutsideTheGridIsBlocked()
    {
        var dir = BlockInfo();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var grid = WalkabilityGrid.Load(dir!, "RouVal02")!;
        grid.IsWalkable(-10, 100).ShouldBeFalse();
        grid.IsWalkableTile(-1, 0).ShouldBeFalse();
        grid.IsWalkableTile(grid.WidthTiles, 0).ShouldBeFalse();
        grid.IsWalkableTile(0, grid.HeightTiles).ShouldBeFalse();
    }

    /// <summary>⭐ A FRACTION, NOT A BOOLEAN. A strict whole-line test called 37 of 37 kites on open ground
    /// "into a wall": a path of several hundred units crosses fifty-odd tiles and a map that is 83%
    /// walkable will clip a scattered one on nearly any line — so it measured path LENGTH, not
    /// obstruction.</summary>
    [SkippableFact]
    public void ReachableFractionIsOneOnOpenGroundAndFallsAtAWall()
    {
        var dir = BlockInfo();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var grid = WalkabilityGrid.Load(dir!, "RouVal02")!;
        grid.ReachableFraction(6223, 7280, 6223 + 200, 7280).ShouldBe(1, 0.01);

        // Into the wall the bot stranded itself on, measured at (5652,8539) with blocked ground to its west.
        var into = grid.ReachableFraction(5652, 8539, 5652 - 300, 8539);
        output.WriteLine($"westward from the pinned spot: {into:F2} of the way");
        into.ShouldBeLessThan(1.0);
    }

    /// <summary>A route exists across open ground, and it is smoothed rather than one waypoint per tile.
    ///
    /// <para>⚠️ Unsmoothed, A* returns a waypoint per TILE at the tile's origin CORNER — the first can sit
    /// behind a character standing mid-tile, and since `walkTo` is called every tick while chasing, the
    /// character oscillates instead of closing.</para></summary>
    [SkippableFact]
    public void APathAcrossOpenGroundIsASingleHop()
    {
        var dir = BlockInfo();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var grid = WalkabilityGrid.Load(dir!, "RouVal02")!;
        var path = TilePathFinder.FindPath(grid, 6223, 7280, 6223 + 250, 7280);

        path.ShouldNotBeNull();
        output.WriteLine($"waypoints across 250u of open ground: {path.Count}");
        path.Count.ShouldBeLessThanOrEqualTo(3, "string-pulling should collapse open ground to a hop or two");
        path[^1].ShouldBe((6223 + 250, 7280), "the route must finish where the caller asked");
    }

    /// <summary>⭐ A ROUTE AROUND AN OBSTACLE MUST FIT IN THE BUDGET.
    ///
    /// <para>⚠️ The budget was 20,000 expansions, chosen because "a kite is tens of tiles" — true of the
    /// DISTANCE and irrelevant to the SEARCH. When the direct line is blocked, A*'s heuristic drags it
    /// into the obstacle and it fans out over a large region first. A <b>36-tile</b> journey from the spot
    /// the driver stranded itself needed more than 20,000; the result was that `walkTo` failed to route
    /// <b>100 of 101</b> calls, every walk fell back to a straight line into geometry, and the character
    /// was blocked on 844 of 1,500 ticks. A budget that is too small does not degrade gracefully — it
    /// turns the pathfinder off and looks like a bot that cannot navigate.</para></summary>
    [SkippableFact]
    public void ARouteAroundGeometryFitsInsideTheDefaultBudget()
    {
        var dir = BlockInfo();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var grid = WalkabilityGrid.Load(dir!, "RouVal02")!;

        // The measured case: pinned at (5652,8539), nearest mob 223 units away past a wall.
        var path = TilePathFinder.FindPath(grid, 5652, 8539, 5429, 8548);
        path.ShouldNotBeNull("a 36-tile route around an obstacle must fit in the default budget");
        output.WriteLine($"route around the wall: {path.Count} waypoints");

        TilePathFinder.FindPath(grid, 5652, 8539, 5429, 8548, maxExpansions: 100)
            .ShouldBeNull("and a deliberately tiny budget must still return null, not throw");
    }

    /// <summary>⚠️ NULL MEANS "NOT FOUND WITHIN BUDGET", NEVER "IMPOSSIBLE" — and callers must fall back to
    /// walking rather than freezing, because refusing to move is a bigger fiction than moving badly.</summary>
    [SkippableFact]
    public void AnImpossibleGoalReturnsNullRatherThanThrowing()
    {
        var dir = BlockInfo();
        Skip.If(dir is null, "server data not present; set SHINE_DATA");

        var grid = WalkabilityGrid.Load(dir!, "RouVal02")!;
        TilePathFinder.FindPath(grid, 6223, 7280, -5000, -5000).ShouldBeNull();
    }
}
