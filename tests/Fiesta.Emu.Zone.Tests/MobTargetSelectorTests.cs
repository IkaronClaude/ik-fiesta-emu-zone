using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

internal sealed class Obj : IShineObject
{
    public ushort Handle { get; init; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool IsAlive { get; set; } = true;
}

/// <summary>Acquisition, per docs/AGGRO.md.
///
/// These are branch-coverage tests over the ported rule, not oracle-derived values — nothing here has been
/// run against `Zone.exe` yet, so they lock in the reading rather than prove it. That distinction is
/// recorded deliberately: an expectation written from a reading passes for the wrong reason if the reading
/// is wrong.</summary>
public class MobTargetSelectorTests
{
    private static MobTargetSelector Selector(int range) => new() { DetectRange = range };

    [Fact]
    public void PicksTheNearestValidCandidate()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var far = new Obj { Handle = 2, X = 60, Y = 0 };
        var near = new Obj { Handle = 3, X = 20, Y = 0 };

        Selector(100).mts_SelectTarget(mob, [far, near]).ShouldBe(near);
    }

    [Fact]
    public void IgnoresCandidatesBeyondTheDetectRange()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var outside = new Obj { Handle = 2, X = 101, Y = 0 };

        Selector(100).mts_SelectTarget(mob, [outside]).ShouldBeNull();
    }

    /// <summary>The boundary is the reason the r-squared seed is worth porting literally. The server
    /// compares with `jge` against a best-so-far seeded at r², so a candidate at exactly r is REJECTED.
    /// A port written as "filter by distance &lt;= r, then take the nearest" would accept it.</summary>
    [Fact]
    public void RejectsACandidateAtExactlyTheRangeBoundary()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var onBoundary = new Obj { Handle = 2, X = 100, Y = 0 };
        var justInside = new Obj { Handle = 3, X = 99, Y = 0 };

        var s = Selector(100);
        s.mts_SelectTarget(mob, [onBoundary]).ShouldBeNull();
        s.mts_SelectTarget(mob, [justInside]).ShouldBe(justInside);
    }

    [Fact]
    public void GatesRunBeforeTheDistanceComparison_SoARejectedNearCandidateDoesNotHideAFarOne()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var nearButInvalid = new Obj { Handle = 2, X = 10, Y = 0 };
        var farButValid = new Obj { Handle = 3, X = 50, Y = 0 };

        var s = Selector(100);
        s.CanTarget = (_, c) => !ReferenceEquals(c, nearButInvalid);

        s.mts_SelectTarget(mob, [nearButInvalid, farButValid]).ShouldBe(farButValid);
    }

    [Fact]
    public void SkipsTheScannerItselfAndTheDead()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var corpse = new Obj { Handle = 2, X = 5, Y = 0, IsAlive = false };

        Selector(100).mts_SelectTarget(mob, [mob, corpse]).ShouldBeNull();
    }

    [Fact]
    public void ReturnsNullWhenNothingIsNearby()
        => Selector(100).mts_SelectTarget(new Obj { Handle = 1 }, []).ShouldBeNull();

    [Fact]
    public void DistanceIsSquaredAndNeverRooted()
        => MobTargetSelector.SquaredDistance(new Obj { X = 0, Y = 0 }, new Obj { X = 3, Y = 4 })
            .ShouldBe(25);

    /// <summary>Squared distance is computed in 64-bit here, which is a DELIBERATE DEVIATION: the server
    /// carries it as a 32-bit `unsigned long` and squares the range with a 32-bit `imul`, so both can wrap.
    ///
    /// <para>At real map scale the two agree — a detect range in the hundreds gives r² in the millions —
    /// and 64-bit avoids a wrap that would make a distant mob look adjacent. But it IS a deviation, and it
    /// is recorded rather than quietly assumed harmless: a range above 46,340 squares past int32 and the
    /// server would wrap where this does not.</para>
    ///
    /// <para>This test originally asserted no overflow at int32 coordinate extremes. It failed, correctly:
    /// (int.MaxValue - int.MinValue)² overflows even 64-bit. The assertion was wrong, not the code — and
    /// the server would not survive those inputs either.</para></summary>
    [Theory]
    [InlineData(0, 0, 3, 4, 25L)]
    [InlineData(0, 0, 30000, 40000, 2_500_000_000L)]   // past int32, fine in 64-bit
    [InlineData(-1000, -1000, 1000, 1000, 8_000_000L)]
    public void SquaredDistanceIsExactAtMapScale(int ax, int ay, int bx, int by, long expected)
        => MobTargetSelector.SquaredDistance(new Obj { X = ax, Y = ay }, new Obj { X = bx, Y = by })
            .ShouldBe(expected);

    // ---- hate list ----------------------------------------------------------------------------------

    [Fact]
    public void AggroAccumulatesPerAttackerAndTheTopEntryWins()
    {
        var s = Selector(100);
        var a = new Obj { Handle = 2 };
        var b = new Obj { Handle = 3 };

        s.mts_AppendAggroPoint(a, 100);
        s.mts_AppendAggroPoint(b, 40);
        s.mts_AppendAggroPoint(b, 90);       // b overtakes

        s.AggroList.Count.ShouldBe(2);
        s.mts_GetTopAggroTarget().ShouldBe(b);
    }

    [Fact]
    public void DecreasingAggroCanChangeTheTopTarget()
    {
        var s = Selector(100);
        var a = new Obj { Handle = 2 };
        var b = new Obj { Handle = 3 };
        s.mts_AppendAggroPoint(a, 100);
        s.mts_AppendAggroPoint(b, 80);

        s.mts_DecreaseAggroPoint(a, 50);
        s.mts_GetTopAggroTarget().ShouldBe(b);
    }

    [Fact]
    public void DeadEntriesAreNotChosen()
    {
        var s = Selector(100);
        var dead = new Obj { Handle = 2, IsAlive = false };
        var alive = new Obj { Handle = 3 };
        s.mts_AppendAggroPoint(dead, 1000);
        s.mts_AppendAggroPoint(alive, 1);

        s.mts_GetTopAggroTarget().ShouldBe(alive);
    }

    [Fact]
    public void ClearEmptiesTheList()
    {
        var s = Selector(100);
        s.mts_AppendAggroPoint(new Obj { Handle = 2 }, 10);
        s.mts_AggroClear();
        s.AggroList.ShouldBeEmpty();
        s.mts_GetTopAggroTarget().ShouldBeNull();
    }
}
