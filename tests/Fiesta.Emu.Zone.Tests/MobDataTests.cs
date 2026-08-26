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

        var weapon = box.NormalAttackOf("MushRoom")!;
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

    /// <summary>Mobs hit for their own weapon's damage, and different mobs hit differently.</summary>
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

        var fromMushroom = TotalDamageTaken(box, "MushRoom");
        var fromOrc = TotalDamageTaken(box, "Orc");

        fromMushroom.ShouldBeGreaterThan(0, "a mob with a weapon should land hits");
        fromOrc.ShouldBeGreaterThan(fromMushroom, "an Orc hits far harder than a Mushroom");
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
