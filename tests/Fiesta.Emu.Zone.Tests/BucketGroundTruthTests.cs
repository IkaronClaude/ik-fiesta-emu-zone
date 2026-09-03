using System.Text.Json;
using Fiesta.Emu.Zone.Abstate;
using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Data;
using Fiesta.Emu.Zone.Parameter;
using Shouldly;
using Xunit;

namespace Fiesta.Emu.Zone.Tests;

/// <summary>Every swing in a capture, sorted into buckets of identical combat state, predicted by this
/// port and checked against what the server actually did.
///
/// <para>A different instrument from <see cref="PcapGroundTruthTests"/>, and strictly better where it
/// applies. That one slices a capture by the operator's chat annotations — which bounds a window by
/// TYPING, not by state — and rebuilds the character by inverting displayed totals. This one takes state
/// from the packets (<c>tools/damage_buckets.py</c>) and the character's attack and defence from the
/// server's own numbers.</para>
///
/// <para><b>No container is reconstructed for the player.</b>
/// `ShinePlayer::so_mobile_NotifyParameterChange` builds the 0x1035 packet as
/// <c>ftol(roe_Xxx(&amp;arg)) + freeStat</c> per field, so the wire already carries `roe_MinWC`,
/// `roe_MaxWC` and `roe_AC`. Backing the free-stat term out is the whole of the reconstruction; mastery
/// PLUS values, layer splits and enhancement placement never arise, because they are already inside those
/// numbers. The mastery RATE is not — see <see cref="MasteryRate"/>.</para>
///
/// <para><b>⚠️ OVERSHOOT IS THE FAILURE.</b> A bucket whose hits sit inside the band without reaching its
/// ends is fine — it simply never rolled the maximum. A bucket whose hits go ABOVE the ceiling by even one
/// point means the server produced damage this port says is impossible. No tolerance: a ±1 slop is the
/// same size as the thing being measured.</para>
///
/// <para>Generate the fixture (BYO, never committed; it pairs with its own capture):</para>
/// <code>
/// python tools/damage_buckets.py --pcap Z:/FighterDamageLvl60.pcapng --port 9022 --out buckets.json
/// FIESTA_BUCKETS=...\buckets.json dotnet test
/// </code></summary>
public class BucketGroundTruthTests
{
    /// <summary>An abnormal state as the wire carries it. `ABSTATE_INFORMATION` is
    /// <c>{abstateID, restKeeptime, strength}</c>, and STRENGTH is not decoration: `SubAbState` rows are
    /// keyed by (name, Strength), so `StaMoraleDecreaseWC` alone spans an argument of 1490..2148 across
    /// ranks 17-20. A rank is a different effect, not a different label.</summary>
    private sealed record Abstate(int Id, int Strength);

    private sealed record Bucket(string Side, int Mob, int Level, IReadOnlyList<Abstate> SelfAbstates,
                                 IReadOnlyList<Abstate> EnemyAbstates,
                                 IReadOnlyDictionary<string, int> Params,
                                 IReadOnlyList<int> Passives, int? Weapon, int N, int Min, int Max);

    /// <summary>A skill bucket: the same state key as a swing, plus WHICH skill was cast.</summary>
    private sealed record SkillBucket(string Side, int Skill, int Mob, int Level,
                                      IReadOnlyList<Abstate> EnemyAbstates,
                                      IReadOnlyDictionary<string, int> Params,
                                      IReadOnlyList<int> Passives, int? Weapon, int N, int Min, int Max);

    private sealed record Fixture(IReadOnlyList<Bucket> Buckets, IReadOnlyDictionary<string, int> FreeStat,
                                  int ChrClass, IReadOnlyList<SkillBucket> SkillBuckets,
                                  IReadOnlyDictionary<int, int> SkillEmpower);

    /// <summary>`NC_CHAR_CHANGEPARAMCHANGE_CMD` parameter ids for the three fields that matter.
    ///
    /// <para>Identified from the capture rather than assumed: ids 6 and 7 move together on every weapon
    /// change and 8 jumps when a shield goes on. That agrees with `analyse_damage.py`'s independent map
    /// (6=DmgMin, 7=DmgMax, 8=DEF) — two derivations, one answer.</para>
    ///
    /// <para>⚠️ These are NOT `Parameter::Stat` slot indices. An item's random-option codes are (see
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

    private static Abstate Pair(JsonElement e)
        => new(e[0].GetInt32(), e[1].GetInt32());

    private static Fixture Load(string path)
    {
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        // Buckets with no CLEAN hits carry no damage band -- since the bucketer started counting misses,
        // blocks and criticals, a bucket can exist because a swing missed there and nothing landed. Those
        // have nothing for a damage check to predict, so they are dropped HERE rather than by the tool,
        // which still needs to report them.
        var buckets = root.GetProperty("buckets").EnumerateArray()
            .Where(b => b.GetProperty("n").GetInt32() > 0)
            .Select(b => new Bucket(
            b.GetProperty("side").GetString()!,
            b.GetProperty("mob").GetInt32(),
            b.GetProperty("level").GetInt32(),
            b.GetProperty("selfAbstates").EnumerateArray().Select(Pair).ToList(),
            b.GetProperty("enemyAbstates").EnumerateArray().Select(Pair).ToList(),
            b.GetProperty("params").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
            b.GetProperty("passives").EnumerateArray().Select(x => x.GetInt32()).ToList(),
            b.GetProperty("weapon").ValueKind == JsonValueKind.Number
                ? b.GetProperty("weapon").GetInt32() : null,
            b.GetProperty("n").GetInt32(),
            b.GetProperty("min").GetInt32(),
            b.GetProperty("max").GetInt32())).ToList();

        var skillBuckets = root.TryGetProperty("skillBuckets", out var sb)
            ? sb.EnumerateArray().Where(b => b.GetProperty("n").GetInt32() > 0).Select(b => new SkillBucket(
                b.GetProperty("side").GetString()!,
                b.GetProperty("skill").GetInt32(),
                b.GetProperty("mob").GetInt32(),
                b.GetProperty("level").GetInt32(),
                b.GetProperty("enemyAbstates").EnumerateArray().Select(Pair).ToList(),
                b.GetProperty("params").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
                b.GetProperty("passives").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                b.GetProperty("weapon").ValueKind == JsonValueKind.Number
                    ? b.GetProperty("weapon").GetInt32() : null,
                b.GetProperty("n").GetInt32(),
                b.GetProperty("min").GetInt32(),
                b.GetProperty("max").GetInt32())).ToList()
            : [];

        return new Fixture(buckets,
            root.GetProperty("freeStat").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
            root.GetProperty("chrclass").GetInt32(), skillBuckets,
            // Absent in a fixture built before the empower allocation was read off the wire. Empty is the
            // honest reading there -- "this capture did not record one" -- and it costs nothing, because a
            // zero allocation contributes zero.
            root.TryGetProperty("skillEmpower", out var emp) && emp.ValueKind == JsonValueKind.Object
                ? emp.EnumerateObject().ToDictionary(x => int.Parse(x.Name), x => x.Value.GetInt32())
                : new Dictionary<int, int>());
    }

    /// <summary>The free-stat Str term, <b>which is NOT the allocated point count.</b>
    ///
    /// <para>`so_ply_FreeStatStr` returns a <c>ShineCommonParameter::FreeStatStr</c> record and every
    /// caller reads the <b>u16 at +1</b>. The PDB names the fields:</para>
    ///
    /// <code>
    ///   +0x00  Stat        unsigned char     &lt;- the allocation
    ///   +0x01  WCAbsolute  unsigned short    &lt;- what callers actually read
    ///   +0x03  checksum    unsigned char
    /// </code>
    ///
    /// <para><c>Stat</c> is identity and <c>WCAbsolute</c> is not, so reading the record as "the points"
    /// is right for one field and wrong for the one that matters. Measured out of a live zone through the
    /// pointer array at 0x0DA50BC4 — <b>all 181 entries</b>, zero mismatches against
    /// <c>points + points/5</c>:</para>
    ///
    /// <code>
    ///   points   0  1  2  3  5  10  15  16  17  18  20  25  33  50
    ///   Stat     0  1  2  3  5  10  15  16  17  18  20  25  33  50
    ///   WCAbs    0  1  2  3  6  12  18  19  20  21  24  30  39  60
    /// </code>
    ///
    /// <para>This mattered by about 1%: the term enters TWICE, subtracted from the wire's Dmg pair and
    /// added back as `roe_Damage`'s flat override, so at 17 points using 17 instead of 20 left three
    /// outgoing buckets 2-3 points over the ceiling. It cannot touch incoming, because the defender's side
    /// uses FreeStatCon and this character has no Con allocated — which is exactly why incoming was already
    /// exact and only outgoing was short.</para>
    ///
    /// <para>⚠️ The earlier reading ("Str is 1:1") came from sampling two entries, 0 and 2, where the two
    /// fields agree. Two points do not distinguish <c>n</c> from <c>n + n/5</c>.</para></summary>
    private static int FreeStatStr(int points) => points + points / 5;

    /// <summary>The Con equivalent, <c>ceil(n/2)</c> from the sibling table at 0x0DA50BD0 (Con[19]=10,
    /// Con[20]=10, Con[21]=11, Con[50]=25).
    ///
    /// <para>⚠️ Sampled at four points, not read in full the way the Str table now has been. It is unused
    /// by this capture — the character has zero Con allocated — so it has never been exercised here.
    /// Verify it before trusting it on a character that has spent points in Con.</para></summary>
    private static int FreeStatCon(int points) => (points + 1) / 2;

    /// <summary>`FreeStatInt.MAAbsolute` — the same `n + n/5` curve as Str's `WCAbsolute`, read in full
    /// from a live zone (see <see cref="Fiesta.Emu.Zone.Parameter.FreeStatTables"/>).</summary>
    private static int FreeStatInt(int points) => points + points / 5;

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

    /// <summary>Which `PassiveSkill.shn` mastery column a weapon selects, read out of
    /// <c>PassiveMasteryVariable::PassiveMasteryVariable</c> (0x00444DB0), which fills the `pmv` table at
    /// 0x008BEA40 with FIELD OFFSETS into `PassiveSkillInfo`.
    ///
    /// <para><c>cpl_RecalcParam</c> indexes it as <c>pmv[twoHand*44 + WeaponType]</c> against two bases 88
    /// bytes (22 entries) apart — one for the rate half, one for the plus half. Everything not listed here
    /// falls back to <c>MstRtTmp</c>, which is also the out-of-range case (<c>cmp eax, 0x16; jae</c>).</para>
    ///
    /// <para>This is a struct-layout fact read from the binary, not game data — the same category as the
    /// `Stat` enum, and it is why it may be written down here rather than loaded.</para></summary>
    private static readonly Dictionary<int, string> OneHandRateColumn = new()
    {
        [1] = "MstRtSword1", [5] = "MstRtMace1", [13] = "MstRtHammer1",
        [17] = "MstRtDSword", [18] = "MstRtClaw",
    };

    private static readonly Dictionary<int, string> TwoHandRateColumn = new()
    {
        [2] = "MstRtBow2", [3] = "MstRtStaff2", [4] = "MstRtAxe2", [10] = "MstRtCrossBow2",
        [11] = "MstRtWand2", [17] = "MstRtDSword", [18] = "MstRtClaw", [21] = "MstRtSword2",
    };

    /// <summary>`WeaponType` values that route to MagicalWeaponMastery instead — <c>cmp eax, 3</c> and
    /// <c>cmp eax, 0xB</c> in `cpl_RecalcParam`. A physical swing with one of these gets no physical
    /// mastery at all.</summary>
    private static readonly int[] MagicalWeaponTypes = { 3, 11 };

    /// <summary>`Rate[PassiveSkill][PhisycalWeaponMastery]`, which `roe_AttackPower` multiplies the rolled
    /// attack by AFTER the accessors — so it is NOT inside the Dmg pair the wire carries, and omitting it
    /// under-predicts every outgoing swing by exactly this factor.
    ///
    /// <para><c>cpl_RecalcParam+0x1C5</c> is <c>add eax, 0x3E8</c> then <c>mov [container+0x858]</c>:
    /// <b>rate = MstRt + 1000</b>. And it is <c>mov</c>, not <c>add</c>, guarded by
    /// <c>test eax,eax; je</c> — ranks of one mastery line do NOT sum, the last learned non-zero wins.
    /// Summing `BraveMastery01..06` would give 750 and a rate of 1750; last-wins gives 150 and 1150, and
    /// the capture says 1150.</para>
    ///
    /// <para>The weapon comes from `NC_ITEM_EQUIPCHANGE_CMD` (slot 12), which is our own character's
    /// equipment sent to us and carries every swap. `NC_BRIEFINFO_CHANGEWEAPON_CMD` broadcasts appearance
    /// to other players and does NOT — it misses this capture's Splitter → Kaineneceflight change
    /// entirely, and relying on it left every bucket reading 257.</para></summary>
    private static int? MasteryRate(ShnFile passiveTable, ShnFile items, IReadOnlyList<int> passives,
                                    int? weaponId)
    {
        if (weaponId is not { } id) return null;
        var item = items.Rows.FirstOrDefault(r => ShnFile.Int(r, "ID") == id);
        if (item is null) return null;

        var type = ShnFile.Int(item, "WeaponType");
        if (MagicalWeaponTypes.Contains(type)) return 1000;      // routed to the magical slot instead

        var table = ShnFile.Int(item, "TwoHand") != 0 ? TwoHandRateColumn : OneHandRateColumn;
        var column = table.GetValueOrDefault(type, "MstRtTmp");

        var byId = passiveTable.Rows.ToDictionary(r => ShnFile.Int(r, "ID"));
        var value = 0;
        foreach (var pid in passives)
            if (byId.TryGetValue(pid, out var row) && ShnFile.Int(row, column) != 0)
                value = ShnFile.Int(row, column);                // `mov`, not `add`: last non-zero wins
        return 1000 + value;
    }

    /// <summary>Diagnostics for a failing bucket: the mob's level, the level-gap rate actually used, and
    /// the mob's armour BEFORE abstates. Enough to hand-check the arithmetic without a debugger.</summary>
    private static (int lv, int gap, double ac) Diag(Bucket b, MobDataBox box,
                                                     IReadOnlyDictionary<int, string> mobNames,
                                                     LevelGapTable gaps)
    {
        var mob = MobCombatant.Build(box, mobNames[b.Mob])!;
        var gap = b.Side == "OUT"
            ? gaps.Rate(CombatantKind.Player, b.Level, CombatantKind.Monster, mob.Level)
            : gaps.Rate(CombatantKind.Monster, mob.Level, CombatantKind.Player, b.Level);
        return (mob.Level, gap, DamageCalculator.ArmourClass(mob));
    }

    private static string Show(IReadOnlyList<Abstate> a)
        => string.Join(",", a.Select(x => $"{x.Id}@{x.Strength}"));

    /// <summary>What one `SubAbstateAction` does to a `Parameter::Container`, read out of
    /// `AbstateElementInObject::aeo_ParameterEnchant` (0x004079F0) -- a jump table on the action index
    /// whose handlers write straight into the AbnormalState cluster (plus half at +0x8C4, rate half at
    /// +0x990, four bytes per <see cref="Stat"/> slot).
    ///
    /// <code>
    ///    4 SAA_WCRATE          add rate[0x9A4],[0x9A8]  -> Rate WCmin,WCmax += arg
    ///   94 SAA_WCMINUS         sub plus[0x8D8],[0x8DC]  -> Plus WCmin,WCmax -= arg
    ///   73 SAA_ACMINUS         sub plus[0x8E0]          -> Plus AC          -= arg
    ///   74 SAA_ACDOWNRATE      sub rate[0x9AC]          -> Rate AC          -= arg
    ///   81 SAA_DEXMINUS        sub plus[0x8CC]          -> Plus Dex         -= arg
    ///   18 SAA_SHIELDACRATE    add rate[0x9F8]          -> Rate ShieldAC    += arg
    ///   21 SAA_ATTACKSPEEDRATE add rate[0xA18]          -> Rate AttSpeed    += arg
    /// </code>
    ///
    /// <para>Note WC actions touch BOTH bounds, which is why a debuff shifts the whole range rather than
    /// squashing it.</para></summary>
    /// <summary>An empty <c>Stats</c> list means the action provably writes no stat slot — a behavioural
    /// flag — as opposed to one this port has not read, which is refused instead.</summary>
    private sealed record AbstateAction(bool Rate, int Sign, params Stat[] Stats);

    /// <summary>⭐ The abstate action table is the LIBRARY's generated one, not a local copy.
    ///
    /// <para>This used to be a hand-written dictionary of NINE actions — the nine the Fighter capture
    /// happened to exercise — and everything else made a bucket unpredictable. That silently refused 15
    /// of the level-60 Mage capture's 37 hits, because `StaChainLightningStun` and `StaFrostNova` both
    /// carry action 88, <c>SAA_SPEEDDOWNRATE</c>, which writes `AbnormalState.Rate[MoveSpeed]` and cannot
    /// touch damage at all.</para>
    ///
    /// <para><see cref="AbstateEffects"/> is generated from `aeo_ParameterEnchant`'s jump table by
    /// `tools/abstate_actions.py --csharp` and covers all 120, and — the part that matters here — it
    /// distinguishes an action that writes NOTHING (49 of them resolve to the shared epilogue) from one
    /// nobody has read. Only the second kind may refuse a bucket.</para></summary>

    /// <summary>Apply a mob's abnormal states to its own container.
    ///
    /// <para><b>Only the MOB's.</b> The character's are already inside the numbers this test feeds in: the
    /// server recomputes and re-sends 0x1035 when a buff lands, so the wire's Dmg and DEF have them baked
    /// in. Applying them again would double-count — and the buckets prove the wire tracks them, because
    /// the parameter vector changes exactly when a self-buff appears.</para>
    ///
    /// <para>Returns false on an action this port has not read, so an unmodelled effect makes the bucket
    /// unpredictable instead of silently predicted wrong.</para></summary>
    private static bool ApplyAbstates(ParameterContainer p, IReadOnlyList<Abstate> abstates,
                                      ShnFile abState, ShnFile subAbState)
    {
        foreach (var ab in abstates)
        {
            var def = abState.Rows.FirstOrDefault(r => ShnFile.Int(r, "AbStataIndex") == ab.Id);
            var name = def is null ? null : ShnFile.Str(def, "SubAbState");
            if (string.IsNullOrEmpty(name) || name == "-") continue;   // no sub-state: no parameter effect

            var rows = subAbState.Rows.Where(r => ShnFile.Str(r, "InxName") == name).ToList();
            var row = rows.FirstOrDefault(r => ShnFile.Int(r, "Strength") == ab.Strength);
            if (row is null)
            {
                // No row at this strength. Rather than guess the server's fallback, ask a question the
                // DATA answers: does ANY row for this sub-state carry an action? If none does, the
                // sub-state cannot change a parameter whichever row the server would have picked, so it is
                // safe to skip. If one does, refuse -- picking a row would be a guess.
                //
                // `StaImmortal` (291) arrives at strength 1 while its only `SubStaKeepTime_Eternal` row is
                // at Strength 999 with every action zero, which is what this exists for.
                var anyAction = rows.Any(r => new[] { "A", "B", "C", "D" }
                    .Any(k => ShnFile.Int(r, "ActionIndex" + k) != 0));
                if (anyAction) return false;
                continue;
            }

            foreach (var slot in new[] { "A", "B", "C", "D" })
            {
                var index = ShnFile.Int(row, "ActionIndex" + slot);
                var arg = ShnFile.Int(row, "ActionArg" + slot);
                if (index == 0) continue;
                var action = (SubAbstateAction)index;
                // Out of the dispatcher's 1..120 range: the server would fall straight through, so this
                // is not an unknown -- it is a known no-op.
                if (!AbstateEffects.IsDispatched(action)) continue;
                var effect = AbstateEffects.For(action);
                // Dispatched but absent from the table = the handler IS the shared epilogue: it writes
                // nothing. A read result, not a gap, so it must NOT refuse the bucket.
                if (effect is null) continue;
                foreach (var w in effect.Stats)
                {
                    var cluster = w.Half == StatHalf.Rate ? p.Rate(StatModifier.AbnormalState)
                                                          : p.Plus(StatModifier.AbnormalState);
                    cluster[w.Stat] += w.Sign * arg;
                }
                // ⚠️ Second-tier fields and behaviour bits are NOT applied here, and that is deliberate:
                // this harness reconstructs a container for the DAMAGE formula only. None of the fields
                // (`RangeOver`, heal rates) or flags (stun / entangle / cannot-attack) is read by
                // `roe_AttackPower` / `roe_DefendPower` / `roe_Damage`, so ignoring them changes no
                // prediction -- see AbstateEffect and docs/SUBABSTATE_ACTIONS.md.
            }
        }
        return true;
    }

    private readonly record struct Band(int Floor, int Ceiling);

    /// <summary>A combatant that reproduces known ACCESSOR OUTPUTS exactly.
    ///
    /// <para>`roe_MinWC` is the Str chain plus the WCmin slot and the Str chain is floored at 1, so
    /// <c>Base[Str] = 1</c> with <c>Base[WCmin] = attack - 1</c> returns exactly <c>attack</c>. Same shape
    /// for `roe_AC` over Con. Not a reconstruction of the character — a container chosen so the accessors
    /// return what the SERVER put on the wire, which lets the real pipeline run on top of it.</para></summary>
    private static Combatant Exactly(int level, int minAttack, int maxAttack, int armour,
                                     int masteryRate = 1000)
    {
        var p = new ParameterContainer();
        p.Base[Stat.Str] = 1;
        p.Base[Stat.WCmin] = minAttack - 1;
        p.Base[Stat.WCmax] = maxAttack - 1;
        p.Base[Stat.Con] = 1;
        p.Base[Stat.AC] = armour - 1;
        // Only `roe_AttackPower`'s trailing multiply sees this here. `ScaledWeaponItemBonus` reads it too
        // but scales Item.Plus, which is zero in this container, so the accessor outputs stay exactly the
        // numbers the wire gave.
        p.Rate(StatModifier.PassiveSkill)[Stat.PhisycalWeaponMastery] = masteryRate;
        return new Combatant(level, p);
    }

    /// <summary>The band, computed through <see cref="DamageCalculator.Resolve"/> rather than by
    /// multiplying doubles.
    ///
    /// <para>Ordering is the point: the server truncates to an integer with <c>_ftol</c> and only THEN
    /// applies the job-change multiplier and the level gap, both as integer operations. Scaling a double
    /// band by those rates gives a different number, and the difference is exactly the one or two points
    /// that decide whether a swing is inside.</para>
    ///
    /// <para>Both bounds pin the roll and forbid a critical, so the band is the whole space of clean
    /// outcomes — nothing random is left in it.</para></summary>
    private static Band Through(ICombatant attacker, ICombatant defender, AttackModifiers mods,
                                EngagementRule rule = EngagementRule.NormalPhysical)
    {
        var rng = new System.Random(1);
        var lo = DamageCalculator.ResolveDamage(attacker, defender, mods with { RollPermille = 0 }, rng, rule);
        var hi = DamageCalculator.ResolveDamage(attacker, defender, mods with { RollPermille = 1000 }, rng, rule);
        return new Band(Math.Min(lo, hi), Math.Max(lo, hi));
    }

    private static Band? Predict(Fixture f, Bucket b, MobDataBox box, LevelGapTable gaps,
                                 IReadOnlyDictionary<int, string> mobNames, int? jobChange,
                                 int? masteryRate, ShnFile abState, ShnFile subAbState)
    {
        if (!mobNames.TryGetValue(b.Mob, out var name)) return null;
        var mob = MobCombatant.Build(box, name);
        if (mob is null) return null;
        if (!ApplyAbstates(mob.Parameters, b.EnemyAbstates, abState, subAbState)) return null;

        var freeStr = FreeStatStr(f.FreeStat.GetValueOrDefault("Strength"));
        var freeCon = FreeStatCon(f.FreeStat.GetValueOrDefault("Constitute"));

        if (b.Side == "OUT")
        {
            if (!b.Params.TryGetValue(DmgMin, out var dmin) || !b.Params.TryGetValue(DmgMax, out var dmax))
                return null;
            if (masteryRate is not { } rate) return null;
            var me = Exactly(b.Level, dmin - freeStr, dmax - freeStr, 1, rate);
            return Through(me, mob, new AttackModifiers
            {
                ForceCritical = false,
                // `roe_Damage`'s per-rule override: the attacker's Str free stat minus the defender's Con
                // free stat, and a mob's free-stat accessors return table[0] = 0.
                AttackerFreeStat = freeStr,
                DefenderFreeStat = 0,
                JobChangeDamageUpPermille = jobChange,
                LevelGapRatePermille = gaps.Rate(CombatantKind.Player, b.Level,
                                                 CombatantKind.Monster, mob.Level),
            });
        }

        if (!b.Params.TryGetValue(Def, out var def) || def - freeCon <= 0) return null;
        var target = Exactly(b.Level, 1, 1, def - freeCon);
        return Through(mob, target, new AttackModifiers
        {
            ForceCritical = false,
            AttackerFreeStat = 0,
            DefenderFreeStat = freeCon,
            // A MOB attacker never reaches so_ply_JobChangeDamageUp: ShineObject's slot returns the damage
            // unchanged. Not a modelling choice -- the server does not run it.
            JobChangeDamageUpPermille = null,
            LevelGapRatePermille = gaps.Rate(CombatantKind.Monster, mob.Level,
                                             CombatantKind.Player, b.Level),
        });
    }

    /// <summary>Predict every bucket and require that no clean hit exceeds what a maximum roll can
    /// produce. <b>510 of 510 hits inside 175 buckets. Nothing over a ceiling, nothing under a floor.</b>
    ///
    /// <para>Both directions, four mobs, two levels, three weapons, with buffs and debuffs live on both
    /// sides. The 46 unpredictable hits are buckets carrying a `SubAbstateAction` this port has not read;
    /// they are REFUSED rather than predicted, so an unmodelled effect can never masquerade as
    /// agreement.</para>
    ///
    /// <para>Asserted on OVERSHOOT with no tolerance — a single point over means the server produced
    /// damage this port calls impossible. Undershoot does not fail: a bucket may simply never roll its
    /// maximum, and with n=2 it usually will not.</para>
    ///
    /// <para><b>What it took, in the order the errors were found:</b> the character rebuilt the way
    /// `so_mobile_NotifyParameterChange` fills the wire rather than by inverting displayed totals;
    /// `JobChangeDmgUp`; the band computed through the real integer pipeline instead of scaled doubles;
    /// weapon mastery (`cpl_RecalcParam`, rate = MstRt + 1000, ranks do not sum); the mob's abnormal
    /// states (`aeo_ParameterEnchant`); and finally `FreeStatStr.WCAbsolute`, which is not the point
    /// count — see <see cref="FreeStatStr"/>. Every one of those was a term read out of the binary or the
    /// live server, and every one was found because a prediction disagreed with a capture.</para></summary>
    [SkippableFact]
    public void NoBucketExceedsWhatAMaximumRollCanProduce()
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
        var passiveTable = ShnFile.Load(Path.Combine(shine!, "PassiveSkill.shn"));
        var items = ShnFile.Load(Path.Combine(shine!, "ItemInfo.shn"));
        var abState = ShnFile.Load(Path.Combine(shine!, "AbState.shn"));
        var subAbState = ShnFile.Load(Path.Combine(shine!, "SubAbState.shn"));

        int hits = 0, inside = 0, predicted = 0, unpredictable = 0;
        int worstOver = 0, overHits = 0, underHits = 0, overBuckets = 0;
        int overWithNoEnemyAbstate = 0, underWithNoEnemyAbstate = 0;
        var report = new List<string>();

        foreach (var b in f.Buckets.OrderBy(b => b.Side).ThenBy(b => b.Mob).ThenBy(b => b.Level)
                           .ThenByDescending(b => b.N))
        {
            var band = Predict(f, b, box, gaps, mobNames, jobByLevel[b.Level],
                               MasteryRate(passiveTable, items, b.Passives, b.Weapon),
                               abState, subAbState);
            if (band is not { } p)
            {
                unpredictable += b.N;
                continue;
            }
            predicted++;
            hits += b.N;

            var over = b.Max - p.Ceiling;
            var under = p.Floor - b.Min;
            if (over <= 0 && under <= 0) inside += b.N;
            if (over > 0)
            {
                overHits += b.N;
                overBuckets++;
                worstOver = Math.Max(worstOver, over);
                if (b.EnemyAbstates.Count == 0) overWithNoEnemyAbstate++;
            }
            if (under > 0)
            {
                underHits += b.N;
                if (b.EnemyAbstates.Count == 0) underWithNoEnemyAbstate++;
            }

            // Every overshooting bucket is reported whatever its size: a one-hit bucket that exceeds the
            // band is exactly as much of a contradiction as a twenty-hit one.
            if (b.N >= 5 || over > 0)
                report.Add($"  {b.Side,-4} mob {b.Mob,-4} lv{b.Level} n={b.N,-3}"
                           + $" observed {b.Min}..{b.Max}  predicted {p.Floor}..{p.Ceiling}"
                           + (over > 0 ? $"  OVER BY {over}  self=[{Show(b.SelfAbstates)}]"
                                          + $" enemy=[{Show(b.EnemyAbstates)}]"
                                          + $" wpn={b.Weapon} dmg={b.Params.GetValueOrDefault(DmgMin)}"
                                          + $"..{b.Params.GetValueOrDefault(DmgMax)}"
                                          + $" mobLv={Diag(b, box, mobNames, gaps).lv}"
                                          + $" gap={Diag(b, box, mobNames, gaps).gap}"
                                          + $" mobAC={Diag(b, box, mobNames, gaps).ac:F1}"
                              : under > 0 ? $"  under by {under}  enemy=[{Show(b.EnemyAbstates)}]"
                              : "  inside"));
        }

        var summary = $"{inside}/{hits} hits fully inside across {predicted} buckets; "
                      + $"{overHits} hits in {overBuckets} OVERSHOOTING buckets (worst by {worstOver}), "
                      + $"{overWithNoEnemyAbstate} of them with NO enemy abstate; "
                      + $"{underHits} below floor in buckets of which {underWithNoEnemyAbstate} have no "
                      + $"enemy abstate; {unpredictable} unpredictable"
                      + Environment.NewLine + string.Join(Environment.NewLine, report);

        hits.ShouldBeGreaterThan(0, "the fixture should contain predictable buckets");

        // THE CLAIM: where the port models everything that is acting, it is exact. Buckets carrying an
        // abnormal state on the MOB are excluded because their effects are not modelled at all yet -- an
        // enemy DEF debuff (fatal slash) makes our damage exceed the band, and an enemy damage debuff
        // (demoralising hit) makes theirs fall below it. Both are unmodelled, both have the sign the
        // operator's own chat predicts, and neither is a defect in the damage engine.
        //
        // Asserted on OVERSHOOT only, and only where nothing unmodelled was acting: that is the case where
        // the server produced damage this port says is impossible.
        overBuckets.ShouldBe(0, summary);
    }

    // ---- skills ----------------------------------------------------------------------------------

    /// <summary>`ActiveSkill.shn`'s four damage columns per skill id — the ones `roe_AttackPower` reads
    /// out of `sdi_Activ`.</summary>
    /// ⚠️ Two types carry this game struct's name: `Data.ActiveSkillInfo` (id/name/cast timing, loaded
    /// by `SkillDataBox`) and `Skill.ActiveSkillInfo` (the four DAMAGE columns `roe_AttackPower` reads).
    /// They model different halves of one row and both are legitimately named; qualify at every use.
    /// <summary>`nT0`..`nT3` — the four empower tables, five entries each, as the one flat run of twenty
    /// the engine indexes.
    ///
    /// <para>⚠️ SHARED BY BOTH SKILL LOADERS ON PURPOSE. It began as a local function inside
    /// <see cref="SkillRows"/> and <see cref="SkillRowsMagical"/> simply never got one, so every magical
    /// prediction silently dropped the empower term — visible only as `MagicBall01` needing a bigger
    /// correction than every other skill, since it is the one skill in the capture that carries an
    /// allocation.</para>
    ///
    /// <para>The column names are the client's own: only the first of each run is named, the other four
    /// are `UndefinedN` in file order. The grouping is confirmed rather than assumed — the declared
    /// lengths (`nIMPT`, `Undefined0`, `Undefined1`, `Undefined2`) are all 5, and `Undefined1` is 0 for
    /// exactly the skills whose `nT2` run is all zeros. The four runs are DAMAGE / SP / KEEPTIME /
    /// COOLTIME, matching `SKILL_EMPOWER`'s four bitfields: `nT1`'s top entry is almost exactly half the
    /// skill's own `SP` for every skill checked, and `nT2` is empty for precisely the skills that apply no
    /// lasting state.</para></summary>
    private static Fiesta.Emu.Zone.Skill.SkillEmpowerTable EmpowerTable(IReadOnlyDictionary<string, object> r)
    {
        uint[] Run(string first, params string[] rest)
            => [(uint)ShnFile.Int(r, first), .. rest.Select(c => (uint)ShnFile.Int(r, c))];
        return Fiesta.Emu.Zone.Skill.SkillEmpowerTable.FromArrays(
            Run("nT0", "Undefined3", "Undefined4", "Undefined5", "Undefined6"),
            Run("nT1", "Undefined7", "Undefined8", "Undefined9", "Undefined10"),
            Run("nT2", "Undefined11", "Undefined12", "Undefined13", "Undefined14"),
            Run("nT3", "Undefined15", "Undefined16", "Undefined17", "Undefined18"));
    }

    private static IReadOnlyDictionary<int, Fiesta.Emu.Zone.Skill.ActiveSkillInfo> SkillRows(string ressystem)
    {
        var table = ShnFile.Load(Path.Combine(ressystem, "ActiveSkill.shn"));
        var rows = new Dictionary<int, Fiesta.Emu.Zone.Skill.ActiveSkillInfo>();
        foreach (var r in table.Rows)
        {
            var id = ShnFile.Int(r, "ID");
            // ⭐ `nT0`..`nT3` -- the four empower tables, five entries each. The column names are the
            // client's own and only the first of each run is named; the other four are `UndefinedN`, in
            // file order. Their LENGTHS are declared too (`nIMPT`, `Undefined0`, `Undefined1`,
            // `Undefined2`), which is how the grouping was confirmed rather than assumed: `Undefined1` is
            // 0 for exactly the skills whose `nT2` run is all zeros.
            //
            // The four runs are, in order, DAMAGE / SP / KEEPTIME / COOLTIME -- the same order as
            // `SKILL_EMPOWER`'s four bitfields. Read off the data, not assumed: `nT1`'s top entry is
            // almost exactly half the skill's own `SP` for every skill checked (SP 106 -> 52, 120 -> 60,
            // 82 -> 40, 154 -> 77), and `nT2` is empty for precisely the skills that apply no lasting
            // state (`PowerHit`, `TripleHit`) while `RedSlash` and `GreatSwing`, which do, carry one.
            rows[id] = new Fiesta.Emu.Zone.Skill.ActiveSkillInfo(
                MinFlat: (uint)ShnFile.Int(r, "MinWC"),
                MinRatePermille: (uint)ShnFile.Int(r, "MinWCRate"),
                MaxFlat: (uint)ShnFile.Int(r, "MaxWC"),
                MaxRatePermille: (uint)ShnFile.Int(r, "MaxWCRate"),
                Empower: EmpowerTable(r));
        }
        return rows;
    }

    /// <summary>⭐ Each skill id mapped to the BASE of its skill line, by walking `DemandSk`.
    ///
    /// <para>An empower allocation is reported against the line's first rank, not the rank being cast.
    /// The Fighter capture allocates on ids 20, 40, 100 and 120 — `SeverBone01`, `RedSlash01` and
    /// `PowerHit01` — while the character is level 60 and casts 24, 45 and 124. Four of four landing on a
    /// line base is what says the allocation belongs to the LINE; a per-rank reading would have the
    /// character spending points on ranks it never casts.</para>
    ///
    /// <para>`DemandSk` holds the prerequisite skill by `InxName` (`PowerHit05` requires `PowerHit04`),
    /// so the walk is name to row to name until a row demands nothing. Cycles cannot happen in the data
    /// but the walk is bounded anyway — a malformed chain must not hang the suite.</para></summary>
    private static IReadOnlyDictionary<int, int> SkillLineBase(string ressystem)
    {
        var table = ShnFile.Load(Path.Combine(ressystem, "ActiveSkill.shn"));
        var byName = new Dictionary<string, IReadOnlyDictionary<string, object>>();
        foreach (var r in table.Rows) byName[ShnFile.Str(r, "InxName")] = r;

        var baseOf = new Dictionary<int, int>();
        foreach (var r in table.Rows)
        {
            var row = r;
            for (var hop = 0; hop < 64; hop++)
            {
                var demand = ShnFile.Str(row, "DemandSk");
                if (string.IsNullOrEmpty(demand) || demand == "-"
                    || !byName.TryGetValue(demand, out var prev)) break;
                row = prev;
            }
            baseOf[ShnFile.Int(r, "ID")] = ShnFile.Int(row, "ID");
        }
        return baseOf;
    }

    /// <summary>Each skill's `HitID` — the `MultiHitType.shn` sequence it fires, or 0 for a single
    /// strike.</summary>
    private static IReadOnlyDictionary<int, int> SkillHitIds(string ressystem)
    {
        // ⚠️ `ActiveSkill.shn` has DUPLICATE ids (9034 appears twice), so ToDictionary throws. Last row
        // wins, matching how SkillRows above builds its map -- consistent, and the duplicates are not in
        // any capture analysed so far.
        var table = ShnFile.Load(Path.Combine(ressystem, "ActiveSkill.shn"));
        var map = new Dictionary<int, int>();
        foreach (var r in table.Rows) map[ShnFile.Int(r, "ID")] = ShnFile.Int(r, "HitID");
        return map;
    }

    /// <summary>⭐ `MultiHitType.shn` — the per-strike damage rates, as (lowest, highest) per sequence id.
    ///
    /// <para>A multi-hit skill does NOT deal its damage once. `TripleHit06` fires sequence 20, which is
    /// three strikes at <b>300 / 200 / 500</b> permille summing to 1000 — it hits three times and splits
    /// the damage. `roe_CalcDamage` computes the whole hit and `smo_SkillBlast` then scales each strike by
    /// its own `mha_DamageRate` (see <see cref="MultiHit.HitDamage"/>).</para>
    ///
    /// <para>A bucket cannot know WHICH strike of a sequence a given hit was — the wire carries the
    /// damage, not the step. So the band has to span the whole sequence: the floor scaled by the LOWEST
    /// rate and the ceiling by the HIGHEST. That is wider than the truth for any single strike and is the
    /// honest bound, not a fudge; narrowing it would need the strike index, which is not on the wire.</para></summary>
    private static IReadOnlyDictionary<int, (int Low, int High)> MultiHitRates(string shine)
    {
        var table = ShnFile.Load(Path.Combine(shine, "MultiHitType.shn"));
        var by = new Dictionary<int, (int Low, int High)>();
        foreach (var r in table.Rows)
        {
            var id = ShnFile.Int(r, "ID");
            var rate = ShnFile.Int(r, "DmgRate");
            by[id] = by.TryGetValue(id, out var cur)
                ? (Math.Min(cur.Low, rate), Math.Max(cur.High, rate))
                : (rate, rate);
        }
        return by;
    }

    /// <summary>`NC_CHAR_CHANGEPARAMCHANGE_CMD` ids for the MAGICAL pair and magic defence.
    ///
    /// <para>Identified by comparing a level-59 FIGHTER's vector against a level-21 MAGE's: the Fighter's
    /// ids 11 and 12 sit at 48-59 — exactly its untrained INT (id 3) — while the Mage's are 112 and 125.
    /// A Fighter has no magic attack and a Mage's is its main stat, so the pair is unambiguous. Id 13 is
    /// magic defence by the same argument (Fighter 242-348, Mage 84).</para></summary>
    private const string MaMin = "11", MaMax = "12", Mr = "13";

    /// <summary>The magical mirror of <see cref="Exactly"/>: a container whose `roe_MinMA` / `roe_MaxMA` /
    /// `roe_MR` return exactly the numbers the wire gave.
    ///
    /// <para>Int governs magic attack the way Str governs weapon damage, and Men governs magic resistance
    /// the way Con governs armour — so the same floor-at-1 trick applies to the other pair.</para></summary>
    private static Combatant ExactlyMagical(int level, int minMa, int maxMa, int magicResist)
    {
        var p = new ParameterContainer();
        p.Base[Stat.Int] = 1;
        p.Base[Stat.MAmin] = minMa - 1;
        p.Base[Stat.MAmax] = maxMa - 1;
        p.Base[Stat.Men] = 1;
        p.Base[Stat.MR] = magicResist - 1;
        return new Combatant(level, p);
    }

    /// <summary>The MAGICAL half of the same skill row. `roe_AttackPower@MagicalSkill` reads +0xEB/+0xEF/
    /// +0xF3/+0xF7 (`MinMA`/`MinMARate`/`MaxMA`/`MaxMARate`) where the physical one reads +0xDB..+0xE7 —
    /// the two are a mirrored pair and a magical skill's WC columns are ZERO, so loading the wrong half
    /// silently contributes nothing at all.</summary>
    private static IReadOnlyDictionary<int, Fiesta.Emu.Zone.Skill.ActiveSkillInfo> SkillRowsMagical(string ressystem)
    {
        var table = ShnFile.Load(Path.Combine(ressystem, "ActiveSkill.shn"));
        var rows = new Dictionary<int, Fiesta.Emu.Zone.Skill.ActiveSkillInfo>();
        foreach (var r in table.Rows)
            rows[ShnFile.Int(r, "ID")] = new Fiesta.Emu.Zone.Skill.ActiveSkillInfo(
                MinFlat: (uint)ShnFile.Int(r, "MinMA"),
                MinRatePermille: (uint)ShnFile.Int(r, "MinMARate"),
                MaxFlat: (uint)ShnFile.Int(r, "MaxMA"),
                MaxRatePermille: (uint)ShnFile.Int(r, "MaxMARate"),
                Empower: EmpowerTable(r));
        return rows;
    }

    private static string? Ressystem()
    {
        var root = Environment.GetEnvironmentVariable("CLIENT_DATA") ?? @"Z:/ClientProd2/ressystem";
        return File.Exists(Path.Combine(root, "ActiveSkill.shn")) ? root : null;
    }

    /// <summary>⭐ Every clean SKILL hit must sit inside the band the engine can produce.
    ///
    /// <para>Same pipeline as the swing check, with two additions: the rule is
    /// <see cref="EngagementRule.PhysicalSkill"/>, and the cast's `ActiveSkillInfo` row is threaded into
    /// attack power — where its flat terms land on the weapon BOUNDS before the roll, not on the
    /// result.</para>
    ///
    /// <para>The skills in this capture all carry `MinWCRate`/`MaxWCRate` of 0, so their contribution is
    /// purely the flat add. That is a property of the DATA and not of the code — the independent-bounds
    /// widening is real and simply unexercised here.</para>
    ///
    /// <para>⭐ <b>THE MULTI-HIT SPLIT was the dominant missing term</b>, and finding it moved the fit from
    /// 216 to 443 inside. `TripleHit06` fires `MultiHitType` sequence 20 — three strikes at
    /// <b>300 / 200 / 500</b> permille summing to 1000 — so each wire hit is a FRACTION of what
    /// `roe_CalcDamage` produced, and predicting every hit as the whole thing overshot by 3-5x.</para>
    ///
    /// <para>⭐ <b>545 of 545, over nine skills.</b> The last two residuals were not one bug, and neither
    /// was a formula error: one was a bug in the HARNESS, the other a missing per-character input.</para>
    ///
    /// <para><b>1. A hit does not benefit from the debuff it applies.</b> `RedSlash` takes 78 flat off the
    /// target's AC (`SubStaRedSlash` action 73), and the server broadcasts that ABSTATESET immediately
    /// before the damage packet of the hit that applied it — same timestamp, so only the frame ORDER
    /// separates them. `damage_buckets.py` was tagging the hit with a debuff the hit itself caused, handing
    /// the defender -78 AC on damage the server had resolved at full armour. That was 68 of `RedSlash`'s
    /// 71 hits; the other 3 were second hits on the same mob, which genuinely do benefit and already
    /// fitted. Measured, the second hit on a handle runs 1.30x the first (n=3, 1.216-1.413). Fixed by
    /// tagging a skill hit with the state as of the order its CAST opened.</para>
    ///
    /// <para><b>2. Skill empower.</b> `PowerHit05` was over by a consistent 1.20-1.34x — tight enough to be
    /// a missing constant rather than a missing input. It is `PROTO_SKILLREADBLOCKCLIENT.empow`, a
    /// `SKILL_EMPOWER` bitfield the CHARACTER carries and no data file holds: this one has
    /// <c>damage 5</c> on the `PowerHit` line and on no other line. `nT0[5]` = 376 is then the unique tier
    /// that fits — the observations alone bound the missing flat to [331, 418] and tiers 1-4 all fall
    /// short. See <see cref="SkillLineBase"/> for why the allocation is read off the line's first rank.
    /// Reading it at all needed `--hex-limit` raised: at `pcap_decode`'s default of 128 bytes only 9 of
    /// the packet's 65 skill records survived, and the allocation simply was not there.</para>
    ///
    /// <para><b>How the two were told apart.</b> Damage is LINEAR in the flat add, so bracketing each
    /// bucket at scale 0.0 and 1.0 SOLVES for the flat that would fit it rather than guessing. Seven
    /// skills straddled 1.0 — correct as modelled. Skill 124 needed [1.313, 1.440]: tight, consistent, a
    /// missing constant. Skill 45 wanted anything from -0.111 to 1.404, which no constant can be, and its
    /// residual tracked WEAPON SIZE — the signature of a wrong model shape, not a missing term.</para>
    ///
    /// <para>Ruled OUT along the way, each on evidence: duplicate rows in `ActiveSkill.shn` (only id 9034
    /// repeats, unrelated); weapon mastery (a uniform 1150 across every skill and weapon here);
    /// `MiscDataTable.txt` (it is `SkillBreedMob`, summons and traps); `DamageByAngle`, which can only
    /// RAISE damage and so cannot explain an undershoot; and the rank-substitution theory, which died when
    /// `NC_CHAR_CLIENT_SKILL_CMD` showed the character holding exactly ids 45 and 124 — the ids on the
    /// wire. `SKILL_ROW_SUBST` survives to re-run that experiment, not to be switched on.</para></summary>
    [SkippableFact]
    public void EverySkillHitInTheCaptureFallsInsideItsPredictedBand()
    {
        var path = FixturePath();
        var shine = Shine();
        var ressystem = Ressystem();
        Skip.If(path is null, "no bucket fixture; see the class comment for how to make one");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");

        var f = Load(path!);
        Skip.If(f.SkillBuckets.Count == 0, "fixture has no skill buckets -- re-run damage_buckets.py");

        var box = MobDataBox.Load(shine!);
        var gaps = LevelGapTable.Load(shine!);
        var mobNames = box.Info.Where(kv => kv.Value.Id > 0)
                              .GroupBy(kv => kv.Value.Id)
                              .ToDictionary(g => g.Key, g => g.First().Key);
        var skills = SkillRows(ressystem!);
        var hitIds = SkillHitIds(ressystem!);
        var lineBase = SkillLineBase(ressystem!);
        var multiHit = MultiHitRates(shine!);
        var abState = ShnFile.Load(Path.Combine(shine!, "AbState.shn"));
        var subAbState = ShnFile.Load(Path.Combine(shine!, "SubAbState.shn"));
        var passiveTable = ShnFile.Load(Path.Combine(shine!, "PassiveSkill.shn"));
        var items = ShnFile.Load(Path.Combine(shine!, "ItemInfo.shn"));
        var jobByLevel = f.SkillBuckets.Select(b => b.Level).Distinct()
                                       .ToDictionary(l => l, l => JobChangeRate(f, shine!, l));

        int inside = 0, over = 0, under = 0, skipped = 0, hits = 0;
        var report = new List<string>();
        // ⚠️ Refusals get a REASON. A single `skipped` counter hides which gate is rejecting, and this
        // test first shipped refusing all 590 hits while reporting a pass -- the coverage floor below
        // caught it, but only the breakdown said WHY (a null weapon, so MasteryRate returned null).
        var why = new SortedDictionary<string, int>();
        void Refuse(string reason, int n)
        {
            skipped += n;
            why[reason] = why.GetValueOrDefault(reason) + n;
        }

        foreach (var b in f.SkillBuckets.Where(b => b.Side == "OUT")
                           .OrderBy(b => b.Skill).ThenBy(b => b.Mob))
        {
            if (!skills.TryGetValue(b.Skill, out var row)) { Refuse("skill not in ActiveSkill.shn", b.N); continue; }
            // `SKILL_FLAT_SCALE` scales the skill's flat WC add. Damage is LINEAR in it (roe_Damage is
            // (level+1)*attack/defend), so running 0.0 and 1.0 brackets every bucket and the scale that
            // fits an observation can be solved rather than guessed.
            if (Environment.GetEnvironmentVariable("SKILL_FLAT_SCALE") is { } sc
                && double.TryParse(sc, System.Globalization.CultureInfo.InvariantCulture, out var scale))
                row = row with { MinFlat = (uint)(row.MinFlat * scale), MaxFlat = (uint)(row.MaxFlat * scale) };
            // `SKILL_ROW_SUBST=45:42,124:126` predicts skill 45 using skill 42's row -- the experiment for
            // "the wire id and the row disagree", run per skill instead of as one global scale.
            if (Environment.GetEnvironmentVariable("SKILL_ROW_SUBST") is { } subst)
                foreach (var pair in subst.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[0], out var from) && from == b.Skill
                        && int.TryParse(kv[1], out var to) && skills.TryGetValue(to, out var other))
                        row = other;
                }
            if (!mobNames.TryGetValue(b.Mob, out var name)) { Refuse("mob id not named", b.N); continue; }
            var mob = MobCombatant.Build(box, name);
            if (mob is null) { Refuse("mob has no combat data", b.N); continue; }
            if (!ApplyAbstates(mob.Parameters, b.EnemyAbstates, abState, subAbState)) { Refuse("unread abstate action", b.N); continue; }
            if (!b.Params.TryGetValue(DmgMin, out var dmin) || !b.Params.TryGetValue(DmgMax, out var dmax))
            { Refuse("no DmgMin/DmgMax on the wire yet", b.N); continue; }

            var rate = MasteryRate(passiveTable, items, b.Passives, b.Weapon);
            if (rate is not { } mastery) { Refuse("no weapon known, so no mastery rate", b.N); continue; }

            var freeStr = FreeStatStr(f.FreeStat.GetValueOrDefault("Strength"));
            var me = Exactly(b.Level, dmin - freeStr, dmax - freeStr, 1, mastery);
            var band = Through(me, mob, new AttackModifiers
            {
                ForceCritical = false,
                AttackerFreeStat = freeStr,
                DefenderFreeStat = 0,
                JobChangeDamageUpPermille = jobByLevel.GetValueOrDefault(b.Level),
                LevelGapRatePermille = gaps.Rate(CombatantKind.Player, b.Level,
                                                 CombatantKind.Monster, mob.Level),
                Skill = row,
                // The allocation is per LINE and the wire reports it on the line's base rank.
                Empower = new Fiesta.Emu.Zone.Skill.SkillEmpower((ushort)f.SkillEmpower
                    .GetValueOrDefault(lineBase.GetValueOrDefault(b.Skill, b.Skill))),
            }, EngagementRule.PhysicalSkill);

            // THE MULTI-HIT SPLIT. A sequence's strikes each carry their own permille of the finished
            // damage, so a hit from a multi-hit skill is a FRACTION of what roe_CalcDamage produced.
            // HitID 0 means a single strike and leaves the band alone.
            var hitId = hitIds.GetValueOrDefault(b.Skill);
            if (hitId != 0 && multiHit.TryGetValue(hitId, out var span))
                band = new Band(Fiesta.Emu.Zone.Skill.MultiHit.HitDamage(band.Floor, span.Low),
                                Fiesta.Emu.Zone.Skill.MultiHit.HitDamage(band.Ceiling, span.High));

            // Diagnostic dump: one line per bucket, so the SHAPE of a mismatch is visible (a consistent
            // ratio means a missing multiplier; scattered means a missing input).
            if (Environment.GetEnvironmentVariable("SKILL_DIAG") is not null)
                report.Add($"DIAG skill={b.Skill} hitId={hitId} mob={b.Mob} n={b.N} mastery={mastery} wpn={b.Weapon} "
                         + $"obs={b.Min}..{b.Max} pred={band.Floor}..{band.Ceiling} "
                         + $"loRatio={(band.Floor == 0 ? 0 : (double)b.Min / band.Floor):F3} "
                         + $"hiRatio={(band.Ceiling == 0 ? 0 : (double)b.Max / band.Ceiling):F3}");

            hits += b.N;
            if (b.Max > band.Ceiling) { over += b.N; report.Add($"skill {b.Skill} mob {b.Mob}: max {b.Max} > ceiling {band.Ceiling}"); }
            else if (b.Min < band.Floor) { under += b.N; report.Add($"skill {b.Skill} mob {b.Mob}: min {b.Min} < floor {band.Floor}"); }
            else inside += b.N;
        }

        var summary = $"skill hits: {inside} inside, {over} over, {under} under, {skipped} unpredictable "
                    + $"(of {hits + skipped})"
                    + (why.Count > 0 ? "  [" + string.Join("; ", why.Select(k => $"{k.Key}={k.Value}")) + "]" : "");
        report.Insert(0, summary);
        // `SKILL_DIAG=<path>` writes the whole per-bucket dump out, because a PASSING test never surfaces
        // its report and the dump is the point when hunting a residual.
        if (Environment.GetEnvironmentVariable("SKILL_DIAG") is { } diag && diag.Length > 1)
            File.WriteAllLines(diag, report);

        // ⚠️ A COVERAGE FLOOR, so this cannot pass vacuously. Every refusal path above (`continue`) is a
        // hit this port declined to predict, and this test FIRST SHIPPED refusing all 590 while reporting
        // a pass -- the floor is what caught it. Keep it.
        inside.ShouldBeGreaterThan(500, "predicted too few skill hits to mean anything: " + summary);
        // Refusing a hit is the other way a green suite hides one it cannot predict.
        skipped.ShouldBe(0, "some hits were refused rather than predicted: " + summary);

        // ⭐ EXACT: 545 of 545 inside, nothing over a ceiling and nothing under a floor.
        //
        // ⚠️ And the bands are TIGHT, which is the claim that actually matters -- a wide enough band would
        // swallow anything. Across the nine skills the closest observation sits at 1.000-1.045x its floor
        // and 0.961-1.000x its ceiling, so the samples press against BOTH edges; `TripleHit06` touches
        // each bound exactly.
        over.ShouldBe(0, "a hit exceeded the ceiling: " + summary);
        under.ShouldBe(0, "a hit fell under the floor: " + summary);
    }

    /// <summary>⭐ MAGICAL skill damage — `roe_magical`, exercised for the first time.
    ///
    /// <para>Every mage skill in the capture is `SkillHitType=1`, which `sdb_Load` maps to
    /// <c>roe_magical</c> (the jump table at 0x587CC8 selects the rule from that field). So this runs
    /// <see cref="EngagementRule.MagicalSkill"/>, which draws on Int/MAmin/MAmax, defends on the mob's
    /// magic resistance, and — unlike every other rule — applies NO weapon mastery.</para>
    ///
    /// <para>Fixture comes from a live MageZero packet log rather than a pcap; see `tools/mage_fixture.py`,
    /// which walks the bot's own log format and emits the same JSON.</para>
    ///
    /// <para>⛔ <b>RED: the magical path under-predicts, 37 of 37 above the ceiling.</b> Fixture is a real
    /// level-60 client capture (`MageDamageLvl60.pcapng`, class 18 `Enchanter`), 37 clean hits over 8
    /// skills, nothing refused. The miss runs 1.28x to 1.76x.</para>
    ///
    /// <para><b>The shape says an INPUT is wrong, not a coefficient.</b> The multiplier each skill needs
    /// varies INVERSELY with its own `MinMA` — `FireBall01` (MinMA 2840) needs ~1.07-1.31 while
    /// `LightningBolt08` (327) needs ~1.31-1.70 — which is what a missing ADDITIVE term on the base magic
    /// attack looks like. But a pure additive does not fit either (`MagicBall01` wants C in [722, 975]
    /// where every other skill wants [320, 500]), and a grid search over combined (additive C,
    /// multiplier M) against all 19 buckets returns NO solution. Several buckets need the predicted band
    /// to span a range wider than any (C, M) produces.</para>
    ///
    /// <para>Ruled OUT on data:</para>
    /// <list type="bullet">
    /// <item><b>Job-change damage-up.</b> Wired now, and this character is class 18 whose
    /// `JobChangeDmgUp` is 1700 permille at level 60 — a real 1.7x that the Fighter capture could never
    /// exercise, since class 1 is 1000. It is NOT the cause: the level-21 base-Mage bot log, whose rate is
    /// 1000 and so applies nothing at all, under-predicts by the same ~1.3x.</item>
    /// <item><b>Weapon mastery.</b> The weapon is a `FairyWand` (WeaponType 11, two-hand), and the mage's
    /// `WandMastery05` gives `MstPlWand2` = +34 — a flat PLUS, not a rate, and already inside the 622 the
    /// wire reports, since that number is the accessor's OUTPUT.</item>
    /// <item><b>The MA parameter mapping.</b> Ids 11/12 read 622/702 against a weapon pair (6/7) of 85/98
    /// and an Int (3) of 288 — the only pair that can be a level-60 mage's magic attack.</item>
    /// <item><b>Multi-hit, AoE division, empower, free-stat INT.</b> Every skill carries `HitID` 0;
    /// `TargetNumber` is 1 except `MagicBurst03`; the empower allocation is read from the wire; and the
    /// free-stat block is all zeros on this character.</item>
    /// <item><b>The mob's magic resistance</b>, by symmetry: `roe_AC` is Con-chain + AC and the physical
    /// side is exact at 545/545 with it, so `roe_MR` = Men-chain + MR should be sound the same way.</item>
    /// </list>
    ///
    /// <para>⭐ <b>EVERY LINK IN THE MAGICAL CHAIN HAS NOW BEEN RUN AGAINST THE REAL FUNCTION, AND EVERY
    /// ONE IS EXACT.</b> `tools/oracle_magical.py` and `tools/oracle_magical_skill.py` execute the
    /// server's own code under emulation rather than fitting coefficients:</para>
    ///
    /// <list type="bullet">
    /// <item>`roe_MinMA` / `roe_MaxMA` / `roe_MR` — mage 622 / 702 / 468, Orc MR 239, Pinky MR 319. All
    /// match.</item>
    /// <item>`roe_AttackPower@MagicalSkill` — eight cases from the capture: plain, four real skill rows,
    /// the empower allocation, and each rate scaled alone. All match, including that `MinMARate` moves
    /// only the low bound and `MaxMARate` only the high.</item>
    /// <item>`roe_DefendPower@MagicalSkill` — Orc 239, Pinky 319. Matches.</item>
    /// <item>`roe_Damage` — ⚠️ the two paths do NOT share it: `MagicalSkill` inherits
    /// `NormalMA::roe_Damage` while `PhisycalSkill` inherits `NormalPY::roe_Damage`. Run side by side they
    /// return identical values for these inputs, and both equal the port's <c>(level+1)*attack/defend</c>.</item>
    /// <item>`roe_LevelGapDamageRevision` — returns 1000 for every level pair in the capture.</item>
    /// <item>`JobChangeDmgUp` — exact by its own oracle, and NOT magic-specific: the Fighter capture's
    /// character is class 3 `Warrior`, whose rate at level 60 is <b>1700, the same as `Enchanter`</b>. So
    /// the physical side exercises the 1.7x multiply too and is exact at 545/545 with it.</item>
    /// <item>`DamageByAngle` — the deployed table is flat 1000 (read live from two zones) and spans only
    /// 1000-1200 even in the stock file, so it cannot account for 1.28x-1.76x.</item>
    /// </list>
    ///
    /// <para>Also eliminated from the FIXTURE side: the mage carries no self-buffs (every bucket's
    /// `selfAbstates` is empty); its free-stat block really is all zeros in the packet, not a parse
    /// failure; the level parses as 60 from `+25`; and the SAME fixture's 56 incoming swings pass
    /// <see cref="NoBucketExceedsWhatAMaximumRollCanProduce"/>, which independently validates the player's
    /// level and defence and the mob data.</para>
    ///
    /// <para>⭐ <b>So the residual is an INPUT this harness does not have, not a formula error.</b> Solving
    /// each bucket for the attack power that would reproduce it gives a strikingly consistent
    /// <b>+460 to +490 on attack</b> across most Orc buckets — a constant ADD, not a multiplier, on a
    /// quantity the oracle has already proved the engine computes correctly from `roe_MinMA`. Which means
    /// the wire's magic-attack pair is not the whole input: something contributes ~470 that
    /// `NC_CHAR_CHANGEPARAMCHANGE_CMD` ids 11/12 do not report.</para>
    ///
    /// <para>⚠️ Recorded as an OBSERVATION and explicitly not adopted: the character's id 13 is 468, and
    /// 622 + 468 = 1090 sits inside the required 1082-1112. That is a coincidence of one number until a
    /// mechanism says otherwise — adding a defensive stat to attack power makes no sense — and fitting it
    /// would be exactly the curve-fitting the oracles exist to avoid.</para>
    ///
    /// <para>⭐ <b>LEADING HYPOTHESIS: ITEM ACTIONS.</b> `roe_CalcDamage` calls `EventRun` and
    /// <c>GetPlusAppliValue</c> against BOTH combatants' item-action managers, and
    /// <see cref="DamageCalculator.AttackPower"/> already models the result as applying to EACH BOUND
    /// before the roll rather than to the rolled figure. `AttackModifiers.ItemActions` is the input, and
    /// BOTH ground-truth tests pass it as <c>null</c> — a pcap does not state a character's item actions,
    /// so nothing has ever populated it.</para>
    ///
    /// <para>That is the right SHAPE for this residual: a per-character constant, added to both bounds,
    /// invisible to every stat packet, and identical across skills because the gear does not change
    /// between them. It also explains why the physical capture is unaffected — a different character with
    /// different equipment. The capture carries the character's items
    /// (`NC_CHAR_CLIENT_ITEM_CMD`, and `NC_ITEM_EQUIPCHANGE_CMD` for swaps), so the next step is to read
    /// the equipped set and its options out of the wire and drive `ItemActionResults` from them —
    /// evidence, not another fit.</para></summary>
    [SkippableFact]
    public void MagicalSkillDamageIsTrackedAgainstItsBaseline_KNOWN_RED()
    {
        var path = Environment.GetEnvironmentVariable("MAGE_BUCKETS");
        var shine = Shine();
        var ressystem = Ressystem();
        Skip.If(path is null || !File.Exists(path), "no mage fixture; set MAGE_BUCKETS");
        Skip.If(shine is null, "server data not present; set SHINE_DATA");
        Skip.If(ressystem is null, "client data not present; set CLIENT_DATA");

        var f = Load(path!);
        Skip.If(f.SkillBuckets.Count == 0, "mage fixture has no skill buckets");

        var box = MobDataBox.Load(shine!);
        var gaps = LevelGapTable.Load(shine!);
        var mobNames = box.Info.Where(kv => kv.Value.Id > 0)
                              .GroupBy(kv => kv.Value.Id)
                              .ToDictionary(g => g.Key, g => g.First().Key);
        var skills = SkillRowsMagical(ressystem!);
        var lineBase = SkillLineBase(ressystem!);
        var abState = ShnFile.Load(Path.Combine(shine!, "AbState.shn"));
        var subAbState = ShnFile.Load(Path.Combine(shine!, "SubAbState.shn"));
        // ⭐ THE JOB-CHANGE MULTIPLIER IS NOT OPTIONAL HERE. This capture's character is class 18,
        // `Enchanter`, whose `JobChangeDmgUp` at level 60 is 1700 permille — a 1.7x on every hit it deals
        // to a monster. Passing `null` (as this test did while it only had a base-Mage bot log, where the
        // value is 1000) under-predicts by that whole factor.
        var jobByLevel = f.SkillBuckets.Select(b => b.Level).Distinct()
                                       .ToDictionary(l => l, l => JobChangeRate(f, shine!, l));

        // `FreeStat` is read from the fixture, which reads it from `NC_CHAR_CLIENT_BASE_CMD` +0x57.
        // `MAGE_FREE_INT` remains only as a probe for a fixture that predates that being captured; it
        // must never be used to make numbers fit — the wire reports the allocation.
        var freeInt = int.TryParse(Environment.GetEnvironmentVariable("MAGE_FREE_INT"), out var fi)
            ? fi : f.FreeStat.GetValueOrDefault("Intelligence");

        int inside = 0, over = 0, under = 0, skipped = 0;
        var report = new List<string>();

        foreach (var b in f.SkillBuckets.Where(b => b.Side == "OUT").OrderBy(b => b.Skill))
        {
            if (!skills.TryGetValue(b.Skill, out var row)) { skipped += b.N; continue; }
            if (!mobNames.TryGetValue(b.Mob, out var name)) { skipped += b.N; continue; }
            var mob = MobCombatant.Build(box, name);
            if (mob is null) { skipped += b.N; continue; }
            if (!ApplyAbstates(mob.Parameters, b.EnemyAbstates, abState, subAbState)) { skipped += b.N; continue; }
            if (!b.Params.TryGetValue(MaMin, out var amin) || !b.Params.TryGetValue(MaMax, out var amax))
            { skipped += b.N; continue; }

            // ⚠️ The MAGICAL skill row is MinMA/MaxMA, not MinWC/MaxWC -- `roe_AttackPower@MagicalSkill`
            // reads +0xEB/+0xEF/+0xF3/+0xF7 where the physical one reads +0xDB..+0xE7.
            var me = ExactlyMagical(b.Level, amin, amax, 1);
            var band = Through(me, mob, new AttackModifiers
            {
                ForceCritical = false,
                // `roe_Damage@NormalMA`'s per-rule override adds the attacker's FreeStatInt and subtracts
                // the defender's FreeStatMen; MagicalSkill shares that override. A mob's free-stat
                // accessors return table[0] = 0, so only the attacker's term is live.
                AttackerFreeStat = FreeStatInt(freeInt),
                DefenderFreeStat = 0,
                JobChangeDamageUpPermille = jobByLevel.GetValueOrDefault(b.Level),
                LevelGapRatePermille = gaps.Rate(CombatantKind.Player, b.Level,
                                                 CombatantKind.Monster, mob.Level),
                Skill = row,
                // Allocated per skill LINE and reported on the line's base rank — see SkillLineBase.
                Empower = new Fiesta.Emu.Zone.Skill.SkillEmpower((ushort)f.SkillEmpower
                    .GetValueOrDefault(lineBase.GetValueOrDefault(b.Skill, b.Skill))),
            }, EngagementRule.MagicalSkill);

            report.Add($"DIAG skill={b.Skill} mob={b.Mob} n={b.N} obs={b.Min}..{b.Max} "
                     + $"pred={band.Floor}..{band.Ceiling} "
                     + $"loRatio={(band.Floor == 0 ? 0 : (double)b.Min / band.Floor):F3} "
                     + $"hiRatio={(band.Ceiling == 0 ? 0 : (double)b.Max / band.Ceiling):F3}");

            if (b.Max > band.Ceiling) over += b.N;
            else if (b.Min < band.Floor) under += b.N;
            else inside += b.N;
        }

        var summary = $"magical hits: {inside} inside, {over} over, {under} under, {skipped} unpredictable";
        report.Insert(0, summary);
        if (Environment.GetEnvironmentVariable("SKILL_DIAG") is { } diag && diag.Length > 1)
            File.WriteAllLines(diag, report);

        // ⛔ RED, AND HONESTLY SO. Every magical hit lands ABOVE the predicted ceiling -- 10 of 10 on the
        // MageZero sample -- so the magical path under-predicts. Asserted the same way the physical one
        // is, because a weaker assertion is how this went unnoticed: the previous version asserted only
        // "more than 20 hits were predicted" and passed while 28 of 28 sat outside the band.
        skipped.ShouldBe(0, "some magical hits were refused rather than predicted: " + summary);
        over.ShouldBe(0, "a magical hit exceeded the ceiling: " + summary);
        under.ShouldBe(0, "a magical hit fell under the floor: " + summary);
    }
}
