namespace Fiesta.Emu.Zone.Lua;

/// <summary>⭐ `.shbd` WALKABILITY — the map's walls, so a kite can be judged.
///
/// <para>Until this existed the simulation had no geometry at all: `walkTo` travelled in a straight line
/// through anything, so a character could flee through a mountain. That made one half of the operator's
/// report — the driver "kites straight into a nearby wall or group of multiple mobs" — untestable, and it
/// meant any kiting fix validated here would be unvalidated against the thing that most often ruins a
/// kite.</para>
///
/// <para><b>The format</b>, one bit per tile: two little-endian int32s, bytes-per-row then height, then
/// the rows. A SET bit is BLOCKED.</para>
/// <code>
/// blocked(tx, ty) = (data[8 + ty * bytesPerRow + (tx >> 3)] >> (tx &amp; 7)) &amp; 1
/// </code>
///
/// <para>⚠️ <b>THE ONE-TILE ORIGIN SHIFT IS REAL AND HARD-WON.</b> The bit at array index (i, j) describes
/// the world cell one tile over in EACH axis. The sibling bot project found this from a wall-hug trace and
/// it had previously cost it a hilly-map pathing wedge; a reader without the shift is wrong by 6.25 world
/// units in x and y, which is exactly enough to let a character walk into geometry and to reject the tile
/// it is standing on.</para>
///
/// <para>Implemented from the format rather than vendored: sibling repos are borrowed from for patterns,
/// not for files. The door overlay, erosion and MOVEFAIL-learning that the bot's own reader carries are
/// deliberately absent — this models the static map, and anything dynamic would be a claim the simulation
/// cannot back.</para></summary>
public sealed class WalkabilityGrid
{
    /// <summary>World units per tile: 50 world units per map-unit over 8 tiles per map-unit.</summary>
    public const double WorldPerTile = 6.25;

    /// <summary>The `.shbd` bit at (i, j) describes the world cell one tile over in each axis.</summary>
    public const int TileShift = 1;

    private readonly byte[] _data;
    private readonly int _bytesPerRow;

    public int WidthTiles { get; }
    public int HeightTiles { get; }

    private WalkabilityGrid(byte[] data, int bytesPerRow, int height)
    {
        _data = data;
        _bytesPerRow = bytesPerRow;
        HeightTiles = height;
        WidthTiles = bytesPerRow * 8;
    }

    /// <summary>Load a map's `.shbd`, or null when the map has none — a missing file is a map without
    /// geometry data, not an error, and the simulation then behaves as it did before: open ground.</summary>
    public static WalkabilityGrid? Load(string blockInfoDirectory, string mapName)
    {
        var path = Path.Combine(blockInfoDirectory, $"{mapName}.shbd");
        if (!File.Exists(path)) return null;

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8) return null;

        var bytesPerRow = BitConverter.ToInt32(bytes, 0);
        var height = BitConverter.ToInt32(bytes, 4);
        if (bytesPerRow <= 0 || height <= 0 || bytes.Length < 8L + (long)bytesPerRow * height) return null;

        return new WalkabilityGrid(bytes, bytesPerRow, height);
    }

    /// <summary>⚠️ Off the edge of the map is NOT walkable. A character that wanders outside the grid has
    /// left the world, and treating that as open ground would let a kite escape into nothing.</summary>
    public bool IsWalkableTile(int tx, int ty)
    {
        if ((uint)tx >= (uint)WidthTiles || (uint)ty >= (uint)HeightTiles) return false;
        return ((_data[8 + ty * _bytesPerRow + (tx >> 3)] >> (tx & 7)) & 1) == 0;
    }

    public bool IsWalkable(int worldX, int worldY)
    {
        if (worldX < 0 || worldY < 0) return false;
        return IsWalkableTile((int)(worldX / WorldPerTile) + TileShift,
                              (int)(worldY / WorldPerTile) + TileShift);
    }

    /// <summary>How much of a straight line from one point to another is walkable, sampled a tile at a
    /// time. Returns the last walkable point before the first blocked tile — which is where a character
    /// walking straight at a wall actually ends up.</summary>
    public (int X, int Y) FurthestAlong(int fromX, int fromY, int toX, int toY)
    {
        double dx = toX - fromX, dy = toY - fromY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < 1) return (fromX, fromY);

        var steps = (int)(distance / WorldPerTile) + 1;
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
        return (lastX, lastY);
    }

    /// <summary>⭐ HOW FAR ALONG A STRAIGHT LINE THE CHARACTER GETS, as a fraction of the distance asked
    /// for. 1.0 is a clear run.
    ///
    /// <para>⚠️ This replaced a strict "is the whole line clear" test, which was <b>a bad metric and said
    /// so loudly</b>: it flagged 37 of 37 kites on an open field map as "into a wall". A kite of several
    /// hundred units crosses fifty-odd tiles, and a map that is 83% walkable around the grind spot will
    /// have a scattered blocked tile on almost any such line — so the strict test measured path LENGTH,
    /// not obstruction. What matters is whether the character can actually get away, and that is a
    /// fraction, not a boolean.</para></summary>
    public double ReachableFraction(int fromX, int fromY, int toX, int toY)
    {
        double dx = toX - fromX, dy = toY - fromY;
        var wanted = Math.Sqrt(dx * dx + dy * dy);
        if (wanted < WorldPerTile) return 1;

        var (x, y) = FurthestAlong(fromX, fromY, toX, toY);
        double gx = x - fromX, gy = y - fromY;
        return Math.Sqrt(gx * gx + gy * gy) / wanted;
    }

    /// <summary>Whether a straight line gets essentially the whole way. Kept for callers that want a
    /// boolean; <see cref="ReachableFraction"/> is the honest measurement.</summary>
    public bool LineIsClear(int fromX, int fromY, int toX, int toY)
        => ReachableFraction(fromX, fromY, toX, toY) >= 0.99;
}
