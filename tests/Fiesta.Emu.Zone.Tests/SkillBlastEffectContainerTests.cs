using Fiesta.Emu.Zone.Skill;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`sbec_Routine` — how a multi-hit sequence lands over TIME.</summary>
public class SkillBlastEffectContainerTests
{
    private static SkillBlastEffect At(long tick, uint caster = 7)
        => new(tick, CasterRegistNumber: caster, TargetRegistNumber: 9);

    /// <summary>⭐ The queue is TICK-ORDERED and the loop BREAKS at the first effect that is not due — it
    /// does not continue past it.
    ///
    /// <para>So a later-but-due effect sitting behind an earlier-but-not-due one does NOT fire. Modelling
    /// this as "scan all, fire the due ones" gives a different firing order from the server's under any
    /// out-of-order insertion, which is exactly the case a simulation would hit.</para></summary>
    [Fact]
    public void ThePassStopsAtTheFirstEffectNotYetDue()
    {
        List<SkillBlastEffect> queue = [At(100), At(5000), At(200)];

        var fired = SkillBlastEffectContainer.Fire(queue, nowMs: 1000, _ => true, out var reached);

        fired.Select(e => e.BlastTick).ShouldBe([100L]);
        reached.ShouldBe(1, "the 5000 stops the pass, so the due 200 behind it never fires");
    }

    /// <summary>In proper order everything due goes at once.</summary>
    [Fact]
    public void EverythingDueInOrderFiresInOnePass()
    {
        List<SkillBlastEffect> queue = [At(100), At(200), At(300), At(5000)];

        var fired = SkillBlastEffectContainer.Fire(queue, 1000, _ => true, out var reached);

        fired.Select(e => e.BlastTick).ShouldBe([100L, 200L, 300L]);
        reached.ShouldBe(3);
    }

    /// <summary>Due is inclusive: an effect whose tick equals the clock fires.</summary>
    [Fact]
    public void AnEffectExactlyOnTheClockIsDue()
    {
        SkillBlastEffectContainer.IsDue(At(1000), 1000).ShouldBeTrue();
        SkillBlastEffectContainer.IsDue(At(1001), 1000).ShouldBeFalse();
    }

    /// <summary>⭐ The caster is re-validated by REGISTRATION NUMBER, not by pointer — so a recycled object
    /// at the same address cannot inherit a queued strike. Same recycling trap that bites handle-keyed
    /// state elsewhere in this project.</summary>
    [Fact]
    public void ARecycledCasterFailsTheRegistrationCheck()
    {
        var effect = At(100, caster: 7);

        SkillBlastEffectContainer.CasterStillValid(effect, hasSkill: true, casterPresent: true,
            casterAlive: true, currentRegistNumber: 7).ShouldBeTrue();

        SkillBlastEffectContainer.CasterStillValid(effect, true, true, true, currentRegistNumber: 8)
            .ShouldBeFalse("the slot was reused by a different object");

        SkillBlastEffectContainer.CasterStillValid(effect, true, true, true, currentRegistNumber: null)
            .ShouldBeFalse("no caster at all");
        SkillBlastEffectContainer.CasterStillValid(effect, hasSkill: false, true, true, 7).ShouldBeFalse();
        SkillBlastEffectContainer.CasterStillValid(effect, true, casterPresent: false, true, 7).ShouldBeFalse();
        SkillBlastEffectContainer.CasterStillValid(effect, true, true, casterAlive: false, 7).ShouldBeFalse();
    }

    /// <summary>⚠️ A due effect that fails validation is still CONSUMED — the original drops it and moves
    /// on rather than leaving it queued. So a caller must remove everything the pass REACHED, not only
    /// what it fired, or a dead caster's strike blocks the queue forever.</summary>
    [Fact]
    public void AnInvalidEffectIsConsumedNotLeftQueued()
    {
        List<SkillBlastEffect> queue = [At(100, caster: 7), At(200, caster: 8)];

        var fired = SkillBlastEffectContainer.Fire(queue, 1000,
            e => e.CasterRegistNumber == 8, out var reached);

        fired.Select(e => e.BlastTick).ShouldBe([200L]);
        reached.ShouldBe(2, "both were reached; only one fired, and BOTH must be removed");
    }

    /// <summary>Each queued strike owns its `MultiHitArgument` BY VALUE (embedded at +0x2C, not a
    /// pointer), which is why one sequence's strikes can carry different damage rates without
    /// interfering.</summary>
    [Fact]
    public void EachStrikeCarriesItsOwnMultiHitArgument()
    {
        var first = At(100) with { MultiHit = new MultiHitArgument(HitStep: 0, DamageRate: 300) };
        var second = At(200) with { MultiHit = new MultiHitArgument(HitStep: 1, DamageRate: 700) };

        var fired = SkillBlastEffectContainer.Fire([first, second], 1000, _ => true, out _);

        fired.Select(e => e.MultiHit!.DamageRate).ShouldBe([300, 700]);
    }
}
