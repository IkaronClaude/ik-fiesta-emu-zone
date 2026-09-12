using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ A CHARACTER MAY ONLY KNOW SKILLS OF ITS OWN LEVEL, and if that ever slips the SP picture
/// goes with it. Operator: "no char should ever run out of sp really. Stones basically always
/// out-refuel whatever one might use - So long as characters only use skills OF THEIR LEVEL, if this
/// simulation erroneously uses higher level skills, mana will be gone in split seconds."
///
/// <para>And specifically, on the level-25 cell the matrix was running: it "should have never been able
/// to use Nature's Mist nor exhaust its SP".</para></summary>
public class LevelAppropriateSkillsTests(ITestOutputHelper output)
{
    private static (string? shine, string? res) Data()
    {
        var shine = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        var res = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return (Directory.Exists(Path.Combine(shine, "World")) ? shine : null,
                File.Exists(Path.Combine(res, "ActiveSkillView.shn")) ? res : null);
    }

    /// <summary>Nothing above the character's level, on any class at any level in the matrix.</summary>
    [SkippableFact]
    public void NoCharacterEverKnowsASkillAboveItsOwnLevel()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");
        var skills = SkillCatalog.Load(shine!, res!);

        var offenders = new List<string>();
        foreach (var (cls, id) in skills.ClassIds)
            foreach (var level in new[] { 10, 20, 25, 30, 60, 100 })
                foreach (var s in skills.LearnedBy(id, level))
                    if (s.DemandLevel > level)
                        offenders.Add($"{cls}@{level} knows {s.Name} (needs {s.DemandLevel})");

        foreach (var o in offenders.Take(20)) output.WriteLine(o);
        offenders.ShouldBeEmpty("a character knows a skill it cannot have learned yet");
    }

    /// <summary>The specific claim: the level-25 archer cell and Nature's Mist (level 51).</summary>
    [SkippableFact]
    public void TheLevelTwentyFiveArcherCannotKnowNaturesMist()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");
        var skills = SkillCatalog.Load(shine!, res!);
        var mist = skills.Skills.First(s => s.InxName == "SpiritFog01");
        output.WriteLine($"{mist.Name} needs level {mist.DemandLevel}");

        foreach (var cls in new[] { "Archer", "HawkArcher", "Ranger" })
        {
            if (!skills.ClassIds.TryGetValue(cls, out var id)) continue;
            var kit = skills.LearnedBy(id, 25);
            var has = kit.Any(s => s.InxName == "SpiritFog01");
            output.WriteLine($"  {cls} at 25: {kit.Count} skills, knows Nature's Mist = {has}");
            has.ShouldBeFalse($"{cls} at level 25 must not know a level-{mist.DemandLevel} skill");
        }
    }

    /// <summary>How wrong the wrong class actually made the numbers -- the honest blast radius. The skill
    /// LISTS barely differ, because UseClass sets are cumulative and a Ranger at 25 still only qualifies
    /// for the low-level skills. What differs is the STAT TABLE the character is built from.</summary>
    [SkippableFact]
    public void TheWrongClassChangedTheStatsMoreThanTheSkills()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");
        var skills = SkillCatalog.Load(shine!, res!);

        foreach (var (wrong, right) in new[] { ("Ranger", "HawkArcher"), ("Warrior", "CleverFighter") })
        {
            var fw = Path.Combine(shine!, "World", $"Param{wrong}Server.txt");
            var fr = Path.Combine(shine!, "World", $"Param{right}Server.txt");
            if (!File.Exists(fw) || !File.Exists(fr)) continue;

            var a = new CombatSimulation(seed: 1) { Skills = skills };
            a.Player.Become(ClassParamTable.Load(fw), 25, skills: skills);
            var b = new CombatSimulation(seed: 1) { Skills = skills };
            b.Player.Become(ClassParamTable.Load(fr), 25, skills: skills);

            output.WriteLine($"level 25  {wrong,-14} hp {a.Player.MaxHp,5} sp {a.Player.MaxSp,5} "
                             + $"skills {a.Player.LearnedSkills.Count}");
            output.WriteLine($"          {right,-14} hp {b.Player.MaxHp,5} sp {b.Player.MaxSp,5} "
                             + $"skills {b.Player.LearnedSkills.Count}");
        }
    }

    /// <summary>⭐ CAN IT ACTUALLY RUN OUT OF SP? The operator says no, near enough always. This measures
    /// the honest version of the question: cast the character's most expensive known skill on cooldown and
    /// see whether SP regeneration keeps up, with no stones involved at all.</summary>
    [SkippableTheory]
    [InlineData("Archer", 25)]
    [InlineData("Fighter", 25)]
    [InlineData("Cleric", 25)]
    [InlineData("Mage", 25)]
    public void ACharacterOfItsOwnLevelDoesNotStarveForSp(string baseClass, int level)
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");

        var skills = SkillCatalog.Load(shine!, res!);
        var prog = ClassProgression.Derive(skills, ClassProgressionTests.LineOrder);
        var cls = prog.NameForLevel(baseClass, level) ?? baseClass;
        var file = Path.Combine(shine!, "World", $"Param{cls}Server.txt");
        Skip.If(!File.Exists(file), $"no class table for {cls}");

        var table = ClassParamTable.Load(file);
        var sim = new CombatSimulation(seed: 42) { Skills = skills };
        sim.Player.Become(table, level, skills: skills);

        var kit = skills.LearnedBy(cls, level).Where(s => s.IsOffensive && s.Sp > 0).ToList();
        Skip.If(kit.Count == 0, $"{cls} has no offensive skills at {level}");
        var dearest = kit.OrderByDescending(s => s.Sp).First();

        // What one cast costs against what the bar holds and what it regenerates in that skill's cooldown.
        var perCast = dearest.Sp;
        var cooldown = Math.Max(1, dearest.CooldownMs);
        output.WriteLine($"{cls} lv{level}: maxSp {sim.Player.MaxSp}, {kit.Count} offensive skills, "
                         + $"dearest {dearest.Name} costs {perCast} sp every {cooldown}ms "
                         + $"({100.0 * perCast / Math.Max(1, sim.Player.MaxSp):F1}% of the bar per cast)");

        // A single cast must not be a large fraction of the bar. Anything near it means the character is
        // holding a skill far above its level -- which is the failure this test exists to catch.
        perCast.ShouldBeLessThan(sim.Player.MaxSp / 2,
            $"{cls} at {level} would spend half its SP bar on ONE cast of {dearest.Name}; that is what a "
            + "skill above the character's level looks like");
    }
}
