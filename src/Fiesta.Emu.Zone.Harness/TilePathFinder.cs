namespace Fiesta.Emu.Zone.Lua;

/// <summary>⭐ A* OVER THE `.shbd` TILES — so `walkTo` routes around geometry the way the live one does.
///
/// <para>⚠️ <b>Without this, walls made the simulation lie about the driver.</b> `bot.walkTo` in the real
/// bot goes through its own pathfinder (`CoarsePathFinder` / `NavMeshPath`); here it was a straight line.
/// Switching walls on therefore punished the driver for a harness gap — measured, a level-25 Warrior on
/// Burning Hill fell from 19 kills to 1, having wandered into terrain it could not path out of, on a map
/// whose grind area is 83% walkable with all sixteen directions clear at 300 units.</para>
///
/// <para>This is deliberately a plain bounded A*, not a port of the bot's navmesh. The simulation needs
/// "can a character get there, roughly the way a real one would", not the bot's exact route; claiming to
/// reproduce its pathfinder would be a claim this cannot back. What it must not do is fail SILENTLY —
/// a refused path returns null and the caller decides, rather than quietly walking into a cliff.</para></summary>
public static class TilePathFinder
{
    /// <summary>⚠️ THERE IS NO EXPANSION BUDGET ANY MORE, and its absence is the point.
    ///
    /// <para>A bounded tile A* lived here with a cap of 20,000, chosen because "a kite is tens of tiles" —
    /// true of the DISTANCE and irrelevant to the SEARCH. It failed to route <b>100 of 101</b> `walkTo`
    /// calls on Burning Hill, because a 36-tile journey around an obstacle fans a grid search out over
    /// tens of thousands of tiles first. Raising the cap tenfold made it work and was still the wrong
    /// answer: the search was the wrong shape.</para>
    ///
    /// <para>The navmesh routes over regions instead — thousands of nodes, not millions of tiles — so
    /// there is nothing left to bound. Blocked ticks went from 844 to <b>0</b> and the run got faster.
    /// A budget parameter is not kept "just in case": one that no longer does anything is exactly the
    /// silent lie this file has already been bitten by.</para></summary>

    /// <summary>Route from one world point to another, as world-space waypoints (excluding the start).
    /// Null when no route was found inside the budget.</summary>
    /// <summary>One navmesh per grid, built on first use. Decomposition costs a pass over the tiles and
    /// is reused for every query on that map.</summary>
    private static readonly Dictionary<WalkabilityGrid, NavMesh> Meshes = [];

    public static NavMesh MeshFor(WalkabilityGrid grid)
    {
        lock (Meshes)
        {
            if (!Meshes.TryGetValue(grid, out var mesh)) Meshes[grid] = mesh = NavMesh.Build(grid);
            return mesh;
        }
    }

    public static IReadOnlyList<(int X, int Y)>? FindPath(
        WalkabilityGrid grid, int fromX, int fromY, int toX, int toY)
    {
        var start = ToTile(fromX, fromY);
        var goal = ToTile(toX, toY);

        // ⚠️ An unwalkable GOAL is the common case, not an error: the driver aims at a mob standing in
        // terrain this grid calls blocked, or at a point inside a wall. Slide the goal to the nearest
        // walkable tile rather than refusing, which is what a player does without thinking about it.
        if (!grid.IsWalkableTile(goal.X, goal.Y) && NearestWalkable(grid, goal) is { } slid) goal = slid;
        if (!grid.IsWalkableTile(goal.X, goal.Y)) return null;

        // ⭐ AND THE SAME FOR THE START, which is the asymmetry that made this useless.
        //
        // Refusing to route from an unwalkable tile sounds principled and is not: a character brushing a
        // wall lands in a tile this grid calls blocked (the slide checks the step's destination, not
        // whether the tile it settles in is open at the grid's rounding), and from then on EVERY path
        // request fails. Measured on Burning Hill with walls on: `walkTo` called 101 times, routed 1,
        // no-path 100 — so the driver fell back to straight lines, walked into geometry, and was blocked
        // on 844 of 1,500 ticks. It looked exactly like a bot that cannot navigate.
        //
        // A character that is somehow inside geometry still has to be able to leave.
        if (!grid.IsWalkableTile(start.X, start.Y) && NearestWalkable(grid, start) is { } out_) start = out_;
        if (!grid.IsWalkableTile(start.X, start.Y)) return null;
        if (start == goal) return [];

        // ⭐ THE REGION GRAPH FIRST. Tile A* pays full-grid cost to decide WHICH WAY to go; the navmesh
        // answers that over thousands of rectangles instead of millions of tiles, and the straight line
        // between portal crossings answers the rest. This is what took `walkTo` from routing 1 call in 101
        // to routing all of them.
        var mesh = MeshFor(grid);
        var route = mesh.RegionRoute(mesh.RegionAt(start.X, start.Y), mesh.RegionAt(goal.X, goal.Y));
        if (route is null) return null;

        var waypoints = new List<(int X, int Y)>();
        var atX = fromX;
        var atY = fromY;
        for (var i = 1; i < route.Count; i++)
        {
            // Cross each portal at the middle of its run. The funnel algorithm would choose a better
            // crossing; string-pulling below recovers most of that, and claiming a funnel this is not
            // would be worse than being plainly approximate.
            var portal = mesh.Portals[route[i - 1]].First(p => p.To == route[i]);
            var (px, py) = portal.Vertical
                ? (portal.At, portal.Middle)
                : (portal.Middle, portal.At);
            waypoints.Add(TileCentreWorld(px, py));
        }
        waypoints.Add((toX, toY));
        return Smooth(grid, atX, atY, waypoints, toX, toY);

        // (No tile A* below: the navmesh answers the routing question. An earlier bounded grid search
        // lived here and was removed rather than left as a fallback -- two pathfinders disagreeing about
        // what is reachable is how `canReach` starts lying.)
    }

    /// <summary>The tile a world point sits in, with the grid's origin shift applied — the same mapping
    /// <see cref="WalkabilityGrid.IsWalkable"/> uses, so the two can never disagree.</summary>
    private static (int X, int Y) ToTile(int worldX, int worldY)
        => ((int)(worldX / WalkabilityGrid.WorldPerTile) + WalkabilityGrid.TileShift,
            (int)(worldY / WalkabilityGrid.WorldPerTile) + WalkabilityGrid.TileShift);

    /// <summary>The CENTRE of a tile in world units. Portal crossings must not be tile corners — a corner
    /// can sit inside the neighbouring wall, and it is also what made the old per-tile route aim backwards.</summary>
    private static (int X, int Y) TileCentreWorld(int tileX, int tileY)
        => ((int)((tileX - WalkabilityGrid.TileShift + 0.5) * WalkabilityGrid.WorldPerTile),
            (int)((tileY - WalkabilityGrid.TileShift + 0.5) * WalkabilityGrid.WorldPerTile));



    /// <summary>The closest walkable tile to a blocked one, searched in rings. Bounded — a goal deep
    /// inside a mountain has no near neighbour and the caller gets null.</summary>
    private static (int X, int Y)? NearestWalkable(WalkabilityGrid grid, (int X, int Y) tile, int maxRing = 8)
    {
        for (var r = 1; r <= maxRing; r++)
            for (var dx = -r; dx <= r; dx++)
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;   // ring edge only
                    if (grid.IsWalkableTile(tile.X + dx, tile.Y + dy)) return (tile.X + dx, tile.Y + dy);
                }
        return null;
    }

    /// <summary>⭐ STRING-PULL THE ROUTE: keep only the waypoints that geometry actually forces.
    ///
    /// <para>⚠️ <b>Without this the pathfinder made the driver worse, not better.</b> A* returns one
    /// waypoint per TILE and each is the tile's origin CORNER, so the first one can sit up to 6.25 units
    /// BEHIND a character standing mid-tile. `walkTo` is called every tick while chasing, so each call
    /// re-routed and re-aimed at that corner: the character oscillated instead of closing, and its casts
    /// fell from 48 to 4 in a 150-second run while it stood in melee range doing nothing.</para>
    ///
    /// <para>Keeping the furthest waypoint still on a clear line makes the walk head where a player would
    /// head — straight at the target on open ground, hugging only the corners that matter.</para></summary>
    private static List<(int X, int Y)> Smooth(
        WalkabilityGrid grid, int fromX, int fromY, List<(int X, int Y)> route, int toX, int toY)
    {
        if (route.Count > 0) route[^1] = (toX, toY);          // finish where the caller asked, not at a corner

        var pulled = new List<(int X, int Y)>();
        var atX = fromX;
        var atY = fromY;
        var i = 0;
        while (i < route.Count)
        {
            // The furthest waypoint reachable in a straight line from here.
            var furthest = i;
            for (var j = route.Count - 1; j > i; j--)
            {
                if (!grid.LineIsClear(atX, atY, route[j].X, route[j].Y)) continue;
                furthest = j;
                break;
            }

            pulled.Add(route[furthest]);
            (atX, atY) = route[furthest];
            i = furthest + 1;
        }
        return pulled;
    }

}
