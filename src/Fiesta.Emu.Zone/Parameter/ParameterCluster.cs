namespace Fiesta.Emu.Zone.Parameter;

/// <summary>`Parameter::Cluster` — 51 int32 stat slots, one per <see cref="Stat"/>.
///
/// <para>Ported from the two operators the server defines on it, both fully unrolled over all 51 slots:
/// <c>Cluster::operator+=</c> (0x004C9040) and <c>Cluster::operator*=</c> (0x004C9160).</para></summary>
public sealed class ParameterCluster
{
    /// <summary>51 — <c>rep movsd</c> with <c>ecx = 0x33</c> in `c_clear`, and exactly the number of
    /// <see cref="Stat"/> values.</summary>
    public const int SlotCount = 51;

    /// <summary>The identity for <see cref="Add"/>: adding zero changes nothing.</summary>
    public const int PlusIdentity = 0;

    /// <summary>The identity for <see cref="ApplyRate"/>. Rates are permille, so 1000 means "unchanged" —
    /// and <c>operator*=</c> tests for exactly this value and skips the slot, which is what pins it.</summary>
    public const int RateIdentity = 1000;

    private readonly int[] _slots;

    private ParameterCluster(int[] slots) => _slots = slots;

    /// <summary>A cluster seeded with <see cref="PlusIdentity"/> — the server's `parameter_eraser_plus`.</summary>
    public static ParameterCluster Plus() => new(new int[SlotCount]);

    /// <summary>A cluster seeded with <see cref="RateIdentity"/> — the server's `parameter_eraser_rate`.
    ///
    /// <para>⚠️ The two eraser globals live in the executable's uninitialised section (VA 0x0074C000
    /// upward), so their CONTENTS cannot be read out of the file — they are filled at start-up. The values
    /// here are taken from the operators instead, which is a stronger source than a memory dump anyway:
    /// <c>operator*=</c> compares each slot against 1000 and skips it, so 1000 is provably the no-op, and 0
    /// is provably the no-op for a field-wise add.</para></summary>
    public static ParameterCluster Rate()
    {
        var slots = new int[SlotCount];
        Array.Fill(slots, RateIdentity);
        return new ParameterCluster(slots);
    }

    /// <summary>Seed by half, so callers can say which kind they want without repeating the mapping.</summary>
    public static ParameterCluster For(StatHalf half)
        => half == StatHalf.Rate ? Rate() : Plus();

    public int this[Stat stat]
    {
        get => _slots[(int)stat];
        set => _slots[(int)stat] = value;
    }

    public ParameterCluster Clone() => new((int[])_slots.Clone());

    /// <summary>`Cluster::operator+=` — field-wise addition, all 51 slots.</summary>
    public void Add(ParameterCluster other)
    {
        for (var i = 0; i < SlotCount; i++)
            _slots[i] += other._slots[i];
    }

    /// <summary>`Cluster::operator*=` — field-wise permille scaling.
    ///
    /// <para>Two details are load-bearing and both are visible in the disassembly:</para>
    /// <list type="bullet">
    ///   <item>A rate of exactly <see cref="RateIdentity"/> is SKIPPED (<c>cmp eax, 0x3E8; je</c>), not
    ///         multiplied and divided. With truncating division those differ: 7 * 1000 / 1000 is 7 either
    ///         way, but the skip means a 1000 rate can never round anything.</item>
    ///   <item>The divide is the <c>0x10624DD3</c> / <c>sar 6</c> idiom followed by
    ///         <c>shr eax,31; add eax,edx</c> — a signed divide by 1000 that truncates TOWARD ZERO. C#'s
    ///         integer division does the same, so this is a direct translation rather than a reproduction
    ///         of the idiom.</item>
    /// </list></summary>
    public void ApplyRate(ParameterCluster rate)
    {
        for (var i = 0; i < SlotCount; i++)
        {
            var r = rate._slots[i];
            if (r == RateIdentity) continue;
            _slots[i] = (int)((long)_slots[i] * r / 1000);
        }
    }

    /// <summary>Raise every slot below <paramref name="floor"/> up to it, over a slot range.
    ///
    /// <para>`c_MakeTotal` ends by flooring the first five slots at 1 — the primaries can never reach
    /// zero however punishing the debuffs.</para></summary>
    public void FloorSlots(int firstSlot, int count, int floor)
    {
        for (var i = firstSlot; i < firstSlot + count; i++)
            if (_slots[i] < floor) _slots[i] = floor;
    }

    /// <summary>Every non-identity slot, for logging a cluster without printing 51 mostly-empty numbers.</summary>
    public IEnumerable<(Stat Stat, int Value)> NonDefault(StatHalf half)
    {
        var identity = half == StatHalf.Rate ? RateIdentity : PlusIdentity;
        for (var i = 0; i < SlotCount; i++)
            if (_slots[i] != identity) yield return ((Stat)i, _slots[i]);
    }

    public override string ToString()
        => string.Join(", ", NonDefault(StatHalf.Plus).Select(p => $"{p.Stat}={p.Value}"));
}
