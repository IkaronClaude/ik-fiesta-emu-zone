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

    /// <summary>The seven slots whose rate-eraser entry is <b>0, not 1000</b>.
    ///
    /// <para>A contiguous run from <see cref="Stat.CriticalTB"/> to <see cref="Stat.ResistGTI"/>. Read out of
    /// a LIVE zone process — see <see cref="Rate"/>.</para></summary>
    public static readonly IReadOnlyList<Stat> RateErasedSlots =
    [
        Stat.CriticalTB, Stat.RegistNone, Stat.ResistPoison, Stat.ResistDeaseas,
        Stat.ResistCurse, Stat.ResistMoveSpdDown, Stat.ResistGTI,
    ];

    /// <summary>A cluster seeded like the server's `parameter_eraser_rate`.
    ///
    /// <para><b>Not uniformly 1000.</b> Slots 0..41 and 49..50 hold 1000; slots 42..48 hold <b>0</b>.</para>
    ///
    /// <para>⚠️ HOW THIS IS KNOWN, because it could not be read from the file. Both eraser globals live in
    /// the executable's uninitialised section (VA 0x0074C000 upward) and are filled at start-up, so the
    /// image contains nothing. An earlier version of this method INFERRED uniform 1000 from
    /// <c>operator*=</c>, which tests each slot against 1000 and skips it. That inference was right in
    /// general and wrong in the tail.</para>
    ///
    /// <para>The values here were read out of a running zone server's memory (`/proc/&lt;pid&gt;/mem` at
    /// 0x0DA3FA78; Wine maps the image at its preferred 0x400000, so static addresses apply directly).
    /// `parameter_eraser_plus` came back as 51 zeros, confirming <see cref="Plus"/>.</para>
    ///
    /// <para>It matters: <c>operator*=</c> skips ONLY on exactly 1000, so a 0 in a rate slot multiplies that
    /// stat by zero. Under the uniform-1000 version those seven resistances survive a rate step untouched;
    /// under the server's they are wiped. Whether that is the intent or whether something always writes
    /// those slots first is an open question — but the seeded value is now what the server actually
    /// has.</para></summary>
    public static ParameterCluster Rate()
    {
        var slots = new int[SlotCount];
        Array.Fill(slots, RateIdentity);
        foreach (var stat in RateErasedSlots)
            slots[(int)stat] = 0;
        return new ParameterCluster(slots);
    }

    /// <summary>The rate slots a LIVE container holds at 0 even though the eraser puts 1000 there,
    /// per cluster. Read out of a running zone (see <see cref="Rate"/>'s note).
    ///
    /// <para>These are exactly the slots the engine reads ADDITIVELY. `roe_CriticalRate` sums
    /// `Item.Rate`, `WeaponTitle.Rate` and `AbnormalState.Rate` at <see cref="Stat.CriDamRate"/> and
    /// subtracts two halves at <see cref="Stat.CriticalTB"/>; an additive term needs a zero identity, and
    /// these have one. The eraser's 1000 is a generic template that something clears afterwards.</para></summary>
    public static readonly IReadOnlyDictionary<StatModifier, IReadOnlyList<Stat>> RateZeroedAfterErase =
        new Dictionary<StatModifier, IReadOnlyList<Stat>>
        {
            [StatModifier.Item] = [Stat.CriDamRate, Stat.MagCriDamRate],
            [StatModifier.WeaponTitle] = [Stat.CriDamRate, Stat.MagCriDamRate],
            [StatModifier.AbnormalState] = [Stat.CriDamRate],
        };

    /// <summary>A rate cluster as a LIVE container holds it: the eraser, then
    /// <see cref="RateZeroedAfterErase"/> applied for this source.</summary>
    public static ParameterCluster RateFor(StatModifier source)
    {
        var c = Rate();
        if (RateZeroedAfterErase.TryGetValue(source, out var zeroed))
            foreach (var stat in zeroed)
                c[stat] = 0;
        return c;
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
