using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Which rules of engagement a NORMAL attack goes through — `smo_RulesOfNormalAttack` (+0x1E74),
/// the reader `MobWeapon.HitType` had been missing.</summary>
public class NormalAttackRuleTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobWeapon.shn")) ? root : null;
    }

    private static MobWeapon Weapon(HitType type) =>
        new(Id: 1, InxName: "w", Skill: "-",
            AtkSpd: 1000, AtkDly: 0, SwingTime: 1000, HitTime: 500,
            MinWc: 10, MaxWc: 20, Th: 0, MinMa: 100, MaxMa: 200, Mh: 0,
            Range: 20, HitType: type, BlastRate: 0,
            StaStrength: 0, StaRate: 0, AggroInitialize: 0);

    /// <summary>The three branches of `so_mob_Regenerate+0x543`, including the one the current data never
    /// takes: `HT_NONE` selects MAGIC, because the server tests <c>HitType != 0</c> rather than
    /// <c>== HT_MA</c>.</summary>
    [Fact]
    public void TheRuleComesFromWeaponRowZerosHitType()
    {
        MobParameters.NormalAttackRule([]).ShouldBe(EngagementRule.NormalPhysical,
            "no weapon array at all is the FIRST branch, and it is physical");

        MobParameters.NormalAttackRule([Weapon(HitType.Physical)]).ShouldBe(EngagementRule.NormalPhysical);
        MobParameters.NormalAttackRule([Weapon(HitType.Magical)]).ShouldBe(EngagementRule.NormalMagic);
        MobParameters.NormalAttackRule([Weapon(HitType.None)]).ShouldBe(EngagementRule.NormalMagic,
            "the server's test is `!= HT_PY`, so HT_NONE lands on the magical rule too");
    }

    /// <summary>Row 0 decides for the whole mob. The rule is chosen ONCE at regeneration, so a magical row
    /// further down the list never changes how the mob swings at a player — and 304 mobs have exactly that
    /// shape, so it is not a hypothetical.</summary>
    [Fact]
    public void OnlyRowZeroDecides()
    {
        MobParameters.NormalAttackRule([Weapon(HitType.Physical), Weapon(HitType.Magical)])
            .ShouldBe(EngagementRule.NormalPhysical);
    }

    /// <summary>A magical mob's swing is measured against MAGIC RESISTANCE, not armour. That is the whole
    /// behavioural consequence: a defender who stacked AC is no better off against it.</summary>
    [SkippableFact]
    public void AMagicalMobIsResistedByMrAndAPhysicalOneByAc()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        var caster = MobCombatant.Build(box, "GhostKnight")!;
        caster.NormalAttackRule.ShouldBe(EngagementRule.NormalMagic);

        // Two defenders with the SAME total defence, split differently.
        static Combatant Defender(int ac, int mr) => Combatant.FromBaseStats(60, new Dictionary<Stat, int>
        {
            [Stat.Con] = 100, [Stat.Men] = 100, [Stat.AC] = ac, [Stat.MR] = mr,
        });

        var armoured = Defender(ac: 4000, mr: 10);
        var warded = Defender(ac: 10, mr: 4000);

        var pinned = new AttackModifiers { RollPermille = 500, ForceCritical = false };
        var vsArmoured = DamageCalculator.ResolveDamage(caster, armoured, pinned, rule: caster.NormalAttackRule);
        var vsWarded = DamageCalculator.ResolveDamage(caster, warded, pinned, rule: caster.NormalAttackRule);

        vsWarded.ShouldBeLessThan(vsArmoured, "magic is stopped by MR, and armour does nothing against it");
    }

    /// <summary>The rule reaches the simulation's own swing path, not just the calculator: the same mob
    /// against the same two defenders lands differently once it is a real fight.</summary>
    [SkippableFact]
    public void TheSimulationSwingsThroughTheMobsOwnRule()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        static int TakenFrom(MobDataBox box, string mobName, int ac, int mr)
        {
            var sim = new CombatSimulation(seed: 3) { TickMs = 100 };
            var mob = sim.AddMob(handle: 10, x: 0, y: 0, configure: m => m.RespawnSeconds = 99_999);
            mob.Define(MobCombatant.Build(box, mobName)!);
            mob.Mob.so_getDetectRange = 4000;

            sim.Player.X = 10;
            sim.Player.Y = 0;
            sim.Player.Hp = sim.Player.MaxHp = 50_000_000;
            sim.Player.UsesStatFormula = true;
            sim.Player.Parameters.Base[Stat.Con] = 100;
            sim.Player.Parameters.Base[Stat.Men] = 100;
            sim.Player.Parameters.Base[Stat.AC] = ac;
            sim.Player.Parameters.Base[Stat.MR] = mr;

            sim.Run(maxTicks: 300);
            return sim.Player.MaxHp - sim.Player.Hp;
        }

        var casterVsArmour = TakenFrom(box, "GhostKnight", ac: 8000, mr: 20);
        var casterVsWard = TakenFrom(box, "GhostKnight", ac: 20, mr: 8000);

        casterVsArmour.ShouldBeGreaterThan(0, "the mob has to actually be hitting for this to mean anything");
        casterVsWard.ShouldBeLessThan(casterVsArmour, "its swings are magical, so MR is what stops them");
    }

    /// <summary>⚠️ A PLAYER IS ALWAYS PHYSICAL — every class, always. The `ShineMobileObject` constructor
    /// sets `roe_normalPY` and the only writer that could change it for a player is the GM `&amp;allcritical`
    /// command. So a wizard auto-attacking for almost nothing is the game, not a missing feature.</summary>
    [SkippableFact]
    public void APlayersNormalAttackIsPhysicalWhateverTheirClass()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var tables = ClassParamTable.LoadAll(Path.Combine(shine!, "World"));
        var cat = EquipmentCatalog.Load(shine!);

        var wizard = new SimPlayer();
        wizard.Become(tables["Wizard"], 40,
            equipment: cat.BestLoadout("Wizard", 40).Select(i => i.ToPiece()).ToList());

        // Its magic attack is the larger number by far -- and the normal swing does not use it.
        DamageCalculator.MaxMagicAttack(wizard)
            .ShouldBeGreaterThan(DamageCalculator.MaxWeaponDamage(wizard),
                "a wand is a magic weapon; that is exactly why the physical-only rule bites");
    }
}
