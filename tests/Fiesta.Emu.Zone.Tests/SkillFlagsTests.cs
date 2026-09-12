using Fiesta.Emu.Zone.Data;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`IsMovingSkill` and `Stun`, the two ActiveSkill columns the driver's kiting is built on.
///
/// <para>Both were absent from the simulation's `skillInfo`, and absent reads as FALSE - so the driver's
/// `stunSkill()` returned 0 for every class on every tick, and nothing that depends on "can I cast this
/// while running" or "is a stun ready" could be exercised or benched at all.</para></summary>
public class SkillFlagsTests(ITestOutputHelper output)
{
    private static (string? shine, string? res) Data()
    {
        var shine = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        var res = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return (Directory.Exists(Path.Combine(shine, "MobRegen")) ? shine : null,
                File.Exists(Path.Combine(res, "ActiveSkillView.shn")) ? res : null);
    }

    [SkippableFact]
    public void StunsAndMovingSkillsAreBothFoundInTheSkillFile()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");

        var skills = SkillCatalog.Load(shine!, res!);
        var all = skills.Skills.ToList();
        var stuns = all.Where(s => s.Stun).ToList();
        var moving = all.Where(s => s.IsMovingSkill).ToList();

        output.WriteLine($"{all.Count} skills: {stuns.Count} stun, {moving.Count} castable while moving");
        output.WriteLine("stuns:  " + string.Join(", ", stuns.Select(s => s.Name).Distinct().Take(10)));
        output.WriteLine("moving: " + string.Join(", ", moving.Select(s => s.Name).Distinct().Take(10)));

        // Both must be non-empty, and neither may be "everything" - a column read off by one position
        // produces exactly those two failures, and both would read as a plausible flag.
        stuns.ShouldNotBeEmpty("no skill in the file stuns; the StaName columns are not being read");
        moving.ShouldNotBeEmpty("no skill is castable while moving; IsMovingSkill is not being read");
        stuns.Count.ShouldBeLessThan(all.Count / 2, "half the file cannot be stuns");
        moving.Count.ShouldBeLessThan(all.Count / 2, "half the file cannot be castable while moving");
    }

    /// <summary>The archer's own kit, which is what the operator's kiting design turns on: "Archers can
    /// use some skills while running, so they can just kite through large groups of mobs."</summary>
    [SkippableFact]
    public void AnArcherHasSkillsItCanCastWhileRunning()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");

        var skills = SkillCatalog.Load(shine!, res!);
        Skip.If(!skills.ClassIds.TryGetValue("Ranger", out var ranger), "no Ranger in ClassName.shn");

        var kit = skills.LearnedBy(ranger, 25).ToList();
        var runnable = kit.Where(s => s.IsMovingSkill).ToList();
        output.WriteLine($"Ranger L25 knows {kit.Count} skills, {runnable.Count} castable while running:");
        foreach (var s in runnable) output.WriteLine($"   {s.Name} (range {s.Range}, cd {s.CooldownMs}ms)");
        foreach (var s in kit.Where(s => !s.IsMovingSkill && s.IsOffensive))
            output.WriteLine($"   [stationary] {s.Name} (range {s.Range}, cd {s.CooldownMs}ms)");

        runnable.ShouldNotBeEmpty("an archer that cannot shoot while running cannot kite, and the "
                                  + "operator's design for it depends on exactly this");
    }
}
