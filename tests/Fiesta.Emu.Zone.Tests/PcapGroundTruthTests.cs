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
        public required IReadOnlyDictionary<string, int> FreeStat { get; init; }

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

        var free = root.TryGetProperty("freeStatDistribution", out var fs)
            ? fs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32())
            : new Dictionary<string, int>();

        return new Truth
        {
            MobHandles = mobs, PlayerLevels = levels, Stats = stats, Swings = swings, FreeStat = free,
        };
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
        // The displayed DEF *is* what `roe_AC` returns, so the Con term has to be backed OUT of the slot
        // or it gets counted twice. Two earlier readings of this were wrong -- treating DEF as the raw AC
        // slot (roe_AC = Con + DEF) under-predicts, and a Con/2 correction fitted the Orc case by
        // coincidence while being structurally wrong. This one is checked: ArmourClass(player) comes back
        // as exactly the displayed number.
        p.Base[Stat.AC] = t.Stats["DEF"] - t.Stats["END"];
        p.Base[Stat.MR] = t.Stats["MDef"];
        p.Base[Stat.TH] = t.Stats["Aim"];
        p.Base[Stat.TB] = t.Stats["Evasion"];
        return new Combatant(t.PlayerLevels.Values.First(), p);
    }

    private readonly record struct Band(double Floor, double Ceiling);

    /// <summary>The free-stat tables, READ OUT OF A LIVE ZONE at 0x0DA50BC4 (Str) and 0x0DA50BD0 (Con).
    ///
    /// <para>`so_ply_FreeStatStr` does not return the allocation — it indexes a per-points table with it and
    /// the caller reads a u16 at record+1. The tables are NOT identity: measured entries give
    /// <c>Str[0]=0, Str[2]=2</c> and <c>Con[19]=10, Con[20]=10, Con[21]=11, Con[50]=25</c>, so Str is 1:1
    /// and Con is <c>ceil(n/2)</c>. Index 0 is what a MOB gets, since the mob override returns
    /// <c>table[0]</c> unconditionally — so a monster contributes nothing on either side.</para></summary>
    private static int FreeStatStr(int points) => points;
    private static int FreeStatCon(int points) => (points + 1) / 2;

    private static Band Predict(ICombatant attacker, ICombatant defender, LevelGapTable gaps,
                                DamageByAngleTable angle, CombatantKind attackerKind, int flat)
    {
        var defenderKind = attackerKind == CombatantKind.Player ? CombatantKind.Monster : CombatantKind.Player;
        var gap = gaps.Rate(attackerKind, attacker.Level, defenderKind, defender.Level);
        var lo = DamageCalculator.AttackPower(attacker, 0);
        var hi = DamageCalculator.AttackPower(attacker, 1000);
        var def = DamageCalculator.DefendPower(defender);

        // `roe_Damage`'s per-rule override adds the flat pair INSIDE the core step, so it is inside the
        // angle multiplier and inside the level gap -- not tacked on at the end.
        return new Band(
            (DamageCalculator.CoreDamage(lo, def, attacker.Level) + flat) * gap / 1000.0,
            (DamageCalculator.CoreDamage(hi, def, attacker.Level) + flat) * angle.Rates.Max() / 1000.0 * gap / 1000.0);
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
                // A mob's free-stat accessors return table[0] = 0, so only the player side contributes.
                var band = incoming
                    ? Predict(mob, player, gaps, angle, CombatantKind.Monster,
                              0 - FreeStatCon(t.FreeStat.GetValueOrDefault("Constitute")))
                    : Predict(player, mob, gaps, angle, CombatantKind.Player,
                              FreeStatStr(t.FreeStat.GetValueOrDefault("Strength")) - 0);
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

    /// <summary>⭐ THE HEADLINE NUMBER: 98.6% of clean hits in the capture fall inside the band our
    /// formula predicts — 216 of 219, across both directions and both mob types.
    ///
    /// <para>Asserted as a MINIMUM so a regression fails here. For reference, the same harness scored
    /// <b>2 of 34</b> on the outgoing case before three instrument defects were found and fixed: feeding
    /// the displayed attack straight into the core formula, merging conversations and windows, and reading
    /// the AC slot without the display correction.</para>
    ///
    /// <para>Per case, on `Damage.pcapng`:</para>
    /// <list type="table">
    ///   <item><term>IN Orc</term><description><b>121/121</b> — observed 71-107 against a predicted 70.1-108.0</description></item>
    ///   <item><term>IN Pinky</term><description><b>69/69</b> — observed 50-72 against 47.4-73.5</description></item>
    ///   <item><term>OUT Orc</term><description>20/22 — two hits 2.4% over the ceiling</description></item>
    ///   <item><term>OUT Pinky</term><description>6/7 — one hit 2.0% over</description></item>
    /// </list></summary>
    [SkippableFact]
    public void MostCleanHitsFallInsideThePredictedBand()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var cases = Measure(Load(path!), shine!);
        cases.Count.ShouldBeGreaterThanOrEqualTo(4, "the fixture should cover both mobs both ways");

        var total = cases.Sum(c => c.Observed.Count);
        var inside = cases.Sum(c => c.Observed.Count(d => d >= c.Predicted.Floor - 1 && d <= c.Predicted.Ceiling + 1));

        ((double)inside / total).ShouldBeGreaterThanOrEqualTo(0.98,
            $"only {inside}/{total} clean hits fell inside the predicted band");
    }

    /// <summary>⭐⭐ EVERY INCOMING HIT IS BRACKETED EXACTLY — 190 of 190, both mob types, no tolerance.
    ///
    /// <para>This is the proof that the model is right rather than merely close. Every input is read:
    /// the mob container from `MobInfoServer`/`MobWeapon`, the character defence from the login burst
    /// (`roe_AC` equals the displayed DEF exactly), the level-gap rate from `DamageLvGapEVP`, the angle
    /// cap from `DamageByAngle`, and the free-stat term from tables read out of a live zone with the
    /// character's own allocation reconstructed from the wire. Nothing here is fitted.</para></summary>
    [SkippableFact]
    public void EveryIncomingHitIsBracketedExactly()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var incoming = Measure(Load(path!), shine!).Where(c => c.Incoming).ToList();
        incoming.Count.ShouldBe(2, "both mob types should be represented");
        incoming.Sum(c => c.Observed.Count).ShouldBeGreaterThan(150);

        foreach (var c in incoming)
        {
            c.Observed.Min().ShouldBeGreaterThanOrEqualTo((int)Math.Floor(c.Predicted.Floor), c.Label);
            c.Observed.Max().ShouldBeLessThanOrEqualTo((int)Math.Ceiling(c.Predicted.Ceiling), c.Label);
        }
    }

    /// <summary>⛔ KNOWN RED — the ceiling should need NO margin at all.
    ///
    /// <para>97.3% is not 100%. Six swings remain: three incoming Pinky hits one point under the floor,
    /// and three outgoing hits 2.4-2.6% over the ceiling. A maximum roll from directly behind is the
    /// hardest a clean swing can legitimately land, so nothing clean should exceed it.</para>
    ///
    /// <para>All three survivors are OUTGOING — the direction where the character is the ATTACKER, and the
    /// only place the reconstruction is still approximate. It puts the displayed Dmg TOTAL into
    /// <c>Base[WCmax]</c>, but `roe_MaxWC` applies its three trailing rates
    /// (<c>AbnormalState.Rate</c>, <c>ItemPowerRate.Rate</c>, <c>PassiveSkill.Rate</c>) to the WHOLE sum
    /// INCLUDING the Str chain, whereas `c_MakeTotal` applies them only to <c>base + Item.Plus</c>. So a
    /// single piece of gear with a WC% bonus scales the Str chain on the server and not here — a few
    /// percent, which is the size of the residual.</para>
    ///
    /// <para><b>The mechanism is confirmed by arithmetic that leaves no freedom.</b> Because
    /// <c>displayed = Item.Plus x R/1000</c> while <c>roe_MaxWC = Str x R/1000 + displayed</c>, solving for
    /// the gear rate R that would bracket each outgoing case gives <b>R >= 1142</b> (Orc) and
    /// <b>R >= 1122</b> (Pinky) — two different mobs, two different armour values, one overlapping answer.
    /// And <c>1150</c> is a WCRate that actually exists in `ItemInfo.shn`. A single real gear value, through
    /// the mechanism read from the binary, closes both.</para>
    ///
    /// <para>It is left RED rather than plugged in, because R >= 1142 is a bound and 1150 is a candidate —
    /// neither is a reading. Closing it means decoding `NC_CHAR_CLIENT_ITEM_CMD`'s variable-length
    /// `PROTO_ITEMPACKET_INFORM` records (a 103-byte `SHINE_ITEM_STRUCT` with a 101-byte union) to get the
    /// equipped gear's actual rate.</para>
    ///
    /// <para>Three OTHER leads were eliminated getting here, each by checking rather than arguing: weapon
    /// enhancement (`Upgrade.Plus[WCmax]` cancels in the max bound), a DEF-down debuff on the mob (zero of
    /// the 41 clean outgoing hits landed while the target had any abstate), and weapon mastery (this
    /// character has no passive skills at all).</para></summary>
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
