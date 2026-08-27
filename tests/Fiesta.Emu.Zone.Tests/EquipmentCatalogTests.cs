using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Class-correct gear, from `ItemInfo` × `UseClassTypeInfo` × `ClassName`.</summary>
public class EquipmentCatalogTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "ItemInfo.shn")) ? root : null;
    }

    [SkippableFact]
    public void TheClassMatrixLoadsForAllTwentySevenClasses()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var cat = EquipmentCatalog.Load(Shine()!);

        cat.ClassIds.Count.ShouldBe(27);
        cat.Items.Count.ShouldBeGreaterThan(10_000);
        cat.ClassIds["Fighter"].ShouldBe(1);
        cat.ClassIds["Savior"].ShouldBe(27);
    }

    /// <summary>`UseClass` bands are CUMULATIVE UP a promotion line, and a band excludes the classes below
    /// it. UseClass 8 is Cleric and its promotions; UseClass 9 is the promotions only.
    ///
    /// <para>This is why a base Cleric at level 40 cannot wear level-40 hammers: they are UseClass 9, and by
    /// 40 a character would have promoted. Testing "Cleric at 40" produces a character in level-17 gear and
    /// looks like a catalogue bug — it is the data being right.</para></summary>
    [SkippableFact]
    public void PromotionBandsExcludeTheBaseClass()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var cat = EquipmentCatalog.Load(Shine()!);

        var cleric = cat.ClassIds["Cleric"];
        var high = cat.ClassIds["HighCleric"];

        cat.UseClassAllows[8].ShouldContain(cleric);
        cat.UseClassAllows[8].ShouldContain(high);

        cat.UseClassAllows[9].ShouldNotContain(cleric, "band 9 is the promotions only");
        cat.UseClassAllows[9].ShouldContain(high);

        // UseClass 1 is everyone; 0 is nobody. Both fall out of the matrix.
        cat.UseClassAllows[1].Count.ShouldBe(27);
        cat.UseClassAllows[0].ShouldBeEmpty();
    }

    /// <summary>Each class gets weapons its own line can use — the point of the join.</summary>
    [SkippableFact]
    public void EachClassGetsItsOwnKindOfWeapon()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var cat = EquipmentCatalog.Load(Shine()!);

        string Weapon(string cls) =>
            cat.BestLoadout(cls, 40).Where(i => i.MaxWc > 0).OrderByDescending(i => i.MaxWc)
                .Select(i => i.Name).FirstOrDefault() ?? "";

        Weapon("HighCleric").ShouldContain("Hammer");
        Weapon("Warrior").ShouldContain("Axe");
        Weapon("Ranger").ShouldContain("bow", Case.Insensitive);
        Weapon("Wizard").ShouldContain("Wand");
    }

    /// <summary>⚠️ The obtainable filter is what stops every class converging on the same debug weapon: a
    /// flat `WC 1000-1000` GM item with an undecodable name outscores all real gear.</summary>
    [SkippableFact]
    public void TheObtainableFilterKeepsDebugItemsOut()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var cat = EquipmentCatalog.Load(Shine()!);

        var filtered = cat.BestLoadout("Warrior", 40).Where(i => i.MaxWc > 0).OrderByDescending(i => i.MaxWc).First();
        var unfiltered = cat.BestLoadout("Warrior", 40, obtainableOnly: false)
            .Where(i => i.MaxWc > 0).OrderByDescending(i => i.MaxWc).First();

        filtered.MinWc.ShouldBeLessThan(filtered.MaxWc, "real weapons have a damage range");
        unfiltered.MaxWc.ShouldBeGreaterThan(filtered.MaxWc, "the debug item outscores everything");
    }

    /// <summary>Gear reaches the damage formula through the Item layer, so a better-armed class hits harder.</summary>
    [SkippableFact]
    public void GearFromTheCatalogueReachesTheFormula()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var cat = EquipmentCatalog.Load(shine!);
        var tables = ClassParamTable.LoadAll(Path.Combine(shine!, "World"));

        double MaxDamage(string cls)
        {
            var p = new SimPlayer();
            p.Become(tables[cls], 40, equipment: cat.BestLoadout(cls, 40).Select(i => i.ToPiece()).ToList());
            return Combat.DamageCalculator.MaxWeaponDamage(p);
        }

        MaxDamage("Warrior").ShouldBeGreaterThan(MaxDamage("Wizard"),
            "a wizard's wand is not a physical weapon");
        MaxDamage("Warrior").ShouldBeGreaterThan(0);
    }
}

/// <summary>A kill quest the driver can see, progress and finish.</summary>
public class KillQuestTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobInfo.shn")) ? root : null;
    }

    private const string Grind = """
        function tick()
          local q = bot.activeQuests()[1]
          if q and q.done then return end
          local mobs = bot.nearbyMobs()
          if #mobs == 0 then bot.walkTo(bot.x() + 60, bot.y() + 25) return end
          local best, bd = nil, 1e9
          for i = 1, #mobs do if mobs[i].dist < bd then best, bd = mobs[i], mobs[i].dist end end
          if not bot.attack(best.handle) then bot.walkTo(best.x, best.y) end
        end
        """;

    /// <summary>Progress comes from the simulation's own per-mob kill tally, not a counter the harness
    /// bumps — so a quest for Orcs is not advanced by killing something else.</summary>
    [SkippableFact]
    public void QuestProgressCountsOnlyTheQuestMob()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(shine!);

        var sim = new CombatSimulation(seed: 7);
        var orc = sim.AddMob(handle: 10, x: 8, y: 0, configure: m => m.RespawnSeconds = 99_999);
        orc.Define(MobCombatant.Build(box, "Orc")!);
        var pinky = sim.AddMob(handle: 11, x: 12, y: 0, configure: m => m.RespawnSeconds = 99_999);
        pinky.Define(MobCombatant.Build(box, "Pinky")!);

        sim.Player.AttackDamage = 100_000;
        sim.Player.AttackRange = 50;

        var h = LevelingBotHarness.Attach(sim, Grind);
        h.Quests.Add(new LevelingBotHarness.KillQuest(1, "Orc Cull", "Orc", box.InfoFor("Orc")!.Id, 5));

        sim.PlayerAttack(pinky);
        h.ProgressOf(h.Quests[0]).ShouldBe(0, "a Pinky is not an Orc");

        sim.PlayerAttack(orc);
        h.ProgressOf(h.Quests[0]).ShouldBe(1);
    }

    /// <summary>The driver sees the quest through its own path — `activeQuests`, and the static/pulse pair
    /// it really reads (`questStatics` for objectives, `questPulse` for live progress).</summary>
    [SkippableFact]
    public void TheQuestIsVisibleThroughTheDriversOwnAccessors()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(shine!);

        var sim = new CombatSimulation(seed: 7);
        const string probe = """
            seen = {}
            function tick()
              local q = bot.activeQuests()[1]
              seen.id = q and q.id or -1
              seen.need = q and q.need or -1
              local st = bot.questStatics({}) [seen.id]
              seen.mob = st and st.objectives[1].mob or -1
              seen.count = st and st.objectives[1].count or -1
              local pulse = bot.questPulse()
              seen.prog = pulse.quests[seen.id].objProg[1]
            end
            """;

        var h = LevelingBotHarness.Attach(sim, probe);
        var orcId = box.InfoFor("Orc")!.Id;
        h.Quests.Add(new LevelingBotHarness.KillQuest(9001, "Orc Cull", "Orc", orcId, 1000));
        h.Run(probe, ticks: 2);

        h.Errors.ShouldBeEmpty();
        var seen = sim.Script.Globals.Get("seen").Table;
        seen.Get("id").Number.ShouldBe(9001);
        seen.Get("need").Number.ShouldBe(1000);
        seen.Get("mob").Number.ShouldBe(orcId, "objectives carry the numeric MobInfo id");
        seen.Get("count").Number.ShouldBe(1000);
        seen.Get("prog").Number.ShouldBe(0);
    }

    /// <summary>THE GRIND: a class-geared character working a 1000-Orc quest in a real Uruga.</summary>
    [SkippableFact]
    public void AGearedCharacterGrindsTheQuestMob()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var urg = Path.Combine(shine!, "MobRegen", "Urg.txt");
        Skip.If(!File.Exists(urg), "Urg.txt not present");

        var box = MobDataBox.Load(shine!);
        var cat = EquipmentCatalog.Load(shine!);
        var map = MobRegenData.Load(urg);
        var tables = ClassParamTable.LoadAll(Path.Combine(shine!, "World"));

        var sim = new CombatSimulation(seed: 42);
        sim.SpawnFightable(map, box, spawnSeed: 7);
        sim.Player.Become(tables["Warrior"], 40,
            equipment: cat.BestLoadout("Warrior", 40).Select(i => i.ToPiece()).ToList());
        sim.Player.Hp = sim.Player.MaxHp = 5_000_000;
        sim.Player.AttackRange = 25;
        sim.Player.MoveSpeed = 14;
        var (x, y) = map.BusiestArea();
        sim.Player.X = x;
        sim.Player.Y = y;

        var h = LevelingBotHarness.Attach(sim, Grind);
        h.Quests.Add(new LevelingBotHarness.KillQuest(9001, "Orc Cull", "Orc", box.InfoFor("Orc")!.Id, 1000));
        h.Run(Grind, ticks: 4000);

        h.Errors.ShouldBeEmpty();
        sim.Kills.ShouldBeGreaterThan(10);
        h.ProgressOf(h.Quests[0]).ShouldBeGreaterThan(0, "it is killing the quest mob, not just anything");
        h.ProgressOf(h.Quests[0]).ShouldBeLessThan(sim.Kills, "and not only the quest mob");
    }
}
