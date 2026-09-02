using Fiesta.Emu.Zone.Combat;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`ActionResults::GetRateAppliValue` — the fold the item-action rates actually go through.</summary>
public class ItemActionResultsTests
{
    /// <summary>A single rate behaves as expected, truncating.</summary>
    [Theory]
    [InlineData(1000, 1100, 1100)]
    [InlineData(1000, 500, 500)]
    [InlineData(457, 333, 152)]
    public void OneResultIsAPlainPermilleMultiply(int value, int rate, int expected)
        => new ItemActionResults([rate]).Apply(value).ShouldBe(expected);

    /// <summary>⭐ The rates COMPOUND, and each truncates on its own — so three results of 1100 are NOT one
    /// rate of 1331, and they are not a sum either. Both shortcuts drift, and drift further the more
    /// results there are.</summary>
    [Fact]
    public void RatesCompoundWithATruncationAtEveryStep()
    {
        var chained = new ItemActionResults([1100, 1100, 1100]).Apply(1000);

        chained.ShouldBe(1331);
        chained.ShouldNotBe(1000 * 3300 / 1000, "summing the rates is a different number");

        // Where the per-step truncation actually bites: an odd value that loses a fraction each time.
        var lossy = new ItemActionResults([1001, 1001, 1001]).Apply(7);
        lossy.ShouldBe(7, "7 * 1001 / 1000 = 7 each time -- the gain is truncated away entirely");

        // ...and the single-multiply shortcut would have produced something larger.
        (7L * 1001 * 1001 * 1001 / 1_000_000_000L).ShouldBe(7L);
    }

    /// <summary>⭐ Order matters — which is why the results are a SEQUENCE, not a set, and why they cannot
    /// be pre-multiplied into one rate.
    ///
    /// <para>It takes a case where an intermediate actually loses a fraction: multiplication is
    /// commutative, so most pairs agree and a carelessly chosen example proves nothing. Here the small
    /// rate first floors 1.5 to 1 and the whole chain never recovers.</para></summary>
    [Fact]
    public void OrderCanChangeTheResult()
    {
        var smallFirst = new ItemActionResults([300, 7000]).Apply(5);
        var largeFirst = new ItemActionResults([7000, 300]).Apply(5);

        smallFirst.ShouldBe(7, "5 -> 1 (1.5 floored) -> 7");
        largeFirst.ShouldBe(10, "5 -> 35 -> 10 (10.5 floored)");
        smallFirst.ShouldNotBe(largeFirst);
    }

    /// <summary>The function's own short-circuit: zero in, zero out, before the loop runs at all.</summary>
    [Fact]
    public void ZeroShortCircuits()
        => new ItemActionResults([2000, 2000]).Apply(0).ShouldBe(0);

    /// <summary>⚠️ "Nothing fired" is not "fired and multiplied by one". `roe_AttackPower` GATES the whole
    /// item-action block on at least one action having run, and inside that block each weapon bound is
    /// truncated to an integer on the way past — so an empty result set skips a truncation that a neutral
    /// rate would not.</summary>
    [Fact]
    public void NoResultsIsADifferentPathFromANeutralRate()
    {
        ItemActionResults.None.Count.ShouldBe(0);
        ItemActionResults.None.Apply(1234).ShouldBe(1234);
        new ItemActionResults([1000]).Apply(1234).ShouldBe(1234);
        // Identical here; the difference is the truncation of the BOUNDS, which the gate skips entirely.
    }

    /// <summary>Three gates, all pointers, and failing any of them means no results at all — which leaves
    /// `roe_AttackPower`'s item-action block gated shut rather than applying a neutral rate.</summary>
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void EveryPointerMustBePresentForTheEventToRun(bool results, bool att, bool def, bool runs)
        => ItemActionEvent.WouldRun(results, att, def).ShouldBe(runs);

    /// <summary>The two literals baked into `EventRun_IncDmgRate`'s call.</summary>
    [Fact]
    public void TheEventSelectorAndSlotMaskAreLiterals()
    {
        ItemActionEvent.IncreaseDamageRate.ShouldBe(9);
        ItemActionEvent.AllSlots.ShouldBe(0xFFFF);
    }
}
