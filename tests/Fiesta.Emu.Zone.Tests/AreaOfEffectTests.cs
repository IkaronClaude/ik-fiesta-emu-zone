using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ FIELD AREA SKILLS, which this harness could not exercise at all: a completed cast landed on
/// exactly ONE mob, so Devastate's `Area 200 / TargetNumber 10` dealt one mob's worth of damage and the
/// whole area playstyle - how a Fighter fights from 43, an Archer from 51, a Mage from 60 - scored as
/// single-target. Every conclusion drawn here about a caster in a pack was drawn in a world without AoE.
///
/// <para>The operator named the three that matter and drew the distinction that shapes the model:
/// <b>Devastate IS cast on one targeted enemy</b> and spreads from there, while <b>Frost Nova and
/// Nature's Mist need no target at all</b> - they are aimed at the ground. The file agrees:
/// `ActiveSkill.First` is 0 for Devastate and 4 for the other two, and the protocol carries the
/// difference as two different packets, `NC_BAT_SKILLBASH_OBJ_CAST_REQ` (0x2440) against
/// `NC_BAT_SKILLBASH_FLD_CAST_REQ` (0x2441).</para></summary>
public class AreaOfEffectTests(ITestOutputHelper output)
{
    private static (string? shine, string? res) Data()
    {
        var shine = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        var res = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return (Directory.Exists(Path.Combine(shine, "MobRegen")) ? shine : null,
                File.Exists(Path.Combine(res, "ActiveSkillView.shn")) ? res : null);
    }

    private static SkillDefinition Skill(SkillCatalog c, string inxName)
        => c.Skills.First(s => s.InxName == inxName);

    /// <summary>The three skills, read off the file. Levels and shapes are the operator's from play; this
    /// is the check that the columns being read are the ones they were talking about.</summary>
    [SkippableFact]
    public void TheThreeAreaSkillsReadAsTheOperatorDescribesThem()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");
        var c = SkillCatalog.Load(shine!, res!);

        var devastate = Skill(c, "GreatSwing01");
        var mist = Skill(c, "SpiritFog01");
        var nova = Skill(c, "FrostNova01");
        var multi = Skill(c, "MultiShot01");

        foreach (var s in new[] { devastate, mist, nova, multi })
            output.WriteLine($"{s.Name,-20} lv{s.DemandLevel,-4} area {s.Area,-4} targets {s.TargetNumber,-3} "
                             + $"range {s.Range,-4} castFrom {s.CastFrom} moving {s.IsMovingSkill} "
                             + $"cast {s.CastTimeMs}ms");

        // All four are genuinely area skills.
        foreach (var s in new[] { devastate, mist, nova, multi })
            s.IsAreaOfEffect.ShouldBeTrue($"{s.Name} should be an area skill");

        // DEVASTATE IS AIMED AT AN ENEMY. The operator was explicit, and the file agrees.
        devastate.IsFieldCast.ShouldBeFalse("Devastate is cast on one targeted enemy, not at the ground");
        devastate.DemandLevel.ShouldBe(43);

        // The other two are aimed at the ground and need no target.
        mist.IsFieldCast.ShouldBeTrue("Nature's Mist is aimed at a position");
        nova.IsFieldCast.ShouldBeTrue("Frost Nova is aimed at a position");
        mist.DemandLevel.ShouldBe(51);
        nova.DemandLevel.ShouldBe(60);

        // ⚠️ THE ONE PLACE THE FILE AND THE OPERATOR'S MEMORY DIVERGE, kept visible rather than smoothed
        // over. Nature's Mist really is cast on the move (moving, and no cast bar at all). Frost Nova
        // really does root you. Multi-Shot is FLAGGED castable while moving but carries a 1450ms cast
        // bar -- and movement cancels a cast bar, so in practice it is a stand-still skill, which is how
        // the operator remembers it ("multi shot has a cast time though and I think even a proper cast
        // windup with standing still, like frost nova").
        mist.IsMovingSkill.ShouldBeTrue();
        mist.CastTimeMs.ShouldBe(0);
        nova.IsMovingSkill.ShouldBeFalse();
        nova.CastTimeMs.ShouldBeGreaterThan(0);
        multi.IsMovingSkill.ShouldBeTrue();
        multi.CastTimeMs.ShouldBeGreaterThan(0);
    }

    /// <summary>Devastate, cast at ONE mob, damages the others standing around it.</summary>
    [SkippableFact]
    public void DevastateHitsEveryMobInsideItsCircle()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");
        var skills = SkillCatalog.Load(shine!, res!);
        var devastate = Skill(skills, "GreatSwing01");

        var sim = new CombatSimulation(seed: 42) { Skills = skills, LevelGaps = LevelGapTable.Load(shine!) };
        sim.Player.Become(ClassParamTable.Load(Path.Combine(shine!, "World", "ParamFighterServer.txt")),
                          level: 60, equipment: [new EquipmentPiece("axe", MinWC: 400, MaxWC: 500)],
                          skills: skills);
        sim.Player.LearnedSkills = [devastate];
        sim.Player.X = sim.Player.Y = 1000;

        // Four inside the 200u circle, one well outside it.
        var inside = new[] { (1010, 1000), (1000, 1080), (1120, 1010), (1050, 1150) };
        ushort h = 10;
        var mobs = inside.Select(p => sim.AddMob(h++, p.Item1, p.Item2, m => m.Hp = m.MaxHp = 100_000)).ToList();
        var far = sim.AddMob(h, 1000 + devastate.Area + 400, 1000, m => m.Hp = m.MaxHp = 100_000);

        sim.Cast(devastate.Id, mobs[0].Mob.Handle).ShouldBe(CombatSimulation.CastRefusal.Accepted);
        for (var i = 0; i < 40; i++) sim.Step();

        foreach (var m in mobs)
            output.WriteLine($"  mob at ({m.Mob.X},{m.Mob.Y}) hp {m.Hp}/{m.MaxHp}");
        output.WriteLine($"  far mob at ({far.Mob.X},{far.Mob.Y}) hp {far.Hp}/{far.MaxHp}");

        mobs.Count(m => m.Hp < m.MaxHp)
            .ShouldBeGreaterThan(1, "Devastate has Area 200 and TargetNumber 10; landing it on one mob "
                                    + "only is the single-target behaviour this test exists to catch");
        far.Hp.ShouldBe(far.MaxHp, "a mob outside the circle must not be touched");
    }

    /// <summary>Frost Nova and Nature's Mist take a POINT and no target. Devastate must not.</summary>
    [SkippableFact]
    public void TheGroundAimedSkillsCastWithNoTargetAtAll()
    {
        var (shine, res) = Data();
        Skip.If(shine is null || res is null, "game data not present");
        var skills = SkillCatalog.Load(shine!, res!);

        foreach (var (inx, cls) in new[] { ("FrostNova01", "Mage"), ("SpiritFog01", "Ranger") })
        {
            var skill = Skill(skills, inx);
            var sim = new CombatSimulation(seed: 42) { Skills = skills, LevelGaps = LevelGapTable.Load(shine!) };
            sim.Player.Become(ClassParamTable.Load(Path.Combine(shine!, "World", $"Param{cls}Server.txt")),
                              level: 60, equipment: [new EquipmentPiece("w", MinWC: 300, MaxWC: 400,
                                                                        MinMA: 300, MaxMA: 400)],
                              skills: skills);
            sim.Player.LearnedSkills = [skill];
            sim.Player.X = sim.Player.Y = 1000;

            // Three mobs clustered AWAY from us, and NO target is ever chosen.
            ushort h = 20;
            var at = (X: 1000 + skill.Range / 2, Y: 1000);
            var pack = new[] { (at.X, at.Y), (at.X + 30, at.Y + 20), (at.X - 25, at.Y + 35) }
                .Select(p => sim.AddMob(h++, p.Item1, p.Item2, m => m.Hp = m.MaxHp = 100_000)).ToList();

            sim.CastAt(skill.Id, at.X, at.Y).ShouldBe(CombatSimulation.CastRefusal.Accepted,
                $"{skill.Name} is a field cast and must not need a target");
            for (var i = 0; i < 60; i++) sim.Step();

            var hit = pack.Count(m => m.Hp < m.MaxHp);
            output.WriteLine($"{skill.Name}: aimed at ({at.X},{at.Y}) with no target -> hit {hit} of {pack.Count}");
            hit.ShouldBeGreaterThan(1, $"{skill.Name} has Area {skill.Area} and should catch the cluster");
        }

        // ...and the object-cast skill is refused as a field cast rather than quietly treated as one.
        var dev = Skill(skills, "GreatSwing01");
        var s2 = new CombatSimulation(seed: 42) { Skills = skills, LevelGaps = LevelGapTable.Load(shine!) };
        s2.Player.LearnedSkills = [dev];
        s2.CastAt(dev.Id, 100, 100).ShouldNotBe(CombatSimulation.CastRefusal.Accepted,
            "Devastate is cast on a targeted enemy; accepting it as a ground cast would model the "
            + "operator's distinction backwards");
    }
}
