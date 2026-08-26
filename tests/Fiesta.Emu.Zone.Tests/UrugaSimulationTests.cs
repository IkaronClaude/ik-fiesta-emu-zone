using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>A full simulation of Uruga, populated from the server's own spawn tables.
///
/// <para>These read `9Data/Shine/MobRegen/Urg.txt` from a server-files tree, which this repo does not
/// ship. Set <c>SHINE_DATA</c> to the `9Data/Shine` directory, or leave it and the tests skip — a machine
/// without the data should not report failures it cannot fix.</para></summary>
public class UrugaSimulationTests
{
    private static string? UrugaPath()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA")
                   ?? @"Z:/ServerSource/9Data/Shine";
        var p = Path.Combine(root, "MobRegen", "Urg.txt");
        return File.Exists(p) ? p : null;
    }

    private const string RoamAndFight = """
        function on_tick()
          local mobs = bot.nearbyMobs()
          if #mobs == 0 then
            -- nothing in view: wander, so the character actually covers ground
            bot.walkTo(bot.x() + 40, bot.y() + 15)
            return
          end
          local best, bestDist = nil, 1e9
          for i = 1, #mobs do
            if mobs[i].dist < bestDist then best, bestDist = mobs[i], mobs[i].dist end
          end
          if not bot.attack(best.handle) then
            bot.walkTo(best.x, best.y)
          end
        end
        """;

    [SkippableFact]
    public void UrugaSpawnTablesLoad()
    {
        var path = UrugaPath();
        Skip.If(path is null, "server data not present; set SHINE_DATA");

        var map = MobRegenData.Load(path!);

        map.Groups.Count.ShouldBeGreaterThan(30);
        map.Entries.Count.ShouldBeGreaterThan(60);
        map.Groups.Count.ShouldBeLessThanOrEqualTo(MobRegenData.MaxSpawnGroups);

        // Both area shapes really are present in this map, which is why both samplers were ported.
        map.Groups.ShouldContain(g => g.IsCircular);
        map.Groups.ShouldContain(g => !g.IsCircular);

        var population = map.Population();
        population.Keys.ShouldContain("Orc");
        population.Values.Sum().ShouldBeGreaterThan(100);
    }

    [SkippableFact]
    public void SpawnPointsLandInsideTheirArea()
    {
        var path = UrugaPath();
        Skip.If(path is null, "server data not present; set SHINE_DATA");

        var map = MobRegenData.Load(path!);
        var sim = new CombatSimulation();
        var mobs = sim.SpawnAll(map);

        mobs.Count.ShouldBeGreaterThan(100);

        // Every mob should be within its group's extent -- a rotated rectangle fits inside the circle of
        // radius hypot(w,h), which is the cheap invariant that still catches a botched rotation.
        foreach (var m in mobs)
        {
            var g = map.Groups.First(x => x.GroupIndex == m.SpawnGroup);
            var limit = g.IsCircular ? g.Radius : (int)Math.Ceiling(Math.Sqrt(
                (double)g.Width * g.Width + (double)g.Height * g.Height));
            var dx = m.Mob.X - g.CenterX;
            var dy = m.Mob.Y - g.CenterY;
            Math.Sqrt((double)dx * dx + dy * dy).ShouldBeLessThanOrEqualTo(limit + 2);
        }
    }

    /// <summary>THE GOAL: a character with made-up stats runs around Uruga and fights what it finds.</summary>
    [SkippableFact]
    public void AFakeCharacterRunsAroundUrugaAndFights()
    {
        var path = UrugaPath();
        Skip.If(path is null, "server data not present; set SHINE_DATA");

        var map = MobRegenData.Load(path!);
        var sim = new CombatSimulation(seed: 42);
        sim.SpawnAll(map, spawnSeed: 7);

        // A fake character with fake stats, dropped where the mobs actually are.
        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;
        sim.Player.Hp = sim.Player.MaxHp = 20_000;
        sim.Player.AttackDamage = 90;
        sim.Player.AttackRange = 14;
        sim.Player.MoveSpeed = 8;
        sim.Player.Level = 30;

        sim.LoadScript(RoamAndFight);
        sim.Run(maxTicks: 3000);          // 5 simulated minutes

        sim.Kills.ShouldBeGreaterThan(0);
        sim.Player.IsAlive.ShouldBeTrue();
        sim.Log.ShouldContain(l => l.Contains("died"));

        // It should have taken damage too -- a fight the mobs never join is not a fight.
        sim.Log.ShouldContain(l => l.Contains("hits for"));
    }

    [SkippableFact]
    public void KilledMobsComeBackInTheirOwnSpawnArea()
    {
        var path = UrugaPath();
        Skip.If(path is null, "server data not present; set SHINE_DATA");

        var map = MobRegenData.Load(path!);
        var sim = new CombatSimulation(seed: 3);
        sim.SpawnAll(map, spawnSeed: 3);

        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;
        sim.Player.Hp = sim.Player.MaxHp = 1_000_000;
        sim.Player.AttackDamage = 5_000;          // one-shot everything, to force respawns quickly

        sim.LoadScript(RoamAndFight);
        sim.Run(maxTicks: 2000);

        sim.Kills.ShouldBeGreaterThan(0);
        sim.Log.ShouldContain(l => l.Contains("respawned"));

        // ⚠️ ASSERT ON EVENTS, NOT END STATE. This run keeps going after a respawn: the mob is put back
        // on its spawn point, re-acquires, chases, and here gets one-shot again. Two earlier versions of
        // this test asserted end state -- exact position (off by 15 because it had walked) and then
        // IsAlive (false because it had been killed again) -- and both failures were the simulation
        // behaving correctly. In a continuously running world the log is the record of what happened;
        // the final snapshot is just wherever things happened to stop.
        var respawns = sim.Log.Count(l => l.Contains("respawned"));
        respawns.ShouldBeGreaterThan(0);

        var handle = sim.Log.First(l => l.Contains("respawned")).Split('#')[1].Split(' ')[0];
        var mob = sim.Mobs.First(m => m.Mob.Handle.ToString() == handle);
        map.Groups.ShouldContain(g => g.GroupIndex == mob.SpawnGroup);

        // Exact placement on respawn is pinned in isolation by RespawnRestoresTheExactSpawnPoint, where
        // the scenario is frozen and nothing can move the mob afterwards.
    }

    /// <summary>Placement on respawn, in isolation: with nothing to chase, a mob must come back exactly
    /// where it spawned and with its hate list cleared.</summary>
    [Fact]
    public void RespawnRestoresTheExactSpawnPoint()
    {
        var sim = new CombatSimulation();
        var mob = sim.AddMob(handle: 10, x: 500, y: 700, configure: m =>
        {
            m.Hp = m.MaxHp = 10;
            m.RespawnSeconds = 1;
        });
        sim.Player.X = 505;                     // in range to be hit...
        sim.Player.AttackDamage = 100;

        sim.PlayerAttack(mob);
        mob.Mob.IsAlive.ShouldBeFalse();
        mob.Mob.X = 9999;                       // drag the corpse somewhere it should not stay
        sim.Player.X = 100_000;                 // ...then leave, so nothing pulls it after respawn

        for (var i = 0; i < 20; i++) sim.Tick();

        mob.Mob.IsAlive.ShouldBeTrue();
        mob.Mob.X.ShouldBe(500);
        mob.Mob.Y.ShouldBe(700);
        mob.Hp.ShouldBe(mob.MaxHp);
        mob.Mob.Selector.AggroList.ShouldBeEmpty();
    }

    /// <summary>The whole point of simulating: minutes of a populated map in a moment.</summary>
    [SkippableFact]
    public void APopulatedUrugaRunsFasterThanRealTime()
    {
        var path = UrugaPath();
        Skip.If(path is null, "server data not present; set SHINE_DATA");

        var map = MobRegenData.Load(path!);
        var sim = new CombatSimulation();
        var mobs = sim.SpawnAll(map);

        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;
        sim.Player.Hp = sim.Player.MaxHp = 10_000_000;
        sim.LoadScript(RoamAndFight);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        sim.Run(maxTicks: 3000);
        sw.Stop();

        sim.Now.ShouldBe(300_000u);                       // 5 minutes simulated
        sw.ElapsedMilliseconds.ShouldBeLessThan(20_000);  // in far less than 5 minutes
    }
}
