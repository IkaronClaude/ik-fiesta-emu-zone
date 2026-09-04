namespace Fiesta.Emu.Zone.Lua;

/// <summary>One skill's readiness at the moment a hit landed.</summary>
/// <param name="Id">Skill id.</param>
/// <param name="Name">`InxName`, so a row is readable without a lookup.</param>
/// <param name="ReadyInMs">0 when it could have been cast right then.</param>
/// <param name="AffordableSp">Whether the character had the SP for it.</param>
public sealed record SkillReadiness(int Id, string Name, double ReadyInMs, bool AffordableSp)
{
    public bool Usable => ReadyInMs <= 0 && AffordableSp;
}

/// <summary>⭐ ONE ROW PER INCOMING HIT — what hit us, and <b>everything we could have done about it</b>.
///
/// <para>The operator asked for this directly: "on each enemy hit, list all stone cds and skill cds". The
/// reason is that a death trace without it is unreadable. A character dying with 80 unused HP stones looks
/// like a bot that forgot to heal; the cooldown column shows it was healing at its ceiling. A character
/// dying with every skill ready looks like a rotation bug; the same column shows whether it had SP.</para>
///
/// <para>⚠️ The distance and movement fields are here because <b>kiting is the expected answer</b> in these
/// areas — the operator's words: these mobs are meant to outdamage you. So "was I moving, and how far away
/// was the thing hitting me" is the measurement that decides whether the driver even tried.</para></summary>
/// <param name="At">Simulation clock, ms.</param>
/// <param name="Attacker">The mob's `InxName`.</param>
/// <param name="AttackerHandle">Its handle, to tell two of the same kind apart.</param>
/// <param name="Damage">Damage this hit landed.</param>
/// <param name="Hp">Player HP after the hit.</param>
/// <param name="MaxHp">Player maximum HP.</param>
/// <param name="Sp">Player SP after the hit.</param>
/// <param name="DistanceToAttacker">World units between us and it when the hit landed.</param>
/// <param name="Walking">Whether a walk was in progress — the kiting tell.</param>
/// <param name="Casting">Whether a cast bar was up. A cast that movement would cancel.</param>
/// <param name="AutoAttacking">Whether auto-attack was engaged.</param>
/// <param name="Aggressors">How many mobs were targeting the player.</param>
/// <param name="HpStones">Charges left.</param>
/// <param name="HpStoneReadyInMs">0 when a charge could have been spent right then; -1 when the reserve is
/// empty, matching the live accessor.</param>
/// <param name="SpStones">SP charges left.</param>
/// <param name="SpStoneReadyInMs">As above, for SP.</param>
/// <param name="MobsNearby">Live mobs within <see cref="CombatLogEntry.CrowdRadius"/> of the player.</param>
/// <param name="MobsNearWalkTarget">⭐ Live mobs within the same radius of where the player is WALKING TO,
/// or -1 when it is not walking. <b>This is the "kites into a group" test.</b> The operator reports from
/// play that the driver "kites straight into a nearby wall or group of multiple mobs"; a kite whose
/// destination is more crowded than where it started is a kite into trouble, and this is the column that
/// says so.</param>
/// <param name="Skills">Every learned skill and whether it was usable.</param>
public sealed record CombatLogEntry(
    uint At,
    string Attacker,
    ushort AttackerHandle,
    int Damage,
    int Hp,
    int MaxHp,
    int Sp,
    double DistanceToAttacker,
    bool Walking,
    bool Casting,
    bool AutoAttacking,
    int Aggressors,
    int HpStones,
    double HpStoneReadyInMs,
    int SpStones,
    double SpStoneReadyInMs,
    int MobsNearby,
    int MobsNearWalkTarget,
    IReadOnlyList<SkillReadiness> Skills)
{
    /// <summary>What counts as "around" the player for the crowd columns. Roughly the aggro radius of a
    /// normal mob, so it answers "how many things can reach me here".</summary>
    public const int CrowdRadius = 400;

    public int HpPercent => MaxHp == 0 ? 0 : 100 * Hp / MaxHp;

    /// <summary>⚠️ A kite that ends somewhere MORE crowded than it started. -1 (not walking) is not a bad
    /// kite, it is no kite — the two must not be conflated.</summary>
    public bool KitingIntoACrowd => MobsNearWalkTarget >= 0 && MobsNearWalkTarget > MobsNearby;

    /// <summary>Skills that were off cooldown AND affordable — what the driver chose not to use.</summary>
    public IReadOnlyList<SkillReadiness> Usable => [.. Skills.Where(s => s.Usable)];

    /// <summary>A single readable line. The skill list is summarised as "ready/known" plus the names of
    /// the ready ones, because a full dump at 100 hits a minute is unreadable.</summary>
    public string Format()
    {
        var stone = HpStoneReadyInMs < 0 ? "empty"
                  : HpStoneReadyInMs == 0 ? "READY"
                  : $"{HpStoneReadyInMs:F0}ms";
        var state = Walking ? "walking" : Casting ? "casting" : AutoAttacking ? "bashing" : "standing";
        var ready = Usable;
        var names = ready.Count == 0 ? "-" : string.Join(",", ready.Take(4).Select(s => s.Name));
        var crowd = MobsNearWalkTarget < 0 ? $"crowd {MobsNearby,2}"
                  : $"crowd {MobsNearby,2}->{MobsNearWalkTarget,-2}{(KitingIntoACrowd ? "!" : " ")}";
        return $"[{At,7}] -{Damage,5} hp {Hp,6}/{MaxHp} ({HpPercent,3}%) sp {Sp,6} | "
             + $"{Attacker}#{AttackerHandle} at {DistanceToAttacker,5:F0}u | {state,-8} aggro={Aggressors} {crowd} | "
             + $"hpStone {HpStones,3}x {stone,-7} spStone {SpStones,3} | "
             + $"skills {ready.Count}/{Skills.Count} ready: {names}";
    }
}

/// <summary>Every incoming hit of a run, with what the character could have done about each one.
///
/// <para>Off by default: building a readiness list per hit costs a dictionary walk per learned skill, and
/// a full scenario matrix does not need it. Turn it on for one cell when a run needs explaining.</para></summary>
public sealed class CombatLog
{
    private readonly List<CombatLogEntry> _entries = [];

    public IReadOnlyList<CombatLogEntry> Entries => _entries;

    /// <summary>Cap on retained rows, so a long run cannot exhaust memory. The OLDEST are dropped —
    /// a death is explained by the hits just before it.</summary>
    public int Capacity { get; init; } = 5000;

    public void Add(CombatLogEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
    }

    /// <summary>The last <paramref name="count"/> hits, formatted — normally the ones before a death.</summary>
    public IEnumerable<string> Tail(int count = 40)
        => _entries.TakeLast(count).Select(e => e.Format());

    /// <summary>⭐ THE SUMMARY THAT ANSWERS "WHY DID IT DIE". Every number here is a measurement of the
    /// DRIVER's behaviour under fire, not of the fight.</summary>
    public string Summarise()
    {
        if (_entries.Count == 0) return "no incoming hits";

        var span = Math.Max(1u, _entries[^1].At - _entries[0].At) / 1000.0;
        var total = _entries.Sum(e => (long)e.Damage);
        var walking = _entries.Count(e => e.Walking);
        var stoneReady = _entries.Count(e => e.HpStoneReadyInMs == 0);
        var skillsReady = _entries.Count(e => e.Usable.Count > 0);
        var median = _entries.Select(e => e.DistanceToAttacker).Order().ElementAt(_entries.Count / 2);
        var kites = _entries.Where(e => e.MobsNearWalkTarget >= 0).ToList();
        var badKites = kites.Count(e => e.KitingIntoACrowd);

        return $"""
            {_entries.Count} hits over {span:F0}s -- {total} damage, {total / span:F0} dps incoming
              moving when hit ........ {walking,5} ({100.0 * walking / _entries.Count:F0}%)   <- the kiting tell
              HP stone was READY ..... {stoneReady,5} ({100.0 * stoneReady / _entries.Count:F0}%)   <- healing headroom left unused
              a skill was usable ..... {skillsReady,5} ({100.0 * skillsReady / _entries.Count:F0}%)
              median attacker range .. {median,5:F0}u
              kiting INTO a crowd .... {badKites,5} of {kites.Count} kites   <- destination more crowded than the start
            """;
    }
}
