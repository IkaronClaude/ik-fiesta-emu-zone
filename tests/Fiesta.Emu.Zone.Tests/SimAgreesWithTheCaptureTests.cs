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
    /// <para>⚠️ <b>THE NINE-POINT GAP IS SOLVED, AND THE HYPOTHESIS THAT STOOD HERE WAS WRONG.</b> This
    /// used to say the rebuild's DEF 327 fell nine short of the capture's 336 because
    /// `NC_ITEM_EQUIPCHANGE_CMD` carries no ENHANCEMENT level, so a +N on the Nature pieces added armour
    /// `ItemInfo` could not see. <b>That is refuted:</b> enhancement touches AC and MR and nothing else,
    /// and the capture's Strength, Dexterity, Intelligence and MentalPower are off the class row too.
    /// Armour was never the odd one out; the whole stat vector was.</para>
    ///
    /// <para>⭐ <b>What the capture actually proves — the armour identity, at two independent points in
    /// one session:</b></para>
    /// <code>
    /// at login, no equipment yet:  Con 138, item AC sum   0  ->  reported AC 138
    /// after equipping nine items:  Con 147, item AC sum 189  ->  reported AC 336
    ///
    ///     AC = Con + sum(item AC)
    /// </code>
    ///
    /// <para>Both readings come from `NC_CHAR_CHANGEPARAMCHANGE_CMD` in `MageDamageLvl60.pcapng`, and the
    /// first one is decisive: with no equipment on, reported AC EQUALS Constitution exactly. So the
    /// engine's armour chain is right, and the nine points of DEF are nine points of CON.</para>
    ///
    /// <para>What remains open is why this character's base stats sit above its level-60 class row at all
    /// — +4 Str, +9 Con, +9 Dex — when the wire reports its level as 60 in every one of the five
    /// `NC_CHAR_CLIENT_BASE_CMD` blocks and its free-stat allocation as Int 50 / Men 25, nothing in Str,
    /// Con or Dex. It is an ADMIN character (the capture carries `NC_ACT_NOTICE_CMD` "Admin level is
    /// 100"), which would explain hand-set stats, but that is a plausible cause and not a proven one. See
    /// OPEN_QUESTIONS §5.</para>
    ///
    /// <para>So this test now asserts the MECHANISM, which is exactly reproducible, rather than a
    /// tolerance around a number the class table cannot produce.</para></summary>
    [SkippableFact]
    public void AnOrcHitsTheCapturesCharacterAsHardAsTheCaptureSaysItDid()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var box = MobDataBox.Load(shine!);
        var enchanter = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamEnchanterServer.txt"));

        // ⚠️ NO FREE STATS. The capture's char-base block reports an allocation of Int 50 / Men 25, but
        // its BASE stats carry none of it -- reported Int is 288 where the class row plus the pet gives
        // 275, not 325. Free-stat points feed the derived tables (`Int.MAAbsolute`, `Con.ACAbsoulte`,
        // `Men.MaxSP`), which is where the magical-damage fix already adds them; passing them here as well
        // would double-count into the base cluster and move armour by a number the wire does not show.
        var player = new CombatSimulation().Player;
        player.Become(enchanter, level: 60, equipment: Equipment(shine!));

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

        // ⭐ AC = Con + sum(item AC) -- the identity the capture proves twice. Asserting it directly says
        // far more than a tolerance around 336 would: it is exact, and it holds for ANY character, so it
        // fails loudly if the armour chain ever grows a term.
        var con = player.EffectiveStats()[Stat.Con];
        var itemArmour = Equipment(shine!).Sum(p => p.AC);
        DamageCalculator.ArmourClass(player).ShouldBe(con + itemArmour, 0.5,
            "the capture shows AC = Con + sum(item AC) -- at login, with nothing equipped, its reported AC "
            + "equalled its Constitution exactly (138), and after equipping nine items worth 189 armour it "
            + "read 336 against Con 147");

        // And the residual against the capture is ENTIRELY the Constitution difference: the capture's
        // character carries Con 147 where the level-60 class row gives 138. Nine points of Con, nine
        // points of DEF. Restating it as an equation keeps the two halves from drifting apart.
        const int captureCon = 147, captureDef = 336;
        (captureDef - (int)DamageCalculator.ArmourClass(player)).ShouldBe(captureCon - con,
            "the whole DEF residual should be the Con residual; if these ever differ, something OTHER than "
            + "the base stats has changed and the armour chain needs re-deriving");

        hi.ShouldBeGreaterThanOrEqualTo(observedHi,
            "the engine's ceiling must cover the hardest hit observed");
        lo.ShouldBeLessThanOrEqualTo(observedHi,
            "the band must overlap what the capture saw at all");

        // The floor sits above the softest observed hit by what the 9 points of Con are worth. Bounded so
        // it cannot quietly get worse.
        (lo - observedLo).ShouldBeLessThanOrEqualTo(12,
            "the floor is above the softest observed hit by more than the Constitution difference explains");
    }

    /// <summary>⭐ THE IDENTITY ON ITS OWN, AT THE POINT THE CAPTURE PROVES IT MOST CLEANLY: a character
    /// with NOTHING equipped has AC exactly equal to its Constitution.
    ///
    /// <para>`MageDamageLvl60.pcapng`'s first `NC_CHAR_CHANGEPARAMCHANGE_CMD` after zone login reports
    /// <c>Con 138, AC 138</c> — and the same block carries Str 43, Dex 157, Int 273, Men 186, MaxHp 1245,
    /// MaxSp 1826, every one of them the level-60 Enchanter class row to the unit. The equipment packets
    /// arrive afterwards.</para>
    ///
    /// <para>That is the anchor that turned the nine-point DEF gap from "missing armour enhancement" into
    /// "nine points of Constitution": armour cannot be the explanation for a difference that is already
    /// visible before any armour is worn.</para></summary>
    [SkippableFact]
    public void AnUnequippedCharacterHasArmourEqualToItsConstitution()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var enchanter = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamEnchanterServer.txt"));
        var player = new CombatSimulation().Player;
        player.Become(enchanter, level: 60);

        var stats = player.EffectiveStats();
        output.WriteLine($"level-60 Enchanter, nothing equipped: Str={stats[Stat.Str]} Con={stats[Stat.Con]} "
                         + $"Dex={stats[Stat.Dex]} Int={stats[Stat.Int]} Men={stats[Stat.Men]} "
                         + $"AC={DamageCalculator.ArmourClass(player):F0} maxHp={player.MaxHp} maxSp={player.MaxSp}");

        // The capture's own login block, field for field.
        stats[Stat.Str].ShouldBe(43);
        stats[Stat.Con].ShouldBe(138);
        stats[Stat.Dex].ShouldBe(157);
        stats[Stat.Int].ShouldBe(273);
        stats[Stat.Men].ShouldBe(186);
        player.MaxHp.ShouldBe(1245);
        player.MaxSp.ShouldBe(1826);

        DamageCalculator.ArmourClass(player).ShouldBe(138, 0.5,
            "with no equipment the capture's reported AC equalled its Constitution exactly");
    }

    /// <summary>⭐ WHAT IS LEFT OF THE GAP IS A UNIFORM <b>+5% ON EVERY PRIMARY STAT</b>, and naming its
    /// shape is the whole value of this test.
    ///
    /// <para>Two things were found and fixed before this residual came into focus. The class row
    /// reproduces the login state exactly, and the <b>Mini Phino pet</b> grants +2 to all five stats
    /// through `GradeItemOption.shn` — the operator named that mechanic, and the wire shows the equip
    /// packet followed 35 ms later by exactly those five increments. With both modelled the rebuild
    /// reaches <c>Str 45, Con 140, Dex 159, Int 275, Men 188</c>.</para>
    ///
    /// <para>The capture reports <c>47, 147, 166, 288, 197</c>. Every one of those is the rebuilt value
    /// plus <b>floor(value × 5%)</b>:</para>
    /// <code>
    /// Str  45 + floor(2.25)  = 47      Int  275 + floor(13.75) = 288
    /// Con 140 + floor(7.00)  = 147     Men  188 + floor( 9.40) = 197
    /// Dex 159 + floor(7.95)  = 166
    /// </code>
    ///
    /// <para>Five exact hits from a one-parameter model, and the truncation is what pins it: 7.95 gives 7,
    /// not 8. <b>Whatever the source is, it multiplies — it does not add.</b></para>
    ///
    /// <para>The source is not identified. Ruled out from the wire and the tables, each checked rather
    /// than assumed: the level (all five `NC_CHAR_CLIENT_BASE_CMD` blocks say 60), free-stat allocation
    /// (all 93 hits share one block, Int 50 / Men 25, nothing in Str/Con/Dex), the remaining equipment
    /// (`GradeItemOption` has rows for the pet alone), set effects, the 18 learned passives, the character
    /// title (`NC_CHAR_CLIENT_CHARTITLE_CMD` reports none), `BT_Inx` (it indexes `BelongTypeInfo` —
    /// binding flags), and the only self-abstate in the capture (index 291, `StaImmortal`, a GM state).
    /// The character IS an admin, so a hand-applied buff is the leading guess and stays a guess.</para>
    ///
    /// <para>This test asserts the SHAPE, so the next session inherits a signature to search for rather
    /// than a number to nudge.</para></summary>
    [SkippableFact]
    public void TheResidualAgainstTheCaptureIsAUniformFivePercentOnEveryStat()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var enchanter = ClassParamTable.Load(Path.Combine(shine!, "World", "ParamEnchanterServer.txt"));
        var player = new CombatSimulation().Player;
        player.Become(enchanter, level: 60, equipment: Equipment(shine!));
        var built = player.EffectiveStats();

        // The capture's own figures, from the CHANGEPARAM block covering all 93 damaging hits.
        var captured = new Dictionary<Stat, int>
        {
            [Stat.Str] = 47, [Stat.Con] = 147, [Stat.Dex] = 166, [Stat.Int] = 288, [Stat.Men] = 197,
        };

        foreach (var (stat, capture) in captured)
        {
            var mine = built[stat];
            var withFivePercent = mine + (int)(mine * 5 / 100.0);
            output.WriteLine($"{stat,-4} rebuilt {mine,4}  +5% -> {withFivePercent,4}  capture {capture,4}");
            withFivePercent.ShouldBe(capture,
                $"{stat} should be the rebuilt value plus a truncated 5%; if this breaks, either the "
                + "residual is not 5% after all or something in the base chain has moved");
        }
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
