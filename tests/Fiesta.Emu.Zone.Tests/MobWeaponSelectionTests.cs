using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Which of a mob's several weapons it actually uses.</summary>
public class MobWeaponSelectionTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobWeapon.shn")) ? root : null;
    }

    /// <summary>Against a player the server forces weapon INDEX 0 — `mab_Think` dynamic-casts the target to
    /// `ShinePlayer` and zeroes the index when the cast succeeds. Only non-player targets use the index the
    /// mob's own virtual returned.</summary>
    [SkippableFact]
    public void TheAttackAgainstAPlayerIsWeaponIndexZero()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        foreach (var name in new[] { "Orc", "Boar", "Marlone", "Pinky" })
            box.AttackAgainstPlayer(name).ShouldBe(box.WeaponsFor(name)[0]);
    }

    /// <summary>Index 0 happens to be the skill-less row for EVERY mob, which is why the earlier
    /// "first row whose Skill is `-`" rule gave the right answers.
    ///
    /// <para>It was right for the wrong reason. This pins the coincidence, so if the data ever stops holding
    /// it a test says so, rather than the simulation quietly adopting a skill attack as a mob's basic
    /// swing.</para></summary>
    [SkippableFact]
    public void Index0IsTheSkillessRowForEveryMob()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        foreach (var (name, weapons) in box.Weapons)
            weapons[0].Skill.ShouldBeOneOf("-", "");
    }

    /// <summary>Most mobs have skill attacks beyond their basic swing, and none of that is modelled.
    /// Recorded in OPEN_QUESTIONS.md rather than left to be rediscovered.</summary>
    [SkippableFact]
    public void MostMobsHaveSkillAttacksThatAreNotModelled()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        box.Weapons.Count(kv => kv.Value.Count > 1).ShouldBeGreaterThan(1000);
    }

    // ---- so_mob_SelectWeapon, the selection itself ---------------------------------------------

    private static MobWeaponOption Weapon(int rate, long sp = 0, long cooldownUntil = 0, int skill = 1)
        => new(skill, rate, sp, cooldownUntil);

    private static IEnumerator<int> Draws(params int[] values) => ((IEnumerable<int>)values).GetEnumerator();

    /// <summary>⭐ <b>Within `so_mob_SelectWeapon` the list is walked from the HIGHEST index DOWN</b>, so
    /// higher indices get first refusal and index 0 catches what none of them wanted.
    ///
    /// <para>⚠️ Read together with <see cref="TheAttackAgainstAPlayerIsWeaponIndexZero"/> above: against a
    /// PLAYER this whole selection is bypassed and the index forced to 0. So the descending walk governs
    /// mob-versus-non-player attacks, and "we always use weapon 0" is right for the player case.</para></summary>
    [Fact]
    public void TheHighestIndexGetsFirstRefusal()
    {
        // Three weapons, all certain to pass their roll. The last one wins.
        List<MobWeaponOption> weapons = [Weapon(1000), Weapon(1000), Weapon(1000)];
        MobWeaponSelection.SelectWeapon(weapons, currentSp: 100, nowMs: 0, Draws(0, 0, 0)).ShouldBe(2);
    }

    /// <summary>A weapon that fails its roll hands over to the next one DOWN, and index 0 catches what
    /// nothing above it wanted.</summary>
    [Fact]
    public void AFailedRollFallsThroughToTheNextLowerIndex()
    {
        List<MobWeaponOption> weapons = [Weapon(1000), Weapon(100), Weapon(100)];

        // Index 2 rolls 500 (fails), index 1 rolls 500 (fails), index 0 rolls 500 (passes: rate 1000).
        MobWeaponSelection.SelectWeapon(weapons, 100, 0, Draws(500, 500, 500)).ShouldBe(0);

        // Index 2 rolls 50 and takes it immediately.
        MobWeaponSelection.SelectWeapon(weapons, 100, 0, Draws(50)).ShouldBe(2);
    }

    /// <summary>⚠️ The roll is INCLUSIVE (<c>draw &lt;= rate</c>), so a `BlastRate` of 0 is not "never" —
    /// a draw of 0 still selects, one time in a thousand. Reading 0 as "disabled" is the recurring
    /// zero-as-sentinel mistake and the binary genuinely gives zero a meaning here.</summary>
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(1000, 1000, true)]
    [InlineData(999, 1000, true)]
    public void TheRateRollIsInclusiveSoZeroStillFiresOnce(int draw, int rate, bool passes)
        => MobWeaponSelection.RatePasses(draw, rate).ShouldBe(passes);

    /// <summary>The three gates are checked BEFORE the roll, so a weapon that fails one consumes no draw
    /// — which is what keeps the shared WELL512 stream in step.</summary>
    [Fact]
    public void TheGatesConsumeNoDraw()
    {
        List<MobWeaponOption> weapons =
        [
            Weapon(1000),                       // 0: always available
            Weapon(1000, sp: 500),              // 1: too expensive
            Weapon(1000, cooldownUntil: 5000),  // 2: on cooldown
        ];

        // Only index 0 ever rolls, so one draw is enough.
        MobWeaponSelection.SelectWeapon(weapons, currentSp: 100, nowMs: 1000, Draws(0)).ShouldBe(0);
    }

    /// <summary>A weapon whose skill is not in `SkillDataBox` is skipped before SP or cooldown are even
    /// looked at.</summary>
    [Fact]
    public void AnUnresolvableSkillIsSkipped()
    {
        List<MobWeaponOption> weapons = [Weapon(1000, skill: 1), Weapon(1000, skill: 999)];
        MobWeaponSelection.SelectWeapon(weapons, 100, 0, Draws(0),
                skillResolves: id => id != 999)
            .ShouldBe(0);
    }

    /// <summary>⚠️ Nothing selected returns <b>-1</b>, not 0 — 0 is a real weapon index and the two states
    /// must stay distinguishable.</summary>
    [Fact]
    public void NothingSelectedIsMinusOneNotZero()
    {
        List<MobWeaponOption> weapons = [Weapon(100), Weapon(100)];
        MobWeaponSelection.SelectWeapon(weapons, 100, 0, Draws(999, 999))
            .ShouldBe(MobWeaponSelection.NoWeapon);
        MobWeaponSelection.NoWeapon.ShouldBe(-1);
    }

    /// <summary>`smo_SkillBlastOption() == 2` short-circuits everything and forces weapon 0 — the one
    /// place index 0 really is a default.</summary>
    [Fact]
    public void ABlockedMobAttacksWithWeaponZero()
    {
        List<MobWeaponOption> weapons = [Weapon(1000), Weapon(1000)];
        MobWeaponSelection.SelectWeapon(weapons, 100, 0, Draws(),
                blastOption: MobWeaponSelection.BlastOptionBlocked)
            .ShouldBe(0);
    }

    /// <summary>The per-instance override vector is consulted FIRST, so two mobs of the same kind can
    /// disagree about their weapon weights without the data changing.</summary>
    [Fact]
    public void AnInstanceOverrideBeatsTheMobWeaponRow()
    {
        List<MobWeaponOption> weapons = [Weapon(1000), Weapon(1000)];

        MobWeaponSelection.UseWeaponRate(weapons, 1).ShouldBe(1000);
        MobWeaponSelection.UseWeaponRate(weapons, 1, instanceOverrides: [0, 0]).ShouldBe(0);

        // Past the end of the override vector it falls back to the row, and past both it is 0.
        MobWeaponSelection.UseWeaponRate(weapons, 1, instanceOverrides: [50]).ShouldBe(1000);
        MobWeaponSelection.UseWeaponRate(weapons, 9).ShouldBe(0);
    }
}
