using System.Text.Json;
using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>The simulation checked against A REAL SERVER'S numbers, from a packet capture.
///
/// <para>Every other test here compares the port to the binary it was read from. This compares it to what
/// the live server actually put on the wire, which is the only check that can catch a reading that was
/// faithful and still wrong.</para>
///
/// <para>Generate the fixture (the capture is BYO and never committed):</para>
/// <code>
/// python tools/pcap_combat_truth.py --pcap Z:/Damage.pcapng --port 9022 --stream 4 \
///        --start-packet 1736 --end-packet 4279 --out truth.json
/// FIESTA_COMBAT_TRUTH=...\truth.json dotnet test
/// </code>
///
/// <para>`Damage.pcapng` is the right capture for this: the operator drove it deliberately as a damage
/// experiment and narrated the configuration in chat ("Okay END was +3, now going to +20",
/// "Forward-facing only now"). The window above is one clean stretch with no stat change inside it — the
/// extractor warns when a window mixes configurations, and a mixed window makes every range meaningless.</para></summary>
public class PcapGroundTruthTests
{
    private sealed record Swing(int Conv, int Attacker, int Defender, int Damage, string[] Flags);

    private sealed class Truth
    {
        public required IReadOnlyDictionary<int, int> MobHandles { get; init; }
        public required IReadOnlyDictionary<int, int> PlayerLevels { get; init; }
        public required IReadOnlyDictionary<string, int> Stats { get; init; }
        public required IReadOnlyList<Swing> Swings { get; init; }

        /// <summary>Clean hits only: no flag set at all, and non-zero damage.
        ///
        /// <para>A flagged swing is a miss, a block, a critical or an immunity, and each follows a different
        /// rule — mixing them into one damage range makes the range meaningless. Zero damage is a real
        /// outcome rather than a decode failure, which is why it is dropped HERE and kept in the
        /// extractor: the miss rate is ground truth too, just not for this question.</para></summary>
        public IEnumerable<Swing> Clean => Swings.Where(s => s.Flags.Length == 0 && s.Damage > 0);
    }

    private static string? TruthPath()
    {
        var p = Environment.GetEnvironmentVariable("FIESTA_COMBAT_TRUTH");
        return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
    }

    private static string? Shine()
    {
        var root = Environment.GetEnvironmentVariable("SHINE_DATA") ?? @"Z:/ServerSource/9Data/Shine";
        return File.Exists(Path.Combine(root, "MobWeapon.shn")) ? root : null;
    }

    private static Truth Load(string path)
    {
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        var mobs = root.GetProperty("mobHandles").EnumerateObject()
            .ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32());
        var levels = root.GetProperty("players").EnumerateArray()
            .ToDictionary(p => p.GetProperty("handle").GetInt32(), p => p.GetProperty("level").GetInt32());
        var stats = root.TryGetProperty("playerStats", out var st)
            ? st.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32())
            : new Dictionary<string, int>();
        var swings = root.GetProperty("swings").EnumerateArray().Select(s => new Swing(
            s.GetProperty("conv").GetInt32(),
            s.GetProperty("attacker").GetInt32(),
            s.GetProperty("defender").GetInt32(),
            s.GetProperty("damage").GetInt32(),
            s.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray())).ToList();

        return new Truth { MobHandles = mobs, PlayerLevels = levels, Stats = stats, Swings = swings };
    }

    /// <summary>Clean damage in one direction, for one mob type, within ONE conversation.
    ///
    /// <para>⚠️ The conversation filter is not optional. A relog opens a new one, and the character's stats
    /// and gear can differ across it — merged, the observed range for a single mob widened from 55-118 to
    /// 55-213 and stopped meaning anything.</para></summary>
    private static List<int> Damages(Truth t, MobDataBox box, string mob, int conv, bool incoming)
    {
        var mobId = box.InfoFor(mob)!.Id;
        return t.Clean.Where(s =>
        {
            if (s.Conv != conv) return false;
            var (from, to) = incoming ? (s.Attacker, s.Defender) : (s.Defender, s.Attacker);
            return t.PlayerLevels.ContainsKey(to)
                   && t.MobHandles.TryGetValue(from, out var id) && id == mobId;
        }).Select(s => s.Damage).ToList();
    }

    [SkippableFact]
    public void TheCaptureDecodesIntoSwingsThatCanBeAttributed()
    {
        var path = TruthPath();
        Skip.If(path is null, "no capture fixture; see the class comment for how to make one");
        var t = Load(path!);

        t.Swings.Count.ShouldBeGreaterThan(100);
        t.Clean.Count().ShouldBeGreaterThan(50);
        t.PlayerLevels.Values.ShouldAllBe(l => l > 0, "the level comes from NC_BAT_TARGETINFO_CMD");
        t.Stats["DEF"].ShouldBeGreaterThan(0);
        t.Stats["DmgMin"].ShouldBeLessThanOrEqualTo(t.Stats["DmgMax"]);

        // Misses and blocks are present and NOT silently dropped by the extractor.
        t.Swings.ShouldContain(s => s.Flags.Contains("ismissed"));
    }

    /// <summary>A monster's damage against a player stays UNDER the ceiling we predict.
    ///
    /// <para>The ceiling is a maximum weapon roll through the core formula, times the largest
    /// `DamageByAngle` rate (1200, a hit from directly behind), times the EVP level-gap rate — which every
    /// row of that table says is 1000. Nothing legitimate can exceed it, so this is a real check even
    /// though it is one-sided.</para>
    ///
    /// <para>The FLOOR is deliberately not asserted. Observed damage runs about 30% below our predicted
    /// minimum, the same unexplained factor as the outgoing case below; asserting a floor known to be wrong
    /// would be asserting the bug.</para></summary>
    [SkippableFact]
    public void AMonsterNeverHitsHarderThanOurCeiling()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var t = Load(path!);
        var box = MobDataBox.Load(shine!);
        var gaps = LevelGapTable.Load(shine!);
        var angle = DamageByAngleTable.Load(Path.Combine(shine!, "World"));
        var playerLevel = t.PlayerLevels.Values.First();
        var checkedAny = false;

        foreach (var name in new[] { "Orc", "Pinky" })
        {
            var observed = Damages(t, box, name, conv: 0, incoming: true);
            if (observed.Count < 20) continue;
            checkedAny = true;

            var mob = MobCombatant.Build(box, name)!;
            var gap = gaps.Rate(CombatantKind.Monster, mob.Level, CombatantKind.Player, playerLevel);
            gap.ShouldBe(1000, "every DamageLvGapEVP row is 1000");

            var ceiling = DamageCalculator.CoreDamage(
                    DamageCalculator.MaxWeaponDamage(mob), t.Stats["DEF"], mob.Level)
                * angle.Rates.Max() / 1000.0 * gap / 1000.0;

            observed.Max().ShouldBeLessThanOrEqualTo((int)Math.Ceiling(ceiling),
                $"{name} hit harder than a maximum roll from directly behind allows");
        }

        checkedAny.ShouldBeTrue("the fixture carried no mob with enough clean hits to check");
    }

    /// <summary>⛔ KNOWN RED — the port under-predicts a player's damage by a consistent factor.
    ///
    /// <para>Measured on `Damage.pcapng`, conversation 0: a level 82 character with a displayed attack of
    /// 1709–1840 who changed neither gear nor stats inside the window.</para>
    ///
    /// <list type="table">
    ///   <item><term>Orc (lv 61, our AC 242)</term><description>observed 1128–1401, predicted 879–1136 — 6% inside</description></item>
    ///   <item><term>Pinky (lv 61, our AC 288)</term><description>observed 964–1174, predicted 739–954 — 0% inside</description></item>
    /// </list>
    ///
    /// <para><b>The shape of the error is the useful part.</b> Solving for the attack power that WOULD
    /// produce the observed minimum gives 2192 against the Orc and 2230 against the Pinky — the same number
    /// for two mobs with DIFFERENT armour. A defence error cannot do that; it would give two different
    /// answers. So the missing term is on the ATTACK side, worth roughly +30% over the client's displayed
    /// 1709, and it is neither the level gap (already applied at 1500) nor the angle (capped at 1200).</para>
    ///
    /// <para>Do not close this by scaling something until it fits. Close it by finding what the server's
    /// attack power reads that this port does not: the capture says what the answer is worth, not what it
    /// is.</para></summary>
    [SkippableFact]
    public void PlayerDamageMatchesTheCapture_KNOWN_RED()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var t = Load(path!);
        var box = MobDataBox.Load(shine!);
        var gaps = LevelGapTable.Load(shine!);
        var angle = DamageByAngleTable.Load(Path.Combine(shine!, "World"));
        var playerLevel = t.PlayerLevels.Values.First();

        var observed = Damages(t, box, "Orc", conv: 0, incoming: false);
        observed.Count.ShouldBeGreaterThan(20, "not enough clean hits to say anything");

        var mob = MobCombatant.Build(box, "Orc")!;
        var armour = DamageCalculator.ArmourClass(mob);
        var gap = gaps.Rate(CombatantKind.Player, playerLevel, CombatantKind.Monster, mob.Level);
        gap.ShouldBe(1500, "82 against 61 is a gap of -21, which lands on the -10 row");

        var floor = DamageCalculator.CoreDamage(t.Stats["DmgMin"], armour, playerLevel) * gap / 1000.0;
        var ceiling = DamageCalculator.CoreDamage(t.Stats["DmgMax"], armour, playerLevel)
                      * angle.Rates.Max() / 1000.0 * gap / 1000.0;

        var inside = observed.Count(d => d >= floor - 1 && d <= ceiling + 1);
        inside.ShouldBe(observed.Count,
            $"every clean hit should sit in [{floor:F0}, {ceiling:F0}]; {inside}/{observed.Count} do");
    }
}
