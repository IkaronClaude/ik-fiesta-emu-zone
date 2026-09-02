using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`WeaponTitleData.shn` loaded from the server's own table, and driven through the staging.</summary>
public class WeaponTitleCatalogTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "WeaponTitleData.shn")) ? root : null;
    }

    /// <summary>The table loads and is keyed per monster — a license is per MOB, which is the whole
    /// reason the damage half is conditional.</summary>
    [SkippableFact]
    public void TheTableLoadsAndIsKeyedPerMob()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var catalog = WeaponTitleCatalog.Load(Shine()!);

        catalog.MobCount.ShouldBeGreaterThan(100, "1971 rows spread across many monsters");
    }

    /// <summary>⭐ Levels ascend with `MobKillCount`, and `EarnedLevel` gives the last COMPLETED level —
    /// a partially-progressed license grants what you have, not what you are working towards.</summary>
    [SkippableFact]
    public void EarnedLevelIsTheLastCompletedOne()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var catalog = WeaponTitleCatalog.Load(Shine()!);

        // Any mob with several levels will do; take the first with more than two.
        var mobId = Enumerable.Range(0, 4000).FirstOrDefault(id => catalog.LevelsFor(id).Count > 2);
        Skip.If(mobId == 0, "no multi-level licence in this data");

        var levels = catalog.LevelsFor(mobId);
        levels.Select(l => l.Level).ShouldBeInOrder();
        levels.Select(l => l.MobKillCount).ShouldBeInOrder();

        catalog.EarnedLevel(mobId, 0)?.Level.ShouldBe(levels[0].Level, "the first tier costs 0 kills, or none is earned");
        catalog.EarnedLevel(mobId, uint.MaxValue)!.Level.ShouldBe(levels[^1].Level, "everything earned");
    }

    /// <summary>A mob with no license yields nothing, which is the case `Stage` takes as null.</summary>
    [SkippableFact]
    public void AMobWithNoLicenceYieldsNothing()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var catalog = WeaponTitleCatalog.Load(Shine()!);

        catalog.LevelsFor(65000).ShouldBeEmpty();
        catalog.EarnedLevel(65000, 999999).ShouldBeNull();
    }

    /// <summary>⭐ End to end on REAL data: load a license, stage it, and the container carries both halves
    /// — the conditional damage rate on the weapon bounds and the unconditional crit.</summary>
    [SkippableFact]
    public void ARealLicenceStagesIntoTheContainer()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var catalog = WeaponTitleCatalog.Load(Shine()!);

        // A row that actually carries a damage bonus.
        var row = Enumerable.Range(0, 4000)
            .SelectMany(catalog.LevelsFor)
            .FirstOrDefault(l => l.MinAdd > 0 && l.MaxAdd > 0);
        Skip.If(row is null, "no licence with a damage bonus in this data");

        var c = new ParameterContainer();
        WeaponTitleLicense.Stage(c, row);
        var rate = c.Rate(StatModifier.WeaponTitle);

        rate[Stat.WCmin].ShouldBe(row!.MinAdd);
        rate[Stat.WCmax].ShouldBe(row.MaxAdd);

        // And staging a mob we have no licence for takes it all back out.
        WeaponTitleLicense.Stage(c, null);
        c.Rate(StatModifier.WeaponTitle)[Stat.WCmin].ShouldBe(ParameterCluster.RateIdentity);
    }

    /// <summary>Reference 0 means "no option in this slot" — `sp_WeaponTitleOption` returns immediately on
    /// it — so the loader drops those rather than reporting three options for every row.</summary>
    [SkippableFact]
    public void EmptyOptionSlotsAreNotLoaded()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var catalog = WeaponTitleCatalog.Load(Shine()!);

        var all = Enumerable.Range(0, 4000).SelectMany(catalog.LevelsFor).ToList();
        all.ShouldNotBeEmpty();
        all.ShouldAllBe(l => l.Options == null || l.Options.All(o => o.Reference != 0));
    }
}
