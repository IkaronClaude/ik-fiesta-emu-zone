using Fiesta.Bot.Pathfinding;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>⭐ THE MAP'S WALLS — a thin adapter over the BOT'S OWN `BlockGrid`, copied verbatim into
/// `Pathfinding/` on the operator's instruction: <i>"Just use the shbd code and navmesh from the bots -
/// it works and is correct"</i>.
///
/// <para>⚠️ <b>This used to be a reimplementation, and every piece of it was wrong in a way that looked
/// like the BOT misbehaving.</b> Its goal-slide searched 8 tiles while a refused destination sat 32 tiles
/// — 200 world units — inside a wall; a bounded tile A* silently turned itself off and failed to route
/// 100 of 101 walks; a per-candidate reachability check made a 150-second run take 179 seconds. Each was
/// diagnosed as a driver defect first. Using the working implementation removes the whole class.</para>
///
/// <para>The adapter exists only so the rest of the harness keeps one vocabulary — world coordinates, and
/// a nullable grid meaning "this map has no geometry data". It adds no logic of its own.</para></summary>
public sealed class WalkabilityGrid
{
    /// <summary>World units per tile — `BlockGrid`'s own constant, re-exported so callers need not reach
    /// past the adapter.</summary>
    public const double WorldPerTile = BlockGrid.WorldPerTile;

    /// <summary>⚠️ ZERO, deliberately. `BlockGrid` applies the one-tile `.shbd` origin shift INSIDE
    /// `WorldToTile`, so adding another here would double it. Callers converting world to tile themselves
    /// must go through <see cref="ToTile"/> rather than doing the arithmetic.</summary>
    public const int TileShift = 0;

    public BlockGrid Grid { get; }

    private WalkabilityGrid(BlockGrid grid) => Grid = grid;

    public int WidthTiles => Grid.WidthTiles;
    public int HeightTiles => Grid.HeightTiles;

    /// <summary>Load a map's `.shbd`, or null when the map has none — a missing file is a map without
    /// geometry data, not an error, and the simulation then behaves as open ground.</summary>
    public static WalkabilityGrid? Load(string blockInfoDirectory, string mapName)
    {
        var path = Path.Combine(blockInfoDirectory, $"{mapName}.shbd");
        if (!File.Exists(path)) return null;
        try { return new WalkabilityGrid(BlockGrid.Load(path)); }
        catch { return null; }
    }

    /// <summary>The tile a world point sits in, the way `BlockGrid` does it — origin shift included.</summary>
    public (int X, int Y) ToTile(int worldX, int worldY)
        => Grid.WorldToTile((uint)Math.Max(0, worldX), (uint)Math.Max(0, worldY));

    public bool IsWalkableTile(int tileX, int tileY) => Grid.IsWalkableTile(tileX, tileY);

    public bool IsWalkable(int worldX, int worldY)
        => worldX >= 0 && worldY >= 0 && Grid.IsWalkableWorld((uint)worldX, (uint)worldY);

    /// <summary>How far along a straight line the character gets before geometry stops it, as a fraction
    /// of the distance asked for.
    ///
    /// <para>⚠️ A FRACTION, not a boolean. A strict whole-line test once flagged 37 of 37 kites on open
    /// ground as "into a wall": a path of several hundred units crosses fifty-odd tiles, and a map that is
    /// mostly walkable will still clip a scattered one on nearly any line — so it measured path LENGTH
    /// rather than obstruction.</para></summary>
    public double ReachableFraction(int fromX, int fromY, int toX, int toY)
    {
        double dx = toX - fromX, dy = toY - fromY;
        var wanted = Math.Sqrt(dx * dx + dy * dy);
        if (wanted < WorldPerTile) return 1;

        var steps = (int)(wanted / WorldPerTile) + 1;
        var lastX = fromX;
        var lastY = fromY;
        for (var i = 1; i <= steps; i++)
        {
            var x = (int)(fromX + dx * i / steps);
            var y = (int)(fromY + dy * i / steps);
            if (!IsWalkable(x, y)) break;
            lastX = x;
            lastY = y;
        }

        double gx = lastX - fromX, gy = lastY - fromY;
        return Math.Sqrt(gx * gx + gy * gy) / wanted;
    }

    public bool LineIsClear(int fromX, int fromY, int toX, int toY)
        => ReachableFraction(fromX, fromY, toX, toY) >= 0.99;
}
