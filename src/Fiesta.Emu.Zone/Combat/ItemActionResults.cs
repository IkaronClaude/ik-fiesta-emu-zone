namespace Fiesta.Emu.Zone.Combat;

/// <summary>`ItemActionObserveManager::ActionResults` — what an item-action run leaves behind for the
/// damage path to apply.
///
/// <para>`roe_AttackPower` collects results from BOTH sides (`EventRun_IncDmgRate` on the attacker's
/// observer manager and again on the defender's, plus `EventRunBySkillGroupIndex` for a cast) into one
/// `ActionResults`, then folds it into each weapon bound through
/// <see cref="Apply"/>.</para></summary>
public sealed class ItemActionResults(IEnumerable<int> ratesPermille)
{
    private readonly int[] _rates = [.. ratesPermille];

    /// <summary>No item actions fired. Distinct from a single neutral rate: the whole block in
    /// `roe_AttackPower` is GATED on at least one action having fired, so with none the bounds are not
    /// even truncated to integers on the way past. "Nothing happened" and "something happened and
    /// multiplied by one" are not the same path.</summary>
    public static ItemActionResults None { get; } = new([]);

    /// <summary>How many results accumulated. Each contributes its own multiply and its own divide.</summary>
    public int Count => _rates.Length;

    /// <summary>`ActionResults::GetRateAppliValue` (0x005D0C20).
    ///
    /// <code>
    /// if (value == 0) return 0;
    /// for (i = 0; i &lt; count; i++)
    ///     value = (uint)(value * result[i].rate) / 1000;      // UNSIGNED divide, truncating
    /// return value;
    /// </code>
    ///
    /// <para><b>The rates COMPOUND, and each one truncates on its own.</b> This is the detail that makes a
    /// shortcut wrong: three results of 1100 are not one rate of 1331, because the intermediate value is
    /// floored to an integer after every step. Summing them, or multiplying them together and dividing
    /// once, both drift — and drift further the more results there are.</para>
    ///
    /// <para>The <c>value == 0</c> short-circuit is the function's own, and it is the reason an empty
    /// result set and a zero input are indistinguishable in the return.</para></summary>
    public uint Apply(uint value)
    {
        if (value == 0) return 0;
        foreach (var rate in _rates)
            value = (uint)((ulong)value * (uint)rate / 1000u);
        return value;
    }

    /// <summary>The same fold over a signed bound, which is how `roe_AttackPower` uses it — each weapon
    /// bound is truncated to an integer (`fistp`, toward zero) before being passed in.</summary>
    public int Apply(int value) => (int)Apply((uint)value);
}

/// <summary>`EventRun_IncDmgRate` (0x005D2010) — the entry point `roe_AttackPower` calls on each side.
///
/// <para>Its own body is short; everything interesting is in the dispatcher it calls and in
/// <see cref="ItemActionResults.Apply"/>, which is what the damage actually goes through.</para>
///
/// <code>
/// if (results == null || attacker == null || defender == null) return false;   // three gates
/// arg = { condition: 0, effect: attackType, event: 9, me: this-&gt;[0x20],
///         subject: attacker, object: defender };
/// return _EventRun(results, &amp;arg, 0xFFFF);
/// </code>
///
/// <para>The 9 is the event selector for "increase damage rate" and the 0xFFFF is the slot mask —
/// every slot. Both are literals in the call, not parameters.</para></summary>
public static class ItemActionEvent
{
    /// <summary>The event selector `EventRun_IncDmgRate` writes into the argument — `[ebp-0x14] = 9`.</summary>
    public const int IncreaseDamageRate = 9;

    /// <summary>The slot mask it passes to `_EventRun`: 0xFFFF, i.e. every slot.</summary>
    public const int AllSlots = 0xFFFF;

    /// <summary>All three pointers must be non-null or the run is skipped and no result is produced —
    /// which leaves `roe_AttackPower`'s whole item-action block gated shut.</summary>
    public static bool WouldRun(bool hasResults, bool hasAttacker, bool hasDefender)
        => hasResults && hasAttacker && hasDefender;
}
