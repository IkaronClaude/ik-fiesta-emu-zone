using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Mob;

/// <summary>Anything a mob can consider attacking. The simulator's stand-in for `ShineObject`.</summary>
public interface IShineObject
{
    ushort Handle { get; }
    int X { get; }
    int Y { get; }
    bool IsAlive { get; }

    /// <summary>`Parameter::Container::flag`, the object's behaviour bits.
    ///
    /// <para>The container sits at +0xFC0 inside a `ShineMobileObject`, so the flag byte is +0x1C8E from
    /// the object — which is how its readers were found. There are exactly two in the whole image, and
    /// neither is where this project assumed:</para>
    ///
    /// <list type="bullet">
    ///   <item><c>so_ReinforceMove@ShineMobileObject+0x90</c> — <c>test byte [edi+0x1C8E], 2</c>, and
    ///         returns immediately. Movement is gated on the MOVE function, not on the tactic states.</item>
    ///   <item><c>sp_Schedule_SwingStart@ShinePlayer+0xAF</c> — <c>test byte [eax+0xCCE], 4</c>. Attacking
    ///         is gated in the PLAYER's swing scheduler.</item>
    /// </list>
    ///
    /// <para>⚠️ <see cref="ContainerFlag.CannotMoveStun"/> has <b>no reader at all</b>. `SAA_NOMOVE`'s
    /// alternative branch and `SAA_AWAY` both set it and nothing in the image tests it, at either
    /// displacement. So the two immobilisations the PDB names separately are not both enforced here, and
    /// how a stunned MOB is actually stopped is still unread — it is not this bit.</para></summary>
    ContainerFlag Flags => ContainerFlag.None;
}

/// <summary>One entry in a mob's hate list. The server's `MobTargetStruct` is 12 bytes; the fields here
/// are the ones the simulator needs rather than a byte-exact mirror, because the layout only matters when
/// this code has to share memory with the original, which it does not.</summary>
public sealed class MobTargetStruct
{
    public required IShineObject Object { get; init; }
    public int AggroPoint { get; set; }

    /// <summary>`mts_LastHit` (+0x10) - the clockwatch, in tenths, at the last aggro append.
    /// `mts_AppendAggroPoint@MobTargetBout` stamps it on both paths (+0x19B and +0x22B), and it is the
    /// only input to mts_Routine's CutNonAT test.</summary>
    public int LastHitTenths { get; set; }
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
/// <summary>Which `MobTargetSelector` subclass a mob behaves as. Chosen per mob by
/// `MobInfoServer.EnemyDetectType`; see <see cref="Data.EnemyDetect"/>.</summary>
public enum TargetingPolicy
{
    /// <summary>`MobTargetNoBrain` — never acquires anything. 764 mobs, mostly shopkeepers.</summary>
    NoBrain,
    /// <summary>`MobTargetBout` — retaliate only, no sight scan at all. 220 mobs, including the starter
    /// set (Slime, MushRoom, Imp, Crab).</summary>
    Bout,
    /// <summary>`MobTargetAggresive` — hate list first, then the forward sight scan. 1,872 mobs.</summary>
    Aggressive,
}

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

    /// <summary>How far ahead of itself a mob's detection circle sits, as a fraction of its detect range —
    /// <c>imul eax, eax, 0xCD</c> then <c>sar eax, 9</c>, i.e. <b>× 205 / 512</b> ≈ 0.4004.</summary>
    public const int SightOffsetNumerator = 205;
    public const int SightOffsetShift = 9;

    /// <summary>The mob's facing, in direction units (2° each, 180 to a full turn).</summary>
    public int Facing { get; set; }

    /// <summary>Which targeting policy this mob uses — the `MobTargetSelector` SUBCLASS the server would
    /// give it, chosen by `MobInfoServer.EnemyDetectType`.
    ///
    /// <para>Modelled as an enum rather than a class hierarchy because the three behaviours are small and
    /// the dispatch is the only thing that differs. The hierarchy is
    /// <c>MobTargetSelector → MobTargetBout → { MobTargetNoBrain, MobTargetAggresive }</c>, which is why
    /// Aggressive is Bout PLUS a scan rather than an alternative to it.</para></summary>
    public TargetingPolicy Policy { get; set; } = TargetingPolicy.Aggressive;

    /// <summary>`ShineMob::so_mob_SightCenter` (0x004ABCD0) — where a mob's detection circle is CENTRED.
    ///
    /// <para><b>Not on the mob.</b> The base <c>ShineObject</c> version is three instructions and returns the
    /// object's own position; the <c>ShineMob</c> override seeds the output with that position and then calls
    /// <c>ddt_GetFoward(facing, range * 205 / 512, out)</c>, pushing it forward along the mob's facing by
    /// about 40% of the detect range.</para>
    ///
    /// <para><b>This is what makes aggro direction-dependent</b>, and it is not an angular test at all — the
    /// shape stays a circle, the circle is just not concentric with the mob. Approaching head-on you cross
    /// the boundary at roughly <c>1.40 × range</c>; from directly behind you have to reach <c>0.60 ×
    /// range</c>. A ratio of about 2.3 between front and back.</para>
    ///
    /// <para>This project argued for weeks that detection was "a circle" and treated the operator's report of
    /// orientation-dependent aggro as unexplained. The circle reading was right; what was missed is that its
    /// centre moves. Reading `mts_SelectTarget`'s arguments one at a time is what found it: the `loc` it
    /// hands `so_AllOfRange` is not the mob's position but the return of virtual slot 0x8F4.</para></summary>
    public (int X, int Y) SightCenter(IShineObject scanner)
    {
        var distance = DetectRange * SightOffsetNumerator >> SightOffsetShift;
        var (dx, dy) = Direction.Forward(Facing, distance);
        return (scanner.X + dx, scanner.Y + dy);
    }

    /// <summary>`MobTargetAggresive::mts_SelectTarget` — pick the nearest valid candidate inside the
    /// detect range, or null if none qualifies.
    ///
    /// <para>Distances are measured from <see cref="SightCenter"/>, not from the mob — see there for why
    /// that is the whole of the direction-dependence.</para></summary>
    public IShineObject? mts_SelectTarget(IShineObject scanner, IEnumerable<IShineObject> nearby)
    {
        // POLICY FIRST -- the server puts this in the subclass, so the branch here stands in for the vtable.
        //
        //   MobTargetNoBrain::mts_SelectTarget  is `mts_InitThink()` and nothing else: it never picks a
        //     target, which is why shopkeepers ignore you.
        //   MobTargetBout::mts_SelectTarget     walks the MobTargetStruct hate list with an `mdb_CanIKill`
        //     check and NEVER CALLS so_AllOfRange -- it has no sight scan, so it only ever retaliates.
        //   MobTargetAggresive::mts_SelectTarget adds the scan on top, since it derives from Bout.
        if (Policy == TargetingPolicy.NoBrain)
            return null;

        var hated = mts_GetTopAggroTarget();
        if (hated is not null || Policy == TargetingPolicy.Bout)
            return hated;

        // best := r * r, exactly as mts_SelectTarget+0x3B9 does before calling so_AllOfRange.
        var best = (long)DetectRange * DetectRange;
        IShineObject? chosen = null;
        var (cx, cy) = SightCenter(scanner);

        foreach (var candidate in nearby)
        {
            if (ReferenceEquals(candidate, scanner) || !candidate.IsAlive)
                continue;
            if (!CanTarget(scanner, candidate))
                continue;

            var squared = SquaredDistanceFrom(cx, cy, candidate);

            // ali_Work+0x19F: `cmp eax,[edi+8]` / `jge reject` -- strictly nearer wins, and a candidate
            // exactly at the range boundary loses to the r-squared seed.
            if (squared >= best)
                continue;

            best = squared;
            chosen = candidate;
        }

        return chosen;
    }

    /// <summary>Squared distance from an arbitrary centre — the scan measures from the sight centre.</summary>
    public static long SquaredDistanceFrom(int cx, int cy, IShineObject o)
    {
        long dx = (long)cx - o.X;
        long dy = (long)cy - o.Y;
        return dx * dx + dy * dy;
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
    public void mts_AppendAggroPoint(IShineObject who, int points, int nowTenths = 0)
    {
        var entry = _aggro.FirstOrDefault(e => ReferenceEquals(e.Object, who));
        if (entry is null)
        {
            entry = new MobTargetStruct { Object = who };
            _aggro.Add(entry);
        }
        entry.AggroPoint += points;

        // The binary stamps mts_LastHit on both paths, so it is written after the merge. Without it an
        // entry is freed by the CutNonAT test on the first mts_Routine tick.
        entry.LastHitTenths = nowTenths;
    }

    /// <summary>`mts_DecreaseAggroPoint`.</summary>
    public void mts_DecreaseAggroPoint(IShineObject who, int points, int nowTenths = 0)
        => mts_AppendAggroPoint(who, -points, nowTenths);

    /// <summary>`mts_AggroClear`.</summary>
    public void mts_AggroClear() => _aggro.Clear();

    /// <summary>`MobInfoServer.CutInterval` squared. 0 switches the distance test off, which is what a
    /// mob with no data row gets; a squared distance of 0 would otherwise free every entry at once.</summary>
    public long CutIntervalSquar { get; set; }

    /// <summary>`MobInfoServer.CutNonAT` in tenths. 0 switches the timeout off, same reasoning.</summary>
    public int CutNonAtTenths { get; set; }

    /// <summary>`MobTargetBout::mts_Routine` (0x004AC940) - the aggro list purge, and the only way a mob
    /// stops hating you.
    ///
    /// <para>It builds a MobTarget_EnemyAnalysis over the list and walks it with lid_Call (0x004AC800),
    /// which frees an entry on any of three tests: clockwatch &gt; LastHit + CutNonAT tenths (+0x61); the
    /// object is gone (+0xC6); or dist^2 &gt; CutInterval^2 (+0xEB).</para>
    ///
    /// <para>MobActionChase's so_mob_ChaseRangeSquar test only ends the CHASE. The mob then returns to
    /// Targetting and takes the same entry back off this list, because mts_GetTopAggroTarget has no
    /// distance test. Nothing but this purge removes it.</para>
    ///
    /// <para>Not modelled: the io_ReadReport SpyNet call on the surviving entries.</para></summary>
    /// <returns>How many entries were freed.</returns>
    public int mts_Routine(IShineObject scanner, int nowTenths)
    {
        var freed = 0;
        for (var i = _aggro.Count - 1; i >= 0; i--)
        {
            var entry = _aggro[i];

            // +0x61: `cmp [0x14D41A70], LastHit + timeout` / `jbe` -- strictly greater frees.
            if (CutNonAtTenths > 0 && nowTenths > entry.LastHitTenths + CutNonAtTenths)
            {
                _aggro.RemoveAt(i);
                freed++;
                continue;
            }

            // +0xC6: som_GetObject(handle) returning null frees. A dead object is the nearest equivalent.
            if (!entry.Object.IsAlive)
            {
                _aggro.RemoveAt(i);
                freed++;
                continue;
            }

            // +0xEB: `cmp dist^2, CutInterval^2` / `ja` -- again strictly greater.
            if (CutIntervalSquar > 0 && SquaredDistance(scanner, entry.Object) > CutIntervalSquar)
            {
                _aggro.RemoveAt(i);
                freed++;
            }
        }
        return freed;
    }

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
