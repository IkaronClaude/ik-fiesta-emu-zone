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
        sim.SpawnFightable(map, MobDataBox.Load(shine), spawnSeed: 7, maxRank: MapSpawner.NormalMobMaxRank);
        // ⭐ NORMAL RANKS ONLY, the same rule ScenarioRunner follows: "a dungeon's five or six repeated
        // bosses are a party's problem, and sending a solo character at them measures dying, not
        // grinding". This fixture spawned them, and it did not matter for as long as a mob's detect range
        // was the 60u placeholder -- nothing noticed us, so the bosses stood there. With their real
        // DetectCha they come, and a hand-built sword-and-plate character is measuring how fast a boss
        // kills it rather than how well the driver grinds.

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
        //
        // ⚠️ It used to take `m[1]` while its comment said "nearest", and the two only agreed by luck.
        // Once mobs got their real DetectCha they come to US, so the arbitrary first entry is usually a
        // mob across the field: the script walked at it forever with a pack chewing on it and landed
        // ZERO hits with either weapon -- 20,692 damage taken, 0 dealt, identical in both runs. That
        // reads as "damage does nothing", which is exactly the false signal this control exists to catch.
        const string minimal = """
            function tick()
              local m = bot.nearbyMobs()
              if #m == 0 then return end
              local best, bestd = nil, 1e18
              for _, mob in ipairs(m) do
                local d = bot.dist(mob.handle)
                if d and d < bestd then best, bestd = mob, d end
              end
              if best == nil then return end
              if bestd > 90 then bot.walkTo(best.x, best.y)
              else bot.autoAttack(best.handle) end
            end
            """;

        (long Damage, int Kills) Damage(int lo, int hi)
        {
            var sim = UrugaWarrior(shine!, ressystem!, lo, hi);
            LevelingBotHarness.Attach(sim, minimal).Run(minimal, ticks: Ticks);
            output.WriteLine($"   wc {lo}-{hi}: alive={sim.Player.IsAlive} hp={sim.Player.Hp}/{sim.Player.MaxHp} "
                             + $"now={sim.Now}ms dealt={sim.DamageDealt} taken={sim.DamageTaken} "
                             + $"mobs={sim.Mobs.Count(m => m.Mob.IsAlive)}");
            return (sim.DamageDealt, sim.Kills);
        }

        var weak = Damage(60, 95);
        var strong = Damage(600, 840);
        output.WriteLine($"minimal driver: weapon 60-95 -> {weak.Damage} damage / {weak.Kills} kills, "
                         + $"weapon 600-840 -> {strong.Damage} damage / {strong.Kills} kills");

        // ⚠️ SCORED ON DAMAGE, NOT KILLS, and that is a consequence of mobs having their real DetectCha.
        // This script walks up and swings: no healing, no kiting, no breaking off. In a world where
        // nothing noticed us first it could grind all day and kills were the natural measure; now a pack
        // sees it coming and it dies early with BOTH weapons, so kills read 0 against 0 -- a flat response
        // that means "it died", not "damage does nothing". Damage dealt still answers the question the
        // control exists to ask.
        strong.Damage.ShouldBeGreaterThan(weak.Damage * 2,
            "ten times the weapon damage should deal MUCH more in the same time. A flat response is "
            + "evidence of a BROKEN INPUT, not of a different bottleneck -- that misdiagnosis once cost "
            + "this project a session, when AttackRange sat at 12 units and read as 'quest state is the "
            + "bottleneck'");
    }

    /// <summary>⭐ <b>THE DRIVER IS DAMAGE-RESPONSIVE NOW, AND THAT RETIRED A TRACKED DEFECT.</b>
    ///
    /// <para>This replaces <c>TheRealDriverIsPinnedAtItsOwnCeiling_KNOWN_LIMIT</c>, which pinned the
    /// opposite: `level_quest.lua` killing <b>24 mobs no matter how hard it hit</b>, the same 24 across a
    /// 1000x range of weapon damage, while a walk-up-and-swing script on the same map scaled 6 -> 62. That
    /// test carried its own retirement instruction -- "if the driver has become damage-responsive, the
    /// phase thrash has been fixed, delete this test" -- and it has:</para>
    ///
    /// <code>
    /// weapon          was        now
    ///     60-95    23 kills    2 kills
    ///  50000-60000 24 kills   47 kills
    /// </code>
    ///
    /// <para>Two changes did it. The driver stopped walking past mobs it could already hit, which is where
    /// a quarter of its engaged time was going; and mobs got their real <c>DetectCha</c>, so a weak weapon
    /// is now genuinely punished rather than merely slow -- 2 kills is a character that cannot finish what
    /// it starts, not one grinding patiently.</para>
    ///
    /// <para>The assertion is therefore the ordinary one the control uses: more damage, more kills.</para></summary>
    [SkippableFact]
    public void TheDriverKillsMoreWhenItHitsHarder()
    {
        var (shine, ressystem, driver) = (Shine(), Ressystem(), DriverPath());
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");
        Skip.If(driver is null, "level_quest.lua not present; set LEVEL_QUEST_LUA");

        // ⚠️ TEN TIMES, NOT A THOUSAND. The retired test used 50k-60k, and at that damage every mob dies
        // to one hit, so the run stops being bound by damage and becomes bound by WALKING: 31 kills in
        // 400 seconds is one every 13 seconds, which is travel and retargeting, not fighting. A weapon
        // that absurd cannot measure damage response because damage has stopped being the constraint --
        // it reads as "flat" and invites exactly the wrong conclusion, which is the mistake this file
        // was written to stop being made twice.
        var (weak, _) = Run(shine!, ressystem!, driver!, wcMin: 60, wcMax: 95);
        var (strong, _) = Run(shine!, ressystem!, driver!, wcMin: 600, wcMax: 840);

        output.WriteLine($"level_quest.lua: weapon 60-95  -> kills={weak.Kills} casts={weak.Casts}");
        output.WriteLine($"level_quest.lua: weapon 600-840 -> kills={strong.Kills} casts={strong.Casts}");

        strong.Kills.ShouldBeGreaterThan(weak.Kills,
            "the driver was pinned at a flat 24 kills across a 1000x damage range for months. If it is "
            + "flat again, the phase thrash or the target selection has regressed -- read the driver log "
            + "for PHASE => thrash before suspecting the simulation");
    }
}
