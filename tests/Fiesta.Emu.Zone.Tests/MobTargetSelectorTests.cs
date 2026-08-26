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

    /// <summary>⚠️ Distances are measured from the SIGHT CENTRE, which sits 40% of the detect range ahead of
    /// the mob — see <see cref="MobTargetSelector.SightCenter"/>. With range 100 and facing 0 that is
    /// (40, 0), so every coordinate below is chosen relative to THAT, not to the mob at the origin.
    ///
    /// <para>These three tests previously placed candidates relative to the mob and passed, because the port
    /// then measured from the mob. The rule they check — nearest wins, boundary rejected, gates first — has
    /// not changed; only where the circle is centred.</para></summary>
    [Fact]
    public void PicksTheNearestValidCandidate()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var far = new Obj { Handle = 2, X = 80, Y = 0 };     // 40 from the centre
        var near = new Obj { Handle = 3, X = 45, Y = 0 };    //  5 from the centre

        Selector(100).mts_SelectTarget(mob, [far, near]).ShouldBe(near);
    }

    [Fact]
    public void IgnoresCandidatesBeyondTheDetectRange()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var outside = new Obj { Handle = 2, X = -61, Y = 0 };   // 101 behind the centre

        Selector(100).mts_SelectTarget(mob, [outside]).ShouldBeNull();
    }

    /// <summary>The boundary is the reason the r-squared seed is worth porting literally. The server
    /// compares with `jge` against a best-so-far seeded at r², so a candidate at exactly r is REJECTED.
    /// A port written as "filter by distance &lt;= r, then take the nearest" would accept it.</summary>
    [Fact]
    public void RejectsACandidateAtExactlyTheRangeBoundary()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var onBoundary = new Obj { Handle = 2, X = 140, Y = 0 };   // exactly 100 from the centre
        var justInside = new Obj { Handle = 3, X = 139, Y = 0 };   // 99

        var s = Selector(100);
        s.mts_SelectTarget(mob, [onBoundary]).ShouldBeNull();
        s.mts_SelectTarget(mob, [justInside]).ShouldBe(justInside);
    }

    /// <summary>THE ANSWER TO THE ANGLE QUESTION. A mob sees much further in front of itself than behind,
    /// and the mechanism is not an angular test — the detection region stays a circle, it is simply not
    /// centred on the mob.
    ///
    /// <para>`ShineMob::so_mob_SightCenter` displaces the centre forward by <c>range * 205 / 512</c>, so with
    /// range 100 the reach is about 140 ahead and 60 behind.</para>
    ///
    /// <para>The operator reported orientation-dependent aggro from play; this project repeatedly answered
    /// "the scan is a circle" and left it unexplained. The circle reading was right and the conclusion drawn
    /// from it was wrong.</para></summary>
    [Fact]
    public void AMobSeesFurtherInFrontThanBehind()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var s = Selector(100);              // facing 0 = along +x

        var ahead = new Obj { Handle = 2, X = 139, Y = 0 };
        var behind = new Obj { Handle = 3, X = -59, Y = 0 };
        s.mts_SelectTarget(mob, [ahead]).ShouldBe(ahead, "139 ahead is inside the forward reach");
        s.mts_SelectTarget(mob, [behind]).ShouldBe(behind, "59 behind is inside the rear reach");

        var tooFarAhead = new Obj { Handle = 4, X = 141, Y = 0 };
        var tooFarBehind = new Obj { Handle = 5, X = -61, Y = 0 };
        s.mts_SelectTarget(mob, [tooFarAhead]).ShouldBeNull();
        s.mts_SelectTarget(mob, [tooFarBehind]).ShouldBeNull();
    }

    /// <summary>Turning the mob turns its detection region with it — the same spot is seen or not seen
    /// depending only on facing.</summary>
    [Fact]
    public void TurningAroundChangesWhatTheMobCanSee()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var target = new Obj { Handle = 2, X = 120, Y = 0 };

        var s = Selector(100);
        s.Facing = 0;                                     // looking at it
        s.mts_SelectTarget(mob, [target]).ShouldBe(target);

        s.Facing = 90;                                    // 90 units = 180 degrees: facing away
        s.mts_SelectTarget(mob, [target]).ShouldBeNull();
    }

    /// <summary>The offset is 205/512 of the range, not a fixed distance — a longer-sighted mob is displaced
    /// proportionally further forward.</summary>
    [Fact]
    public void TheSightOffsetScalesWithTheDetectRange()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };

        Selector(100).SightCenter(mob).X.ShouldBe(40);     // 100 * 205 / 512 = 40
        Selector(500).SightCenter(mob).X.ShouldBe(200);    // 500 * 205 / 512 = 200
        Selector(0).SightCenter(mob).ShouldBe((0, 0));     // no range, no offset
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
