using Fiesta.Emu.Zone.Data;

namespace Fiesta.Emu.Zone.Mob;

/// <summary>The Microsoft CRT's <c>rand()</c>.
///
/// <para>Spawn placement uses this, <b>not</b> the WELL512 generator that combat draws from — the
/// rectangular picker calls <c>_rand</c> directly. Two different random sources in one server is the kind
/// of thing that silently ruins reproducibility if you assume there is only one, so it is modelled
/// separately rather than folded into <c>cWell512Random</c>.</para>
///
/// <para>The algorithm is the documented MSVC LCG: <c>seed = seed * 214013 + 2531011</c>, returning
/// <c>(seed &gt;&gt; 16) &amp; 0x7FFF</c>. ⚠️ Not verified against the binary — `_rand` is CRT code, not
/// game code, so this is taken from the published behaviour rather than read out of `Zone.exe`.</para></summary>
public sealed class CrtRandom
{
    private uint _seed;

    public CrtRandom(uint seed = 1) => _seed = seed;

    public const int RandMax = 0x7FFF;

    public int rand()
    {
        _seed = unchecked(_seed * 214013u + 2531011u);
        return (int)((_seed >> 16) & 0x7FFF);
    }
}

/// <summary>Picking a spawn point inside a regen area.
///
/// <para>Ported from `MobRegenClass::MobRegenLoc_Rectangle::mrlr_Get` (0x004B1000) and
/// `MobRegenLoc_Circular::mrlc_Get` (0x004B0E80).</para></summary>
public static class MobRegenLoc
{
    /// <summary>`mrlr_Get` — a uniform point in a ROTATED rectangle.
    ///
    /// <para>The original picks an offset in [-halfWidth, halfWidth) x [-halfHeight, halfHeight) with two
    /// <c>rand()</c> calls, then rotates it with a <b>fixed-point</b> cos/sin scaled by 1024 and shifts
    /// right by 10. The <c>cdq; and edx,0x3FF; lea</c> sequence around each shift is the standard
    /// round-toward-zero correction for arithmetic-shifting a negative value — without it, points left of
    /// centre drift by one unit.</para>
    ///
    /// <para>Argument order matters and is easy to invert: the original computes
    /// <c>x = c*dy - s*dx</c> and <c>y = s*dy + c*dx</c>, i.e. <c>dy</c> pairs with the cosine.</para></summary>
    public static (int X, int Y) Rectangle(MobRegenGroup group, CrtRandom rng)
    {
        var halfW = group.Width;
        var halfH = group.Height;

        var dx = halfW > 0 ? rng.rand() % (2 * halfW) - halfW : 0;
        var dy = halfH > 0 ? rng.rand() % (2 * halfH) - halfH : 0;

        var radians = group.RotationDegrees * Math.PI / 180.0;
        var c = (int)Math.Round(Math.Cos(radians) * 1024);
        var s = (int)Math.Round(Math.Sin(radians) * 1024);

        return (group.CenterX + ShiftRoundToZero(c * dy - s * dx),
                group.CenterY + ShiftRoundToZero(s * dy + c * dx));
    }

    /// <summary>`mrlc_Get` — a point in a circle of <see cref="MobRegenGroup.Radius"/>.
    ///
    /// <para>⚠️ <b>Approximated.</b> The original draws from a precomputed word table with a wrapping
    /// index (globals at 0x14D920E8 / 0x14D8A0E8), not from <c>rand()</c>, and that table's contents are
    /// built at start-up. This uses a uniform disc sample instead, which gives the same distribution but
    /// NOT the same sequence — so circular spawns are statistically right and not reproducible against
    /// the server. Reading that table's construction would close it.</para></summary>
    public static (int X, int Y) Circular(MobRegenGroup group, CrtRandom rng)
    {
        var r = group.Radius;
        if (r <= 0) return (group.CenterX, group.CenterY);

        // sqrt of a uniform draw, so points are uniform over AREA rather than clustered at the centre.
        var u = rng.rand() / (double)(CrtRandom.RandMax + 1);
        var theta = rng.rand() / (double)(CrtRandom.RandMax + 1) * 2.0 * Math.PI;
        var dist = Math.Sqrt(u) * r;

        return (group.CenterX + (int)Math.Round(Math.Cos(theta) * dist),
                group.CenterY + (int)Math.Round(Math.Sin(theta) * dist));
    }

    /// <summary>Pick a point in whichever shape this group is.</summary>
    public static (int X, int Y) Sample(MobRegenGroup group, CrtRandom rng)
        => group.IsCircular ? Circular(group, rng) : Rectangle(group, rng);

    /// <summary>`>> 10` with the original's round-toward-zero fix-up.</summary>
    private static int ShiftRoundToZero(int value)
    {
        var correction = value < 0 ? 0x3FF : 0;
        return (value + correction) >> 10;
    }
}
