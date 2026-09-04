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
    /// <summary>Cap on nodes expanded. A kite is tens of tiles; a cross-map trek is thousands and is not
    /// what this is for. Hitting the cap returns null — <b>"I could not find a path within budget", which
    /// is not the same as "there is no path"</b>, and callers must not read it as the latter.</summary>
    public const int DefaultMaxExpansions = 20_000;

    private static readonly (int Dx, int Dy)[] Neighbours =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    ];

    /// <summary>Route from one world point to another, as world-space waypoints (excluding the start).
    /// Null when no route was found inside the budget.</summary>
    public static IReadOnlyList<(int X, int Y)>? FindPath(
        WalkabilityGrid grid, int fromX, int fromY, int toX, int toY,
        int maxExpansions = DefaultMaxExpansions)
    {
        var start = ToTile(fromX, fromY);
        var goal = ToTile(toX, toY);

        // ⚠️ An unwalkable GOAL is the common case, not an error: the driver aims at a mob standing in
        // terrain this grid calls blocked, or at a point inside a wall. Slide the goal to the nearest
        // walkable tile rather than refusing, which is what a player does without thinking about it.
        if (!grid.IsWalkableTile(goal.X, goal.Y) && NearestWalkable(grid, goal) is { } slid) goal = slid;
        if (!grid.IsWalkableTile(goal.X, goal.Y)) return null;
        if (!grid.IsWalkableTile(start.X, start.Y)) return null;
        if (start == goal) return [];

        var open = new PriorityQueue<(int X, int Y), int>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var cost = new Dictionary<(int X, int Y), int> { [start] = 0 };
        open.Enqueue(start, Heuristic(start, goal));

        var expanded = 0;
        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal) return Smooth(grid, fromX, fromY, Rebuild(cameFrom, current), toX, toY);
            if (++expanded > maxExpansions) return null;

            foreach (var (dx, dy) in Neighbours)
            {
                var next = (X: current.X + dx, Y: current.Y + dy);
                if (!grid.IsWalkableTile(next.X, next.Y)) continue;

                // ⚠️ CORNER CUTTING IS ALLOWED, because the MOVEMENT MODEL allows it.
                //
                // Refusing it is the textbook choice and it was wrong here: `AdvanceWalk`'s wall slide
                // moves one axis at a time and therefore squeezes through a diagonal gap quite happily.
                // With the pathfinder refusing what movement permits, a character could walk INTO a pocket
                // it could not be routed out of — measured on Burning Hill, stranded at (5652,8539) with
                // no path to 15 of its 20 nearest mobs, one of them 223 units away on open ground.
                //
                // The two must agree on what is passable. They agree HERE, permissively, matching the
                // slide; the alternative is to forbid the slide, which would pin a character on any wall
                // it brushed. If the movement model ever gains a proper slide vector, revisit both
                // together and never just one.
                if (dx != 0 && dy != 0 &&
                    !grid.IsWalkableTile(current.X + dx, current.Y) &&
                    !grid.IsWalkableTile(current.X, current.Y + dy))
                    continue;                                 // both orthogonals blocked: a true corner

                var step = dx != 0 && dy != 0 ? 14 : 10;      // 1.4 ~ sqrt 2, in tenths
                var candidate = cost[current] + step;
                if (cost.TryGetValue(next, out var known) && candidate >= known) continue;

                cost[next] = candidate;
                cameFrom[next] = current;
                open.Enqueue(next, candidate + Heuristic(next, goal));
            }
        }
        return null;
    }

    /// <summary>The tile a world point sits in, with the grid's origin shift applied — the same mapping
    /// <see cref="WalkabilityGrid.IsWalkable"/> uses, so the two can never disagree.</summary>
    private static (int X, int Y) ToTile(int worldX, int worldY)
        => ((int)(worldX / WalkabilityGrid.WorldPerTile) + WalkabilityGrid.TileShift,
            (int)(worldY / WalkabilityGrid.WorldPerTile) + WalkabilityGrid.TileShift);

    private static (int X, int Y) ToWorld((int X, int Y) tile)
        => ((int)((tile.X - WalkabilityGrid.TileShift) * WalkabilityGrid.WorldPerTile),
            (int)((tile.Y - WalkabilityGrid.TileShift) * WalkabilityGrid.WorldPerTile));

    /// <summary>Chebyshev-with-diagonals, matching the step costs above so the heuristic never
    /// overestimates and A* stays admissible.</summary>
    private static int Heuristic((int X, int Y) a, (int X, int Y) b)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        return 10 * (dx + dy) + (14 - 2 * 10) * Math.Min(dx, dy);
    }

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

    private static List<(int X, int Y)> Rebuild(
        Dictionary<(int X, int Y), (int X, int Y)> cameFrom, (int X, int Y) at)
    {
        var tiles = new List<(int X, int Y)> { at };
        while (cameFrom.TryGetValue(at, out var previous))
        {
            at = previous;
            tiles.Add(at);
        }
        tiles.Reverse();
        tiles.RemoveAt(0);                                   // the start is where we already are
        return [.. tiles.Select(ToWorld)];
    }
}
