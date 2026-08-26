using Fiesta.Emu.Zone.Data;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>`EnemyDetectType` selects a mob's whole targeting POLICY, not its detection radius.
///
/// <para>The values map one-for-one onto the RTTI hierarchy under `MobTargetSelector`, which is how they
/// were identified. Its practical consequence is that not every mob attacks on sight.</para></summary>
public class EnemyDetectTests
{
    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobInfoServer.shn")) ? root : null;
    }

    /// <summary>The starter mobs are PASSIVE — `ED_BOUT`, which is `MobTargetBout`: retaliate only.
    ///
    /// <para>220 mobs are in that set. A simulation that gives every mob the same selector has all of them
    /// attacking on sight, which is both wrong and much harder to survive.</para></summary>
    [SkippableFact]
    public void TheStarterMobsArePassive()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        foreach (var name in new[] { "Slime", "MushRoom", "Imp", "Crab" })
        {
            var s = box.ServerFor(name)!;
            s.DetectType.ShouldBe(EnemyDetect.Bout);
            s.IsAggressive.ShouldBeFalse($"{name} should not attack on sight");
        }
    }

    /// <summary>Shopkeepers are `ED_NOBRAIN` — they target nothing at all.</summary>
    [SkippableFact]
    public void ShopkeepersHaveNoTargetingPolicy()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        var smith = box.ServerFor("RouSmithJames")!;
        smith.DetectType.ShouldBe(EnemyDetect.NoBrain);
        smith.IsAggressive.ShouldBeFalse();
    }

    /// <summary>The bulk of mobs are ordinary aggressive ones, and every value in the file is a value the
    /// enum knows. An unrecognised one would mean the enum is wrong, not that the data is.</summary>
    [SkippableFact]
    public void EveryDetectTypeInTheFileIsKnown()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        foreach (var s in box.Server.Values)
            Enum.IsDefined(s.DetectType).ShouldBeTrue($"{s.InxName} has detect type {(int)s.DetectType}");

        box.Server.Values.Count(s => s.DetectType == EnemyDetect.Aggressive).ShouldBeGreaterThan(1000);
        box.Server.Values.Count(s => s.DetectType == EnemyDetect.Bout).ShouldBeGreaterThan(100);
    }

    /// <summary>Uruga's residents are the aggressive kind, which is why the map fights back.</summary>
    [SkippableFact]
    public void UrugaCombatantsAreAggressive()
    {
        Skip.If(Shine() is null, "server data not present; set SHINE_DATA");
        var box = MobDataBox.Load(Shine()!);

        foreach (var name in new[] { "Orc", "OrcHunter", "Pinky", "KingMushRoom" })
            box.ServerFor(name)!.IsAggressive.ShouldBeTrue();
    }
}
