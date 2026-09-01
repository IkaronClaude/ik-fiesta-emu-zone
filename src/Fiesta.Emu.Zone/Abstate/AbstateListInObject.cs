using Fiesta.Emu.Zone.Parameter;

namespace Fiesta.Emu.Zone.Abstate;

/// <summary>`AbnormalStateContainer::AbstateElementInObject` — one abnormal state active on one object.
///
/// <para>The three fields the wire carries are exactly the three the server keeps:
/// <c>ABSTATE_INFORMATION = { abstateID u32, restKeeptime u32, strength u32 }</c>.
/// <b><see cref="Strength"/> is not a boolean</b> — it selects the `SubAbState.shn` row, so the same
/// abstate id at two strengths is two different sets of actions. Reading it as an on/off flag was a real
/// bug in this project's decoder and it silently flattened every ranked buff to rank 1.</para></summary>
/// <param name="AbstateId">`AbState.shn`'s `AbStataIndex`, which names the sub-state.</param>
/// <param name="Strength">Selects the `SubAbState.shn` row for that name.</param>
/// <param name="RestKeeptime">Milliseconds remaining when the state was announced.</param>
/// <param name="AppliedAtMs">When it was applied, on the simulation's clock.</param>
public sealed record AbstateElementInObject(int AbstateId, int Strength, int RestKeeptime, long AppliedAtMs)
{
    /// <summary>The sub-state's TYPE byte, at +0x26 of the `SubAbState` row the strength selected.
    ///
    /// <para>Two things in the engine read it, and neither is obvious from the name: it picks
    /// `cannotmove_stun` over `cannotmove_entangle` in `SAA_NOMOVE` (types 0x15 and 0x60), and it selects
    /// which `DotDamagePlus` member a poison tick draws from (see <see cref="DotDamage"/>). Zero is a real
    /// type, so it is not a marker for "unknown".</para></summary>
    public int SubStateType { get; init; }

    /// <summary>Whether this state takes `SAA_NOMOVE`'s alternative branch — a stun rather than an
    /// entangle. `aeo_ParameterEnchant`'s handler compares the type against 0x15 and 0x60.</summary>
    public bool IsStunType => SubStateType is 0x15 or 0x60;

    /// <summary>`aeo_GetRestTime` — how long is left, never negative.</summary>
    public int RestTimeMs(long nowMs) => (int)Math.Max(0, RestKeeptime - (nowMs - AppliedAtMs));

    /// <summary>Whether the state has run out on its own timer.
    ///
    /// <para>⚠️ A `restKeeptime` of 0 means EXPIRED IMMEDIATELY, not "no expiry". Nothing in this capture's
    /// data uses 0 as an eternal marker — `SubStaKeepTime_Eternal` carries a real 5000 — so treating 0 as
    /// infinite would be inventing a mechanic.</para></summary>
    public bool HasExpired(long nowMs) => RestTimeMs(nowMs) == 0;
}

/// <summary>How an abstate ended, for callers that need to tell a timeout from a cancel.</summary>
public enum AbstateEndReason
{
    /// <summary>`restKeeptime` ran out.</summary>
    Expired,

    /// <summary>`aeo_Attack` (0x004A42A0) — the OWNER attacked, and the state's definition byte has bit
    /// 0x04 set.</summary>
    OwnerAttacked,

    /// <summary>`aeo_Attacked` (0x004A4310) — the owner WAS HIT, and the definition byte has bit 0x08.</summary>
    OwnerWasHit,

    /// <summary>An explicit `NC_BRIEFINFO_ABSTATERESET_CMD` (0x2428) from the server.</summary>
    Reset,
}

/// <summary>`AbnormalStateContainer::AbstateListInObject` — the abnormal states on one object, and the
/// parameter effect they add up to.
///
/// <para>This is the piece the damage work has been doing by hand. `BucketGroundTruthTests` reads which
/// abstates the wire says are active and applies their effects itself; nothing could apply, tick or expire
/// one. Everything below is that logic moved where a simulation can use it.</para>
///
/// <para><b>Expiry is why this needs a clock.</b> `damage_buckets.py` removes a state only on an explicit
/// `ABSTATERESET`, so a state that lapses on its own timer is held forever — harmless for `StaImmortal`,
/// which carries no actions, and wrong for `SubStaMoraleDecreaseWC`, which has a 15-second keeptime and a
/// real weapon-damage effect. Frame ORDER is not a clock; this takes milliseconds.</para></summary>
public sealed class AbstateListInObject
{
    private readonly List<AbstateElementInObject> _active = [];

    /// <summary>The states currently applied, in the order they landed.</summary>
    public IReadOnlyList<AbstateElementInObject> Active => _active;

    /// <summary>`asl_Abstate_IsSet` — is this abstate id currently on the object?</summary>
    public bool IsSet(int abstateId) => _active.Any(e => e.AbstateId == abstateId);

    /// <summary>Apply a state, as `NC_BRIEFINFO_ABSTATE_CHANGE_CMD` (0x1C18) announces it.
    ///
    /// <para>Re-applying an id REPLACES the existing element rather than stacking a second one, which is
    /// what the wire shows: the server re-sends a combat debuff constantly and the client does not
    /// accumulate them.</para></summary>
    public void Set(int abstateId, int strength, int restKeeptimeMs, long nowMs, int subStateType = 0)
    {
        _active.RemoveAll(e => e.AbstateId == abstateId);
        _active.Add(new AbstateElementInObject(abstateId, strength, restKeeptimeMs, nowMs)
        {
            SubStateType = subStateType,
        });
    }

    /// <summary>Remove a state, as `NC_BRIEFINFO_ABSTATERESET_CMD` (0x2428) announces it.</summary>
    public bool Reset(int abstateId) => _active.RemoveAll(e => e.AbstateId == abstateId) > 0;

    /// <summary>Replace the whole list, as `NC_BRIEFINFO_ABSTATE_CHANGE_LIST_CMD` (0x1C19) does.</summary>
    public void SetAll(IEnumerable<(int AbstateId, int Strength, int RestKeeptimeMs)> states, long nowMs)
    {
        _active.Clear();
        foreach (var (id, strength, keep) in states)
            _active.Add(new AbstateElementInObject(id, strength, keep, nowMs));
    }

    /// <summary>`ASE_Tick` — drop everything whose `restKeeptime` has run out, and report what went.
    ///
    /// <para><b>Expiry is SERVER state, and here we are the server</b> — so running it is ours to do, and
    /// so is telling every client about it. The returned list is not a convenience: each element in it is
    /// an `ABSTATERESET` that has to go out on the wire, and a state that lapses silently is a bug in our
    /// server rather than a detail of it.</para>
    ///
    /// <para>The distinction matters because the same arithmetic appears in a CAPTURE READER, where it is
    /// the error-prone side: there the server already broadcast the end and inferring it locally competes
    /// with an observation. Measured on `FighterDamageLvl60.pcapng` — <b>7 states dropped by keeptime, all
    /// 7 later confirmed by an explicit `ABSTATERESET`, none unconfirmed</b>, out of 178 resets. The real
    /// server does tell the client every time, so on that side the wire is authoritative and the timer is
    /// only worth running to close the gap between the state ending and the broadcast arriving.</para></summary>
    public IReadOnlyList<AbstateElementInObject> Tick(long nowMs)
    {
        var gone = _active.Where(e => e.HasExpired(nowMs)).ToList();
        foreach (var e in gone)
            _active.Remove(e);
        return gone;
    }

    /// <summary>`aeo_Attack` (0x004A42A0) — the owner attacked. Ends every state whose definition says it
    /// cancels on the owner attacking.
    ///
    /// <para>This is what ends spawn invulnerability: a mob that opens fire drops `StaImmortal` (291) by
    /// its own action, which is why the capture shows two swings from an attacker still holding it — the
    /// `ABSTATERESET` broadcast follows the swing by two to four frames instead of preceding it.</para>
    ///
    /// <para>⚠️ <b>Which struct holds the cancel bits is NOT pinned.</b> The original tests
    /// <c>byte [element+0x70 -&gt; def +2] &amp; 4</c>, and <c>[element+0x70]</c> is not an
    /// `AbnormalStateInfo` — its +2 is `InxName`. Until that struct is named, the caller supplies the
    /// predicate rather than this guessing at it.</para></summary>
    public IReadOnlyList<AbstateElementInObject> OnOwnerAttacked(Func<AbstateElementInObject, bool> cancels)
        => EndWhere(cancels);

    /// <summary>`aeo_Attacked` (0x004A4310) — the owner was hit. Same shape, bit 0x08.</summary>
    public IReadOnlyList<AbstateElementInObject> OnOwnerWasHit(Func<AbstateElementInObject, bool> cancels)
        => EndWhere(cancels);

    private IReadOnlyList<AbstateElementInObject> EndWhere(Func<AbstateElementInObject, bool> predicate)
    {
        var gone = _active.Where(predicate).ToList();
        foreach (var e in gone)
            _active.Remove(e);
        return gone;
    }

    /// <summary>`aeo_ParameterEnchant` (0x004079F0) over every active state — write the whole list's effect
    /// into a container's AbnormalState cluster, its second-tier fields and its behaviour flags.
    ///
    /// <para>The container's abnormal-state layer is REBUILT from the list rather than adjusted, which is
    /// what the server does on each recalculation and is the only way a state can be removed without
    /// leaving its bonus behind.</para>
    ///
    /// <para>Returns false when any active state uses an action index outside the dispatched range, so an
    /// unmodelled effect makes a prediction refuse rather than come out silently wrong. An action that
    /// dispatches but has no container effect is NOT such a case — 49 of the 120 are exactly that, and
    /// they are read results.</para></summary>
    /// <param name="resolve">The state -> actions lookup: `AbState.shn` maps the id to a sub-state name,
    /// `SubAbState.shn` maps (name, strength) to up to four (ActionIndex, ActionArg) pairs.</param>
    /// <param name="altCondition">Whether a state takes `SAA_NOMOVE`'s alternative branch. Defaults to
    /// <see cref="AbstateElementInObject.IsStunType"/>, which is the server's own rule; the parameter
    /// exists for callers reconstructing from a capture that has not resolved the type byte.</param>
    public bool ParameterEnchant(ParameterContainer container,
                                 Func<AbstateElementInObject, IReadOnlyList<(SubAbstateAction Action, int Arg)>> resolve,
                                 Func<AbstateElementInObject, bool>? altCondition = null)
    {
        var plus = container.Plus(StatModifier.AbnormalState);
        var rate = container.Rate(StatModifier.AbnormalState);
        var freshRate = ParameterCluster.RateFor(StatModifier.AbnormalState);
        foreach (var stat in Enum.GetValues<Stat>())
        {
            plus[stat] = ParameterCluster.PlusIdentity;
            rate[stat] = freshRate[stat];
        }
        container.Flags = ContainerFlag.None;

        var ok = true;
        foreach (var element in _active)
        {
            foreach (var (action, arg) in resolve(element))
            {
                if (action == SubAbstateAction.SAA_NONE)
                    continue;
                if (!AbstateEffects.IsDispatched(action))
                {
                    ok = false;
                    continue;
                }
                if (AbstateEffects.For(action) is not { } effect)
                    continue;           // dispatched, and provably writes nothing to the container

                foreach (var w in effect.Stats)
                    (w.Half == StatHalf.Rate ? rate : plus)[w.Stat] += w.Sign * arg;
                foreach (var f in effect.Fields)
                    container.WriteField(f.Field, f.Sign, arg);

                var flags = effect.AltFlags != ContainerFlag.None
                            && (altCondition?.Invoke(element) ?? element.IsStunType)
                    ? effect.AltFlags
                    : effect.Flags;
                container.Flags |= flags;
            }
        }
        return ok;
    }
}
