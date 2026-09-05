using Fiesta.Bot.Pathfinding;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>⭐ ROUTING — delegates to the BOT'S OWN `NavMesh` / `NavMeshPath`, copied verbatim into
/// `Pathfinding/` on the operator's instruction. See that folder's README for what the hand-rolled
/// version cost before it was thrown away.</summary>
public static class TilePathFinder
{
    private static readonly Dictionary<WalkabilityGrid, NavMesh> Meshes = [];

    /// <summary>The map's navmesh, built once per grid.</summary>
    public static NavMesh MeshFor(WalkabilityGrid grid)
    {
        lock (Meshes)
        {
            if (!Meshes.TryGetValue(grid, out var mesh)) Meshes[grid] = mesh = grid.Grid.Mesh();
            return mesh;
        }
    }

    /// <summary>Route between two world points as world-space waypoints, excluding the start. Null when
    /// the mesh cannot route it.
    ///
    /// <para>⚠️ Null now means the MESH could not route it — a real answer. The hand-rolled predecessor
    /// returned null when its expansion budget ran out, which is a different statement entirely and one
    /// the caller had no way to tell apart.</para></summary>
    public static IReadOnlyList<(int X, int Y)>? FindPath(
        WalkabilityGrid grid, int fromX, int fromY, int toX, int toY)
    {
        var (sx, sy) = grid.ToTile(fromX, fromY);
        var (gx, gy) = grid.ToTile(toX, toY);

        var tiles = NavMeshPath.Find(MeshFor(grid), sx, sy, gx, gy);
        if (tiles is null || tiles.Count < 2) return null;

        var route = new List<(int X, int Y)>(tiles.Count - 1);
        // Skip the first tile: it is where we already are.
        for (var i = 1; i < tiles.Count; i++)
        {
            var (wx, wy) = grid.Grid.TileToWorld(tiles[i].X, tiles[i].Y);
            route.Add(((int)wx, (int)wy));
        }
        if (route.Count > 0) route[^1] = (toX, toY);   // finish where the caller asked, not at a tile corner
        return route;
    }

    /// <summary>Walkable route length in world units, or -1 when the mesh cannot route it. 0 when the
    /// caller is already there.</summary>
    public static double RouteLength(WalkabilityGrid grid, int fromX, int fromY, int toX, int toY)
    {
        var route = FindPath(grid, fromX, fromY, toX, toY);
        if (route is null) return -1;

        double total = 0;
        var (px, py) = (fromX, fromY);
        foreach (var (x, y) in route)
        {
            total += Math.Sqrt((double)(x - px) * (x - px) + (double)(y - py) * (y - py));
            (px, py) = (x, y);
        }
        return total;
    }

    /// <summary>Whether a route exists at all, without building one — the question a target picker has to
    /// ask about every candidate on every tick, so it must not be a search.</summary>
    public static bool Reachable(WalkabilityGrid grid, int fromX, int fromY, int toX, int toY)
    {
        var (sx, sy) = grid.ToTile(fromX, fromY);
        var (gx, gy) = grid.ToTile(toX, toY);
        try { return NavMeshPath.Reachable(MeshFor(grid), sx, sy, gx, gy); }
        catch { return true; }   // mesh unavailable: do not veto the move
    }
}
