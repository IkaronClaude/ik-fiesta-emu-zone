using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ THE REAL DRIVER, FIGHTING — `level_quest.lua` running its own rotation against the
/// simulation, at the character's REAL maximum HP.
///
/// <para>`BotSimIntegrationTests` proves the script survives 4,000 ticks and counts how much of its API
/// is backed. That is necessary and it is not the same as working: a driver can run for its whole budget
/// without ever landing a skill. <b>This asserts the behaviour that makes it a bot</b> — it casts, it
/// kills, it earns experience, it measures what its skills do, and it does not die.</para>
///
/// <para>⚠️ <b>Every one of these assertions has already caught a real bug.</b> `casts &gt; 0` is here
/// because the driver cast NOTHING for a whole run: `bot.attack` live is
/// <c>attack(skill, target)</c> and delegates to `CastAsync`, while the simulation had
/// <c>attack(handle)</c> — one melee swing. `level_quest.lua:2076` sends every damage skill through it,
/// so 931 casts were handed a skill id as a mob handle, matched no mob, and returned false. The run
/// still looked healthy: no errors, mobs dying, 15 kills. <b>An arity that happens to accept the call is
/// worse than one that throws.</b></para></summary>
public class DriverFightsInTheSimulationTests(ITestOutputHelper output)
{
    private const int Ticks = 4000;

    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return Directory.Exists(Path.Combine(root, "MobRegen")) ? root : null;
    }

    private static string? Ressystem()
    {
        var root = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return File.Exists(Path.Combine(root, "ActiveSkillView.shn")) ? root : null;
    }

    private static string? DriverPath()
    {
        var p = Environment.GetEnvironmentVariable("LEVEL_QUEST_LUA")
                ?? @"C:/Projects/ik-fiesta-bots/scripts/level_quest.lua";
        return File.Exists(p) ? p : null;
    }

    /// <summary>A level-60 Warrior in Uruga's busiest spawn area, at its own maximum HP.
    ///
    /// <para>⚠️ <b>A JOB-CHANGED class, deliberately.</b> A level-60 "Fighter" knows three offensive
    /// skills, all rank 02, because a real character stops being a Fighter at 20 — the ranks above belong
    /// to `CleverFighter` and then `Warrior`. Naming the base class here would test a level-20 rotation
    /// and call it a level-60 one.</para>
    ///
    /// <para>And no <c>2_000_000</c> HP. That crutch was in every earlier fixture because the simulation
    /// modelled no healing at all, which made survival unevaluable; soul stones now come out of the class
    /// table, so the character can be asked to actually stay alive.</para></summary>
    private static CombatSimulation UrugaWarrior(string shine, string ressystem, int wcMin = 300, int wcMax = 420)
    {
        var skills = SkillCatalog.Load(shine, ressystem);
        var sim = new CombatSimulation(seed: 42)
        {
            Skills = skills,
            LevelGaps = LevelGapTable.Load(shine),
        };

        var map = MobRegenData.Load(Path.Combine(shine, "MobRegen", "Urg.txt"));
        sim.SpawnFightable(map, MobDataBox.Load(shine), spawnSeed: 7);

        sim.Player.Become(
            ClassParamTable.Load(Path.Combine(shine, "World", "ParamWarriorServer.txt")),
            level: 60,
            equipment: [new EquipmentPiece("sword", MinWC: wcMin, MaxWC: wcMax),
                        new EquipmentPiece("plate", AC: 400)],
            skills: skills);

        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;
        return sim;
    }

    private static (CombatSimulation Sim, LevelingBotHarness Harness) Run(
        string shine, string ressystem, string driver, int wcMin = 300, int wcMax = 420)
    {
        var src = File.ReadAllText(driver);
        var sim = UrugaWarrior(shine, ressystem, wcMin, wcMax);
        var harness = LevelingBotHarness.Attach(sim, src);
        harness.Run(src, ticks: Ticks);
        return (sim, harness);
    }

    /// <summary>⭐ THE ONE THAT MATTERS: the driver runs its own rotation and fights with it.</summary>
    [SkippableFact]
    public void TheDriverCastsItsSkillsKillsMobsAndSurvives()
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var (sim, harness) = Run(shine!, ressystem!, driver!);

        output.WriteLine($"{Ticks} ticks ({Ticks * sim.TickMs / 1000}s): kills={sim.Kills} casts={sim.Casts} "
                         + $"exp={sim.Player.Experience} hp={sim.Player.Hp}/{sim.Player.MaxHp} "
                         + $"sp={sim.Player.Sp}/{sim.Player.MaxSp} errors={harness.Errors.Count}");
        output.WriteLine(harness.Report());

        harness.Errors.ShouldBeEmpty("the driver raised; see the report above");

        sim.Casts.ShouldBeGreaterThan(0,
            "the driver must actually LAND skills. It once cast nothing for an entire run while still "
            + "killing mobs by bare melee, because bot.attack(skill, target) was bound to a one-argument "
            + "melee swing -- a green run with zero casts is the exact failure this catches");

        sim.Kills.ShouldBeGreaterThan(0, "a level-60 Warrior with a 300-420 weapon should kill something");

        sim.Player.Experience.ShouldBeGreaterThan(0,
            "experience is the levelling bot's whole scoreboard, awarded per kill from the mob's MonEXP");

        sim.Player.IsAlive.ShouldBeTrue(
            "at its REAL maximum HP, with soul stones from the class table -- if this fails, survival is "
            + "the thing to look at, and it is now possible to look at it");
    }

    /// <summary>⭐ THE LEARNING LOOP CLOSES. `level_quest.lua` ranks its rotation on MEASURED damage:
    /// under-sampled skills sort first so every rank gets its turn, then the best measured damage per
    /// second wins.
    ///
    /// <para>While `skillDamageAvg` and `skillDamageSamples` were stubbed — 149,292 calls each in one run
    /// — `measuredDps` returned nil for every skill and the ranking fell back to the static table
    /// forever. The bot could never discover that a highly-rated skill performs badly against a
    /// particular mob, which is precisely what a simulation is for.</para></summary>
    [SkippableFact]
    public void TheDriverMeasuresWhatItsSkillsActuallyDo()
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var (sim, _) = Run(shine!, ressystem!, driver!);

        var sampled = sim.Player.LearnedSkills
            .Where(s => sim.SkillDamageSamples(s.Id) > 0)
            .ToList();

        foreach (var s in sampled.OrderByDescending(s => sim.SkillDamageAvg(s.Id)))
            output.WriteLine($"  {s.InxName,-20} n={sim.SkillDamageSamples(s.Id),3} "
                             + $"avg={sim.SkillDamageAvg(s.Id),8:F0}");

        sampled.ShouldNotBeEmpty("no skill landed a measurable hit, so the rotation has nothing to rank on");

        foreach (var s in sampled)
            sim.SkillDamageAvg(s.Id).ShouldBeGreaterThan(0, $"{s.InxName} was sampled but averages nothing");

        // An unsampled skill must read as UNKNOWN, not as zero damage -- the driver explores on exactly
        // this distinction, and a 0 would rank a never-cast skill last forever instead of first.
        sim.SkillDamageAvg(-1).ShouldBe(-1, "an unmeasured skill is unknown, and -1 is how that is said");
        sim.SkillDamageSamples(-1).ShouldBe(0);
    }

    /// <summary>⚠️ AND THE SIGNATURE ITSELF, pinned away from the driver so the reason survives even if
    /// `level_quest.lua` stops using it: <c>bot.attack(skill, target)</c> is a CAST.</summary>
    [SkippableFact]
    public void AttackTakesASkillAndATargetJustAsTheLiveApiDoes()
    {
        var (shine, ressystem) = (Shine(), Ressystem());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");

        var sim = UrugaWarrior(shine!, ressystem!);
        var skill = sim.Player.LearnedSkills.First(s => s.IsOffensive && s.Range == 0);

        // Stand on top of a mob so range cannot be the reason for a refusal.
        var mob = sim.Mobs.First(m => m.Mob.IsAlive);
        sim.Player.X = mob.Mob.X;
        sim.Player.Y = mob.Mob.Y;

        sim.Api.attack(skill.Id, mob.Mob.Handle).ShouldBeTrue(
            $"attack({skill.InxName}, handle) should be accepted as a cast");
        sim.Player.CastingSkill?.Id.ShouldBe(skill.Id, "attack must start the CAST, not swing a weapon");

        // ...and the sim's own melee primitive is a different call now.
        sim.Player.CastingSkill = null;
        sim.Api.swing(mob.Mob.Handle).ShouldBeTrue("swing is the one-shot melee primitive");
    }

    /// <summary>⭐ THE SIMULATION RESPONDS TO DAMAGE — measured with a MINIMAL driver, so the answer is
    /// about the simulation and not about `level_quest.lua`'s phase machine.
    ///
    /// <para>Ten times the weapon damage takes a walk-up-and-swing script from 6 kills to 17. That is the
    /// signal a damage change is supposed to produce, and it is the control for the test below.</para></summary>
    [SkippableFact]
    public void AHarderHittingWeaponKillsMore()
    {
        var (shine, ressystem) = (Shine(), Ressystem());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");

        // Walk to the nearest mob, auto-attack it. No quests, no phases, nothing to saturate on.
        const string minimal = """
            function tick()
              local m = bot.nearbyMobs()
              if #m == 0 then return end
              local best = m[1]
              if bot.dist(best.handle) > 90 then bot.walkTo(best.x, best.y)
              else bot.autoAttack(best.handle) end
            end
            """;

        int Kills(int lo, int hi)
        {
            var sim = UrugaWarrior(shine!, ressystem!, lo, hi);
            LevelingBotHarness.Attach(sim, minimal).Run(minimal, ticks: Ticks);
            return sim.Kills;
        }

        var weak = Kills(60, 95);
        var strong = Kills(600, 840);
        output.WriteLine($"minimal driver: weapon 60-95 -> {weak} kills, weapon 600-840 -> {strong} kills");

        strong.ShouldBeGreaterThan(weak * 2,
            "ten times the weapon damage should kill MUCH more in the same time. A flat response is "
            + "evidence of a BROKEN INPUT, not of a different bottleneck -- that misdiagnosis once cost "
            + "this project a session, when AttackRange sat at 12 units and read as 'quest state is the "
            + "bottleneck'");
    }

    /// <summary>⚠️ <b>A KNOWN LIMIT, ASSERTED SO IT CANNOT BE MISTAKEN FOR HEALTH.</b> The real driver's
    /// kill rate is NOT damage-limited: ten times the weapon damage moves it from 23 kills to 24, while
    /// the minimal driver above goes 6 to 17 on the same change.
    ///
    /// <para>So `level_quest.lua` saturates on something other than combat, and the candidate is named:
    /// the quest subsystem is the last unbacked surface — `availableQuests`, `eligibleQuests`, `npcCoord`,
    /// `canPick` and `drops` are still stubbed, because the simulation spawns fightable mobs only and has
    /// no NPC to hand a quest in to. The driver spends its budget cycling phases it cannot complete.</para>
    ///
    /// <para>This test exists because the flat result WOULD HAVE PASSED a naive
    /// <c>strong &gt; weak</c> assertion on a margin of one kill, and that is the shape of green run this
    /// file exists to refuse. It pins the flatness as a fact so the next change to the quest surface has
    /// something to move.</para></summary>
    [SkippableFact]
    public void TheRealDriverIsNotDamageLimited_KNOWN_LIMIT()
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var (weak, _) = Run(shine!, ressystem!, driver!, wcMin: 60, wcMax: 95);
        var (strong, _) = Run(shine!, ressystem!, driver!, wcMin: 600, wcMax: 840);

        output.WriteLine($"level_quest.lua: weapon  60-95 -> kills={weak.Kills} casts={weak.Casts} exp={weak.Player.Experience}");
        output.WriteLine($"level_quest.lua: weapon 600-840 -> kills={strong.Kills} casts={strong.Casts} exp={strong.Player.Experience}");

        // It still out-kills the minimal driver at low damage, because it uses its rotation.
        weak.Kills.ShouldBeGreaterThan(10,
            "even damage-starved, the driver's rotation should beat bare melee");

        // ...and it barely moves when damage is multiplied by ten. THIS IS THE BUG BEING TRACKED.
        (strong.Kills - weak.Kills).ShouldBeLessThan(weak.Kills / 2,
            "if the driver has become damage-responsive, the quest-phase bottleneck has been fixed -- "
            + "delete this test and tighten AHarderHittingWeaponKillsMore to cover the real driver too");
    }
}
