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
    private sealed record Bucket(string Side, int Mob, int Level, IReadOnlyList<int> SelfAbstates,
                                 IReadOnlyList<int> EnemyAbstates, IReadOnlyDictionary<string, int> Params,
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
    /// <para>⚠️ <b>The weapon is only as good as the capture's tracking.</b>
    /// `NC_BRIEFINFO_CHANGEWEAPON_CMD` does not broadcast every one of our own swaps, so the fixture
    /// carries last-known. In THIS capture every weapon held — Splitter and Kaineneceflight (WeaponType 1,
    /// one-handed) and Kainenecefury (4, two-handed) — selects a column worth 150, so the rate is 1150
    /// either way and the gap cannot change the answer. A capture that swapped across weapon TYPES without
    /// a broadcast would need a better source, and this would be silently wrong.</para></summary>
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
                                 int? masteryRate)
    {
        if (!mobNames.TryGetValue(b.Mob, out var name)) return null;
        var mob = MobCombatant.Build(box, name);
        if (mob is null) return null;

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

    /// <summary>Predict every bucket and require that no clean hit exceeds what a maximum roll can produce.
    ///
    /// <para>Asserted on OVERSHOOT only. Buckets below the floor are reported but do not fail: that is what
    /// an unmodelled damage REDUCTION looks like, and in this capture every one of them carries an enemy
    /// abstate.</para></summary>
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

        int hits = 0, inside = 0, predicted = 0, unpredictable = 0;
        int worstOver = 0, overHits = 0, underHits = 0, overBuckets = 0;
        int overWithNoEnemyAbstate = 0, underWithNoEnemyAbstate = 0;
        var report = new List<string>();

        foreach (var b in f.Buckets.OrderBy(b => b.Side).ThenBy(b => b.Mob).ThenBy(b => b.Level)
                           .ThenByDescending(b => b.N))
        {
            var band = Predict(f, b, box, gaps, mobNames, jobByLevel[b.Level],
                               MasteryRate(passiveTable, items, b.Passives, b.Weapon));
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

            if (b.N >= 5)
                report.Add($"  {b.Side,-4} mob {b.Mob,-4} lv{b.Level} n={b.N,-3}"
                           + $" observed {b.Min}..{b.Max}  predicted {p.Floor}..{p.Ceiling}"
                           + (over > 0 ? $"  OVER BY {over}  enemy=[{string.Join(",", b.EnemyAbstates)}]"
                              : under > 0 ? $"  under by {under}  enemy=[{string.Join(",", b.EnemyAbstates)}]"
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
        overWithNoEnemyAbstate.ShouldBe(0, summary);
    }
}
