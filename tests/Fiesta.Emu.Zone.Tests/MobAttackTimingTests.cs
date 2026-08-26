using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Mob;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The four `MobWeapon` delay columns, ported from `mab_Think` rather than guessed at.
///
/// <para>This project guessed twice and was wrong twice before reading the function: first taking `AtkDly`
/// for the swing interval, then taking `SwingTime` for it. These tests exist so a third guess cannot
/// quietly replace the reading.</para></summary>
public class MobAttackTimingTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobWeapon.shn")) ? root : null;
    }

    private static MobWeapon W(int atkSpd, int atkDly, int swingTime, int hitTime)
        => new(0, "test", "-", atkSpd, atkDly, swingTime, hitTime, 0, 0, 0, 0, 0, 0, 0, HitType.Physical, 1000);

    /// <summary>Everything is in TENTHS of a second, because that is the resolution the zone's timed logic
    /// runs at — `mab_Think` converts every weapon timing with `* 10 / 1000`.</summary>
    [Fact]
    public void TimingsAreInTenthsOfASecond()
    {
        var t = MobAttackTimingCalculator.Compute(W(atkSpd: 1000, atkDly: 800, swingTime: 1000, hitTime: 300));

        t.SwingTenths.ShouldBe(10);      // 1000 ms
        t.HitTenths.ShouldBe(3);         //  300 ms
        t.DelayTenths.ShouldBe(8);       //  800 ms
    }

    /// <summary>THE POINT OF THE WHOLE EXERCISE. `AtkDly` is added ON TOP of the swing to get the next
    /// attack time — `mab_Think` computes <c>now + AtkDly + swing + …</c>.
    ///
    /// <para>`AtkDly` exceeding `SwingTime` in half the table looked like a contradiction under both earlier
    /// readings, and is no contradiction at all under this one: they add.</para></summary>
    [Fact]
    public void TheIntervalIsTheDelayPlusTheSwingNotEitherAlone()
    {
        var t = MobAttackTimingCalculator.Compute(W(atkSpd: 1000, atkDly: 1400, swingTime: 1000, hitTime: 400));

        t.SwingTenths.ShouldBe(10);
        t.DelayTenths.ShouldBe(14);
        t.IntervalTenths.ShouldBe(24);            // not 10, and not 14
        t.IntervalMs.ShouldBe(2400u);
    }

    /// <summary>`AtkSpd` is the real swing duration; `SwingTime` is only the reference it is normalised
    /// against. The two `128` scales cancel, which is why the pair are equal for most mobs.</summary>
    [Fact]
    public void AtkSpdDrivesTheSwingAndSwingTimeIsOnlyTheReference()
    {
        // Same SwingTime, different AtkSpd -> different swing.
        MobAttackTimingCalculator.Compute(W(600, 0, 1200, 0)).SwingTenths.ShouldBe(6);
        MobAttackTimingCalculator.Compute(W(2400, 0, 1200, 0)).SwingTenths.ShouldBe(24);

        // Same AtkSpd, different SwingTime -> same swing, because SwingTime cancels.
        MobAttackTimingCalculator.Compute(W(1000, 0, 1000, 0)).SwingTenths
            .ShouldBe(MobAttackTimingCalculator.Compute(W(1000, 0, 2000, 0)).SwingTenths);
    }

    /// <summary>An `AtkSpd` of zero would divide by zero; the original substitutes 1.</summary>
    [Fact]
    public void AZeroAttackSpeedDoesNotDivideByZero()
        => Should.NotThrow(() => MobAttackTimingCalculator.Compute(W(0, 100, 1000, 300)));

    /// <summary>All three scale together with the AbnormalState attack-speed rate — container +0xA18.</summary>
    [Fact]
    public void AnAttackSpeedRateScalesAllThreeTimings()
    {
        var normal = MobAttackTimingCalculator.Compute(W(1800, 400, 1800, 700));
        var hasted = MobAttackTimingCalculator.Compute(W(1800, 400, 1800, 700), attackSpeedRate: 500);

        hasted.SwingTenths.ShouldBe(normal.SwingTenths / 2);
        hasted.HitTenths.ShouldBe(normal.HitTenths / 2);
        hasted.DelayTenths.ShouldBe(normal.DelayTenths / 2);
    }

    /// <summary>Negative results are clamped at zero, so no attack can be scheduled in the past.</summary>
    [Fact]
    public void TimingsCannotGoNegative()
    {
        var t = MobAttackTimingCalculator.Compute(W(1000, 800, 1000, 300), attackSpeedRate: -5000);

        t.SwingTenths.ShouldBe(0);
        t.HitTenths.ShouldBe(0);
        t.DelayTenths.ShouldBe(0);
    }

    /// <summary>Against the real table: the swing tracks `AtkSpd/100` for the great majority, and the
    /// exceptions are truncation in the two-step normalisation — which is why it is not collapsed.</summary>
    [SkippableFact]
    public void OnRealDataTheSwingTracksAtkSpd()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);
        var normals = box.Weapons.Values.SelectMany(w => w)
            .Where(w => w.Skill is "-" or "" && w.SwingTime > 0).ToList();

        var exact = normals.Count(w => MobAttackTimingCalculator.Compute(w).SwingTenths == w.AtkSpd * 10 / 1000);
        (exact / (double)normals.Count).ShouldBeGreaterThan(0.95);
        exact.ShouldBeLessThan(normals.Count, "the truncation exceptions are why the two-step form is kept");
    }

    /// <summary>A spawned mob's swing interval is the ported one, not a constant.</summary>
    [SkippableFact]
    public void ASpawnedMobUsesItsPortedTimings()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        var sim = new CombatSimulation(seed: 1);
        var mob = sim.AddMob(handle: 10, x: 5, y: 0);
        mob.Define(Fiesta.Emu.Zone.Parameter.MobCombatant.Build(box, "Orc")!);

        var expected = MobAttackTimingCalculator.Compute(box.AttackAgainstPlayer("Orc")!);
        mob.Timing.ShouldBe(expected);
        mob.SwingIntervalMs.ShouldBe(expected.IntervalMs);
        mob.SwingLandDelayMs.ShouldBe(expected.HitMs);
    }
}

/// <summary>Not everything a map spawns is an enemy.</summary>
public class GatheringNodeTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobInfo.shn")) ? root : null;
    }

    /// <summary>Uruga's "mushrooms" are gathering nodes, not the level-2 enemy of nearly the same name.</summary>
    [SkippableFact]
    public void UrugaMushroomsAreHerbNodesNotTheMushroomEnemy()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(shine!);

        foreach (var n in new[] { "MUSHROOM7", "MUSHROOM8", "MUSHROOM9" })
        {
            var info = box.InfoFor(n)!;
            info.Type.ShouldBe(MobType.Herb);
            info.IsFightable.ShouldBeFalse();
        }

        // `MushRoom` is a different entry entirely -- a real, fightable level-2 enemy that Uruga never spawns.
        var enemy = box.InfoFor("MushRoom")!;
        enemy.Level.ShouldBe(2);
        enemy.IsFightable.ShouldBeTrue();
    }

    [SkippableFact]
    public void HalfOfUrugasSpawnTypesAreNotEnemies()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var urg = Path.Combine(shine!, "MobRegen", "Urg.txt");
        Skip.If(!File.Exists(urg), "Urg.txt not present");

        var box = MobDataBox.Load(shine!);
        var map = MobRegenData.Load(urg);

        var nodes = map.NonCombatSpawns(box);
        nodes.ShouldContain("MUSHROOM7");
        nodes.ShouldContain("HERB7");
        nodes.ShouldContain("WOOD7");
        nodes.Count.ShouldBeGreaterThan(5);

        // Spawning for combat drops them; spawning the map as the server would keeps them.
        var everything = new CombatSimulation(seed: 1).SpawnAll(map, box, spawnSeed: 7);
        var fightable = new CombatSimulation(seed: 1).SpawnFightable(map, box, spawnSeed: 7);

        fightable.Count.ShouldBeLessThan(everything.Count);
        fightable.ShouldAllBe(m => box.IsFightable(m.Name));
    }

    /// <summary>Magic-vs-physical is a declared field, not something to infer from MA exceeding WC.</summary>
    [SkippableFact]
    public void HitTypeIsDeclaredRatherThanInferredFromMaVersusWc()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        // A real magic attacker: MA far above WC, and declared HT_MA.
        var mage = box.AttackAgainstPlayer("GoblinMage")!;
        mage.IsMagical.ShouldBeTrue();
        mage.MinMa.ShouldBeGreaterThan(mage.MaxWc);

        // Pinky is RANGED but declared PHYSICAL, despite carrying a non-zero MA -- inferring the hit type
        // from MA-versus-WC would have got this one wrong.
        var pinky = box.AttackAgainstPlayer("Pinky")!;
        pinky.IsRanged.ShouldBeTrue();
        pinky.Range.ShouldBe(350);
        pinky.IsMagical.ShouldBeFalse();
        pinky.MinMa.ShouldBeGreaterThan(0);
        pinky.MaxWc.ShouldBeGreaterThan(pinky.MaxMa);
    }
}
