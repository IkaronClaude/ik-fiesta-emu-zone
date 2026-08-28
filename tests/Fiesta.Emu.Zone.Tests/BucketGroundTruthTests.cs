using System.Text.Json;
using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Every swing in a capture, sorted into buckets of identical combat state, predicted by this
/// port and checked against what the server actually did.
///
/// <para>This is a different instrument from <see cref="PcapGroundTruthTests"/> and strictly better where
/// it applies. That one slices a capture by the operator's chat annotations, which bounds a window by
/// TYPING rather than by state, and rebuilds the character by inverting displayed totals. This one takes
/// the state from the packets — <c>tools/damage_buckets.py</c> — and takes the character's attack and
/// defence from the server's own numbers.</para>
///
/// <para><b>No container is reconstructed for the player at all.</b>
/// `ShinePlayer::so_mobile_NotifyParameterChange` builds the 0x1035 packet as
/// <c>ftol(roe_Xxx(&amp;arg)) + freeStat</c> per field, so the wire already carries `roe_MinWC`,
/// `roe_MaxWC` and `roe_AC` — the exact three numbers <see cref="DamageCalculator.CoreDamage"/> needs.
/// Backing the free-stat term out is the whole of the "reconstruction", and everything else that used to
/// be guessed (mastery rates, layer splits, enhancement placement) simply does not arise.</para>
///
/// <para>Generate the fixture (BYO, never committed, and it pairs with its own capture):</para>
/// <code>
/// python tools/damage_buckets.py --pcap Z:/FighterDamageLvl60.pcapng --port 9022 --out buckets.json
/// FIESTA_BUCKETS=...\buckets.json dotnet test
/// </code></summary>
public class BucketGroundTruthTests
{
    private sealed record Bucket(string Side, int Mob, int Level, IReadOnlyList<int> SelfAbstates,
                                 IReadOnlyList<int> EnemyAbstates, IReadOnlyDictionary<string, int> Params,
                                 int N, int Min, int Max);

    private sealed record Fixture(IReadOnlyList<Bucket> Buckets, IReadOnlyDictionary<string, int> FreeStat,
                                  int ChrClass);

    /// <summary>`NC_CHAR_CHANGEPARAMCHANGE_CMD` parameter ids for the three fields that matter.
    ///
    /// <para>Identified from the capture rather than assumed: ids 6 and 7 move together on every weapon
    /// change and 8 jumps when a shield goes on. That agrees with `analyse_damage.py`'s independent map
    /// (6=DmgMin, 7=DmgMax, 8=DEF), which is why they are used here — two derivations, one answer.</para>
    ///
    /// <para>⚠️ These are NOT `Parameter::Stat` slot indices. The random-option codes on an item are (see
    /// `tools/capture_state.py`), and confusing the two would land Dex where DEF belongs.</para></summary>
    private const string DmgMin = "6", DmgMax = "7", Def = "8";

    private static string? FixturePath()
    {
        var p = Environment.GetEnvironmentVariable("FIESTA_BUCKETS");
        return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
    }

    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobWeapon.shn")) ? root : null;
    }

    private static Fixture Load(string path)
    {
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var buckets = root.GetProperty("buckets").EnumerateArray().Select(b => new Bucket(
            b.GetProperty("side").GetString()!,
            b.GetProperty("mob").GetInt32(),
            b.GetProperty("level").GetInt32(),
            b.GetProperty("selfAbstates").EnumerateArray().Select(x => x.GetInt32()).ToList(),
            b.GetProperty("enemyAbstates").EnumerateArray().Select(x => x.GetInt32()).ToList(),
            b.GetProperty("params").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
            b.GetProperty("n").GetInt32(),
            b.GetProperty("min").GetInt32(),
            b.GetProperty("max").GetInt32())).ToList();

        return new Fixture(buckets,
            root.GetProperty("freeStat").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
            root.GetProperty("chrclass").GetInt32());
    }

    /// <summary>The free-stat tables, read out of a live zone at 0x0DA50BC4 (Str) and 0x0DA50BD0 (Con):
    /// Str is 1:1 and Con is <c>ceil(n/2)</c>. A MOB gets <c>table[0]</c> unconditionally, so it
    /// contributes nothing on either side.</summary>
    private static int FreeStatStr(int points) => points;

    private static int FreeStatCon(int points) => (points + 1) / 2;

    /// <summary>`JobChangeDmgUp` for the captured class at a level, read through the game's own tables —
    /// class id off the wire, name out of `ClassName.shn`, rate out of `Param&lt;Class&gt;Server.txt`.</summary>
    private static int? JobChangeRate(Fixture f, string shine, int level)
    {
        var names = ShnFile.Load(Path.Combine(shine, "ClassName.shn"));
        var row = names.Rows.FirstOrDefault(r => ShnFile.Int(r, "ClassID") == f.ChrClass);
        if (row is null) return null;

        var file = Path.Combine(shine, "World", $"Param{ShnFile.Str(row, "acEngName")}Server.txt");
        return File.Exists(file) ? ClassParamTable.Load(file).At(level)?.JobChangeDmgUp : null;
    }

    private readonly record struct Band(double Floor, double Ceiling);

    private static Band? Predict(Fixture f, Bucket b, MobDataBox box, LevelGapTable gaps,
                                 IReadOnlyDictionary<int, string> mobNames, int? jobChange)
    {
        if (!mobNames.TryGetValue(b.Mob, out var name)) return null;
        var mob = MobCombatant.Build(box, name);
        if (mob is null) return null;

        var freeStr = FreeStatStr(f.FreeStat.GetValueOrDefault("Strength"));
        var freeCon = FreeStatCon(f.FreeStat.GetValueOrDefault("Constitute"));

        double lowAttack, highAttack, defence;
        int attackerLevel, flat, gap;

        if (b.Side == "OUT")
        {
            // The wire's Dmg pair IS roe_MinWC/roe_MaxWC plus the free-stat term the packet builder adds.
            if (!b.Params.TryGetValue(DmgMin, out var dmin) || !b.Params.TryGetValue(DmgMax, out var dmax))
                return null;
            lowAttack = dmin - freeStr;
            highAttack = dmax - freeStr;
            defence = DamageCalculator.ArmourClass(mob);
            attackerLevel = b.Level;
            // `roe_Damage`'s per-rule override: attacker's Str free stat minus the defender's Con free
            // stat, and a mob's free-stat accessors return table[0] = 0.
            flat = freeStr;
            gap = gaps.Rate(CombatantKind.Player, b.Level, CombatantKind.Monster, mob.Level);
        }
        else
        {
            if (!b.Params.TryGetValue(Def, out var def)) return null;
            lowAttack = DamageCalculator.MinWeaponDamage(mob);
            highAttack = DamageCalculator.MaxWeaponDamage(mob);
            defence = def - freeCon;
            attackerLevel = mob.Level;
            flat = -freeCon;
            gap = gaps.Rate(CombatantKind.Monster, mob.Level, CombatantKind.Player, b.Level);
            // A MOB attacker never reaches so_ply_JobChangeDamageUp: ShineObject's slot returns the
            // damage unchanged. Not a modelling choice -- the server does not run it.
            jobChange = null;
        }

        if (defence <= 0) return null;
        var job = (jobChange ?? 1000) / 1000.0;

        return new Band(
            (DamageCalculator.CoreDamage(lowAttack, defence, attackerLevel) + flat) * job * gap / 1000.0,
            (DamageCalculator.CoreDamage(highAttack, defence, attackerLevel) + flat) * job * gap / 1000.0);
    }

    /// <summary>Predict every bucket and report how many of the server's own hits land inside the band.
    ///
    /// <para>Asserted as a floor so a regression fails here; the printed table is the actual content.</para></summary>
    [SkippableFact]
    public void EveryBucketIsPredictedByThePort()
    {
        var path = FixturePath();
        var shine = Shine();
        Skip.If(path is null, "no bucket fixture; see the class comment for how to make one");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var f = Load(path!);
        var box = MobDataBox.Load(shine!);
        var gaps = LevelGapTable.Load(shine!);
        var mobNames = box.Info.Where(kv => kv.Value.Id > 0)
                              .GroupBy(kv => kv.Value.Id)
                              .ToDictionary(g => g.Key, g => g.First().Key);

        var jobByLevel = f.Buckets.Select(b => b.Level).Distinct()
                                  .ToDictionary(l => l, l => JobChangeRate(f, shine!, l));

        int hits = 0, inside = 0, predicted = 0, unpredictable = 0;
        var report = new List<string>();
        foreach (var b in f.Buckets.OrderBy(b => b.Side).ThenBy(b => b.Mob).ThenBy(b => b.Level)
                           .ThenByDescending(b => b.N))
        {
            var band = Predict(f, b, box, gaps, mobNames, jobByLevel[b.Level]);
            if (band is not { } p)
            {
                unpredictable += b.N;
                continue;
            }
            predicted++;
            hits += b.N;
            var ok = b.Min >= Math.Floor(p.Floor) - 1 && b.Max <= Math.Ceiling(p.Ceiling) + 1;
            if (ok) inside += b.N;
            if (b.N >= 5)
                report.Add($"  {b.Side,-4} mob {b.Mob,-4} lv{b.Level} n={b.N,-3} observed {b.Min}..{b.Max}"
                           + $"  predicted {p.Floor:F0}..{p.Ceiling:F0}"
                           + $"  floor x{b.Min / p.Floor:F2} ceiling x{b.Max / p.Ceiling:F2}"
                           + (ok ? "  OK" : ""));
        }

        var summary = $"{inside}/{hits} hits inside the band across {predicted} predicted buckets"
                      + $" ({unpredictable} hits in buckets that could not be predicted)";
        (hits > 0).ShouldBeTrue("the fixture should contain predictable buckets");
        ((double)inside / hits).ShouldBeGreaterThanOrEqualTo(0.50,
            summary + Environment.NewLine + string.Join(Environment.NewLine, report));
    }
}
