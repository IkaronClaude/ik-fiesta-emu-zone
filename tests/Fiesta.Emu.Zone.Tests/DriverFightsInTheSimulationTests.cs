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
[Collection(HeavySimulationCollection.Name)]
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

    /// <summary>⚠️ <b>A DRIVER-SIDE CEILING, MEASURED RATHER THAN GUESSED.</b> `level_quest.lua` kills
    /// <b>24 mobs in 400 seconds no matter how hard it hits</b> — the same 24 across a <b>1000x</b> range
    /// of weapon damage — while a walk-up-and-swing script on the identical map scales 6 → 62.
    ///
    /// <code>
    /// weapon        minimal driver     level_quest.lua
    ///     60-95            6 kills          23 kills   (145 casts)
    ///    600-840          17               24          (100)
    ///   5000-6000         58               24           (24)
    ///  50000-60000        62               24           (20)
    /// </code>
    ///
    /// <para>Three things this rules out. It is <b>not damage</b>: a thousandfold increase moves nothing.
    /// It is <b>not the environment's ceiling</b>: the same map yields 62 to a simpler script. It is
    /// <b>not the rotation failing</b>: casts fall from 145 to 20 precisely because mobs die faster, so
    /// the kills it does make get cheaper — the driver simply does not make more of them.</para>
    ///
    /// <para>The cadence names the shape. Kills arrive in bursts of four inside ~17 seconds, then a
    /// <b>~28-second gap</b>, repeating, after a 34.7-second cold start:</para>
    ///
    /// <code>
    /// 34.7 34.9 43.6 51.4 | 79.1 99.7 108.8 116.2 | 143.8 164.2 172.6 180.7 | 208.7 ...
    ///                gap 27.7s          gap 27.6s              gap 28.0s
    /// </code>
    ///
    /// <para>So roughly <b>28 of every 64 seconds go somewhere that is not fighting</b>. The driver's own
    /// log shows it thrashing <c>PHASE =&gt; xpgrind</c> / <c>PHASE =&gt; kill (quest mobs)</c> every tick
    /// against <c>"no active quests KNOWN yet (n=0, lists still loading or genuinely none)"</c> — it
    /// cannot tell an empty quest list from an unloaded one, and this simulation has no NPC to load one
    /// from.</para>
    ///
    /// <para><b>This is a driver defect, not a simulation gap</b>, and the operator has said as much
    /// ("our questing logic is pretty shit and wastes a ton of time, will need improvement"). It is pinned
    /// here because the simulation is now the cheapest place to measure it: 400 simulated seconds per run,
    /// and the number is exact.</para>
    ///
    /// <para>⚠️ It would also have passed a naive <c>strong &gt; weak</c> assertion on a margin of ONE
    /// kill. That is the shape of green run this file exists to refuse.</para></summary>
    [SkippableFact]
    public void TheRealDriverIsPinnedAtItsOwnCeiling_KNOWN_LIMIT()
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        var (weak, _) = Run(shine!, ressystem!, driver!, wcMin: 60, wcMax: 95);
        var (absurd, _) = Run(shine!, ressystem!, driver!, wcMin: 50_000, wcMax: 60_000);

        output.WriteLine($"level_quest.lua: weapon 60-95      -> kills={weak.Kills} casts={weak.Casts}");
        output.WriteLine($"level_quest.lua: weapon 50k-60k    -> kills={absurd.Kills} casts={absurd.Casts}");

        // It out-kills bare melee at low damage, because it uses its rotation.
        weak.Kills.ShouldBeGreaterThan(10, "even damage-starved, the rotation should beat bare melee");

        // ...and a THOUSANDFOLD damage increase buys it almost nothing. THIS IS THE TRACKED DEFECT.
        (absurd.Kills - weak.Kills).ShouldBeLessThan(10,
            "if the driver has become damage-responsive, the phase thrash has been fixed -- delete this "
            + "test and fold the real driver into AHarderHittingWeaponKillsMore");

        // The same map yields far more to a simpler script, so the ceiling is the DRIVER's, not the map's.
        absurd.Kills.ShouldBeLessThan(40,
            "a walk-up-and-swing script reaches 62 kills here; if the driver passes 40 it has started "
            + "closing that gap and these bounds need re-measuring");
    }
}
