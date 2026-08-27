using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The join between the two halves: a character built from class, level and gear, fighting with the
/// ported damage formula rather than a flat number.
///
/// <para>These use a small hand-built class table so they run without a server-files tree. The table's
/// values are shaped like a real one (rising primaries, rising HP) but are not claimed to be any real
/// class — what is under test is the wiring, not the data.</para></summary>
public class StatDrivenCombatTests
{
    private static ClassParamTable Table() => new()
    {
        ClassName = "Test",
        ByLevel = new Dictionary<int, ClassParamRow>
        {
            //         lvl  str con int dex men  hp    sp   soulHp maxSoulHp price soulSp maxSoulSp price jcDmgUp
            [1] = new(1, 5, 4, 1, 3, 4, 46, 32, 32, 15, 3, 25, 11, 1, 1000),
            [20] = new(20, 48, 34, 22, 39, 32, 342, 398, 239, 44, 26, 318, 35, 14, 1000),
            [40] = new(40, 96, 68, 44, 78, 64, 1200, 800, 400, 60, 40, 600, 50, 25, 1000),
        },
    };

    private static SimPlayer Character(int level, params EquipmentPiece[] gear)
    {
        var p = new SimPlayer();
        p.Become(Table(), level, equipment: gear);
        return p;
    }

    /// <summary>A character with no class or level set still fights — by the flat number, explicitly.</summary>
    [Fact]
    public void AnUnconfiguredCharacterUsesTheFlatDamagePath()
    {
        var p = new SimPlayer();

        p.UsesStatFormula.ShouldBeFalse();
        p.Parameters.ShouldNotBeNull();          // always a container, never null
    }

    [Fact]
    public void BecomingACharacterSwitchesOnTheFormula()
    {
        var p = Character(20);

        p.UsesStatFormula.ShouldBeTrue();
        p.Level.ShouldBe(20);
        p.MaxHp.ShouldBe(342);
    }

    /// <summary>⚠️ A player's base weapon value is ZERO — `CharClass::WC` returns 0 and no class overrides
    /// it. So an unarmed character's weapon damage comes entirely from stats, and arming it must raise it.</summary>
    [Fact]
    public void ArmingACharacterRaisesItsWeaponDamage()
    {
        var bare = Character(20);
        var armed = Character(20, new EquipmentPiece("sword", MinWC: 40, MaxWC: 70));

        DamageCalculator.MaxWeaponDamage(armed)
            .ShouldBeGreaterThan(DamageCalculator.MaxWeaponDamage(bare));
    }

    /// <summary>Gear is not merely added on — it is scaled by the character carrying it. The same weapon on
    /// a stronger character is worth more, which is why the item's own numbers are not the answer.</summary>
    [Fact]
    public void TheSameWeaponIsWorthMoreOnAStrongerCharacter()
    {
        var sword = new EquipmentPiece("sword", MinWC: 40, MaxWC: 70);

        var novice = DamageCalculator.MaxWeaponDamage(Character(1, sword));
        var veteran = DamageCalculator.MaxWeaponDamage(Character(40, sword));

        veteran.ShouldBeGreaterThan(novice);
    }

    /// <summary>Armour reaches the defence side of the formula.</summary>
    [Fact]
    public void ArmourRaisesTheDefendPower()
    {
        var bare = Character(20);
        var plated = Character(20, new EquipmentPiece("plate", AC: 120));

        DamageCalculator.ArmourClass(plated).ShouldBeGreaterThan(DamageCalculator.ArmourClass(bare));
    }

    /// <summary>THE POINT OF ALL OF IT: better gear kills faster in an actual simulated fight.
    ///
    /// <para>Both runs use the same seed, the same map, the same script and the same mob, so the only
    /// difference is what the character is wearing. If gear did not reach the damage path this would come
    /// out equal.</para></summary>
    [Fact]
    public void BetterGearKillsFasterInAnActualFight()
    {
        static uint TimeToKill(EquipmentPiece weapon)
        {
            var sim = new CombatSimulation(seed: 7);
            var mob = sim.AddMob(handle: 10, x: 12, y: 0, configure: m =>
            {
                m.Hp = m.MaxHp = 20_000;
                m.AttackDamage = 0;              // the mob is a punching bag; we are timing the player
                m.RespawnSeconds = 99_999;
            });
            sim.Player.Become(Table(), level: 40, equipment: [weapon]);
            sim.Player.AttackRange = 20;
            sim.LoadScript("""
                function on_tick()
                  local m = bot.nearbyMobs()
                  if #m > 0 then bot.attack(m[1].handle) end
                end
                """);
            sim.RunUntil(s => s.Kills > 0, maxTicks: 100_000);
            return sim.Now;
        }

        var poor = TimeToKill(new EquipmentPiece("club", MinWC: 10, MaxWC: 15));
        var good = TimeToKill(new EquipmentPiece("greatsword", MinWC: 200, MaxWC: 300));

        good.ShouldBeLessThan(poor);
    }

    /// <summary>Same seed, same everything: the run is reproducible even though damage is now rolled.
    ///
    /// <para>This is why the damage roll is drawn from the simulation's own WELL512 rather than letting the
    /// calculator reach for `System.Random` — a second generator in the damage path would make runs differ
    /// while every input stayed identical.</para></summary>
    [Fact]
    public void RolledDamageIsStillReproducibleFromTheSeed()
    {
        static (uint Now, int Kills, IReadOnlyList<string> Log) Run()
        {
            var sim = new CombatSimulation(seed: 99);
            sim.AddMob(handle: 10, x: 12, y: 0, configure: m =>
            {
                m.Hp = m.MaxHp = 4000;
                m.AttackDamage = 5;
                m.RespawnSeconds = 99_999;
            });
            sim.Player.Become(Table(), level: 40, equipment: [new EquipmentPiece("axe", MinWC: 50, MaxWC: 160)]);
            sim.Player.AttackRange = 20;
            sim.Player.Hp = sim.Player.MaxHp = 50_000;
            sim.LoadScript("""
                function on_tick()
                  local m = bot.nearbyMobs()
                  if #m > 0 then bot.attack(m[1].handle) end
                end
                """);
            sim.Run(maxTicks: 400);
            return (sim.Now, sim.Kills, sim.Log);
        }

        var a = Run();
        var b = Run();

        b.Now.ShouldBe(a.Now);
        b.Kills.ShouldBe(a.Kills);
        b.Log.ShouldBe(a.Log);
    }

    /// <summary>A rolled swing genuinely varies — otherwise "reproducible" above would be trivially true
    /// because nothing was random in the first place.</summary>
    [Fact]
    public void SwingsActuallyVary()
    {
        var sim = new CombatSimulation(seed: 3);
        var mob = sim.AddMob(handle: 10, x: 5, y: 0, configure: m =>
        {
            m.Hp = m.MaxHp = 1_000_000;
            m.RespawnSeconds = 99_999;
        });
        sim.Player.Become(Table(), level: 40, equipment: [new EquipmentPiece("axe", MinWC: 50, MaxWC: 400)]);

        var seen = new HashSet<int>();
        for (var i = 0; i < 40; i++)
        {
            var before = mob.Hp;
            sim.PlayerAttack(mob);
            seen.Add(before - mob.Hp);
        }

        seen.Count.ShouldBeGreaterThan(1, "a rolled swing should not always land the same number");
    }
}
