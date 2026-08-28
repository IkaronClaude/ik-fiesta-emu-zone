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

        /// <summary>The character's `chrclass`, off `PROTO_AVATAR_SHAPE_INFO`'s packed first byte.
        ///
        /// <para>Needed for one thing and it is not cosmetic: `so_ply_JobChangeDamageUp` multiplies every
        /// hit ON A MONSTER by this class's `JobChangeDmgUp` at this level. Class 8 at level 82 is a
        /// Paladin on 1280 — a 28% multiplier that no stat on the wire hints at.</para></summary>
        public int? ChrClass { get; init; }

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
            ChrClass = root.TryGetProperty("chrclass", out var cc) && cc.ValueKind == JsonValueKind.Number
                ? cc.GetInt32()
                : null,
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
    /// <para><b>Every displayed stat is an ACCESSOR OUTPUT plus a free-stat term, and that is read out of
    /// the binary rather than assumed.</b> `ShinePlayer::so_mobile_NotifyParameterChange` (0x00503C10) is
    /// the function that emits opcode <c>0x1035</c> — the packet these numbers come off. It builds an
    /// `EngageArgument` as <c>(this, this, 0, 0, 0)</c> purely to feed the accessors, and then for each
    /// field does exactly:</para>
    ///
    /// <code>
    ///   call [vtbl + freeStatSlot]        ; so_ply_FreeStatStr / Con / Dex / Int / Men
    ///   movzx esi, word ptr [eax + 1]     ; the free-stat contribution
    ///   call roe_Xxx(&amp;arg)  ;  call __ftol2_sse  ;  add eax, esi  ;  mov [this + 0x1Bxx], eax
    /// </code>
    ///
    /// <list type="table">
    ///   <item><term>+0x1BC8 DmgMin</term><description>`roe_MinWC` + FreeStatStr@+1</description></item>
    ///   <item><term>+0x1BCC DmgMax</term><description>`roe_MaxWC` + FreeStatStr@+1</description></item>
    ///   <item><term>+0x1BD0 DEF</term><description>`roe_AC` + FreeStatCon@+1</description></item>
    ///   <item><term>+0x1BD4 Aim</term><description>`roe_TH` + FreeStatDex@+1</description></item>
    ///   <item><term>+0x1BD8 Evasion</term><description>`roe_TB` + FreeStatDex@+3</description></item>
    ///   <item><term>+0x1BE4 MDef</term><description>`roe_MR` + FreeStatMen@+1</description></item>
    /// </list>
    ///
    /// <para>So rebuilding a container from these numbers means backing out the GOVERNING STAT CHAIN and
    /// the FREE-STAT TERM for each — `roe_MinWC` already adds the Str chain, `roe_AC` already adds the Con
    /// chain, and the packet already added the free stat on top.</para>
    ///
    /// <para><b>This harness previously did that for DEF and not for Dmg</b>, and the asymmetry hid a bug
    /// rather than causing a small error: leaving the Str chain inside the Dmg slot inflates the attack by
    /// 2080/1709 = 1.217, which stood in for the job-change multiplier of 1.28 the port was not applying
    /// at all. Two errors nearly cancelling. Neither the "displayed Dmg is the cluster SLOT" reading nor
    /// the gear-`WCRate`-of-1150 hypothesis it produced survives this function.</para>
    ///
    /// <para>What the wire still cannot carry: mastery rates, passive-skill plus values and abstate rates
    /// never appear in a stream of accessor outputs. That is the honest limit of the instrument, and it is
    /// why <c>tools/oracle_*.py</c> exists.</para></summary>
    private static Combatant RebuildPlayer(Truth t)
    {
        var p = new ParameterContainer();
        p.Base[Stat.Str] = t.Stats["STR"];
        p.Base[Stat.Con] = t.Stats["END"];
        p.Base[Stat.Dex] = t.Stats["DEX"];
        p.Base[Stat.Int] = t.Stats["INT"];
        p.Base[Stat.Men] = t.Stats["SPR"];

        // Every displayed stat is `ftol(roe_Xxx) + freeStat`, so BOTH have to come back out of the slot.
        var freeStr = FreeStatStr(t.FreeStat.GetValueOrDefault("Strength"));
        var freeCon = FreeStatCon(t.FreeStat.GetValueOrDefault("Constitute"));

        p.Base[Stat.WCmin] = t.Stats["DmgMin"] - freeStr - t.Stats["STR"];
        p.Base[Stat.WCmax] = t.Stats["DmgMax"] - freeStr - t.Stats["STR"];
        p.Base[Stat.AC] = t.Stats["DEF"] - freeCon - t.Stats["END"];

        // TH/TB/MR carry their own free-stat terms (Dex at +1 and +3, Men at +1) which are NOT backed out
        // here: their tables have not been read, and nothing in this test uses hit, block or magic
        // resistance. Left as they are, and said so, rather than subtracted with a guessed table.
        p.Base[Stat.MR] = t.Stats["MDef"] - t.Stats["SPR"];
        p.Base[Stat.TH] = t.Stats["Aim"] - t.Stats["DEX"];
        p.Base[Stat.TB] = t.Stats["Evasion"] - t.Stats["DEX"];
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

    /// <summary>The angle multiplier in force for the swings this test looks at: <b>none</b>. Settled two
    /// independent ways, and the stronger one is the operator's own annotation.
    ///
    /// <para><b>1. The capture says so.</b> The operator narrates the experiment in chat, and
    /// <c>"Forward-facing only now"</c> falls INSIDE the analysed window — immediately after
    /// <c>"At 20, let's go"</c>, the marker that opens it. `DamageByAngle` is indexed by
    /// <c>defenderFacing - directionToAttacker</c>, so a frontal engagement sits at index 0, and index 0
    /// is <b>1000 in every version of the table</b>, stock or flattened. Facing the target removes the
    /// term whatever the server had loaded. The angle was engineered out of this experiment on
    /// purpose.</para>
    ///
    /// <para><b>2. The deployed file says so too.</b> `tools/capture_state.py` reads it out of the running
    /// zone pods: flat 1000 in both `DamageByAngle_Chr` and `DamageByAngle_Mob`, since its mtime of
    /// 2026-07-30 10:51 — 67 minutes before the capture — with a `.orig-damagebyangle` sibling from
    /// 2026-03-18 holding the stock curve that tops out at 1200. On its own this proves only what is on
    /// disk TODAY, because the table is expanded at zone STARTUP and nobody recorded whether the zone
    /// restarted in those 67 minutes. It does not have to carry the argument alone.</para>
    ///
    /// <para>⚠️ <b>Not <see cref="DamageByAngleTable"/> loaded from `Z:/ServerSource`.</b> That copy
    /// expands to 1000-1200 and is not what runs. Using it once scored 216/219 and the number was
    /// worthless — the 1.2x came from a file the server does not use and happened to cover the real,
    /// still-unexplained spread.</para>
    ///
    /// <para>⚠️ <b>And do not set it to 1200 because it makes the suite green.</b> It does: with the
    /// job-change multiplier and the corrected reconstruction in place, 1200 gives <b>219 of 219</b> with
    /// no margin anywhere. That configuration was reached and rejected on 2026-08-28. A residual that
    /// happens to be bounded by a table's own maximum is not evidence that the table produced it, and here
    /// the operator had removed the angle twice over. Reaching for it is the same mistake as the 216/219,
    /// one level deeper — which is precisely how it would get made again.</para>
    ///
    /// <para>So the ~1.16x by which all four cases exceed the ceiling is a real unmodelled mechanism.
    /// See the test below and OPEN_QUESTIONS.md §2.</para></summary>
    private const int DeployedAngleMax = 1000;

    /// <summary>`JobChangeDmgUp` for the captured character's own class at its own level, READ from the
    /// capture and the game's tables — the class id off `PROTO_AVATAR_SHAPE_INFO`, the name out of
    /// `ClassName.shn`, the rate out of `Param&lt;Class&gt;Server.txt`.
    ///
    /// <para>Nothing here is written into the test. Hard-coding 1280 would make this instrument agree with
    /// the capture by construction, which is the one thing it exists not to do.</para>
    ///
    /// <para>Null when the capture did not carry a class, or the class has no table — in which case the
    /// prediction goes back to what it was, rather than assuming 1000 and calling that a reading.</para></summary>
    private static int? JobChangeRate(Truth t, string shine)
    {
        if (t.ChrClass is not { } id) return null;
        var names = ShnFile.Load(Path.Combine(shine, "ClassName.shn"));
        var row = names.Rows.FirstOrDefault(r => ShnFile.Int(r, "ClassID") == id);
        if (row is null) return null;

        var file = Path.Combine(shine, "World", $"Param{ShnFile.Str(row, "acEngName")}Server.txt");
        if (!File.Exists(file)) return null;
        return ClassParamTable.Load(file).At(t.PlayerLevels.Values.First())?.JobChangeDmgUp;
    }

    private static Band Predict(ICombatant attacker, ICombatant defender, LevelGapTable gaps,
                                DamageByAngleTable angle, CombatantKind attackerKind, int flat,
                                int? jobChangePermille)
    {
        var defenderKind = attackerKind == CombatantKind.Player ? CombatantKind.Monster : CombatantKind.Player;
        var gap = gaps.Rate(attackerKind, attacker.Level, defenderKind, defender.Level);
        var lo = DamageCalculator.AttackPower(attacker, 0);
        var hi = DamageCalculator.AttackPower(attacker, 1000);
        var def = DamageCalculator.DefendPower(defender);

        // `so_ply_JobChangeDamageUp` sits between the angle multiplier and the level gap, and it runs
        // ONLY when a player hits a monster -- the mob's own slot is `return dmg`. The +1 is the server's
        // 0-or-1 draw from `rndbox` slot 2, taken at the CEILING and not at the floor because that is the
        // side each bound needs to stay a bound.
        var jobLo = jobChangePermille is { } j ? j / 1000.0 : 1.0;
        var jobHi = jobChangePermille is { } k ? (k + 1) / 1000.0 : 1.0;

        // `roe_Damage`'s per-rule override adds the flat pair INSIDE the core step, so it is inside the
        // angle multiplier and inside the level gap -- not tacked on at the end.
        return new Band(
            (DamageCalculator.CoreDamage(lo, def, attacker.Level) + flat) * jobLo * gap / 1000.0,
            (DamageCalculator.CoreDamage(hi, def, attacker.Level) + flat)
                * DeployedAngleMax / 1000.0 * jobHi * gap / 1000.0);
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
        var jobChange = JobChangeRate(t, shine);
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
                              0 - FreeStatCon(t.FreeStat.GetValueOrDefault("Constitute")),
                              // A MOB attacker never reaches the hook: `ShineObject`'s slot returns the
                              // damage unchanged. Not "we chose not to apply it" -- the server does not.
                              jobChangePermille: null)
                    : Predict(player, mob, gaps, angle, CombatantKind.Player,
                              FreeStatStr(t.FreeStat.GetValueOrDefault("Strength")) - 0,
                              jobChange);
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

    /// <summary>The measured agreement against the capture, using the angle the SERVER applies: <b>127 of
    /// 219 (58%)</b>. Asserted as a minimum so a regression fails here.
    ///
    /// <para>Every FLOOR holds — no observed hit lands below a minimum roll, in any of the four cases, and
    /// the floor is angle-independent so that result is solid. Every CEILING is about 20% low.</para>
    ///
    /// <para>The gap is a continuous per-swing multiplier of up to <b>1.24x</b> that applies to a MOB
    /// attacker as well as to the character, so it is not gear, not item actions, and not anything
    /// character-side. The observed outgoing damage spans 1.24x on a smooth distribution while the weapon
    /// range spans only 1.06x — the attack bounds themselves vary per swing by something not yet
    /// found.</para>

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

        ((double)inside / total).ShouldBeGreaterThanOrEqualTo(0.55,
            $"only {inside}/{total} clean hits fell inside the predicted band");
    }

    /// <summary>Every FLOOR holds: no clean incoming hit lands below a minimum roll.
    ///
    /// <para>This is the half of the model that IS verified, and it is angle-independent — a minimum roll,
    /// the EVP level-gap rate (1000 in every row), and the free-stat term, with the mob container from
    /// game data and the character defence from the login burst. Nothing fitted.</para>
    ///
    /// <para>⚠️ This test previously asserted the CEILING too and passed 190/190. That was an artefact of
    /// using the reference tree's angle table instead of the deployed one — see
    /// <c>DeployedAngleMax</c>.</para></summary>
    [SkippableFact]
    public void EveryIncomingHitIsAtLeastOurFloor()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        var incoming = Measure(Load(path!), shine!).Where(c => c.Incoming).ToList();
        incoming.Count.ShouldBe(2, "both mob types should be represented");
        incoming.Sum(c => c.Observed.Count).ShouldBeGreaterThan(150);

        foreach (var c in incoming)
            c.Observed.Min().ShouldBeGreaterThanOrEqualTo((int)Math.Floor(c.Predicted.Floor), c.Label);
    }

    /// <summary>⛔ KNOWN RED — no clean hit should land above what a maximum roll can produce, and with
    /// the angle table this file assumes, all four cases do.
    ///
    /// <code>
    ///   IN  Orc    n=121  observed   71..107   predicted   70..91    ceiling x1.176
    ///   OUT Orc    n=22   observed 1128..1401  predicted 1127..1216  ceiling x1.152
    ///   IN  Pinky  n=69   observed   50..72    predicted   47..62    ceiling x1.161
    ///   OUT Pinky  n=7    observed  964..1174  predicted  948..1022  ceiling x1.149
    /// </code>
    ///
    /// <para><b>The residual is now ONE factor, not two.</b> That is the result worth reading here. It
    /// used to be asymmetric — incoming 1.19x and 1.16x over, outgoing 0.96x and 0.96x under — which meant
    /// two unrelated faults. With the job-change multiplier applied (<c>roe_CalcDamage+0x5B2</c>, verified
    /// under emulation) and the character rebuilt the way the packet builder actually fills the wire (see
    /// <see cref="RebuildPlayer"/>, read out of `so_mobile_NotifyParameterChange`), all four cases sit
    /// over by <b>1.149 to 1.176</b> — the same factor for a MOB attacker and for the character.</para>
    ///
    /// <para><b>A single mechanism, applying to both attackers, and NOT the angle table.</b> The obvious
    /// candidate was the stock `DamageByAngle` curve, which tops out at exactly 1200 — and it is ruled
    /// out. The operator's chat inside this very window reads <c>"Forward-facing only now"</c>, and a
    /// frontal engagement indexes the table at 0, which is 1000 in every version of it. Setting
    /// <see cref="DeployedAngleMax"/> to 1200 does turn this test green at 219/219, and that is a trap:
    /// see the warning there.</para>
    ///
    /// <para><b>So this is an unmodelled mechanism, and the capture narrows it hard.</b> It scales a MOB
    /// attacker and the character by the same ~1.16x; it is not gear, not item actions and not anything
    /// one-sided. The operator was chasing the same thing while capturing — the chat after the window
    /// reads <c>"seems like END has not applied cleanly"</c>, <c>"No, seems like END has no clean flat
    /// effect here"</c>, then <c>"Okay unequipping some armor"</c> and <c>"Unequipping some more (no end
    /// change this time)"</c>. That is a second observation of something wrong around Constitution and
    /// defence, made independently and from play. `OPEN_QUESTIONS.md` §2 carries it.</para>
    ///
        /// <para><b>Eliminated by measurement, not by argument</b> — do not re-run these:</para>
    ///
    /// <list type="number">
    ///   <item><b>The accessors.</b> `tools/oracle_accessors.py` runs the real `roe_MinWC` / `roe_MaxWC` /
    ///         `roe_AC` on these containers and agrees with this port to the digit.</item>
    ///   <item><b>Per-instance mob levels.</b> Every `NC_BAT_TARGETINFO_CMD` reports level 61 for all 20
    ///         Orc handles and all 4 Pinky handles, so the level-gap rate is constant across the pooled
    ///         swings and cannot be widening anything.</item>
    ///   <item><b>The normal attack's <c>damagerate</c> and <c>nBMPDamageRate</c>.</b>
    ///         `smo_SwingDamage+0x132` and <c>+0x13F</c> write the literal 0x3E8 into both.</item>
    ///   <item><b>Weapon mastery</b> (all three `NC_CHAR_CLIENT_PASSIVE_CMD` are `00 00`), <b>a DEF-down
    ///         debuff</b> (no clean outgoing hit landed while the target had any abstate),
    ///         <b>enhancement</b> (+10 is worth +1197 and sits in BOTH displayed bounds, exactly as
    ///         `roe_*WC` reads it from the WCmax slot — it cancels), <b>the equipment layer split</b>,
    ///         <b>random options</b> (all four are primaries, already inside the displayed totals), and
    ///         <b>the HP-down passive</b> (r = +0.26, and the wrong sign).</item>
    ///   <item><b>The gear `WCRate` of 1150.</b> That hypothesis existed only to explain a 5% outgoing
    ///         gap which was the difference between the Str chain (1.217x) standing in for the job-change
    ///         multiplier (1.28x). Both are now read rather than fitted, and it is not needed.</item>
    /// </list>
    ///
    /// <para><b>Still unmodelled, and each is a real hook in this pipeline</b> — see OPEN_QUESTIONS.md §3:
    /// `so_ply_DecreaseDmgPassiveSkill`, the `ChargedEffectContainer` force rates, the critical damage
    /// bonus at container+0xCDC, the `EventRun_IncDmgRate` item actions, and the abstate damage callbacks.
    /// None is reached by a clean unbuffed swing.</para></summary>
    [SkippableFact]
    public void TheCeilingIsExact_KNOWN_RED()
    {
        var path = TruthPath();
        var shine = Shine();
        Skip.If(path is null, "no capture fixture");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");

        // Reported for EVERY case, not failed on the first. Which cases are over and by how much is the
        // whole content of this test while it is red; stopping at the first one hides the other three.
        var over = Measure(Load(path!), shine!)
            .Select(c => (c.Label, Max: c.Observed.Max(), Ceiling: (int)Math.Ceiling(c.Predicted.Ceiling),
                          Floor: (int)Math.Floor(c.Predicted.Floor), Min: c.Observed.Min(), c.Observed.Count))
            .ToList();

        var report = string.Join(Environment.NewLine, over.Select(c =>
            $"  {c.Label,-10} n={c.Count,-4} observed {c.Min}..{c.Max}  predicted {c.Floor}..{c.Ceiling}" +
            $"  ceiling x{(double)c.Max / c.Ceiling:F3}"));

        over.Where(c => c.Max > c.Ceiling).ShouldBeEmpty($"observed damage above a maximum roll:{Environment.NewLine}{report}");
    }
}
