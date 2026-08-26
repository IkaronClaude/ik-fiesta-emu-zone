using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>A character built from the server's own class tables, fighting in a map built from the server's
/// own spawn tables. Needs a server-files tree; set <c>SHINE_DATA</c> or these skip.</summary>
public class RealCharacterTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return Directory.Exists(Path.Combine(root, "World")) ? root : null;
    }

    private const string RoamAndFight = """
        function on_tick()
          local mobs = bot.nearbyMobs()
          if #mobs == 0 then bot.walkTo(bot.x() + 40, bot.y() + 15) return end
          local best, bestDist = nil, 1e9
          for i = 1, #mobs do
            if mobs[i].dist < bestDist then best, bestDist = mobs[i], mobs[i].dist end
          end
          if not bot.attack(best.handle) then bot.walkTo(best.x, best.y) end
        end
        """;

    /// <summary>HP is a looked-up column, so it rises with level exactly as the table says — no curve fit.</summary>
    [SkippableFact]
    public void HpAndStatsComeFromTheClassTableAndRiseWithLevel()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var cleric = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamClericServer.txt"));

        var young = new SimPlayer();
        var older = new SimPlayer();
        young.Become(cleric, 10);
        older.Become(cleric, 40);

        young.MaxHp.ShouldBe(cleric.At(10)!.MaxHp);
        older.MaxHp.ShouldBe(cleric.At(40)!.MaxHp);
        older.MaxHp.ShouldBeGreaterThan(young.MaxHp);
        older.EffectiveStats()[Stat.Men].ShouldBeGreaterThan(young.EffectiveStats()[Stat.Men]);
    }

    /// <summary>Different classes at the same level are genuinely different characters — the whole point of
    /// there being 27 tables rather than one.</summary>
    [SkippableFact]
    public void ClassesDifferFromEachOtherAtTheSameLevel()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var all = ClassParamTable.LoadAll(Path.Combine(shine!, "World"));
        var fighter = all["Fighter"].At(30)!;
        var mage = all["Mage"].At(30)!;

        fighter.Str.ShouldNotBe(mage.Str);
        fighter.MaxHp.ShouldBeGreaterThan(mage.MaxHp);   // the melee class is the tougher one
        mage.Int.ShouldBeGreaterThan(fighter.Int);
    }

    /// <summary>THE GOAL: a character whose stats all came from game data runs around a real map and fights.</summary>
    [SkippableFact]
    public void ARealClericFightsInUruga()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var urg = Path.Combine(shine!, "MobRegen", "Urg.txt");
        Skip.If(!File.Exists(urg), "Urg.txt not present");

        var cleric = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamClericServer.txt"));
        var map = MobRegenData.Load(urg);

        var sim = new CombatSimulation(seed: 42);
        sim.SpawnAll(map, spawnSeed: 7);

        // Everything about this character is data: the class table for stats, gear for weapon damage.
        // The only invented numbers are reach and foot speed, which are bot behaviour, not game facts.
        sim.Player.Become(cleric, level: 40, freeStats: new FreeStats(Str: 20, Con: 20), equipment:
        [
            new EquipmentPiece("mace", MinWC: 60, MaxWC: 95),
            new EquipmentPiece("robe", AC: 40),
        ]);
        sim.Player.AttackRange = 14;
        sim.Player.MoveSpeed = 8;

        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;

        sim.LoadScript(RoamAndFight);
        sim.Run(maxTicks: 3000);          // 5 simulated minutes

        sim.Player.MaxHp.ShouldBe(cleric.At(40)!.MaxHp);
        sim.Player.AttackDamage.ShouldBe(95);        // the mace's MaxWC, through the Item layer
        sim.Kills.ShouldBeGreaterThan(0);
        sim.Log.ShouldContain(l => l.Contains("hits for"));
    }
}
