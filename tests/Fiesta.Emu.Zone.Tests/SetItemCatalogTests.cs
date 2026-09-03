using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Skill;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Matched-equipment SET bonuses, from `ItemInfo.SetItemIndex` through to the staging buffer the
/// damage cascade multiplies by.</summary>
public class SetItemCatalogTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "SetItemEffect.shn")) ? root : null;
    }

    // ---- the membership rule, in isolation ------------------------------------------------------------

    private static readonly Dictionary<string, int> Names = new()
    {
        ["IceBolt01"] = 1, ["IceBolt02"] = 2, ["IceBolt03"] = 3,
        ["PowerNorthBreeze01"] = 40,
    };

    private static readonly Dictionary<string, IReadOnlySet<int>> Classifiers = new()
    {
        ["SBBF"] = new HashSet<int> { 90, 91 },
    };

    [Fact]
    public void AGroupIsANameAndARankRange()
        => SetItemCatalog.SkillsOf("IceBolt", "01", "99", Names, Classifiers)
                         .ShouldBe(new HashSet<int> { 1, 2, 3 }, ignoreOrder: true);

    /// <summary>`From` and `To` really bound it — the group is not just "every rank of this name".</summary>
    [Fact]
    public void TheRankRangeBounds()
        => SetItemCatalog.SkillsOf("IceBolt", "02", "02", Names, Classifiers)
                         .ShouldBe(new HashSet<int> { 2 });

    /// <summary>A group that already carries its rank names exactly one skill. One stock row does this
    /// (`PowerNorthBreeze01`), and without this case it resolves to nothing.</summary>
    [Fact]
    public void AGroupThatIsAlreadyAFullNameNamesOneSkill()
        => SetItemCatalog.SkillsOf("PowerNorthBreeze01", "01", "99", Names, Classifiers)
                         .ShouldBe(new HashSet<int> { 40 });

    [Fact]
    public void AFourLetterGroupIsASkillClassifier()
        => SetItemCatalog.SkillsOf("SBBF", "01", "99", Names, Classifiers)
                         .ShouldBe(new HashSet<int> { 90, 91 }, ignoreOrder: true);

    /// <summary>⚠️ A GROUP THAT RESOLVES TO NOTHING APPLIES TO NOTHING — it does not fall through to
    /// "every skill". Null and empty are opposite instructions to `siel_AppendEffect`, and letting an
    /// unresolvable name become null would hand the bonus to every skill in the game off a typo. One stock
    /// row lands here; see <see cref="TheStockDataResolvesEveryEffectButTheOneDanglingReference"/>.</summary>
    [Fact]
    public void AnUnresolvableGroupAppliesToNoSkill()
    {
        SetItemCatalog.SkillsOf("NoSuchThing", "01", "99", Names, Classifiers).ShouldBeEmpty();

        new SetItemEffectDefinition("x", SetIndex.SET_DAMEGERATE, 1200, new HashSet<int>())
            .AppliesTo(1).ShouldBeFalse();
    }

    /// <summary>...whereas a row with NO group at all has a null `skilllist`, and `siel_AppendEffect`
    /// skips the check entirely. That one really does apply to every skill.</summary>
    [Fact]
    public void AnEffectWithNoGroupAppliesToEverySkill()
        => new SetItemEffectDefinition("x", SetIndex.SET_DAMEGERATE, 1200, null)
               .AppliesTo(12345).ShouldBeTrue();

    // ---- the real data --------------------------------------------------------------------------------

    /// <summary>⭐ THE EVIDENCE FOR THE MEMBERSHIP RULE. Nothing in `Zone.exe` that this project can find
    /// builds `EffectDescription.skilllist`, so the three resolution paths are derived from the data. This
    /// is what makes that derivation falsifiable: all 235 rows carry a group, and the rule resolves 234 of
    /// them. A fourth mechanism, or a wrong one, shows up here as a bigger unresolved count.
    ///
    /// <para>The one that does not resolve is `BrilliantlySP`, whose group `ShiningPurge` matches no skill
    /// name, no rank range and no classifier in this data — a dangling reference in the game files.</para></summary>
    [SkippableFact]
    public void TheStockDataResolvesEveryEffectButTheOneDanglingReference()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var catalog = SetItemCatalog.Load(shine!);

        catalog.Effects.Count.ShouldBe(235);
        catalog.Effects.Values.Count(e => e.Skills is null)
               .ShouldBe(0, "every stock row names a SkillGroup, so none should get the null 'any skill' list");

        var unresolved = catalog.Effects.Values.Where(e => e.Skills is { Count: 0 }).ToList();
        unresolved.Select(e => e.Effect).ShouldBe(["BrilliantlySP"]);
    }

    /// <summary>The effect the operator's own capture would have needed: `IceDameg` is a two-piece bonus
    /// giving +20% damage, and it applies to the `IceBolt` line and nothing else.</summary>
    [SkippableFact]
    public void IceDamegIsATwentyPercentBonusOnTheIceBoltLineAlone()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var catalog = SetItemCatalog.Load(shine!);

        var effect = catalog.Effects["IceDameg"];
        effect.Index.ShouldBe(SetIndex.SET_DAMEGERATE);
        effect.Argument.ShouldBe(1200);
        effect.Skills.ShouldNotBeNull();

        var iceBolt = ShnFile.Load(Path.Combine(shine!, "ActiveSkill.shn")).Rows
                             .Where(r => ShnFile.Str(r, "InxName").StartsWith("IceBolt"))
                             .Select(r => ShnFile.Int(r, "ID")).ToList();
        iceBolt.ShouldNotBeEmpty();
        foreach (var id in iceBolt) effect.AppliesTo(id).ShouldBeTrue();

        // FireBolt08 is a different line and must not benefit.
        effect.AppliesTo(6047).ShouldBeFalse();
    }

    /// <summary>⭐ END TO END, and the number the damage cascade actually consumes: wearing the two pieces
    /// stages `SET_DAMEGERATE` at 1200 for an `IceBolt` cast and leaves it at the neutral 1000 for
    /// anything else. That is the whole point of the machinery — the buffer is rebuilt PER CAST because
    /// the membership check is against the skill being cast.</summary>
    [SkippableFact]
    public void WearingTheSetStagesTheBonusForItsOwnSkillAndNotForOthers()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var catalog = SetItemCatalog.Load(shine!);

        var iceSet = catalog.SetOfItem.Where(kv => kv.Value == "IceSet").Select(kv => kv.Key).ToList();
        Skip.If(iceSet.Count < 2, "IceSet has fewer than two items in this data");
        var worn = iceSet.Take(2).ToList();

        var iceBoltId = ShnFile.Load(Path.Combine(shine!, "ActiveSkill.shn")).Rows
                               .First(r => ShnFile.Str(r, "InxName") == "IceBolt01");

        catalog.Stage(worn, ShnFile.Int(iceBoltId, "ID"))[SetIndex.SET_DAMEGERATE].ShouldBe(1200);
        catalog.Stage(worn, 6047)[SetIndex.SET_DAMEGERATE].ShouldBe(SetItemSkillEffect.Neutral);
    }

    /// <summary>One piece of a two-piece set grants nothing: `sic_SetItemDefine` only reaches a tier when
    /// the worn count is at least that tier's piece count.</summary>
    [SkippableFact]
    public void OnePieceOfATwoPieceSetGrantsNothing()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var catalog = SetItemCatalog.Load(shine!);

        var iceSet = catalog.SetOfItem.Where(kv => kv.Value == "IceSet").Select(kv => kv.Key).ToList();
        Skip.If(iceSet.Count < 1, "IceSet is not in this data");

        catalog.ActiveEffects(catalog.PiecesWorn(iceSet.Take(1))).ShouldBeEmpty();
    }

    /// <summary>⚠️ THE CAPTURE'S OWN CHARACTERS WEAR NO SET, which is what makes the bucket tests' neutral
    /// 1000 correct rather than convenient. Asserted here so that a future fixture with set gear fails
    /// loudly instead of being silently under-predicted.</summary>
    [SkippableFact]
    public void TheMagesEquipmentIsInNoSet()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var catalog = SetItemCatalog.Load(shine!);

        // NatureBoots / NatureHat / NaturePants / NatureShirt / FairyWand — the level-60 Enchanter of
        // MageDamageLvl60.pcapng.
        int[] equipment = [1520, 1521, 1522, 1523, 1804];

        catalog.PiecesWorn(equipment).ShouldBeEmpty();
        catalog.Stage(equipment, 6047)[SetIndex.SET_DAMEGERATE].ShouldBe(SetItemSkillEffect.Neutral);
    }

    /// <summary>A coverage floor on the catalog itself: sets, tiers and set-bearing items all have to be
    /// there, or every test above passes by finding nothing.</summary>
    [SkippableFact]
    public void TheCatalogIsNotEmpty()
    {
        var shine = Shine();
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        var catalog = SetItemCatalog.Load(shine!);

        catalog.Effects.Count.ShouldBeGreaterThan(200);
        catalog.SetOfItem.Count.ShouldBeGreaterThan(100);
        catalog.Effects.Values.Count(e => e.Index == SetIndex.SET_DAMEGERATE).ShouldBe(56);
    }
}
