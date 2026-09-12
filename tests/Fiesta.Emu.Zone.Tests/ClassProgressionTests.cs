using Fiesta.Emu.Zone.Data;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ WHICH CLASS A LEVEL-25 CHARACTER ACTUALLY IS.
///
/// <para>The matrix was benching a <b>Ranger at level 25</b>. Ranger is ClassID 15, the fifth class in the
/// archer line and a level-100 job; a level-25 archer is a HawkArcher, which is what the live ArcherZero
/// is (class 12 at level 23). Operator: "rangers cannot be lvl 25, that's a lvl 100 class".</para></summary>
public class ClassProgressionTests(ITestOutputHelper output)
{
    /// <summary>The four lines, base first then promotions in order. A blank separates them. Which classes
    /// belong to a line is structural in `ClassName.shn` (ids 1-5, 6-10, 11-15, 16-20); only the ORDER is
    /// stated here, and every LEVEL below is derived from the skill file.</summary>
    private static readonly string[] Lines =
    [
        "Fighter", "CleverFighter", "Warrior", "Gladiator", "",
        "Cleric", "HighCleric", "Paladin", "Guardian", "",
        "Archer", "HawkArcher", "Scout", "Ranger", "",
        "Mage", "WizMage", "Enchanter", "Wizard",
    ];

    private static (string? shine, string? res) Data()
    {
        var shine = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        var res = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return (Directory.Exists(Path.Combine(shine, "World")) ? shine : null,
                File.Exists(Path.Combine(res, "ActiveSkillView.shn")) ? res : null);
    }

    [SkippableFact]
    public void TheJobTiersAreDerivedFromTheSkillFileAndAgreeOnTwentySixtyAndOneHundred()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");

        var skills = SkillCatalog.Load(shine!, res!);
        var prog = ClassProgression.Derive(skills, Lines);

        foreach (var line in prog.Lines)
            output.WriteLine(string.Join("  ->  ", line.Select(t => $"{t.Name}({t.ClassId}) from {t.FromLevel}")));

        prog.Lines.Count.ShouldBe(4, "four levelling lines");

        // Every promotion in every line lands on one of the three job-change levels.
        foreach (var line in prog.Lines)
            foreach (var tier in line.Skip(1))
                tier.FromLevel.ShouldBeOneOf(20, 60, 100);
    }

    /// <summary>The level the matrix actually runs, against the live bots.</summary>
    [SkippableTheory]
    [InlineData("Archer", 25, "HawkArcher")]     // ArcherZero is class 12 at level 23
    [InlineData("Fighter", 25, "CleverFighter")] // FighterZero is class 2 at level 26
    [InlineData("Cleric", 25, "HighCleric")]
    [InlineData("Mage", 25, "WizMage")]
    [InlineData("Archer", 19, "Archer")]         // before the first job change
    [InlineData("Archer", 60, "Scout")]
    [InlineData("Archer", 99, "Scout")]
    [InlineData("Archer", 100, "Ranger")]        // ...and only here
    [InlineData("Fighter", 100, "Gladiator")]
    public void ACharacterOfThisLevelIsThisClass(string baseClass, int level, string expected)
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");

        var prog = ClassProgression.Derive(SkillCatalog.Load(shine!, res!), Lines);
        var actual = prog.NameForLevel(baseClass, level);
        output.WriteLine($"{baseClass} at {level} -> {actual}");
        actual.ShouldBe(expected);
    }
}
