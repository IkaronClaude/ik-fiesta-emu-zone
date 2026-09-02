using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`ae4m_NextSkill` — what a mob PROPOSES, before the descending weapon walk decides.</summary>
public class MobAttackSequenceTests
{
    private static List<MobWeaponOption> Weapons(params int[] skillIds)
        => [.. skillIds.Select(id => new MobWeaponOption(id, 1000, 0, 0))];

    /// <summary>⭐ Resolving a sequence step is a SEARCH of the weapon list for the named skill, not an
    /// index into it — and it starts at index <b>1</b>, so a sequence can never propose the basic
    /// swing.</summary>
    [Fact]
    public void ASequenceStepIsResolvedBySearchingFromIndexOne()
    {
        var weapons = Weapons(70, 71, 72);

        MobAttackSequence.FindWeaponForSkill(weapons, 72).ShouldBe(2);
        MobAttackSequence.FindWeaponForSkill(weapons, 71).ShouldBe(1);
        MobAttackSequence.FindWeaponForSkill(weapons, 70)
            .ShouldBeNull("index 0 is the basic swing and is skipped by the search");
    }

    /// <summary>A blank record is skipped before its skill is even compared, so it cannot match by
    /// accident.</summary>
    [Fact]
    public void ABlankRecordIsSkippedNotMatched()
    {
        var weapons = Weapons(0, 71, 71);
        MobAttackSequence.FindWeaponForSkill(weapons, 71, isLive: i => i != 1).ShouldBe(2);
    }

    /// <summary>⚠️ `0xFFFF` terminates the sequence and means "nothing queued" — it is NOT a skill id,
    /// which is precisely what leaves skill id <b>0</b> free to be a real one.</summary>
    [Fact]
    public void TheTerminatorIsFfffSoZeroStaysAValidSkillId()
    {
        List<int> sequence = [0, 71, MobAttackSequence.NoSkill, 72];

        MobAttackSequence.SkillAtStep(sequence, 0).ShouldBe(0, "0 is a real skill id here");
        MobAttackSequence.SkillAtStep(sequence, 1).ShouldBe(71);
        MobAttackSequence.SkillAtStep(sequence, 2).ShouldBeNull("0xFFFF terminates");
        MobAttackSequence.SkillAtStep(sequence, 99).ShouldBeNull("past the end");
    }

    /// <summary>The step lives in the CALLER's pointer and this function does not advance it — which is
    /// what lets two mobs share one sequence without interfering.</summary>
    [Fact]
    public void TheStepIsTheCallersAndIsNotAdvanced()
    {
        List<int> sequence = [0, 71, 72];
        var weapons = Weapons(70, 71, 72);

        MobAttackSequence.NextWeapon(weapons, sequence, step: 1, useQueued: false).ShouldBe(1);
        MobAttackSequence.NextWeapon(weapons, sequence, step: 1, useQueued: false).ShouldBe(1);
        MobAttackSequence.NextWeapon(weapons, sequence, step: 2, useQueued: false).ShouldBe(2);
    }

    /// <summary>The queued one-shot beats the sequence when the caller's flag is set.</summary>
    [Fact]
    public void AQueuedSkillOverridesTheSequenceStep()
    {
        List<int> sequence = [0, 71];
        var weapons = Weapons(70, 71, 72);

        MobAttackSequence.NextWeapon(weapons, sequence, step: 1, useQueued: true, queuedSkillId: 72)
            .ShouldBe(2);
        MobAttackSequence.NextWeapon(weapons, sequence, step: 1, useQueued: false, queuedSkillId: 72)
            .ShouldBe(1, "the flag gates the override; without it the sequence wins");
    }

    /// <summary>⚠️ The queue is CONSUMED whether or not a weapon is found — `sm_SetNextSkillID(0xFFFF)`
    /// runs immediately after the read and before the search. So a script that queues a skill the mob has
    /// no weapon for loses it silently rather than having it retried.
    ///
    /// <para>Expressed here as: an unmatched queue falls through to the sequence in the SAME call, and the
    /// caller is obliged to clear it either way.</para></summary>
    [Fact]
    public void AnUnmatchableQueuedSkillIsStillConsumed()
    {
        List<int> sequence = [0, 71];
        var weapons = Weapons(70, 71);

        MobAttackSequence.TakeQueuedSkill(weapons, 999).ShouldBeNull();
        MobAttackSequence.NextWeapon(weapons, sequence, step: 1, useQueued: true, queuedSkillId: 999)
            .ShouldBe(1, "falls through to the sequence -- and the queue is gone regardless");
    }

    /// <summary>Nothing queued is `0xFFFF`, and it is not a search for skill 65535.</summary>
    [Fact]
    public void NothingQueuedIsNotASearch()
        => MobAttackSequence.TakeQueuedSkill(Weapons(70, MobAttackSequence.NoSkill),
                MobAttackSequence.NoSkill).ShouldBeNull();
}
