using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`FreeStatTables` — what spent points are worth.
///
/// <para>Every formula was read out of a running zone and checked against ALL 181 entries of its table,
/// not sampled. The values pinned below are the ones a reader would want to spot-check by hand.</para></summary>
public class FreeStatTableTests
{
    /// <summary>Str and Int share one curve, `n + n/5`, and the integer divide makes it a staircase rather
    /// than a 1.2x line — 4 points buy 4, 5 buy 6.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(5, 6)]
    [InlineData(17, 20)]
    [InlineData(100, 120)]
    [InlineData(180, 216)]
    public void StrAndIntShareTheSameStaircase(int points, int expected)
    {
        FreeStatTables.StrWcAbsolute(points).ShouldBe(expected);
        FreeStatTables.IntMaAbsolute(points).ShouldBe(expected);
    }

    /// <summary>Con's armour and Men's magic resistance are both `ceil(n/2)`.
    ///
    /// <para>`FUTURE_TESTS.md` listed this as "the same trap, unsprung" — the port guessed `ceil(n/2)`
    /// from four samples and the capture's character had zero Con, so nothing could exercise it. Read in
    /// full, <b>the guess was right</b>, which is worth pinning as much as a correction would have
    /// been.</para></summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(21, 11)]
    [InlineData(50, 25)]
    [InlineData(180, 90)]
    public void ConArmourAndMenResistAreCeilHalf(int points, int expected)
    {
        FreeStatTables.ConAcAbsolute(points).ShouldBe(expected);
        FreeStatTables.MenMrAbsolute(points).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(25, 125)]
    [InlineData(180, 900)]
    public void ConAndMenGiveFiveHealthOrManaPerPoint(int points, int expected)
    {
        FreeStatTables.ConMaxHp(points).ShouldBe(expected);
        FreeStatTables.MenMaxSp(points).ShouldBe(expected);
    }

    /// <summary>Dex's hit rating gets cheaper twice: +3 a point to 33, +2 to 67, +1 after.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(33, 99)]
    [InlineData(34, 101)]
    [InlineData(50, 133)]
    [InlineData(67, 167)]
    [InlineData(68, 168)]
    [InlineData(180, 280)]
    public void DexHitRatingHasThreeBands(int points, int expected)
        => FreeStatTables.DexThRate(points).ShouldBe(expected);

    /// <summary>Dex's block rating has two: +2 a point to 50, +1 after.</summary>
    [Theory]
    [InlineData(33, 66)]
    [InlineData(50, 100)]
    [InlineData(51, 101)]
    [InlineData(180, 230)]
    public void DexBlockRatingHasTwoBands(int points, int expected)
        => FreeStatTables.DexTbRate(points).ShouldBe(expected);

    /// <summary>Con's block rate stops paying at 150 — the last 30 points buy nothing.</summary>
    [Theory]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(52, 51)]
    [InlineData(150, 100)]
    [InlineData(180, 100)]
    public void ConBlockRateFlattensAtOneHundredAndFifty(int points, int expected)
        => FreeStatTables.ConBlockRate(points).ShouldBe(expected);

    /// <summary><b>25 points of MEN are worth exactly +50 permille of critical chance</b> — 5%, which is
    /// the figure the operator gave from play before this table was read. The table agrees to the point.
    ///
    /// <para>It also flattens: +2 a point to 25, +1 to 61, one per two points after, and nothing at all
    /// past 149.</para></summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(17, 34)]
    [InlineData(25, 50)]
    [InlineData(33, 58)]
    [InlineData(50, 75)]
    [InlineData(61, 86)]
    [InlineData(100, 105)]
    [InlineData(149, 130)]
    [InlineData(180, 130)]
    public void TwentyFiveMenIsExactlyFivePercentCrit(int points, int expected)
        => FreeStatTables.MenCriRate(points).ShouldBe(expected);

    /// <summary>Index 0 is a real entry — no points spent, every field zero — and the tables stop at 180,
    /// so anything past it clamps rather than throwing.</summary>
    [Fact]
    public void ZeroIsARealEntryAndTheTablesClampAtOneEightyOne()
    {
        FreeStatTables.StrWcAbsolute(0).ShouldBe(0);
        FreeStatTables.MenCriRate(0).ShouldBe(0);
        FreeStatTables.DexThRate(500).ShouldBe(FreeStatTables.DexThRate(FreeStatTables.MaxPoints));
        FreeStatTables.StrWcAbsolute(-5).ShouldBe(0);
    }

    /// <summary>The capture's character, so a reader can tie the tables to the ground truth:
    /// Ikaron is Str 17 / Con 0 / Dex 33 / Int 0 / Men 25.</summary>
    [Fact]
    public void TheCapturedCharactersFreeStatTerms()
    {
        FreeStatTables.StrWcAbsolute(17).ShouldBe(20);
        FreeStatTables.ConAcAbsolute(0).ShouldBe(0);
        FreeStatTables.DexThRate(33).ShouldBe(99);
        FreeStatTables.DexTbRate(33).ShouldBe(66);
        FreeStatTables.MenCriRate(25).ShouldBe(50);
    }

    /// <summary>The tables ARE the implementation; these closed forms are the readable description of each
    /// shape. Holding the two together means a future table edit that breaks a stated shape shows up as a
    /// finding instead of a silent divergence — and a shape that is simply wrong gets caught here rather
    /// than in a damage number.</summary>
    [Fact]
    public void EveryTableMatchesTheShapeItsCommentClaims()
    {
        for (var n = 0; n <= FreeStatTables.MaxPoints; n++)
        {
            FreeStatTables.StrWcAbsolute(n).ShouldBe(n + n / 5, $"Str at {n}");
            FreeStatTables.IntMaAbsolute(n).ShouldBe(n + n / 5, $"Int at {n}");
            FreeStatTables.ConAcAbsolute(n).ShouldBe((n + 1) / 2, $"Con.AC at {n}");
            FreeStatTables.MenMrAbsolute(n).ShouldBe((n + 1) / 2, $"Men.MR at {n}");
            FreeStatTables.ConMaxHp(n).ShouldBe(5 * n, $"Con.MaxHP at {n}");
            FreeStatTables.MenMaxSp(n).ShouldBe(5 * n, $"Men.MaxSP at {n}");

            FreeStatTables.DexThRate(n).ShouldBe(
                n <= 33 ? 3 * n : n <= 67 ? 99 + 2 * (n - 33) : 167 + (n - 67), $"Dex.TH at {n}");
            FreeStatTables.DexTbRate(n).ShouldBe(
                n <= 50 ? 2 * n : 100 + (n - 50), $"Dex.TB at {n}");
            FreeStatTables.ConBlockRate(n).ShouldBe(
                n <= 50 ? n : n <= 150 ? 50 + (n - 50) / 2 : 100, $"Con.Block at {n}");
            FreeStatTables.MenCriRate(n).ShouldBe(
                n <= 25 ? 2 * n : n <= 61 ? 50 + (n - 25) : Math.Min(86 + (n - 61) / 2, 130),
                $"Men.CriRate at {n}");
        }
    }
}
