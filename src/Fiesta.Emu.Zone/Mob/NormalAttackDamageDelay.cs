namespace Fiesta.Emu.Zone.Mob;

/// <summary>One queued hit — `NormalAttackDamageDelay::NormalAttackDamageElement`.</summary>
public readonly record struct NormalAttackDamageElement(
    uint DueTime,
    IShineObject Target,
    int Damage,
    byte Flag);

/// <summary>`NormalAttackDamageDelay::NormalAttackDamageTick` — damage that lands later than the swing.
///
/// <para><b>A swing does not deal its damage at the moment it is made.</b> The server pushes an element
/// carrying a due time and applies it when the clock reaches it, which is why a mob can die to a blow
/// thrown before it moved out of reach, and why burst damage arrives in a cluster rather than instantly.
/// A simulation that applies damage at swing time gets fight lengths subtly wrong and kite timing
/// completely wrong.</para>
///
/// <para>Ported from `nadt_Routine` at `0x00577520`: it walks the queue from the front, compares each
/// element's time against a global clock, and <b>stops at the first element that is not yet due</b> —
/// so the queue is time-ordered and a late element never blocks an earlier one.</para>
///
/// <para>The original is a ring buffer over a `deque` with head index and count at `+0xC`, `+0x8`,
/// `+0x10`. A `Queue` has the same observable behaviour; the ring layout only matters for a byte-exact
/// port, which this is not.</para></summary>
public sealed class NormalAttackDamageTick
{
    private readonly Queue<NormalAttackDamageElement> _pending = new();

    /// <summary>`nadt_IsEmpty`.</summary>
    public bool nadt_IsEmpty() => _pending.Count == 0;

    public int PendingCount => _pending.Count;

    /// <summary>`nadt_PushBack(damage, dueTime, target, flag)`.
    ///
    /// <para>⚠️ The queue is only time-ordered if callers push in non-decreasing due-time order, which is
    /// what the server's single-swing-at-a-time flow produces. Pushing an earlier hit after a later one
    /// would leave it stuck behind — the original has the same property, since it never sorts.</para></summary>
    public void nadt_PushBack(int damage, uint dueTime, IShineObject target, byte flag = 0)
        => _pending.Enqueue(new NormalAttackDamageElement(dueTime, target, damage, flag));

    /// <summary>`nadt_Routine(now)` — apply every hit whose time has come, in order, and stop at the first
    /// that has not.
    ///
    /// <para>The comparison is `element.DueTime &gt; now -&gt; stop`, so a hit due exactly now DOES land
    /// this tick.</para></summary>
    public IReadOnlyList<NormalAttackDamageElement> nadt_Routine(uint now)
    {
        var landed = new List<NormalAttackDamageElement>();
        while (_pending.Count > 0)
        {
            var head = _pending.Peek();
            if (head.DueTime > now)
                break;                       // time-ordered: nothing behind it can be due either
            _pending.Dequeue();
            landed.Add(head);
        }
        return landed;
    }

    /// <summary>`nadt_PopFront`.</summary>
    public void nadt_PopFront()
    {
        if (_pending.Count > 0) _pending.Dequeue();
    }

    /// <summary>`nadt_Clear`.</summary>
    public void nadt_Clear() => _pending.Clear();

    /// <summary>`nadt_TargetCompare(handle)` — whether anything queued is aimed at this target.</summary>
    public bool nadt_TargetCompare(ushort handle)
        => _pending.Any(e => e.Target.Handle == handle);
}

/// <summary>Per-skill cooldowns — `CharaterSkillList::csl_SetCoolTime` / `csl_CoolTimeCheck` /
/// `csl_UpdateCoolTime`.
///
/// <para>⚠️ The server's versions take more arguments than are modelled here
/// (`csl_SetCoolTime(ushort, ulong, int, ulong, int)`), and there is a separate group-cooldown system in
/// `ItemActionObserveManager` plus an "ignore cooldown" abnormal state
/// (`SubAbnormalStateActorIgnoreCoolTime`). This is the <b>expiry-time model only</b>, which is enough to
/// stop a simulated mob spamming a skill every tick, and is marked as a simplification rather than a
/// port.</para></summary>
public sealed class CharaterSkillList
{
    private readonly Dictionary<ushort, uint> _readyAt = new();

    /// <summary>Skip every cooldown — `sp_SetIgnoreCoolTime` / `sasa_Act_CoolTimeIgnore`.</summary>
    public bool IgnoreCoolTime { get; set; }

    /// <summary>`csl_SetCoolTime` — the skill becomes usable again at <paramref name="readyAt"/>.</summary>
    public void csl_SetCoolTime(ushort skillId, uint readyAt) => _readyAt[skillId] = readyAt;

    /// <summary>`csl_CoolTimeCheck` — is this skill usable now?
    ///
    /// <para>A skill never used is ready. A skill whose ready-time is exactly now IS ready — the boundary
    /// matters at tick granularity, where an off-by-one costs a whole tick of DPS.</para></summary>
    public bool csl_CoolTimeCheck(ushort skillId, uint now)
        => IgnoreCoolTime || !_readyAt.TryGetValue(skillId, out var ready) || now >= ready;

    /// <summary>`csl_DmgCoolTimeDown` — clear everything.</summary>
    public void csl_DmgCoolTimeDown() => _readyAt.Clear();

    /// <summary>When a skill becomes usable, or null if it has never been used.</summary>
    public uint? ReadyAt(ushort skillId) => _readyAt.TryGetValue(skillId, out var r) ? r : null;
}
