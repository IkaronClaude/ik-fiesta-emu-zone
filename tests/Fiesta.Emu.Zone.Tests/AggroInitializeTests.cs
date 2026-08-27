using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Mob;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`MobWeapon.AggroInitialize` — what a landed hit does to the ATTACKER'S hate list.</summary>
public class AggroInitializeTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobWeapon.shn")) ? root : null;
    }

    private sealed class Dummy(ushort handle) : IShineObject
    {
        public ushort Handle { get; } = handle;
        public int X { get; set; }
        public int Y { get; set; }
        public bool IsAlive { get; set; } = true;
    }

    /// <summary>The direction, which is the opposite of what the column name suggests: a mob that lands a
    /// blow sheds hate for its victim, it does not gain it.</summary>
    [Fact]
    public void ALandedHitTakesHateOffTheAttackersOwnList()
    {
        var mob = new ShineMob { Handle = 1 };
        var victim = new Dummy(2);

        mob.so_mob_AppendAggro(victim, 1000);
        mob.so_mob_DecreaseAggro(victim, 400);

        mob.Selector.AggroList.Single(e => ReferenceEquals(e.Object, victim)).AggroPoint.ShouldBe(600);
    }

    /// <summary>`so_mob_DecreaseAggro` walks `sm_FamilyList`, so the shed is shared by the whole pack — a
    /// linked family all lose interest together.</summary>
    [Fact]
    public void TheShedIsAppliedToEveryFamilyMember()
    {
        var leader = new ShineMob { Handle = 1 };
        var friend = new ShineMob { Handle = 2 };
        leader.Family.Add(leader);
        leader.Family.Add(friend);

        var victim = new Dummy(9);
        leader.so_mob_AppendAggro(victim, 900);
        friend.so_mob_AppendAggro(victim, 900);

        leader.so_mob_DecreaseAggro(victim, 500);

        leader.Selector.AggroList.Single().AggroPoint.ShouldBe(400);
        friend.Selector.AggroList.Single().AggroPoint.ShouldBe(400, "the family sheds together");
    }

    /// <summary>The column reads, and it is a BOSS mechanic: 139 of 5,815 weapon rows carry it, at values
    /// large enough (400-1000) to rotate a boss off its current target.</summary>
    [SkippableFact]
    public void OnlyBossesShedAggro()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);
        var rows = box.Weapons.SelectMany(k => k.Value).ToList();

        rows.Count(w => w.AggroInitialize != 0).ShouldBeInRange(100, 200);
        box.AttackAgainstPlayer("GhostKnight")!.AggroInitialize.ShouldBe(400);
        box.AttackAgainstPlayer("Orc")!.AggroInitialize.ShouldBe(0, "an ordinary field mob does not rotate");
    }

    /// <summary>It reaches the simulation: a boss whose swing lands ends up with LESS hate for the player
    /// than the raw damage it dealt would have given it, because each landed hit gives some back.</summary>
    [SkippableFact]
    public void TheShedReachesTheSimulationWhenASwingLands()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        var sim = new CombatSimulation(seed: 5) { TickMs = 100 };
        var boss = sim.AddMob(handle: 10, x: 0, y: 0, configure: m => m.RespawnSeconds = 99_999);
        boss.Define(MobCombatant.Build(box, "GhostKnight")!);
        boss.Mob.so_getDetectRange = 4000;

        sim.Player.X = 10;
        sim.Player.Hp = sim.Player.MaxHp = 50_000_000;

        // Put the player on the hate list first, so there is something to shed.
        boss.Mob.so_mob_AppendAggro(sim.Player, 100_000);
        var before = boss.Mob.Selector.AggroList.Single().AggroPoint;

        sim.Run(maxTicks: 200);

        sim.Log.ShouldContain(l => l.Contains("sheds 400 aggro"), "the boss has to have actually connected");
        boss.Mob.Selector.AggroList.Single().AggroPoint.ShouldBeLessThan(before);
    }
}
