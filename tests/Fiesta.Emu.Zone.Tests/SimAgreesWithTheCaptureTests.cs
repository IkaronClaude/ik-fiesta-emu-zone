using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Lua;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>⭐ STAGE 4 of `docs/BOT_SIM_INTEGRATION.md` — the step that stops the simulation becoming a
/// fast wrong answer.
///
/// <para>`BotSimIntegrationTests` proves the driver runs and fights. It cannot prove the fight is
/// REALISTIC, because everything in it is measured against the simulation itself. This measures the
/// simulation against `MageDamageLvl60.pcapng`, which is a real client session.</para>
///
/// <para>⚠️ <b>It is also the answer to a fixture problem that has twice looked like a bug.</b> The
/// integration fixture invents a character — "a mace with MinWC 60-95 and a robe with AC 40" — and drops
/// it into Uruga, a level-61 zone. It takes 639-746 damage per mob swing against a real max HP of 879, and
/// only survives because the test hands it 2,000,000 HP. That reads as "mobs hit far too hard"; the
/// capture says an Orc hits a level-60 character for 290-358. The difference is not the engine, it is 40
/// points of armour against the capture character's 336.</para>
///
/// <para>So the scenario below is built from the capture's OWN equipment rather than from invented
/// numbers, and asserted against what the capture observed.</para></summary>
public class SimAgreesWithTheCaptureTests(ITestOutputHelper output)
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "ItemInfo.shn")) ? root : null;
    }

    /// <summary>The nine items `damage_buckets.py` recovers from the capture's
    /// `NC_ITEM_EQUIPCHANGE_CMD` packets: a FairyWand, four Nature armour pieces, and four cosmetics.
    ///
    /// <para>⚠️ The cosmetics are not decoration. Three of them carry `CriRate` 50 — the character's item
    /// critical rate is 180, not the wand's 30 — and this project spent three sessions reasoning about
    /// only the five non-cosmetic ones.</para></summary>
    private static readonly int[] CaptureEquipment = [1521, 1523, 35738, 32413, 1804, 1522, 1520, 30817, 32409];

    private static EquipmentPiece[] Equipment(string shine)
    {
        var catalog = EquipmentCatalog.Load(shine);
        return [.. CaptureEquipment.Select(id => catalog.Items.First(i => i.Id == id).ToPiece())];
    }

    /// <summary>⭐ THE CAPTURE'S OWN NUMBER. An Orc hit the level-60 Enchanter for <b>290-358</b> across 23
    /// observed swings, with the character's reported DEF at 336. Rebuild that character from its actual
    /// equipment and the engine has to land in the same place.
    ///
    /// <para>The band is the whole space of clean outcomes — minimum roll to maximum — so the observed
    /// range should sit INSIDE it. What would fail badly is the band missing the observations entirely,
    /// which is what an under-armoured fixture does.</para>
    ///
    /// <para>⚠️ <b>IT DOES NOT QUITE FIT, AND THE GAP IS RECORDED RATHER THAN TUNED AWAY.</b> The rebuild
    /// gives DEF <b>327</b> where the capture reports <b>336</b> — nine points short, 2.7% — so the band
    /// comes out at 297..371 against an observed 290..358 and its FLOOR is seven too high. Nine points of
    /// armour is what this harness cannot see: `NC_ITEM_EQUIPCHANGE_CMD` carries item ids and no
    /// ENHANCEMENT level, and a +N on the Nature pieces adds AC that `ItemInfo` alone does not know about.
    /// The character's Con free stat is 0, so that is not it.</para>
    ///
    /// <para>So this asserts what is actually true — the bands overlap heavily and the armour is within
    /// 3% — and the shortfall is filed in OPEN_QUESTIONS rather than absorbed into a wider bound.</para></summary>
    [SkippableFact]
    public void AnOrcHitsTheCapturesCharacterAsHardAsTheCaptureSaysItDid()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var box = MobDataBox.Load(shine!);
        var enchanter = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamEnchanterServer.txt"));

        var player = new CombatSimulation().Player;
        player.Become(enchanter, level: 60, freeStats: new FreeStats(Int: 50, Men: 25),
                      equipment: Equipment(shine!));

        var orc = MobCombatant.Build(box, "Orc")!;
        var gaps = LevelGapTable.Load(shine!);
        var mods = new AttackModifiers
        {
            ForceCritical = false,
            LevelGapRatePermille = gaps.Rate(CombatantKind.Monster, orc.Level, CombatantKind.Player, 60),
        };

        var lo = DamageCalculator.ResolveDamage(orc, player, mods with { RollPermille = 0 });
        var hi = DamageCalculator.ResolveDamage(orc, player, mods with { RollPermille = 1000 });

        output.WriteLine($"player DEF={DamageCalculator.ArmourClass(player):F0} (capture reported 336), "
                         + $"maxHp={player.MaxHp}; Orc({orc.Level}) hits for {lo}..{hi}, capture saw 290..358");

        // The capture's 23 Orc swings ran 290..358.
        const int observedLo = 290, observedHi = 358;

        DamageCalculator.ArmourClass(player).ShouldBeInRange(326, 346,
            "the rebuilt armour should be within a few points of the 336 the capture reports; a bigger gap "
            + "means something other than the missing enhancement levels is wrong");

        hi.ShouldBeGreaterThanOrEqualTo(observedHi,
            "the engine's ceiling must cover the hardest hit observed");
        lo.ShouldBeLessThanOrEqualTo(observedHi,
            "the band must overlap what the capture saw at all");

        // The floor is currently 7 over the softest observed hit, from the 9 points of armour this
        // harness cannot see. Bounded so it cannot quietly get worse.
        (lo - observedLo).ShouldBeLessThanOrEqualTo(12,
            "the floor is above the softest observed hit by more than the missing enhancement can explain");
    }

    /// <summary>⚠️ THE FIXTURE, NOT THE ENGINE. The same Orc against the integration test's invented
    /// character hits about twice as hard — and that is correct, because the invented character wears 40
    /// points of armour where the real one wears ten times that.
    ///
    /// <para>This exists so the difference is recorded as a property of the fixture rather than
    /// rediscovered as a bug. It is why `BotSimIntegrationTests` has to hand its character 2,000,000 HP to
    /// keep it standing, and why survival, fleeing and consumables cannot be evaluated there.</para></summary>
    [SkippableFact]
    public void TheInventedFixtureIsFarSquishierThanTheRealCharacter()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var box = MobDataBox.Load(shine!);
        var orc = MobCombatant.Build(box, "Orc")!;
        var gaps = LevelGapTable.Load(shine!);
        var mods = new AttackModifiers
        {
            ForceCritical = false,
            RollPermille = 1000,
            LevelGapRatePermille = gaps.Rate(CombatantKind.Monster, orc.Level, CombatantKind.Player, 60),
        };

        var real = new CombatSimulation().Player;
        real.Become(ClassParamTable.Load(Path.Combine(shine!, "World", "ParamEnchanterServer.txt")),
                    level: 60, freeStats: new FreeStats(Int: 50, Men: 25), equipment: Equipment(shine!));

        var invented = new CombatSimulation().Player;
        invented.Become(ClassParamTable.Load(Path.Combine(shine!, "World", "ParamClericServer.txt")),
                        level: 60, freeStats: new FreeStats(Str: 20, Con: 20),
                        equipment: [new EquipmentPiece("mace", MinWC: 60, MaxWC: 95),
                                    new EquipmentPiece("robe", AC: 40)]);

        var onReal = DamageCalculator.ResolveDamage(orc, real, mods);
        var onInvented = DamageCalculator.ResolveDamage(orc, invented, mods);

        output.WriteLine($"worst Orc swing: {onReal} on the capture's character (AC {DamageCalculator.ArmourClass(real):F0}), "
                         + $"{onInvented} on the invented one (AC {DamageCalculator.ArmourClass(invented):F0})");

        onInvented.ShouldBeGreaterThan(onReal,
            "the invented fixture wears almost no armour, so it should take more -- if this ever flips, "
            + "the armour chain has changed and the integration fixture's survivability numbers are stale");
    }
}
