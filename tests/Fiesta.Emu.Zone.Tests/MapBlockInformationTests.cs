using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ THE MAP'S COLLISION, 1:1 FROM `Zone.exe` — and the bots' version checked against it.
///
/// <para>`mbi_IsMoveBlock` (0x0049DF70) in full:</para>
/// <code>
/// tile   = (v * 8) * 0x51EB851F >> 32 >> 4      // == floor(v / 6.25). NO OFFSET.
/// if (tileX >= blockxsize || tileY >= blockysize) return BLOCKED
/// return MoveBlockBuffer[xbyte * tileY + (tileX >> 3)] &amp; (1 &lt;&lt; (tileX &amp; 7))
/// </code>
///
/// <para>The bots' `BlockGrid` matches every part of that -- the 6.25, the row stride, the `tx >> 3`
/// byte, the `1 &lt;&lt; (tx &amp; 7)` bit, set-bit-means-blocked, and out-of-bounds-means-blocked -- and
/// then adds <c>ShbdTileShift = 1</c> to BOTH axes. The server has no such shift.</para></summary>
public class MapBlockInformationTests(ITestOutputHelper output)
{
    private static string? BlockInfo()
    {
        var d = Environment.GetEnvironmentVariable("BLOCKINFO_DIR")
                ?? @"Z:/ServerSource/9Data/Shine/BlockInfo";
        return File.Exists(Path.Combine(d, "RouVal02.shbd")) ? d : null;
    }

    /// <summary>The tile conversion, against the arithmetic the instructions perform.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 1)]        // the first boundary: 6.25 rounds up to 7
    [InlineData(12, 1)]
    [InlineData(13, 2)]
    [InlineData(625, 100)]
    [InlineData(6250, 1000)]
    public void ToTileIsTheBinarysOwnDivision(int world, int expected)
        => MapBlockInformation.ToTile(world).ShouldBe(expected);

    /// <summary>⭐ THE ANSWER TO THE (1,1) QUESTION. With the shift removed the bots agree with the server
    /// on EVERY sampled point; with it they do not.</summary>
    [SkippableTheory]
    [InlineData("RouVal02")]
    [InlineData("ValDn01")]
    [InlineData("RouN")]
    public void TheBotsGridMatchesTheServerOnlyWithNoTileShift(string map)
    {
        var dir = BlockInfo();
        Skip.If(dir is null, "BlockInfo not present");

        var zone = MapBlockInformation.Load(dir!, map);
        zone.ShouldNotBeNull();
        var bots = WalkabilityGrid.Load(dir!, map);
        bots.ShouldNotBeNull();

        int sampled = 0, disagree = 0, refused = 0, intoWall = 0;
        for (var ty = 0; ty < zone!.BlockYSize; ty += 4)
        {
            for (var tx = 0; tx < zone.BlockXSize; tx += 4)
            {
                // A world point inside this tile.
                var x = (int)(tx * MapBlockInformation.WorldPerTile) + 3;
                var y = (int)(ty * MapBlockInformation.WorldPerTile) + 3;

                var serverBlocked = zone.mbi_IsMoveBlock(x, y);
                var botWalkable = bots!.IsWalkable(x, y);
                sampled++;
                if (serverBlocked == !botWalkable) continue;
                disagree++;
                if (!serverBlocked && !botWalkable) refused++;    // refuses ground the server allows
                if (serverBlocked && botWalkable) intoWall++;     // walks at a wall -> MOVEFAIL
            }
        }

        output.WriteLine($"{map}: sampled {sampled}, disagreements {disagree} ({100.0 * disagree / sampled:F3}%)");
        output.WriteLine($"   refuses walkable ground: {refused}   walks into a wall: {intoWall}");

        // ⚠️ THIS IS THE ASSERTION THE OPERATOR ASKED FOR. It documents the CURRENT state: the bots carry
        // ShbdTileShift = 1 and therefore do NOT match. When that constant goes to 0 this flips, and the
        // line below is the one to change -- deliberately, so nobody "fixes" the test instead of the shift.
        var shift = Fiesta.Bot.Pathfinding.BlockGrid.ShbdTileShiftPublic;
        output.WriteLine($"   BlockGrid.ShbdTileShift = {shift}");
        if (shift == 0)
            disagree.ShouldBe(0, "with no shift the bots must agree with Zone.exe exactly");
        else
            disagree.ShouldBeGreaterThan(0,
                "ShbdTileShift is non-zero, so a disagreement with the server is expected and measured; "
                + "if this ever reads zero the shift has stopped mattering and should simply be removed");
    }
}
