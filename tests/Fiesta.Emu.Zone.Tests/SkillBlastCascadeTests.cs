using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Skill;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`smo_SkillBlast`'s post-`roe_CalcDamage` cascade, step by step.
///
/// <para>The point of most of these is the NEUTRAL case: every one of the six steps has to pass an
/// ordinary hit through untouched, because that is why the whole cascade went unnoticed while the damage
/// engine was being verified against two captures. A step that quietly scaled a neutral hit would have
/// shown up there; a step that is simply absent would not.</para></summary>
public class SkillBlastCascadeTests
{
    private static SkillSpecialArguments Special(SkillSpecial index, int value)
        => SkillSpecialArguments.From((index, value));

    [Fact]
    public void AnOrdinaryHitPassesThroughUntouched()
    {
        var outcome = SkillBlastCascade.Apply(1234, new SkillBlastInputs());

        outcome.Damage.ShouldBe(1234);
        outcome.IsDamage.ShouldBeTrue();
    }

    [Fact]
    public void TheSetItemRateScalesTheDamage()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs { SetItemDamageRatePermille = 1200 })
                            .Damage.ShouldBe(1200);

    [Fact]
    public void TheMultiHitRateSplitsIt()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs { MultiHitDamageRatePermille = 300 })
                            .Damage.ShouldBe(300);

    /// <summary>Both rates apply, and they apply in ORDER with a truncation between them — 999 * 1200
    /// truncates to 1198 before the second multiply, so the answer is 359 rather than the 359.64 a single
    /// combined multiply would round from.</summary>
    [Fact]
    public void TheTwoRatesTruncateSeparately()
        => SkillBlastCascade.Apply(999, new SkillBlastInputs { SetItemDamageRatePermille = 1200,
                                                             MultiHitDamageRatePermille = 300 })
                            .Damage.ShouldBe(1198 * 300 / 1000);

    /// <summary>⭐ A strike that scales away to nothing still lands for 1 — but only when it carries a
    /// positive rate.</summary>
    [Fact]
    public void AStrikeThatScalesToNothingStillLandsForOne()
        => SkillBlastCascade.Apply(1, new SkillBlastInputs { MultiHitDamageRatePermille = 1 })
                            .Damage.ShouldBe(1);

    /// <summary>⭐ A ZERO rate is the other case, and it is NOT a hit for zero: `smo_SkillBlast+0x998`
    /// clears the record's `isdamage` bit instead of flooring. Reporting only the number loses that.</summary>
    [Fact]
    public void AZeroRateStrikeClearsIsDamageRatherThanDealingZero()
    {
        var outcome = SkillBlastCascade.Apply(500, new SkillBlastInputs { MultiHitDamageRatePermille = 0 });

        outcome.Damage.ShouldBe(0);
        outcome.IsDamage.ShouldBeFalse();
    }

    /// <summary>...and a genuine zero from the engine leaves the flag alone, because the server's first
    /// condition is on the damage BEFORE the multi-hit rate.</summary>
    [Fact]
    public void AZeroFromTheEngineDoesNotClearIsDamage()
        => SkillBlastCascade.Apply(0, new SkillBlastInputs { MultiHitDamageRatePermille = 0 })
                            .IsDamage.ShouldBeTrue();

    // ---- 4. TARGETHPDOWNDMGUPRATE ---------------------------------------------------------------------

    [Fact]
    public void TheExecuteBonusIsNothingAtFullHealth()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
               Special = Special(SkillSpecial.TargetHpDownDmgUpRate, 1000),
               TargetMaxHp = 5000, TargetHp = 5000 }).Damage.ShouldBe(1000);

    /// <summary>Half the target's HP gone is +50%: the bonus is `damage * hpMissingPermille / 1000`.</summary>
    [Fact]
    public void TheExecuteBonusTracksTheTargetsMissingHealth()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
               Special = Special(SkillSpecial.TargetHpDownDmgUpRate, 1000),
               TargetMaxHp = 5000, TargetHp = 2500 }).Damage.ShouldBe(1500);

    /// <summary>⭐ The configured rate cancels out of the arithmetic — `((m*r)/1000 * d) / r` — so two
    /// wildly different rates give the same answer wherever the truncations do not bite. That reads as a
    /// bug in the original and it is what the instructions do.</summary>
    [Fact]
    public void TheExecuteBonusRateCancelsOut()
    {
        var atOneThousand = SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
            Special = Special(SkillSpecial.TargetHpDownDmgUpRate, 1000),
            TargetMaxHp = 5000, TargetHp = 2500 }).Damage;
        var atFiveThousand = SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
            Special = Special(SkillSpecial.TargetHpDownDmgUpRate, 5000),
            TargetMaxHp = 5000, TargetHp = 2500 }).Damage;

        atFiveThousand.ShouldBe(atOneThousand);
    }

    /// <summary>A max HP of zero skips the step, which is the server's own null check and not a guard
    /// added here — without it the 64-bit divide would fault.</summary>
    [Fact]
    public void TheExecuteBonusSkipsATargetWithNoMaxHealth()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
               Special = Special(SkillSpecial.TargetHpDownDmgUpRate, 1000),
               TargetMaxHp = 0, TargetHp = 0 }).Damage.ShouldBe(1000);

    /// <summary>⚠️ The step is skipped WHOLE when the skill has no such special, which is not the same as
    /// a rate of 0 — that would divide by zero. Absent must stay absent.</summary>
    [Fact]
    public void AnAbsentExecuteBonusIsNotARateOfZero()
        => Should.NotThrow(() => SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
               TargetMaxHp = 5000, TargetHp = 1 }));

    // ---- 5. DMGDOWNRATE -------------------------------------------------------------------------------

    /// <summary>⭐ It SUBTRACTS. The magic constant is 0xEF9DB22D — the /1000 magic negated — so the step
    /// divides by MINUS a thousand, which is what "damage down rate" means.</summary>
    [Fact]
    public void TheDamageDownRateReducesTheDamage()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
               Special = Special(SkillSpecial.DmgDownRate, 200), WaveCount = 1 }).Damage.ShouldBe(800);

    /// <summary>The rate is multiplied by the blast's own wave counter, so a chained skill loses more of
    /// its damage at every step.</summary>
    [Fact]
    public void TheDamageDownRateScalesWithTheWaveCount()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
               Special = Special(SkillSpecial.DmgDownRate, 200), WaveCount = 3 }).Damage.ShouldBe(400);

    [Fact]
    public void TheDamageDownRateIsCappedByMaxDmgDownRate()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs {
               Special = SkillSpecialArguments.From((SkillSpecial.DmgDownRate, 200),
                                                    (SkillSpecial.MaxDmgDownRate, 450)),
               WaveCount = 4 }).Damage.ShouldBe(550);

    /// <summary>The cap only ever caps: a wave count that keeps the scaled rate under it leaves it
    /// alone.</summary>
    [Fact]
    public void TheCapDoesNotRaiseTheRate()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs {
               Special = SkillSpecialArguments.From((SkillSpecial.DmgDownRate, 200),
                                                    (SkillSpecial.MaxDmgDownRate, 900)),
               WaveCount = 1 }).Damage.ShouldBe(800);

    // ---- 6. UNDEADTODMG -------------------------------------------------------------------------------

    [Fact]
    public void TheUndeadBonusAppliesOnlyToUndead()
    {
        var special = Special(SkillSpecial.UndeadToDmg, 500);

        SkillBlastCascade.Apply(1000, new SkillBlastInputs { Special = special, TargetType = MobType.Undead })
                         .Damage.ShouldBe(1500);
        SkillBlastCascade.Apply(1000, new SkillBlastInputs { Special = special, TargetType = MobType.Beast })
                         .Damage.ShouldBe(1000);
    }

    /// <summary>A target with no `MobDataBox` — a player — never qualifies, because the server reaches
    /// the type THROUGH the box and gives up when it is null.</summary>
    [Fact]
    public void TheUndeadBonusSkipsATargetWithNoMobData()
        => SkillBlastCascade.Apply(1000, new SkillBlastInputs { 
               Special = Special(SkillSpecial.UndeadToDmg, 500), TargetType = null }).Damage.ShouldBe(1000);

    // ---- 7. the static-damage override ----------------------------------------------------------------

    /// <summary>`ShineMobileObject`'s constructor sets the slot to -1, so the normal case is inert.</summary>
    [Fact]
    public void TheStaticDamageOverrideIsInertByDefault()
        => SkillBlastCascade.Apply(777, new SkillBlastInputs { StaticDamage = -1 }).Damage.ShouldBe(777);

    /// <summary>Zero is not an override either — the test is strictly greater than zero.</summary>
    [Fact]
    public void ZeroStaticDamageIsNotAnOverride()
        => SkillBlastCascade.Apply(777, new SkillBlastInputs { StaticDamage = 0 }).Damage.ShouldBe(777);

    /// <summary>⭐ It REPLACES, and it does so last — after every rate, bonus and reduction.</summary>
    [Fact]
    public void StaticDamageReplacesEverythingBeforeIt()
        => SkillBlastCascade.Apply(777, new SkillBlastInputs { 
               SetItemDamageRatePermille = 1500,
               Special = Special(SkillSpecial.DmgDownRate, 500),
               StaticDamage = 42 }).Damage.ShouldBe(42);

    // ---- order --------------------------------------------------------------------------------------

    /// <summary>⭐ The steps COMPOUND, in order: the reduction takes its cut of the figure the execute
    /// bonus produced, not of the original. Applying the two independently and summing gives 1300, which
    /// is what a rewrite that treats them as parallel modifiers would return.</summary>
    [Fact]
    public void TheStepsCompoundInOrder()
    {
        var outcome = SkillBlastCascade.Apply(1000, new SkillBlastInputs {
            Special = SkillSpecialArguments.From((SkillSpecial.TargetHpDownDmgUpRate, 1000),
                                                 (SkillSpecial.DmgDownRate, 200)),
            TargetMaxHp = 5000, TargetHp = 2500, WaveCount = 1 });

        // 1000 -> 1500 (execute) -> 1500 - 20% of 1500 = 1200.
        // Independently: +500 and -200 off the original would be 1300.
        outcome.Damage.ShouldBe(1200);
    }
}

/// <summary>`sdi_SetArgument` (0x00584800) — the jump table that turns `ActiveSkill.shn`'s
/// `SpecialIndex`/`SpecialValue` pairs into a `SkillDataIndex`'s `EnumStruct` slots.</summary>
public class SkillSpecialArgumentsTests
{
    [Fact]
    public void AnAbsentArgumentIsNullRatherThanZero()
        => SkillSpecialArguments.None[SkillSpecial.DmgDownRate].ShouldBeNull();

    /// <summary>Zero is a real, stored value — and distinguishable from absent, which is the whole reason
    /// this returns a nullable.</summary>
    [Fact]
    public void ZeroIsAStoredValue()
        => SkillSpecialArguments.From((SkillSpecial.DmgDownRate, 0))[SkillSpecial.DmgDownRate]
                                .ShouldBe(0);

    /// <summary>⭐ Ten indices have no case body in the jump table, so a skill carrying one has nothing
    /// marked present — writing the value anyway would invent state the server does not have.</summary>
    [Theory]
    [InlineData(SkillSpecial.Hide)]
    [InlineData(SkillSpecial.Silience)]
    [InlineData(SkillSpecial.Mesmerize)]
    [InlineData(SkillSpecial.Summon)]
    [InlineData(SkillSpecial.Metamorphosis)]
    [InlineData(SkillSpecial.DispelBuff)]
    [InlineData(SkillSpecial.HpRate)]
    [InlineData(SkillSpecial.MoveChr)]
    [InlineData(SkillSpecial.HideChrStart)]
    [InlineData(SkillSpecial.HideChrEnd)]
    public void TheIndicesTheJumpTableIgnoresStoreNothing(SkillSpecial index)
    {
        SkillSpecialArguments.Stores(index).ShouldBeFalse();
        SkillSpecialArguments.From((index, 1234))[index].ShouldBeNull();
    }

    /// <summary>`SS_NONE` is rejected by the range check itself: the table subtracts one and compares
    /// UNSIGNED, so index 0 wraps to 0xFFFFFFFF and falls out.</summary>
    [Fact]
    public void NoneIsOutOfRange()
        => SkillSpecialArguments.Stores(SkillSpecial.None).ShouldBeFalse();

    /// <summary>⭐ `SS_DASH` writes the SAME slot as `SS_WARPING`, so the pair aliases and the later one
    /// wins — which is why the store is keyed by slot and not by index.</summary>
    [Fact]
    public void DashAliasesWarping()
    {
        var a = SkillSpecialArguments.From((SkillSpecial.Warping, 10), (SkillSpecial.Dash, 20));

        a[SkillSpecial.Warping].ShouldBe(20);
        a[SkillSpecial.Dash].ShouldBe(20);
    }

    /// <summary>The three the damage cascade reads all store, which is the claim that matters for
    /// <see cref="SkillBlastCascade"/>.</summary>
    [Theory]
    [InlineData(SkillSpecial.UndeadToDmg)]
    [InlineData(SkillSpecial.DmgDownRate)]
    [InlineData(SkillSpecial.MaxDmgDownRate)]
    [InlineData(SkillSpecial.TargetHpDownDmgUpRate)]
    public void TheDamageArgumentsStore(SkillSpecial index)
        => SkillSpecialArguments.Stores(index).ShouldBeTrue();
}

/// <summary>The cascade's three specials against the real `ActiveSkill.shn`, so the model is exercised by
/// the game's own data rather than only by invented numbers.</summary>
public class SkillSpecialDataTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "ActiveSkill.shn")) ? root : null;
    }

    private static IReadOnlyDictionary<int, SkillSpecialArguments> Load(string shine)
    {
        var table = ShnFile.Load(Path.Combine(shine, "ActiveSkill.shn"));
        var byId = new Dictionary<int, SkillSpecialArguments>();
        foreach (var r in table.Rows) byId[ShnFile.Int(r, "ID")] = SkillSpecialArguments.FromRow(r, ShnFile.Int);
        return byId;
    }

    /// <summary>⭐ `LightningWave01` is why `sbe_nLightningWaveCnt` has that name. It carries
    /// <see cref="SkillSpecial.DmgDownRate"/> 100 capped at <see cref="SkillSpecial.MaxDmgDownRate"/> 900,
    /// and the blast's wave counter multiplies the first — so each bounce of the chain loses another 10%
    /// of its damage, and the cap stops it at 90% off however far the chain runs.</summary>
    [SkippableFact]
    public void LightningWaveLosesTenPercentPerBounceAndIsCappedAtNinety()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var special = Load(shine!)[7335];

        special[SkillSpecial.DmgDownRate].ShouldBe(100);
        special[SkillSpecial.MaxDmgDownRate].ShouldBe(900);

        int At(int wave) => SkillBlastCascade.Apply(1000, new SkillBlastInputs
        {
            Special = special, WaveCount = wave,
        }).Damage;

        At(1).ShouldBe(900);        // -10%
        At(5).ShouldBe(500);        // -50%
        At(9).ShouldBe(100);        // -90%, the cap
        At(20).ShouldBe(100);       // still -90%: the cap holds, it does not go negative
    }

    /// <summary>`HolySmite` is the anti-undead line, and its <see cref="SkillSpecial.UndeadToDmg"/> climbs
    /// with the rank — +15% at rank 1 to +50% at rank 8. Against anything else it is inert.</summary>
    [SkippableFact]
    public void HolySmiteHitsUndeadHarderWithEveryRank()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var byId = Load(shine!);

        byId[1900][SkillSpecial.UndeadToDmg].ShouldBe(150);
        byId[1907][SkillSpecial.UndeadToDmg].ShouldBe(500);

        SkillBlastCascade.Apply(1000, new SkillBlastInputs
        {
            Special = byId[1907], TargetType = MobType.Undead,
        }).Damage.ShouldBe(1500);

        SkillBlastCascade.Apply(1000, new SkillBlastInputs
        {
            Special = byId[1907], TargetType = MobType.Beast,
        }).Damage.ShouldBe(1000);
    }

    /// <summary>`Judge01` carries <see cref="SkillSpecial.TargetHpDownDmgUpRate"/> — an execute, doing up
    /// to double damage as the target's health runs out.</summary>
    [SkippableFact]
    public void JudgeScalesWithTheTargetsMissingHealth()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var special = Load(shine!)[7345];

        special[SkillSpecial.TargetHpDownDmgUpRate].ShouldBe(1000);

        int At(int hp) => SkillBlastCascade.Apply(1000, new SkillBlastInputs
        {
            Special = special, TargetMaxHp = 1000, TargetHp = hp,
        }).Damage;

        At(1000).ShouldBe(1000);
        At(500).ShouldBe(1500);
        At(1).ShouldBe(1999);
    }

    /// <summary>⚠️ THE COVERAGE FLOOR. These specials are rare — a handful of skills each — so a loader
    /// that silently read nothing would pass every test above by skipping. This asserts the file really
    /// does carry them, and it is what would catch a column rename or an off-by-one in the pair reader.
    ///
    /// <para>It also pins the discard count: 114 of the file's (index, value) pairs name an index
    /// `sdi_SetArgument` has no case for, and a loader that stored them anyway would invent state on 114
    /// skills.</para></summary>
    [SkippableFact]
    public void TheFileCarriesTheseSpecialsAndTheDiscardedOnes()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var table = ShnFile.Load(Path.Combine(shine!, "ActiveSkill.shn"));

        var all = table.Rows.Select(r => SkillSpecialArguments.FromRow(r, ShnFile.Int)).ToList();
        all.Count(a => a[SkillSpecial.TargetHpDownDmgUpRate] is not null).ShouldBe(6);
        all.Count(a => a[SkillSpecial.DmgDownRate] is not null).ShouldBe(8);
        all.Count(a => a[SkillSpecial.MaxDmgDownRate] is not null).ShouldBe(8);
        all.Count(a => a[SkillSpecial.UndeadToDmg] is not null).ShouldBe(13);

        var discarded = table.Rows.SelectMany(r => "ABCDE".Select(s => (SkillSpecial)ShnFile.Int(r, "SpecialIndex" + s)))
                                  .Count(i => i != SkillSpecial.None && !SkillSpecialArguments.Stores(i));
        discarded.ShouldBe(114);
    }
}
