using Fiesta.Emu.Zone.Data;
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
}
