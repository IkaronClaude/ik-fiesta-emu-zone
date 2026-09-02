using Fiesta.Emu.Zone.Data;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`CharacterTitleStateServer.shn` — the table that proves titles have combat effects.</summary>
public class CharacterTitleCatalogTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "CharacterTitleStateServer.shn")) ? root : null;
    }

    /// <summary>⭐ Titles DO carry stat effects, and this is the file that says so.
    ///
    /// <para>An earlier pass here read `CharacterTitleData.shn` (definitions: names, thresholds, fame),
    /// found no stat columns and concluded titles had no combat effect. Wrong — the stat side is this
    /// SECOND table. The test exists so that conclusion cannot be reached again from the same partial
    /// reading.</para></summary>
    [SkippableFact]
    public void TitlesCarryAbstatesWithStrengths()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var catalog = CharacterTitleCatalog.Load(Shine()!);

        catalog.Count.ShouldBeGreaterThan(0, "titles grant abstates -- this is not an empty mechanism");
        catalog.All.ShouldAllBe(s => s.StateName.Length > 1);
        catalog.All.ShouldContain(s => s.Strength > 0, "at least one tier has a real rank");
    }

    /// <summary>Tiers are keyed by (Type, TitleLV) and a tier with no effect is absent rather than
    /// present-and-blank, so the lookup answers null for it.</summary>
    [SkippableFact]
    public void ATierWithoutAnEffectIsAbsent()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var catalog = CharacterTitleCatalog.Load(Shine()!);

        catalog.For(9999, 99).ShouldBeNull();

        var any = catalog.All.First();
        catalog.For(any.TitleType, any.TitleLevel).ShouldBe(any);
    }

    /// <summary>The abstate is named, not indexed — resolved through `as_FromName`, the same way the
    /// passive-skill path resolves `PS_AbStateInx`. A row naming an unknown abstate applies nothing.</summary>
    [SkippableFact]
    public void TheAbstateIsIdentifiedByName()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var catalog = CharacterTitleCatalog.Load(Shine()!);

        catalog.All.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.StateName));
    }
}
