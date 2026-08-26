namespace Fiesta.Emu.Zone.Mob;

/// <summary>Anything a mob can consider attacking. The simulator's stand-in for `ShineObject`.</summary>
public interface IShineObject
{
    ushort Handle { get; }
    int X { get; }
    int Y { get; }
    bool IsAlive { get; }
}

/// <summary>One entry in a mob's hate list. The server's `MobTargetStruct` is 12 bytes; the fields here
/// are the ones the simulator needs rather than a byte-exact mirror, because the layout only matters when
/// this code has to share memory with the original, which it does not.</summary>
public sealed class MobTargetStruct
{
    public required IShineObject Object { get; init; }
    public int AggroPoint { get; set; }
}

/// <summary>Target acquisition — the server's `MobTargetSelector` / `MobTargetBout` / `MobTargetAggresive`
/// chain, which derives from `AxialListIterator` because acquisition IS a spatial scan.
///
/// <para>Ported from the reading in docs/AGGRO.md. The rule the server implements is:</para>
/// <list type="number">
///   <item>seed the best distance with <b>r squared</b>, where r is the mob's detect range;</item>
///   <item>walk every nearby candidate, calling <c>ali_Work(scanner, candidate, squaredDistance)</c>;</item>
///   <item>reject on any of ten gates, then keep the candidate if its squared distance is strictly less
///         than the best so far.</item>
/// </list>
///
/// <para><b>The radius test and the nearest-wins test are the same comparison.</b> Seeding the best with
/// r² means anything outside the circle is automatically "farther than the best" and drops out, which is
/// why <c>ali_Work</c> contains no range check of its own. Reproducing that faithfully matters: writing it
/// as a separate range filter plus a nearest search gives the same answer for `&lt;` but a different one at
/// exactly r, where the server rejects and a naive port accepts.</para>
///
/// <para><b>Acquisition is a circle, not a sector.</b> `so_AllOfRange` takes a `FanFormSectorArgument*`
/// and the aggro call site passes NULL. Sectors exist in this engine but belong to skills.</para></summary>
public sealed class MobTargetSelector
{
    private readonly List<MobTargetStruct> _aggro = new();

    /// <summary>The mob's detect range, from its data record at +0x3B (`ShineMob::so_getDetectRange`).
    /// A flat scalar — there is no angular term anywhere in acquisition.</summary>
    public int DetectRange { get; set; }

    /// <summary>The hate list, in no particular order. `mts_GetTopAggroTarget` is what orders it.</summary>
    public IReadOnlyList<MobTargetStruct> AggroList => _aggro;

    /// <summary>The candidate gates from `ali_Work`, collapsed to one predicate.
    ///
    /// <para>The server applies ten of them — `mdb_CanIKill`, sublayer interaction, `so_SubLayer_CanSee`,
    /// two abnormal-state checks, a level gap, and several unresolved vtable predicates. The simulator
    /// supplies its own; what must be preserved is that they run <b>before</b> the distance comparison, so
    /// a rejected near candidate does not displace an accepted far one.</para></summary>
    public Func<IShineObject, IShineObject, bool> CanTarget { get; set; } = (_, _) => true;

    /// <summary>`MobTargetAggresive::mts_SelectTarget` — pick the nearest valid candidate inside the
    /// detect range, or null if none qualifies.</summary>
    public IShineObject? mts_SelectTarget(IShineObject scanner, IEnumerable<IShineObject> nearby)
    {
        // best := r * r, exactly as mts_SelectTarget+0x3B9 does before calling so_AllOfRange.
        var best = (long)DetectRange * DetectRange;
        IShineObject? chosen = null;

        foreach (var candidate in nearby)
        {
            if (ReferenceEquals(candidate, scanner) || !candidate.IsAlive)
                continue;
            if (!CanTarget(scanner, candidate))
                continue;

            var squared = SquaredDistance(scanner, candidate);

            // ali_Work+0x19F: `cmp eax,[edi+8]` / `jge reject` -- strictly nearer wins, and a candidate
            // exactly at the range boundary loses to the r-squared seed.
            if (squared >= best)
                continue;

            best = squared;
            chosen = candidate;
        }

        return chosen;
    }

    /// <summary>The distance the scan hands to `ali_Work`. Squared, never rooted — the server compares
    /// squared distances throughout and never takes a square root on this path.</summary>
    public static long SquaredDistance(IShineObject a, IShineObject b)
    {
        long dx = (long)a.X - b.X;
        long dy = (long)a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>`mts_AppendAggroPoint`. The real body lives on `MobTargetBout`; the `MobTargetSelector`
    /// version is folded with `mts_DecreaseAggroPoint` into a single empty body, so the base class is a
    /// STUB rather than a fallback.
    ///
    /// <para>⚠️ How many points a hit generates is <b>not yet established</b> — the call that turns damage
    /// into aggro has not been found in either `so_DamagedBy` or `mab_Damaged`. Callers currently supply
    /// the amount, and that is a placeholder, not a ported rule.</para></summary>
    public void mts_AppendAggroPoint(IShineObject who, int points)
    {
        var entry = _aggro.FirstOrDefault(e => ReferenceEquals(e.Object, who));
        if (entry is null)
        {
            entry = new MobTargetStruct { Object = who };
            _aggro.Add(entry);
        }
        entry.AggroPoint += points;
    }

    /// <summary>`mts_DecreaseAggroPoint`.</summary>
    public void mts_DecreaseAggroPoint(IShineObject who, int points)
        => mts_AppendAggroPoint(who, -points);

    /// <summary>`mts_AggroClear`.</summary>
    public void mts_AggroClear() => _aggro.Clear();

    /// <summary>`mts_GetTopAggroTarget` — the highest-hate living entry.
    ///
    /// <para>⚠️ Tie-breaking is a guess. The server walks an intrusive list, so its answer on a tie is
    /// whichever entry the list order puts first, and list order depends on insertion and on
    /// `mts_AggroAdjust`. Not yet read.</para></summary>
    public IShineObject? mts_GetTopAggroTarget()
        => _aggro.Where(e => e.Object.IsAlive)
                 .OrderByDescending(e => e.AggroPoint)
                 .FirstOrDefault()?.Object;
}
