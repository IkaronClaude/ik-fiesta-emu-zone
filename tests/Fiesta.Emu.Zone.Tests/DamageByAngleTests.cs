using Fiesta.Emu.Zone.Combat;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`DamageByAngle` — the last table in the damage path that had never been loaded.</summary>
public class DamageByAngleTests
{
    private static string? World()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        var world = Path.Combine(root, "World");
        return File.Exists(Path.Combine(world, "DamageByAngle.txt")) ? world : null;
    }

    /// <summary>The anchors land where `sr_degree2sr` puts them — a truncating halve, so 45° is index 22
    /// and 135° is 67. Getting that wrong shifts the whole ramp by a unit.</summary>
    [Fact]
    public void DegreesBecomeDirectionUnitsByTruncatingHalves()
    {
        DamageByAngleTable.DegreesToUnits(0).ShouldBe(0);
        DamageByAngleTable.DegreesToUnits(45).ShouldBe(22, "22.5 truncates");
        DamageByAngleTable.DegreesToUnits(90).ShouldBe(45);
        DamageByAngleTable.DegreesToUnits(135).ShouldBe(67, "67.5 truncates");
        DamageByAngleTable.DegreesToUnits(180).ShouldBe(90);
        DamageByAngleTable.DegreesToUnits(-90).ShouldBe(135, "it normalises into [0,360) first");
        DamageByAngleTable.DegreesToUnits(720).ShouldBe(0);
    }

    /// <summary>The fill is NOT a linear interpolation. Each slot recomputes its step from the value just
    /// written, and the division truncates, so the ramp hugs the lower anchor and accelerates into the
    /// upper one. A straight lerp between 1000@0 and 1200@90 would put index 45 at 1100; this does not.</summary>
    [Fact]
    public void TheFillIsRepeatedAveragingNotInterpolation()
    {
        var t = DamageByAngleTable.FromAnchors([(0, 1000), (180, 1200)]);

        t[0].ShouldBe(1000);
        t[90].ShouldBe(1200);

        var lerp = 1000 + (1200 - 1000) * 45 / 90;
        t[45].ShouldNotBe(lerp, "repeated truncating averages do not land on the straight line");
        t[45].ShouldBeInRange(1000, 1200);

        // Monotone, and it never overshoots its anchors.
        for (var i = 1; i <= 90; i++)
            t[i].ShouldBeGreaterThanOrEqualTo(t[i - 1]);
    }

    /// <summary>Folding, from `operator[]`: absolute value, then anything past 90 folded back, then 1000 if
    /// it still cannot be placed.</summary>
    [Fact]
    public void TheIndexFoldsTheWayTheOriginalDoes()
    {
        var t = DamageByAngleTable.FromAnchors([(0, 1000), (180, 1200)]);

        t[-90].ShouldBe(t[90], "the sign of the difference does not matter");
        t[0].ShouldBe(1000);
        t[91].ShouldBe(t[89], "91 units is 2 degrees past straight behind, so it comes back down");
        t[180].ShouldBe(t[0], "a full half-turn folds to head-on");
    }

    /// <summary>The real file, and the bound that matters when reading a capture: a hit from behind is worth
    /// at most <b>1.2x</b>. A damage spread wider than that is not explained by facing.</summary>
    [SkippableFact]
    public void TheRealTableTopsOutAtTwentyPercent()
    {
        Skip.If(World() is null, "server data not present; set SHINE_DATA");
        var chr = DamageByAngleTable.Load(World()!);

        chr[0].ShouldBe(1000, "head-on is unmodified");
        chr[DamageByAngleTable.MaxIndex].ShouldBe(1200, "directly behind is +20%, and that is the maximum");
        chr[DamageByAngleTable.DegreesToUnits(90)].ShouldBe(1100, "from the side");
        chr.Rates.Max().ShouldBe(1200);
        chr.Rates.Min().ShouldBe(1000);
    }

    /// <summary>Both tables are in the one file, so the loader has to attribute records to the `#Table`
    /// above them. Concatenating them would give 12 anchors and a wrong ramp.</summary>
    [SkippableFact]
    public void TheTwoTablesInTheFileAreReadSeparately()
    {
        Skip.If(World() is null, "server data not present; set SHINE_DATA");
        var chr = DamageByAngleTable.Load(World()!, "DamageByAngle_Chr");
        var mob = DamageByAngleTable.Load(World()!, "DamageByAngle_Mob");

        chr.Rates.ShouldBe(mob.Rates, "in this data they are identical -- but they are separate tables");
        Should.Throw<InvalidDataException>(() => DamageByAngleTable.Load(World()!, "NoSuchTable"));
    }
}
