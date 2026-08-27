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
/// the live server actually put on the wire — the only check that can catch a reading that was faithful
/// and still wrong.</para>
///
/// <para>Generate the fixture (the capture is BYO and never committed):</para>
/// <code>
/// python tools/pcap_combat_truth.py --pcap Z:/Damage.pcapng --port 9022 --stream 4 \
///        --from-chat "At 20, let" --to-chat "END to 50" \
///        --start-packet 1736 --end-packet 4279 --out truth.json
/// FIESTA_COMBAT_TRUTH=...\truth.json dotnet test
/// </code>
///
/// <para>`Damage.pcapng` is the right capture: the operator drove it as a deliberate damage experiment and
/// narrated the configuration in chat, so the window is delimited by the operator's own annotations rather
/// than by a guess.</para>
///
/// <para><b>What this instrument can and cannot settle.</b> It can show the port models the right system on
/// real inputs. It CANNOT show bit-exactness, because the wire does not carry the character's full
/// `Parameter::Container` — mastery rates, passive-skill plus values and abstate rates never appear in a
/// stream of cluster totals. Bit-exactness is what <c>tools/oracle_free_stat_damage.py</c> is for: it runs
/// the real function under emulation and diffs. Use each for what it is good for.</para></summary>
public class PcapGroundTruthTests
{
    private sealed record Swing(int Conv, int Attacker, int Defender, int Damage, int FlagWord, string[] Flags);

    private sealed class Truth
    {
        public required IReadOnlyDictionary<int, int> MobHandles { get; init; }
        public required IReadOnlyDictionary<int, int> PlayerLevels { get; init; }
        public required IReadOnlyDictionary<string, int> Stats { get; init; }
        public required IReadOnlyList<Swing> Swings { get; init; }

        /// <summary>Clean hits only: the flag word is exactly ZERO, and the damage is non-zero.
        ///
        /// <para>⚠️ <b>Tested on the raw word, never on the decoded name list being empty.</b> The names
        /// cover 11 of 16 bits, so a swing flagged 0x2800 decodes to no names at all — and in this capture
        /// those are among the hits that exceed what a maximum roll can produce. An unknown bit is not the
        /// absence of a flag.</para>
        ///
        /// <para>Zero damage is a real outcome rather than a decode failure, which is why it is dropped
        /// here and kept in the extractor: the miss rate is ground truth too, just not for this
        /// question.</para></summary>
        public IEnumerable<Swing> Clean => Swings.Where(s => s.FlagWord == 0 && s.Damage > 0);
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
            s.GetProperty("flagWord").GetInt32(),
            s.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray())).ToList();

        return new Truth { MobHandles = mobs, PlayerLevels = levels, Stats = stats, Swings = swings };
    }

    /// <summary>Clean damage in one direction, for one mob type, within ONE conversation.
    ///
    /// <para>⚠️ The conversation filter is not optional. A relog opens a new one and the character's
    /// configuration can differ across it — merged, one mob's observed range widened from 55-118 to 55-213
    /// and stopped meaning anything.</para></summary>
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

    /// <summary>Rebuild the captured character from the wire.
    ///
    /// <para>⚠️ <b>The wire carries CLUSTER SLOT TOTALS, not accessor outputs.</b>
    /// `Parameter::Cluster::c_compare` builds `NC_CHAR_BASEPARAMCHANGE` by walking the cluster slot by
    /// slot, so the displayed "Dmg 1709-1840" is the WCmin/WCmax SLOT — `roe_MinWC` adds the Str chain on
    /// top of it. Feeding the displayed number straight into <c>CoreDamage</c> as attack power
    /// under-predicts by about 30% and looks exactly like a missing formula term. It produced a wrong
    /// "the engine is 30% short" finding from this very harness before it was caught.</para>
    ///
    /// <para>An empty-modifier container with those totals as Base reproduces the same totals. What it
    /// cannot reproduce is anything the wire does not carry, which is the honest limit of the
    /// instrument.</para></summary>
    private static Combatant RebuildPlayer(Truth t)
    {
        var p = new ParameterContainer();
        p.Base[Stat.Str] = t.Stats["STR"];
        p.Base[Stat.Con] = t.Stats["END"];
        p.Base[Stat.Dex] = t.Stats["DEX"];
        p.Base[Stat.Int] = t.Stats["INT"];
        p.Base[Stat.Men] = t.Stats["SPR"];
        p.Base[Stat.WCmin] = t.Stats["DmgMin"];
        p.Base[Stat.WCmax] = t.Stats["DmgMax"];
        p.Base[Stat.AC] = t.Stats["DEF"];
        p.Base[Stat.MR] = t.Stats["MDef"];
        p.Base[Stat.TH] = t.Stats["Aim"];
        p.Base[Stat.TB] = t.Stats["Evasion"];
        return new Combatant(t.PlayerLevels.Values.First(), p);
    }

    private readonly record struct Band(double Floor, double Ceiling);

    private static Band Predict(ICombatant attacker, ICombatant defender, LevelGapTable gaps,
                                DamageByAngleTable angle, CombatantKind attackerKind)
    {
        var defenderKind = attackerKind == CombatantKind.Player ? CombatantKind.Monster : CombatantKind.Player;
        var gap = gaps.Rate(attackerKind, attacker.Level, defenderKind, defender.Level);
        var lo = DamageCalculator.AttackPower(attacker, 0);
        var hi = DamageCalculator.AttackPower(attacker, 1000);
        var def = DamageCalculator.DefendPower(defender);

        return new Band(
            DamageCalculator.CoreDamage(lo, def, attacker.Level) * gap / 1000.0,
            DamageCalculator.CoreDamage(hi, def, attacker.Level) * angle.Rates.Max() / 1000.0 * gap / 1000.0);
    }

    private sealed record Case(string Mob, bool Incoming, List<int> Observed, Band Predicted)
    {
        public string Label => (Incoming ? "IN " : "OUT ") + Mob;
    }

    private static List<Case> Measure(Truth t, string shine)
    {
        var box = MobDataBox.Load(shine);
        var gaps = LevelGapTable.Load(shine);
        var angle = DamageByAngleTable.Load(Path.Combine(shine, "World"));
        var player = RebuildPlayer(t);
        var cases = new List<Case>();

        foreach (var name in new[] { "Orc", "Pinky" })
        {
            var mob = MobCombatant.Build(box, name)!;
            foreach (var incoming in new[] { true, false })
            {
                var observed = Damages(t, box, name, conv: 0, incoming: incoming);
                if (observed.Count < 5) continue;
                var band = incoming
                    ? Predict(mob, player, gaps, angle, CombatantKind.Monster)
                    : Predict(player, mob, gaps, angle, CombatantKind.Player);
                cases.Add(new Case(name, incoming, observed, band));
            }
        }
        return cases;
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

    /// <summary>⭐ THE FLOOR HOLDS, EXACTLY — every clean hit in the capture, both directions and both mob
    /// types, is at least what a minimum roll through our formula produces.
    ///
    /// <para>This is the strongest statement the capture can support, and it has no free parameters: a
    /// minimum weapon roll, the level-gap rate read from `DamageLvGapPVE`/`EVP`, and nothing else. No
    /// angle (1000 head-on), no critical, no tolerance.</para></summary>
    [SkippableFact]
    public void EveryCleanHitIsAtLeastOurPredictedMinimum()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var cases = Measure(Load(path!), shine!);
        cases.Count.ShouldBeGreaterThanOrEqualTo(4, "the fixture should cover both mobs both ways");

        foreach (var c in cases)
            c.Observed.Min().ShouldBeGreaterThanOrEqualTo((int)Math.Floor(c.Predicted.Floor),
                $"{c.Label}: a hit landed below a minimum roll");
    }

    /// <summary>The ceiling holds to within a MEASURED margin, asserted as a maximum so any regression
    /// widens it and fails here.
    ///
    /// <para>Measured on `Damage.pcapng`, one chat-delimited window, level 82 character:</para>
    ///
    /// <list type="table">
    ///   <item><term>OUT Pinky</term><description>observed 964-1174 against 899-1147 — ceiling +2.4%</description></item>
    ///   <item><term>OUT Orc</term><description>observed 1128-1401 against 1070-1365 — +2.6%</description></item>
    ///   <item><term>IN Pinky</term><description>observed 50-72 against 46-68 — +5.9%</description></item>
    ///   <item><term>IN Orc</term><description>observed 71-107 against 64-96 — +11.5%</description></item>
    /// </list>
    ///
    /// <para><b>Excluded as the cause</b>, each by reading and porting rather than by argument: the
    /// level-gap rate, the angle multiplier (capped at 1200), the per-rule `roe_Damage` free-stat term
    /// (oracle-verified, and it moves the incoming case the WRONG way), the HP-down passives, conversation
    /// mixing, window mixing, and the flag-decode hole that let unnamed-bit swings count as clean.</para>
    ///
    /// <para><b>What remains</b> is the part of the container the wire does not carry. See the class
    /// comment.</para></summary>
    [SkippableFact]
    public void TheCeilingHoldsToWithinTheMeasuredMargin()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        const double allowed = 0.12;
        foreach (var c in Measure(Load(path!), shine!))
        {
            var over = c.Observed.Max() / c.Predicted.Ceiling - 1.0;
            over.ShouldBeLessThanOrEqualTo(allowed,
                $"{c.Label}: observed {c.Observed.Max()} against a ceiling of {c.Predicted.Ceiling:F0}");
        }
    }

    /// <summary>⛔ KNOWN RED — the ceiling should need NO margin at all.
    ///
    /// <para>The test above pins the residual so it cannot grow. This one states that it should not exist:
    /// a maximum roll from directly behind is the hardest a clean swing can legitimately land, so nothing
    /// clean should exceed it. It stays red rather than the margin being widened until it passes.</para>
    ///
    /// <para>Closing it needs a capture where the character's mastery and passive rates are known, not more
    /// analysis of this one.</para></summary>
    [SkippableFact]
    public void TheCeilingIsExact_KNOWN_RED()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        foreach (var c in Measure(Load(path!), shine!))
            c.Observed.Max().ShouldBeLessThanOrEqualTo((int)Math.Ceiling(c.Predicted.Ceiling), c.Label);
    }
}
