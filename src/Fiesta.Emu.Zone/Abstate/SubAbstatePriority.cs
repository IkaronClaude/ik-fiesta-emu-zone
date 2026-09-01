namespace Fiesta.Emu.Zone.Abstate;

/// <summary>`SubAbstatePriority::StateExchange` — what happens when a state meets one already applied.</summary>
public enum StateExchange
{
    /// <summary>`SAP_NORELATION` — the two are unrelated and BOTH stay. Returned as soon as any of the
    /// four action slots holds a different action index.</summary>
    SAP_NORELATION = 0,

    /// <summary>`SAP_SUBSCRIPT` — the incoming state is subordinate: the existing one keeps its place.
    /// This is the "you already have a stronger version" answer, and also the answer to re-casting the
    /// SAME rank.</summary>
    SAP_SUBSCRIPT = 1,

    /// <summary>`SAP_VANISH` — the existing state goes. This is the "stronger version lands" answer.</summary>
    SAP_VANISH = 2,
}

/// <summary>`SubAbstatePriority::PriorityBase::bp_AbStateChange` (0x00591CE0) — the rank rule.
///
/// <para>The application path (`asl_AbstateSet`) consults this before touching the list, and it is what
/// makes "re-applying a buff" mean something more interesting than replace-or-stack. It compares the two
/// `SubAbState` rows' FOUR (ActionIndex, ActionArg) pairs, in order:</para>
///
/// <code>
/// for i in 0..3:
///     if incoming.action[i] != existing.action[i]  return SAP_NORELATION;   // unrelated, both stay
///     if incoming.arg[i]    &lt; existing.arg[i]      return SAP_SUBSCRIPT;    // weaker: existing wins
///     if incoming.arg[i]    &gt; existing.arg[i]      return SAP_VANISH;       // stronger: existing goes
///     if incoming.arg[i]    &gt; 0                    sawPositive = true;
/// return sawPositive ? SAP_SUBSCRIPT : SAP_VANISH;                          // identical rows
/// </code>
///
/// <para><b>The comparison is on the ACTIONS, not the abstate id.</b> Two different abstate ids that
/// resolve to the same actions do relate, and the same id at two strengths resolves to different rows and
/// therefore different args — which is what makes rank comparison work at all.</para>
///
/// <para><b>The identical-rows case is the subtle one.</b> Re-casting the exact same buff returns
/// SUBSCRIPT, so it does NOT displace what is there — the existing element keeps its remaining keeptime
/// rather than having it refreshed. Only an all-zero row falls through to VANISH.</para>
///
/// <para>⚠️ This corrects an assumption in <see cref="AbstateListInObject"/>: that re-applying an id always
/// REPLACES. That is right for a stronger rank and wrong for a weaker or equal one, and the wire could not
/// distinguish them because the server re-sends a combat debuff at the same rank constantly — which is
/// exactly the case the rule says to leave alone.</para></summary>
public static class SubAbstatePriority
{
    /// <summary>How many (ActionIndex, ActionArg) pairs a `SubAbState` row carries.</summary>
    public const int ActionSlots = 4;

    /// <summary>`bp_AbStateChange`. Both lists are padded to <see cref="ActionSlots"/> with
    /// <see cref="SubAbstateAction.SAA_NONE"/> and a zero argument, which is how a row with fewer than
    /// four actions is stored.</summary>
    public static StateExchange AbStateChange(
        IReadOnlyList<(SubAbstateAction Action, int Arg)> incoming,
        IReadOnlyList<(SubAbstateAction Action, int Arg)> existing)
    {
        var sawPositive = false;
        for (var i = 0; i < ActionSlots; i++)
        {
            var (inAction, inArg) = i < incoming.Count ? incoming[i] : (SubAbstateAction.SAA_NONE, 0);
            var (exAction, exArg) = i < existing.Count ? existing[i] : (SubAbstateAction.SAA_NONE, 0);

            if (inAction != exAction) return StateExchange.SAP_NORELATION;
            if (inArg < exArg) return StateExchange.SAP_SUBSCRIPT;
            if (inArg > exArg) return StateExchange.SAP_VANISH;
            if (inArg > 0) sawPositive = true;
        }
        return sawPositive ? StateExchange.SAP_SUBSCRIPT : StateExchange.SAP_VANISH;
    }
}
