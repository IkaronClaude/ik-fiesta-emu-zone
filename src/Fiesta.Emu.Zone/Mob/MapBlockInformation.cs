namespace Fiesta.Emu.Zone.Mob;

/// <summary>`MapBlock::MapBlockInformation` — the map's collision, ported 1:1 from `Zone.exe`.
///
/// <para>Two bitmaps, loaded from two FILES by `mbi_Load` (0x0049E3B0): `BlockInfo/&lt;map&gt;.shab` into
/// <see cref="AttackBlockBuffer"/> first, then `BlockInfo/&lt;map&gt;.shbd` into
/// <see cref="MoveBlockBuffer"/>. Each file is <c>[bytesPerRow u32][rows u32][bitmap rows*bytesPerRow]</c>,
/// so the bitmap starts at offset 8 and the header gives the stride directly.</para>
///
/// <para>The fields are the struct's own, at the offsets the code uses:
/// <c>mbi_blockxsize</c> +8 = <c>bytesPerRow * 8</c>, <c>mbi_blockysize</c> +0xC = rows,
/// <c>mbi_xbyte</c> +0x10 = bytesPerRow, <c>mbi_MoveBlockBuffer</c> +0x14, and
/// <c>mbi_mapxsize</c> +0 = <c>blockxsize * 625 / 100</c> — which is where the 6.25 comes from.</para></summary>
public sealed class MapBlockInformation
{
    /// <summary>World units per tile. `mbi_Load` computes `mapxsize = blockxsize * 0x271 / 100`, and
    /// 0x271 is 625 — so a tile is 6.25 world units.</summary>
    public const double WorldPerTile = 6.25;

    /// <summary>`mbi_xbyte` — bytes per row of the bitmap.</summary>
    public required int XByte { get; init; }

    /// <summary>`mbi_blockxsize` — <c>xbyte * 8</c>, the width in TILES.</summary>
    public int BlockXSize => XByte * 8;

    /// <summary>`mbi_blockysize` — the height in tiles.</summary>
    public required int BlockYSize { get; init; }

    /// <summary>`mbi_MoveBlockBuffer` — the `.shbd` bitmap, bitmap bytes only (header already skipped).</summary>
    public required byte[] MoveBlockBuffer { get; init; }

    /// <summary>`mbi_AttackBlockBuffer` — the `.shab` bitmap, when that file was present.</summary>
    public byte[]? AttackBlockBuffer { get; init; }

    /// <summary>⭐ THE TILE A WORLD COORDINATE FALLS IN — `mbi_IsMoveBlock` (0x0049DF70) verbatim:
    ///
    /// <code>
    /// lea ecx, [eax*8]              ; v * 8
    /// mov eax, 0x51eb851f ; mul ecx ; edx = high32(v*8 * 0x51EB851F)
    /// shr ecx, 4                    ; tile = that >> 4
    /// </code>
    ///
    /// <para>Which is exactly <c>(v * 8) / 50</c>, i.e. <c>floor(v / 6.25)</c>. <b>THERE IS NO OFFSET.</b>
    /// No +1, no -1, on either axis. Verified against the float form for every coordinate 0..199,999:
    /// zero differences.</para></summary>
    public static int ToTile(int world)
    {
        var n = (uint)world * 8u;
        return (int)((ulong)n * 0x51EB851FUL >> 32 >> 4);
    }

    /// <summary>`mbi_IsMoveBlock` (0x0049DF70) — is this world point blocked for MOVEMENT.
    ///
    /// <para>⚠️ <b>OUT OF BOUNDS IS BLOCKED</b>, not walkable: both bounds tests are `jae` to a
    /// <c>mov al, 1</c>. And the bit being SET means blocked — <c>and eax, (1 &lt;&lt; (tileX &amp; 7))</c>
    /// is the return value, so a set bit returns non-zero.</para></summary>
    public bool mbi_IsMoveBlock(int worldX, int worldY)
        => IsBlockedIn(MoveBlockBuffer, worldX, worldY);

    /// <summary>`mbi_IsAttackBlock` (0x0049DF00) — the same test against the `.shab` bitmap. Blocked when
    /// the file was absent, because out of bounds is blocked and there is nothing to read.</summary>
    public bool mbi_IsAttackBlock(int worldX, int worldY)
        => AttackBlockBuffer is null || IsBlockedIn(AttackBlockBuffer, worldX, worldY);

    private bool IsBlockedIn(byte[] buffer, int worldX, int worldY)
    {
        if (worldX < 0 || worldY < 0) return true;
        var tileX = ToTile(worldX);
        var tileY = ToTile(worldY);

        // cmp ecx, [esi+8] / jae   -- unsigned, so a negative tile is huge and also fails
        if ((uint)tileX >= (uint)BlockXSize) return true;
        if ((uint)tileY >= (uint)BlockYSize) return true;

        // eax = xbyte*tileY + (tileX >> 3);  edx = 1 << (tileX & 7);  return buffer[eax] & edx
        var index = XByte * tileY + (tileX >> 3);
        if ((uint)index >= (uint)buffer.Length) return true;
        return (buffer[index] & (1 << (tileX & 7))) != 0;
    }

    /// <summary>Load a map's collision the way `mbi_Load` does: `.shab` then `.shbd`, each
    /// <c>[bytesPerRow u32][rows u32][bitmap]</c>. The `.shbd` is required; a missing `.shab` leaves
    /// attack-blocking unavailable rather than guessed.</summary>
    public static MapBlockInformation? Load(string blockInfoDirectory, string mapName)
    {
        var shbd = Path.Combine(blockInfoDirectory, mapName + ".shbd");
        if (!File.Exists(shbd)) return null;

        var (move, xbyte, rows) = ReadBitmap(shbd);
        if (move is null) return null;

        byte[]? attack = null;
        var shab = Path.Combine(blockInfoDirectory, mapName + ".shab");
        if (File.Exists(shab)) attack = ReadBitmap(shab).Bitmap;

        return new MapBlockInformation
        {
            XByte = xbyte,
            BlockYSize = rows,
            MoveBlockBuffer = move,
            AttackBlockBuffer = attack,
        };
    }

    private static (byte[]? Bitmap, int XByte, int Rows) ReadBitmap(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 8) return (null, 0, 0);
        var xbyte = BitConverter.ToInt32(raw, 0);
        var rows = BitConverter.ToInt32(raw, 4);
        if (xbyte <= 0 || rows <= 0) return (null, 0, 0);

        var need = (long)xbyte * rows;
        if (raw.Length - 8 < need) return (null, 0, 0);

        var bits = new byte[need];
        Array.Copy(raw, 8, bits, 0, need);
        return (bits, xbyte, rows);
    }
}
