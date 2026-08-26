namespace Fiesta.Emu.Zone.Mob;

/// <summary>Facing, in the server's own units — `DirectDistanceTable`.
///
/// <para><b>A direction unit is two degrees.</b> `ddt_Initialize` builds its table with
/// <c>atan(...) * 90 / PI</c>, where degrees would be <c>* 180 / PI</c>, so a full turn is
/// <see cref="UnitsPerTurn"/> = 180 units, not 360. Every angle the engine handles — facing, the
/// `DamageByAngle` table index — is in these units, and reading them as degrees inverts front and back.</para></summary>
public static class Direction
{
    /// <summary>Degrees per direction unit.</summary>
    public const int DegreesPerUnit = 2;

    /// <summary>A full turn, in direction units.</summary>
    public const int UnitsPerTurn = 180;

    /// <summary>`DirectDistanceTable::ddt_DirectSR(dx, dy)` — the direction from one point to another.
    ///
    /// <para>⚠️ <b>Computed here, table-driven there.</b> The original indexes a large precomputed table
    /// built at start-up by `ddt_Initialize`; this evaluates the same formula directly. That is a
    /// deviation and is <b>not</b> oracle-verified, because building the table under emulation means
    /// materialising tens of megabytes. Expect agreement to within a unit and treat exact equality at a
    /// boundary as unproven.</para></summary>
    public static int ddt_DirectSR(int dx, int dy)
    {
        var units = Math.Atan2(dy, dx) * (UnitsPerTurn / Math.PI) / 2.0;
        var rounded = (int)Math.Round(units);
        return ((rounded % UnitsPerTurn) + UnitsPerTurn) % UnitsPerTurn;
    }

    /// <summary>`DirectDistanceTable::ddt_ShineRadianDiff` — the shortest angular distance between two
    /// directions, in units, always in [0, <see cref="UnitsPerTurn"/> / 2].
    ///
    /// <para>Half a turn is 90 units = 180 degrees, so 90 is "directly behind" and 0 is "dead ahead".</para></summary>
    public static int ddt_ShineRadianDiff(int from, int to)
    {
        var diff = Math.Abs(((to - from) % UnitsPerTurn + UnitsPerTurn) % UnitsPerTurn);
        return Math.Min(diff, UnitsPerTurn - diff);
    }
}
