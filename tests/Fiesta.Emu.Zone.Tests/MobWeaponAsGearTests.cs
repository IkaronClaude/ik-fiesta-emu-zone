using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>A mob's weapon is its GEAR — `sm_PrepareWeapon` stages it into the Item.Plus cluster, the same
/// layer a player's sword goes into.
///
/// <para>This is the fact that makes mob combat work without any special case, and it was the last missing
/// link: `c_StoreMob` zeroes the weapon slots at spawn, weapon selection fills them, `c_MakeTotal` folds
/// Item.Plus in, and `roe_MinWC` then finds the weapon already sitting in the container — which is why it
/// has no mob branch.</para></summary>
public class MobWeaponAsGearTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobWeapon.shn")) ? root : null;
    }

    private static MobWeapon Weapon(int minWc, int maxWc, int th = 0, int minMa = 0, int maxMa = 0, int mh = 0)
        => new(0, "test", "-", 1000, 0, 1000, 300, minWc, maxWc, th, minMa, maxMa, mh, 20, HitType.Physical, 1000);

    /// <summary>The six values land on Item.Plus, not on the base cluster.</summary>
    [Fact]
    public void PrepareWeaponWritesIntoTheItemLayer()
    {
        var c = new ParameterContainer();
        MobParameters.PrepareWeapon(c, Weapon(minWc: 100, maxWc: 200, th: 30, minMa: 5, maxMa: 9, mh: 3));

        var item = c.Plus(StatModifier.Item);
        item[Stat.WCmin].ShouldBe(100);
        item[Stat.WCmax].ShouldBe(200);
        item[Stat.TH].ShouldBe(30);
        item[Stat.MAmin].ShouldBe(5);
        item[Stat.MAmax].ShouldBe(9);
        item[Stat.MH].ShouldBe(3);

        // The BASE cluster stays zero -- that is c_StoreMob's business and it leaves these empty.
        c.Base[Stat.WCmin].ShouldBe(0);
        c.Base[Stat.WCmax].ShouldBe(0);
    }

    /// <summary>And because Item.Plus is folded in by `c_MakeTotal`, the weapon simply appears in the total
    /// with no further step.</summary>
    [Fact]
    public void TheWeaponReachesTheTotalThroughTheNormalCombiningOrder()
    {
        var c = new ParameterContainer();
        MobParameters.PrepareWeapon(c, Weapon(minWc: 100, maxWc: 200));

        var total = c.MakeTotal();
        total[Stat.WCmin].ShouldBe(100);
        total[Stat.WCmax].ShouldBe(200);
    }

    /// <summary>⚠️ REGRESSION GUARD. The original passes `(MaxWC, MinWC, TH, MaxMA, MinMA, MH)` — the pairs
    /// are max-first. Getting that backwards swaps every mob's damage bounds, which would still "work" and
    /// still produce plausible fights.</summary>
    [Fact]
    public void TheMinAndMaxBoundsAreNotSwapped()
    {
        var c = new ParameterContainer();
        MobParameters.PrepareWeapon(c, Weapon(minWc: 10, maxWc: 999, minMa: 3, maxMa: 456));

        var item = c.Plus(StatModifier.Item);
        item[Stat.WCmin].ShouldBeLessThan(item[Stat.WCmax]);
        item[Stat.MAmin].ShouldBeLessThan(item[Stat.MAmax]);
        item[Stat.WCmin].ShouldBe(10);
        item[Stat.MAmax].ShouldBe(456);
    }

    /// <summary>A real mob is now a full combatant: the damage formula sees its weapon.</summary>
    [SkippableFact]
    public void ARealMobHasWeaponDamageInTheFormula()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        var orc = MobCombatant.Build(box, "Orc")!;
        var mushroom = MobCombatant.Build(box, "MushRoom")!;

        DamageCalculator.MaxWeaponDamage(orc).ShouldBeGreaterThan(0);
        DamageCalculator.MaxWeaponDamage(orc)
            .ShouldBeGreaterThan(DamageCalculator.MaxWeaponDamage(mushroom));
    }

    /// <summary>THE PAYOFF: the defender's armour now reduces mob damage, because mob attacks go through the
    /// same formula as everything else.
    ///
    /// <para>Before the `sm_PrepareWeapon` path was traced, mob damage was rolled straight from MinWC/MaxWC
    /// and armour was simply not consulted — a character in plate took exactly as much damage as one in
    /// rags.</para></summary>
    [SkippableFact]
    public void ArmourNowReducesTheDamageAMobDeals()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(shine!);
        var cleric = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamClericServer.txt"));

        int DamageTaken(params EquipmentPiece[] gear)
        {
            var sim = new CombatSimulation(seed: 11);
            var mob = sim.AddMob(handle: 10, x: 6, y: 0, configure: m => m.RespawnSeconds = 99_999);
            mob.Define(MobCombatant.Build(box, "Orc")!);
            mob.Mob.so_getDetectRange = 300;
            sim.Player.Become(cleric, level: 40, equipment: gear);
            sim.Player.Hp = sim.Player.MaxHp = 10_000_000;
            sim.Run(maxTicks: 600);
            return sim.Player.MaxHp - sim.Player.Hp;
        }

        var unarmoured = DamageTaken();
        var plated = DamageTaken(new EquipmentPiece("plate", AC: 4000));

        unarmoured.ShouldBeGreaterThan(0, "the Orc should land hits");
        plated.ShouldBeLessThan(unarmoured, "armour must reduce incoming mob damage");
    }
}
