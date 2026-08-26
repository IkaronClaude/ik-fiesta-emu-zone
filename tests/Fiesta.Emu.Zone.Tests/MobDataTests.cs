using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Mob definitions from the server's own binary tables, and the link that makes a mob a
/// combatant. Needs a server-files tree; set <c>SHINE_DATA</c> or these skip.</summary>
public class MobDataTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobInfo.shn")) ? root : null;
    }

    private static MobDataBox Box() => MobDataBox.Load(Shine()!);

    [SkippableFact]
    public void TheThreeMobTablesLoadAndAgreeOnTheirMobs()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();

        box.Info.Count.ShouldBeGreaterThan(2000);
        box.Server.Count.ShouldBe(box.Info.Count, "MobInfo and MobInfoServer are one row per mob");
        box.Weapons.Count.ShouldBeGreaterThan(2000);
    }

    /// <summary>Spot values straight out of the files, so a decode regression shows up as wrong numbers
    /// rather than as a plausible-looking simulation.</summary>
    [SkippableFact]
    public void MushroomDecodesToItsRealValues()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();

        var info = box.InfoFor("MushRoom")!;
        info.Name.ShouldBe("Mushroom");
        info.Level.ShouldBe(2);
        info.MaxHp.ShouldBe(38);
        info.WalkSpeed.ShouldBe(30);
        info.RunSpeed.ShouldBe(105);

        var server = box.ServerFor("MushRoom")!;
        server.Ac.ShouldBe(4);
        server.Tb.ShouldBe(2);
        server.Mr.ShouldBe(1);
        server.MonExp.ShouldBe(5);

        var weapon = box.AttackAgainstPlayer("MushRoom")!;
        weapon.MinWc.ShouldBe(5);
        weapon.MaxWc.ShouldBe(8);
        weapon.Range.ShouldBe(10);
    }

    /// <summary>`c_StoreMob` puts `MobInfoServer`'s defences and primaries in the base cluster — and swaps
    /// Con and Dex on the way, because the file's order is Str, Dex, Con, Int, Men while the cluster's is
    /// Str, Con, Dex, Int, Men. Copying the file order across would silently swap them on every mob.</summary>
    [SkippableFact]
    public void StoreMobFillsTheBaseClusterWithTheConDexCrossover()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();
        var server = box.ServerFor("Orc")!;

        var mob = MobCombatant.Build(box, "Orc")!;
        var b = mob.Parameters.Base;

        b[Stat.Str].ShouldBe(server.Str);
        b[Stat.Con].ShouldBe(server.Con);
        b[Stat.Dex].ShouldBe(server.Dex);
        b[Stat.Int].ShouldBe(server.Int);
        b[Stat.Men].ShouldBe(server.Men);

        b[Stat.AC].ShouldBe(server.Ac);
        b[Stat.TB].ShouldBe(server.Tb);
        b[Stat.MR].ShouldBe(server.Mr);
        b[Stat.MB].ShouldBe(server.Mb);

        // The original writes ZERO into these rather than skipping them: a mob's attack values are not
        // stat-cluster entries, they live in MobWeapon.
        b[Stat.WCmin].ShouldBe(0);
        b[Stat.WCmax].ShouldBe(0);
        b[Stat.TH].ShouldBe(0);
        b[Stat.MAmin].ShouldBe(0);
        b[Stat.MH].ShouldBe(0);

        // The same tail as c_Storepure.
        b[Stat.MoveSpeed].ShouldBe(1000);
        b[Stat.HPRecover].ShouldBe(1000);
        b[Stat.SPRecover].ShouldBe(1000);
    }

    /// <summary>`CharClassMob::MaxHP` reads `MobInfo[+0x46]` and never touches the cluster — unlike the
    /// player's version there is no Constitution term.</summary>
    [SkippableFact]
    public void MobMaxHpIsTheTableValueWithNoConstitutionTerm()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();

        var mob = MobCombatant.Build(box, "Orc")!;
        mob.MaxHp.ShouldBe(box.InfoFor("Orc")!.MaxHp);
    }

    /// <summary>THE MAPPING THAT WAS WRONG FIRST TIME. `SwingTime` is the swing cycle and `HitTime` the
    /// offset within it at which damage lands.
    ///
    /// <para>`AtkDly` looks like an interval and was taken for one, but it exceeds `SwingTime` in roughly
    /// half the rows, and `HitTime` exceeds it in hundreds — which would land a swing's damage after the
    /// next swing had begun. These assertions are what caught that.</para></summary>
    [SkippableFact]
    public void SwingTimeIsTheCycleAndHitTimeLandsInsideIt()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();
        var normals = box.Weapons.Values.SelectMany(w => w)
            .Where(w => w.Skill is "-" or "" && w.SwingTime > 0)
            .ToList();

        normals.Count.ShouldBeGreaterThan(2000);

        // HitTime lands inside the swing for all but a handful -- THIS is what justifies reading SwingTime
        // as the cycle and HitTime as an offset within it.
        var inside = normals.Count(w => w.HitTime <= w.SwingTime);
        (inside / (double)normals.Count).ShouldBeGreaterThan(0.99);

        // AtkSpd usually equals SwingTime, but NOT always -- about 70 rows differ (Dragon, the KQ_H_ set,
        // the ArkMine bosses). An earlier version of this test asserted it universally on the strength of a
        // seven-mob sample and was wrong; the majority relationship is worth recording, the universal claim
        // is not true.
        var sameAsSwing = normals.Count(w => w.AtkSpd == w.SwingTime);
        (sameAsSwing / (double)normals.Count).ShouldBeGreaterThan(0.9);
        sameAsSwing.ShouldBeLessThan(normals.Count, "if this ever becomes universal, simplify the mapping");

        // And AtkDly is NOT the interval, which is why nothing uses it.
        normals.Count(w => w.AtkDly > w.SwingTime).ShouldBeGreaterThan(normals.Count / 10);
    }

    /// <summary>A spawned Uruga is now populated with real mobs — every one of them.</summary>
    [SkippableFact]
    public void UrugaSpawnsWithRealStatsForEveryMob()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var urg = Path.Combine(shine!, "MobRegen", "Urg.txt");
        Skip.If(!File.Exists(urg), "Urg.txt not present");

        var box = Box();
        var map = MobRegenData.Load(urg);

        // Every mob type the map spawns is known to the tables. A gap here is a decode bug, not a shrug.
        map.MobsMissingData(box).ShouldBeEmpty();

        var sim = new CombatSimulation(seed: 1);
        var mobs = sim.SpawnAll(map, box, spawnSeed: 7);

        mobs.ShouldAllBe(m => m.Definition != null);

        // Every mob's level and HP are its OWN table values -- a stronger check than any threshold, and it
        // does not assume Uruga contains only high-level mobs (it does not).
        foreach (var m in mobs)
        {
            var info = box.InfoFor(m.Name)!;
            m.Level.ShouldBe(info.Level);
            m.MaxHp.ShouldBe(info.MaxHp);
        }

        // Uruga is a level-60ish map, which is a sanity check on the join as much as on the data.
        var orc = mobs.First(m => m.Name == "Orc");
        orc.Level.ShouldBe(box.InfoFor("Orc")!.Level);
        orc.MaxHp.ShouldBe(box.InfoFor("Orc")!.MaxHp);
        orc.NormalAttack.ShouldNotBeNull();
    }

    /// <summary>Mobs hit for their own weapon's damage, and different mobs hit differently.
    ///
    /// <para>⚠️ Both mobs here are AGGRESSIVE. This test used `MushRoom` until per-mob targeting policy was
    /// wired up, and then failed — correctly: `MushRoom` is `ED_BOUT`, passive, and does not attack an
    /// unprovoked player. The failure was the feature working.</para></summary>
    [SkippableFact]
    public void MobsHitForTheirOwnWeaponDamage()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();

        static int TotalDamageTaken(MobDataBox box, string mobName)
        {
            var sim = new CombatSimulation(seed: 5);
            var mob = sim.AddMob(handle: 10, x: 6, y: 0, configure: m => m.RespawnSeconds = 99_999);
            mob.Define(MobCombatant.Build(box, mobName)!);
            mob.Mob.so_getDetectRange = 200;
            sim.Player.Hp = sim.Player.MaxHp = 5_000_000;
            sim.Run(maxTicks: 600);
            return sim.Player.MaxHp - sim.Player.Hp;
        }

        box.ServerFor("Pinky")!.IsAggressive.ShouldBeTrue();
        box.ServerFor("Orc")!.IsAggressive.ShouldBeTrue();

        var fromPinky = TotalDamageTaken(box, "Pinky");
        var fromOrc = TotalDamageTaken(box, "Orc");

        fromPinky.ShouldBeGreaterThan(0, "an aggressive mob attacks on sight");
        fromOrc.ShouldNotBe(fromPinky, "different mobs hit for different amounts");
    }

    /// <summary>A PASSIVE mob ignores a player standing next to it, and fights back the moment it is hit.
    ///
    /// <para>`MobTargetBout::mts_SelectTarget` walks the hate list and never calls `so_AllOfRange` — it has
    /// no sight scan at all. That is 220 mobs, including everything a new character meets first, and until
    /// the policy was wired every one of them attacked on sight.</para></summary>
    [SkippableFact]
    public void APassiveMobOnlyFightsBackOnceProvoked()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();
        box.ServerFor("MushRoom")!.DetectType.ShouldBe(EnemyDetect.Bout);

        static CombatSimulation Setup(MobDataBox box, out SimMob mob)
        {
            var sim = new CombatSimulation(seed: 5);
            var m = sim.AddMob(handle: 10, x: 6, y: 0, configure: x => x.RespawnSeconds = 99_999);
            m.Define(MobCombatant.Build(box, "MushRoom")!);
            m.Mob.so_getDetectRange = 500;          // enormous sight; it still must not care
            sim.Player.Hp = sim.Player.MaxHp = 100_000;
            sim.Player.AttackDamage = 1;            // a Mushroom has 38 HP; the default 40 one-shots it,
                                                    // and a corpse cannot demonstrate retaliation
            mob = m;
            return sim;
        }

        var ignored = Setup(box, out _);
        ignored.Run(maxTicks: 600);
        ignored.Player.Hp.ShouldBe(ignored.Player.MaxHp, "a passive mob does not attack on sight");

        var provoked = Setup(box, out var target);
        provoked.PlayerAttack(target);              // one hit is enough to earn its attention
        provoked.Run(maxTicks: 600);
        provoked.Player.Hp.ShouldBeLessThan(provoked.Player.MaxHp, "but it fights back once struck");
    }

    /// <summary>A shopkeeper never targets anything at all — `MobTargetNoBrain::mts_SelectTarget` is a call
    /// to `mts_InitThink` and nothing else.</summary>
    [SkippableFact]
    public void AShopkeeperNeverAttacksEvenWhenHit()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();
        box.ServerFor("RouSmithJames")!.DetectType.ShouldBe(EnemyDetect.NoBrain);

        var sim = new CombatSimulation(seed: 5);
        var smith = sim.AddMob(handle: 10, x: 6, y: 0, configure: m => m.RespawnSeconds = 99_999);
        smith.Define(MobCombatant.Build(box, "RouSmithJames")!);
        smith.Mob.so_getDetectRange = 500;
        sim.Player.Hp = sim.Player.MaxHp = 100_000;

        sim.PlayerAttack(smith);
        sim.Run(maxTicks: 600);

        sim.Player.Hp.ShouldBe(sim.Player.MaxHp, "NoBrain never acquires a target, even its attacker");
    }

    /// <summary>A mob's stats reach the damage formula: attacking an Orc is harder than a Mushroom because
    /// the Orc's AC is real now, not zero.</summary>
    [SkippableFact]
    public void MobArmourReachesTheDamageFormula()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = Box();

        var mushroom = MobCombatant.Build(box, "MushRoom")!;
        var orc = MobCombatant.Build(box, "Orc")!;

        DamageCalculator.ArmourClass(orc).ShouldBeGreaterThan(DamageCalculator.ArmourClass(mushroom));
        DamageCalculator.DefendPower(orc).ShouldBeGreaterThan(DamageCalculator.DefendPower(mushroom));
    }
}
