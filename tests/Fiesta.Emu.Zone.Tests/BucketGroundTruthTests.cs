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

    private sealed record Fixture(IReadOnlyList<Bucket> Buckets, IReadOnlyDictionary<string, int> FreeStat,
                                  int ChrClass);

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
        var buckets = root.GetProperty("buckets").EnumerateArray().Select(b => new Bucket(
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

        return new Fixture(buckets,
            root.GetProperty("freeStat").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()),
            root.GetProperty("chrclass").GetInt32());
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
    private sealed record AbstateAction(bool Rate, int Sign, params Stat[] Stats);

    private static readonly Dictionary<int, AbstateAction> AbstateActions = new()
    {
        [4] = new(true, +1, Stat.WCmin, Stat.WCmax),
        [94] = new(false, -1, Stat.WCmin, Stat.WCmax),
        [73] = new(false, -1, Stat.AC),
        [74] = new(true, -1, Stat.AC),
        [81] = new(false, -1, Stat.Dex),
        [18] = new(true, +1, Stat.ShieldAC),
        [21] = new(true, +1, Stat.AttSpeed),
    };

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

            var row = subAbState.Rows.FirstOrDefault(
                r => ShnFile.Str(r, "InxName") == name && ShnFile.Int(r, "Strength") == ab.Strength);
            if (row is null) return false;

            foreach (var slot in new[] { "A", "B", "C", "D" })
            {
                var index = ShnFile.Int(row, "ActionIndex" + slot);
                var arg = ShnFile.Int(row, "ActionArg" + slot);
                if (index == 0) continue;
                if (!AbstateActions.TryGetValue(index, out var action)) return false;
                foreach (var stat in action.Stats)
                {
                    var cluster = action.Rate ? p.Rate(StatModifier.AbnormalState)
                                              : p.Plus(StatModifier.AbnormalState);
                    cluster[stat] += action.Sign * arg;
                }
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
    private static Band Through(ICombatant attacker, ICombatant defender, AttackModifiers mods)
    {
        var rng = new System.Random(1);
        var lo = DamageCalculator.ResolveDamage(attacker, defender, mods with { RollPermille = 0 }, rng);
        var hi = DamageCalculator.ResolveDamage(attacker, defender, mods with { RollPermille = 1000 }, rng);
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
}
