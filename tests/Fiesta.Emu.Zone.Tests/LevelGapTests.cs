using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`roe_LevelGapDamageRevision` — the last approximation in the damage pipeline, now a table read.</summary>
public class LevelGapTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "DamageLvGapPVE.shn")) ? root : null;
    }

    /// <summary>The scan takes the FIRST row whose `LvGap` is at least the gap — not an exact match, and not
    /// the last. The −150 first row is a floor that nothing normal reaches.</summary>
    [Fact]
    public void TheLookupTakesTheFirstRowThatCoversTheGap()
    {
        List<LevelGapTable.Row> rows =
        [
            new(-150, 1500), new(-10, 1500), new(-3, 1300), new(0, 1000), new(150, 1000),
        ];

        LevelGapTable.Lookup(rows, -200).ShouldBe(1500, "only a 150-level gap reaches the floor row");
        LevelGapTable.Lookup(rows, -21).ShouldBe(1500);
        LevelGapTable.Lookup(rows, -5).ShouldBe(1300, "-5 is past -10, so the -3 row is the first that covers it");
        LevelGapTable.Lookup(rows, 0).ShouldBe(1000);
        LevelGapTable.Lookup(rows, 400).ShouldBe(LevelGapTable.NoAdjustment, "nothing matched");
    }

    /// <summary>The real tables. A player gets up to +50% against something below their level; a monster
    /// gets nothing, ever — which the port had assumed and can now state as a reading.</summary>
    [SkippableFact]
    public void APlayerGainsUpToHalfAgainAndAMonsterGainsNothing()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var t = LevelGapTable.Load(Shine()!);

        // Level 82 player vs the level 61 Orc from Damage.pcapng: gap -21.
        t.Rate(CombatantKind.Player, 82, CombatantKind.Monster, 61).ShouldBe(1500);

        t.Rate(CombatantKind.Player, 60, CombatantKind.Monster, 60).ShouldBe(1000, "no advantage, no bonus");
        t.Rate(CombatantKind.Player, 61, CombatantKind.Monster, 60).ShouldBe(1100);
        t.Rate(CombatantKind.Player, 63, CombatantKind.Monster, 60).ShouldBe(1300);
        t.Rate(CombatantKind.Player, 65, CombatantKind.Monster, 60).ShouldBe(1500, "it flattens at five levels");
        t.Rate(CombatantKind.Player, 40, CombatantKind.Monster, 60).ShouldBe(1000, "outlevelled the other way");

        t.MonsterToPlayer.ShouldAllBe(r => r.DamageRate == 1000, "every EVP row is 1000");
        t.Rate(CombatantKind.Monster, 61, CombatantKind.Player, 82).ShouldBe(1000);

        // A pairing the original has no table for is left alone rather than guessed at.
        t.Rate(CombatantKind.Monster, 10, CombatantKind.Monster, 90).ShouldBe(LevelGapTable.NoAdjustment);
    }

    /// <summary>⚠️ `LvGap` is stored unsigned and read with `movsx`. Without the sign extension the first
    /// row's −150 reads as 65386, which is ≥ every real gap, so EVERY lookup returns the floor row's rate.</summary>
    [SkippableFact]
    public void TheGapColumnIsSigned()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var t = LevelGapTable.Load(Shine()!);

        t.PlayerToMonster[0].LvGap.ShouldBe(-150);
        t.PlayerToMonster.ShouldContain(r => r.LvGap == 0 && r.DamageRate == 1000);
        t.PlayerToMonster.Count(r => r.DamageRate == 1500).ShouldBeGreaterThan(1);
    }

    /// <summary>It reaches the simulation: the same character hits an underlevelled mob half again as hard
    /// once the tables are loaded.</summary>
    [SkippableFact]
    public void TheTableChangesWhatTheSimulationDeals()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(shine!);
        var tables = ClassParamTable.LoadAll(Path.Combine(shine!, "World"));

        static int Hit(MobDataBox box, ClassParamTable cls, LevelGapTable? gaps)
        {
            var sim = new CombatSimulation(seed: 11) { LevelGaps = gaps };
            var mob = sim.AddMob(handle: 10, x: 0, y: 0, configure: m => m.RespawnSeconds = 99_999);
            mob.Define(MobCombatant.Build(box, "Orc")!);
            mob.Hp = mob.MaxHp = 10_000_000;
            sim.Player.Become(cls, level: 82);
            sim.PlayerAttack(mob);
            return mob.MaxHp - mob.Hp;
        }

        var withoutTable = Hit(box, tables["Warrior"], null);
        var withTable = Hit(box, tables["Warrior"], LevelGapTable.Load(shine!));

        withoutTable.ShouldBeGreaterThan(0);
        withTable.ShouldBe(withoutTable * 3 / 2, "a level 82 hitting a level 61 gets exactly 1500 permille");
    }
}
