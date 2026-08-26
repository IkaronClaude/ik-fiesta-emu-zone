using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The mob AI state machine, per docs/AGGRO.md.
///
/// `mab_Think` returns the NEXT state; the driver adopts whatever comes back. As with the selector tests,
/// these lock in a reading of the binary, not oracle-measured values.</summary>
public class MobActionTests
{
    private static MobActionArgument Arg(IShineObject actor, int range, params IShineObject[] nearby)
        => new()
        {
            Actor = actor,
            Selector = new MobTargetSelector { DetectRange = range },
            Nearby = nearby,
        };

    [Fact]
    public void TheBaseStateAlwaysTransitionsToTargetting()
    {
        var arg = Arg(new Obj { Handle = 1 }, 100);
        MobActionBase.Actor_Base.mab_Think(arg).ShouldBe(MobActionBase.Actor_Targetting);
    }

    [Fact]
    public void TargettingAcquiresAndStaysPut()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var prey = new Obj { Handle = 2, X = 10, Y = 0 };
        var arg = Arg(mob, 100, prey);

        var next = MobActionBase.Actor_Targetting.mab_Think(arg);

        arg.Target.ShouldBe(prey);
        next.ShouldBe(MobActionBase.Actor_Targetting);
    }

    [Fact]
    public void TargettingFallsBackToRoamingWhenNothingQualifies()
    {
        var arg = Arg(new Obj { Handle = 1 }, 100);
        MobActionBase.Actor_Targetting.mab_Think(arg).ShouldBe(MobActionBase.Actor_Roaming);
        arg.Target.ShouldBeNull();
    }

    /// <summary>Line of sight is applied in Targetting, NOT in acquisition — so a target inside the detect
    /// circle can still be dropped. This is the one place the engagement shape can differ from the
    /// acquisition circle.</summary>
    [Fact]
    public void TargettingDiscardsAnAcquiredTargetItCannotSee()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var hidden = new Obj { Handle = 2, X = 10, Y = 0 };
        var arg = Arg(mob, 100, hidden);
        arg.so_CanSeeOtherObject = (_, _) => false;

        var next = MobActionBase.Actor_Targetting.mab_Think(arg);

        arg.Target.ShouldBeNull();
        next.ShouldBe(MobActionBase.Actor_Roaming);
    }

    [Fact]
    public void RoamingStaysUntilSomethingIsInRange()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var arg = Arg(mob, 100);
        MobActionBase.Actor_Roaming.mab_Think(arg).ShouldBe(MobActionBase.Actor_Roaming);

        arg.Nearby = [new Obj { Handle = 2, X = 10, Y = 0 }];
        MobActionBase.Actor_Roaming.mab_Think(arg).ShouldBe(MobActionBase.Actor_Targetting);
    }

    [Fact]
    public void BeingHitMidMoveStopsTheMobAndForcesReacquisition()
    {
        var arg = Arg(new Obj { Handle = 1 }, 100);
        arg.Moving = true;
        arg.Current = new MobActionInMove_Cancelable();

        arg.Current.mab_Damaged(arg);

        arg.Moving.ShouldBeFalse();
        arg.Current.ShouldBe(MobActionBase.Actor_Targetting);
    }

    [Fact]
    public void TheBaseDamagedHandlerIsANoOp()
    {
        var arg = Arg(new Obj { Handle = 1 }, 100);
        arg.Moving = true;

        MobActionBase.Actor_Base.mab_Damaged(arg);

        // 0x00549070 is the universal empty body; the base is a stub, not a fallback.
        arg.Moving.ShouldBeTrue();
        arg.Current.ShouldBe(MobActionBase.Actor_Targetting);
    }

    /// <summary>The whole driver, run as the server runs it: think, adopt, repeat.</summary>
    [Fact]
    public void TheTickLoopAdoptsWhateverThinkReturns()
    {
        var mob = new Obj { Handle = 1, X = 0, Y = 0 };
        var arg = Arg(mob, 100);
        arg.Current = MobActionBase.Actor_Base;

        arg.Current = arg.Current.mab_Think(arg);
        arg.Current.ShouldBe(MobActionBase.Actor_Targetting);

        arg.Current = arg.Current.mab_Think(arg);      // nothing nearby
        arg.Current.ShouldBe(MobActionBase.Actor_Roaming);

        arg.Nearby = [new Obj { Handle = 2, X = 5, Y = 0 }];
        arg.Current = arg.Current.mab_Think(arg);
        arg.Current.ShouldBe(MobActionBase.Actor_Targetting);

        arg.Current = arg.Current.mab_Think(arg);
        arg.Target.ShouldNotBeNull();
    }
}
