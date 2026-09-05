using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>Running the REAL levelling driver against this simulation, and measuring the gap.
///
/// <para>The bot's `level_quest.lua` is ~6,200 lines and reaches for ~180 distinct `bot.*` functions —
/// quests, inventory, shops, storage, travel, skills, mounts. This simulation implements the combat subset.
/// Rather than guess which parts would work, this harness gives the script the WHOLE surface: the real
/// implementation where the simulation has the concept, and a counted stub where it does not.</para>
///
/// <para>The output is therefore a measurement, not an opinion — "the script called these N functions, M of
/// them were real, and here are the stubs it leaned on hardest". That list is the actual specification for
/// what the simulator would need next, in the order the driver cares about.</para>
///
/// <para><b>The surface is discovered from the script, not hardcoded.</b> Any `bot.&lt;name&gt;` the source
/// mentions gets registered, so this adapts to whatever driver is pointed at it and there is no list to
/// drift out of date.</para></summary>
public sealed class LevelingBotHarness
{
    private readonly CombatSimulation _sim;
    private readonly SimBotApi _api;
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stubs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _real = new(StringComparer.Ordinal);

    private LevelingBotHarness(CombatSimulation sim)
    {
        _sim = sim;
        _api = new SimBotApi(sim);
    }

    /// <summary>Every `bot.*` name the script called, with its call count.</summary>
    public IReadOnlyDictionary<string, int> Calls => _calls;

    /// <summary>Names that were called but are NOT backed by the simulation.</summary>
    public IReadOnlySet<string> StubsCalled => _stubs;

    /// <summary>Names that were called and ARE backed by the simulation.</summary>
    public IReadOnlySet<string> RealCalled => _real;

    /// <summary>What went wrong, if the script raised. An empty list means it ran to completion.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>How each stubbed name was classified — for diagnosing the harness itself.</summary>
    public string ShapeOf(string name)
        => _tableShaped.Contains(name) ? "table" : _numberShaped.Contains(name) ? "number" : "heuristic";

    /// <summary>Names mentioned in the source, whether or not they were reached at runtime.</summary>
    public IReadOnlyList<string> Surface { get; private set; } = [];

    private static readonly Regex BotCall = new(@"\bbot\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>Host-provided globals the driver assumes exist. They are NOT defined anywhere in the
    /// script -- the bot host registers them -- so without these it dies on its first log line.</summary>
    private static readonly string[] HostGlobals = ["log", "logi", "logv", "logn", "logd", "print"];

    /// <summary>Everything the script writes through those globals, in order. Valuable on its own: it is
    /// the driver narrating its own decisions.</summary>
    public List<string> Output { get; } = [];

    /// <summary>A kill quest the driver can see and make progress on.
    ///
    /// <para>Enough of a quest for a grind loop to be real: a name, a target mob, a required count, and
    /// progress that comes from the simulation's own kill tally rather than a counter the harness bumps.
    /// It is NOT a port of the quest system — `QuestData.shn`, objectives, rewards and hand-in are all
    /// absent — it is the smallest thing that makes "grind until done" a genuine loop.</para></summary>
    public sealed record KillQuest(int Id, string Name, string MobName, int MobId, int Need);

    /// <summary>Stub values where ZERO is not neutral but BLOCKING.
    ///
    /// <para>The default stub hands back 0, which is a safe nothing for most numbers and actively wrong for
    /// a few: 0 free bag slots reads as a full bag, and the driver locks itself into an inventory-sort phase
    /// and never fights again. It is the same trap as a sentinel — a value that means "no information" being
    /// read as a real measurement.</para>
    ///
    /// <para>Callers can add to this; the defaults exist so a driver is not wedged by the harness's own
    /// silence.</para></summary>
    public Dictionary<string, double> StubDefaults { get; } = new(StringComparer.Ordinal)
    {
        ["bagFreeSlots"] = 40,
        ["maxHpStones"] = 100,
        ["maxSpStones"] = 100,
        ["hpStones"] = 50,
        ["spStones"] = 50,
        ["money"] = 1_000_000,
        ["invenCount"] = 0,
        ["hpStonePrice"] = 10,
        ["spStonePrice"] = 10,
    };

    /// <summary>Quests the driver is told it has.</summary>
    public List<KillQuest> Quests { get; } = [];

    /// <summary>How far along a kill quest is, from the simulation's own per-mob kill tally.</summary>
    public int ProgressOf(KillQuest q) => Math.Min(q.Need, _sim.KillsByName.GetValueOrDefault(q.MobName));

    /// <summary>Attach the harness to a simulation and load a driver script into it.</summary>
    public static LevelingBotHarness Attach(CombatSimulation sim, string luaSource)
    {
        var h = new LevelingBotHarness(sim);
        h.Surface = BotCall.Matches(luaSource).Select(m => m.Groups[1].Value).Distinct().Order().ToList();
        // Evidence, strongest first: direct indexing of the CALL > arithmetic on the call > a local that
        // was assigned from it and indexed somewhere in the file.
        h._tableShaped = TableShaped(luaSource);
        h._numberShaped = NumberShaped(luaSource);
        h._numberShaped.ExceptWith(h._tableShaped);

        var weak = WeaklyTableShaped(luaSource);
        weak.ExceptWith(h._numberShaped);
        h._tableShaped.UnionWith(weak);

        var script = sim.Script;
        var bot = new Table(script);
        var real = h.RealImplementations();

        foreach (var name in h.Surface)
        {
            var captured = name;
            if (real.TryGetValue(captured, out var impl))
            {
                bot[captured] = DynValue.NewCallback((_, a) =>
                {
                    h.Record(captured, isReal: true);
                    return impl(a);
                });
            }
            else
            {
                bot[captured] = DynValue.NewCallback((_, _) =>
                {
                    h.Record(captured, isReal: false);
                    return h.NeutralFor(captured, script);
                });
            }
        }

        script.Globals["bot"] = bot;

        foreach (var g in HostGlobals)
        {
            script.Globals[g] = DynValue.NewCallback((_, a) =>
            {
                var parts = new List<string>();
                for (var i = 0; i < a.Count; i++) parts.Add(a[i].ToPrintString());
                h.Output.Add(string.Join(" ", parts));
                return DynValue.Nil;
            });
        }

        return h;
    }

    private void Record(string name, bool isReal)
    {
        _calls[name] = _calls.GetValueOrDefault(name) + 1;
        (isReal ? _real : _stubs).Add(name);
    }

    /// <summary>Which stub names must hand back a TABLE, worked out from how the script uses them.
    ///
    /// <para>Shape matters more than value: a number where the driver indexes kills it instantly, and a
    /// table where it does arithmetic does the same. Rather than guess from the name, this scans the source
    /// for the three usages that prove a table — <c>#bot.f(</c>, <c>ipairs(bot.f(</c> / <c>pairs(bot.f(</c>,
    /// and <c>bot.f(...)[</c> — plus assignment into a local that is then indexed.</para></summary>
    private static HashSet<string> TableShaped(string source)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pat in new[]
                 {
                     @"#\s*bot\.([A-Za-z_]\w*)\s*\(",
                     @"(?:i?pairs)\s*\(\s*bot\.([A-Za-z_]\w*)\s*\(",
                     @"bot\.([A-Za-z_]\w*)\s*\([^()]*\)\s*\[",
                 })
            foreach (Match m in Regex.Matches(source, pat))
                found.Add(m.Groups[1].Value);

        return found;
    }

    private HashSet<string> _tableShaped = new(StringComparer.Ordinal);
    private HashSet<string> _numberShaped = new(StringComparer.Ordinal);

    /// <summary>WEAK table evidence: `local inv = bot.inventory()` where some local of that name is later
    /// indexed.
    ///
    /// <para>⚠️ Deliberately ranked BELOW numeric evidence. The local's name is searched across the whole
    /// 6,200-line file, so a common one like `free` collides with an unrelated `free[...]` elsewhere — which
    /// is exactly what made `bagFreeSlots` return a table to a `<` comparison. Weak evidence that outranks
    /// strong evidence is worse than no evidence.</para></summary>
    private static HashSet<string> WeaklyTableShaped(string source)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"local\s+(\w+)\s*=\s*bot\.([A-Za-z_]\w*)\s*\(");
            if (!m.Success) continue;

            // Look only at the NEXT FEW LINES. Searching the whole file for a local as common as `free`
            // matches an unrelated `free[...]` hundreds of lines away and mislabels a count as a table.
            var window = string.Join("\n", lines.Skip(i + 1).Take(40));
            var local = Regex.Escape(m.Groups[1].Value);
            if (Regex.IsMatch(window, $@"{local}\s*\[")
                || Regex.IsMatch(window, $@"#\s*{local}")
                || Regex.IsMatch(window, $@"i?pairs\s*\(\s*{local}\s*\)"))
                found.Add(m.Groups[2].Value);
        }
        return found;
    }

    /// <summary>Names the script compares or does arithmetic on, which must therefore be NUMBERS.
    ///
    /// <para>This outranks the plural-name heuristic, and it has to: `bagFreeSlots` ends in an "s" and is a
    /// count, so the name shape says table and the usage says number. Usage wins — it is evidence, the
    /// name is a guess.</para></summary>
    private static HashSet<string> NumberShaped(string source)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(source,
                     @"bot\.([A-Za-z_]\w*)\s*\([^()]*\)\s*(?:[<>]=?|==|~=|[-+*/%])"))
            found.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(source,
                     @"(?:[<>]=?|==|~=|[-+*/%])\s*bot\.([A-Za-z_]\w*)\s*\("))
            found.Add(m.Groups[1].Value);
        return found;
    }

    /// <summary>What a stub hands back.
    ///
    /// <para>Choosing by NAME SHAPE rather than returning nil everywhere, because a nil into an arithmetic
    /// or comparison kills the script on the first stubbed call and tells us nothing beyond that one name.
    /// Neutral values let the driver keep running so the harness can measure the whole surface it touches
    /// in a session, which is the point.</para>
    ///
    /// <para>⚠️ This means a stubbed run is NOT a behavioural simulation of levelling. It measures reach, not
    /// correctness — the bot is walking through a world where every quest is empty and every shop is shut.</para></summary>
    /// <summary>Shapes stated outright, because inference is not worth the candle for these.
    ///
    /// <para>The heuristics handle most of the surface, but the driver uses guarded-call idioms
    /// (<c>bot.f and bot.f() or {}</c>) and multiple assignment (<c>local a, b = bot.f(), bot.g()</c>) that
    /// defeat a regex. Rather than keep bolting epicycles onto the scanner, the handful of names whose shape
    /// actually decides whether the script survives are simply written down.</para>
    ///
    /// <para>Kept SHORT on purpose: every entry here is a place inference failed, so a long list would be a
    /// sign the scanning approach had stopped earning its place.</para></summary>
    private static readonly HashSet<string> KnownTables = new(StringComparer.Ordinal)
    {
        "inventory", "inventoryCounts", "equipment", "drops", "shopItems", "storageItems",
        "activeQuests", "availableQuests", "eligibleQuests", "questStatics", "learnedSkills",
        "skillCooldowns", "selfAbstates", "aggressorSpawns", "gates", "instanceDoors",
        "scenarioAreas", "scenarioAckedAreas", "npcSeedList", "knownShopsOfKind", "npcLocation",
        "npcCoord", "mobLocation", "entityPos", "itemInfo", "skillInfo", "coveragePath",
    };

    private DynValue NeutralFor(string name, Script script)
    {
        if (KnownTables.Contains(name) || _tableShaped.Contains(name))
            return DynValue.NewTable(new Table(script));

        if (StubDefaults.TryGetValue(name, out var chosen))
            return DynValue.NewNumber(chosen);

        if (StubDefaults.TryGetValue(name, out var preset))
            return DynValue.NewNumber(preset);

        if (_numberShaped.Contains(name))
            return DynValue.NewNumber(0);

        if (name.StartsWith("is", StringComparison.Ordinal)
            || name.StartsWith("has", StringComparison.Ordinal)
            || name.StartsWith("can", StringComparison.Ordinal)
            || name is "bagFull" or "mounted" or "traveling" or "walking" or "casting" or "rooted"
                or "dead" or "shopOpen" or "storageOpen" or "questActive" or "questDone"
                or "hpStoneDepleted" or "spStoneDepleted" or "noMount" or "castConfirmed"
                or "dialogConcluded" or "lastBuyOk")
            return DynValue.False;

        // Plural or list-shaped names hand back an empty table so `#list` and `ipairs` stay valid.
        if (name.EndsWith("s", StringComparison.Ordinal)
            && !name.EndsWith("Pct", StringComparison.Ordinal)
            && !name.EndsWith("Ms", StringComparison.Ordinal)
            && name is not ("hpStones" or "spStones" or "maxHpStones" or "maxSpStones"
                or "freeStatPoints" or "ticks" or "questDeaths"))
            return DynValue.NewTable(new Table(script));

        return DynValue.NewNumber(0);
    }

    /// <summary>The names this simulation can answer for real.
    ///
    /// <para>Deliberately narrow. Anything not here is a stub, and a stub that the driver leans on is a
    /// finding rather than an embarrassment.</para></summary>
    private Dictionary<string, Func<CallbackArguments, DynValue>> RealImplementations() => new(StringComparer.Ordinal)
    {
        ["now"] = _ => DynValue.NewNumber(_sim.Now),
        ["ticks"] = _ => DynValue.NewNumber(_sim.Now / Math.Max(1u, _sim.TickMs)),
        ["x"] = _ => DynValue.NewNumber(_sim.Player.X),
        ["y"] = _ => DynValue.NewNumber(_sim.Player.Y),
        ["hp"] = _ => DynValue.NewNumber(_sim.Player.Hp),
        ["maxHp"] = _ => DynValue.NewNumber(_sim.Player.MaxHp),
        ["hpPct"] = _ => DynValue.NewNumber(_api.hpPct()),
        ["level"] = _ => DynValue.NewNumber(_sim.Player.Level),
        ["dead"] = _ => DynValue.NewBoolean(!_sim.Player.IsAlive),
        ["selfHandle"] = _ => DynValue.NewNumber(_sim.Player.Handle),
        ["inCombat"] = _ => DynValue.NewBoolean(_api.inCombat()),
        ["aggressorHandles"] = _ => DynValue.NewTable(_api.aggressorHandles()),
        // Empty-table-stubbed this read as "every chaser has dropped", so shedStep never ran.
        ["aggressorSpawns"] = _ => DynValue.NewTable(_api.aggressorSpawns()),
        // A COUNT, not a list -- `bot.aggressors() > 0`. It ends in "s" and the plural heuristic called it a
        // table, which is the same trap `bagFreeSlots` fell into.
        ["aggressors"] = _ => DynValue.NewNumber(_sim.Mobs.Count(m => m.Arg.Target is SimPlayer)),
        ["nearbyMobs"] = _ => DynValue.NewTable(_api.nearbyMobs()),
        ["killsByMe"] = _ => DynValue.NewNumber(_sim.Kills),
        ["dist"] = a => DynValue.NewNumber(_api.dist((int)a[0].Number)),
        ["isAlive"] = a => DynValue.NewBoolean(_api.isAlive((int)a[0].Number)),
        ["walking"] = _ => DynValue.NewBoolean(_api.walking()),
        ["commitStop"] = _ => { _api.commitStop(); return DynValue.Nil; },
        ["stopTravel"] = _ => { _api.commitStop(); return DynValue.Nil; },
        // The simulation has no mounts. FALSE is a reading, not a stub: a truthy stub makes the driver
        // spend every tick trying to dismount before it will attack.
        ["mounted"] = _ => DynValue.NewBoolean(false),
        // ⚠️ A NAME, not a number. The auto-stub's number made every map comparison in the driver false,
        // 45,849 times per run.
        ["map"] = _ => DynValue.NewString(_sim.MapName),
        ["mapInside"] = _ => DynValue.NewBoolean(false),
        // Pure telemetry in the live bot -- it records what the bot is doing for the operator's dashboard
        // and nothing reads it back. Declared inert rather than simulated.
        ["setFocus"] = _ => DynValue.Nil,
        ["notePhase"] = _ => DynValue.Nil,

        // ⚠️ THESE GATE A FLEE DECISION, so a stub is not acceptable for them -- and the auto-stub's shape
        // guess made `incomingDps` a TABLE, which raised "attempt to compare table with number" and killed
        // the driver at level_quest.lua:2516. Both are MEASURED quantities in the live bot, and -1 is the
        // script's own "not learned yet"; see SimBotApi.
        ["incomingDps"] = a => DynValue.NewNumber(_api.incomingDps(a.Count > 0 ? (int)a[0].Number : 5000)),
        ["sustainableHealDps"] = _ => DynValue.NewNumber(_api.sustainableHealDps()),
        ["soulstoneHp"] = _ => DynValue.NewBoolean(_api.soulstoneHp()),
        ["hpStones"] = _ => DynValue.NewNumber(_api.hpStones()),
        ["maxHpStones"] = _ => DynValue.NewNumber(_api.maxHpStones()),
        ["hpStoneRestore"] = _ => DynValue.NewNumber(_api.hpStoneRestore()),
        ["hpStoneDepleted"] = _ => DynValue.NewBoolean(_api.hpStoneDepleted()),
        ["hpStoneReadyInMs"] = _ => DynValue.NewNumber(_api.hpStoneReadyInMs()),
        ["sp"] = _ => DynValue.NewNumber(_api.sp()),
        ["maxSp"] = _ => DynValue.NewNumber(_api.maxSp()),
        ["spPct"] = _ => DynValue.NewNumber(_api.spPct()),
        ["soulstoneSp"] = _ => DynValue.NewBoolean(_api.soulstoneSp()),
        ["spStones"] = _ => DynValue.NewNumber(_api.spStones()),
        ["maxSpStones"] = _ => DynValue.NewNumber(_api.maxSpStones()),
        ["spStoneRestore"] = _ => DynValue.NewNumber(_api.spStoneRestore()),
        ["spStoneDepleted"] = _ => DynValue.NewBoolean(_api.spStoneDepleted()),
        ["spStoneCooldownMs"] = _ => DynValue.NewNumber(_api.spStoneCooldownMs()),
        ["spStoneReadyIn"] = _ => DynValue.NewNumber(_api.spStoneReadyIn()),
        ["learnedSkills"] = _ => DynValue.NewTable(_api.learnedSkills()),
        ["skillInfo"] = a => _api.skillInfo((int)a[0].Number),
        ["cast"] = a => DynValue.NewBoolean(_api.cast((int)a[0].Number, (int)a[1].Number)),
        ["casting"] = _ => DynValue.NewBoolean(_api.casting()),
        ["castConfirmed"] = _ => DynValue.NewBoolean(_api.castConfirmed()),
        ["skillReadyInMs"] = a => DynValue.NewNumber(_api.skillReadyInMs((int)a[0].Number)),
        ["skillCooldowns"] = _ => DynValue.NewTable(_api.skillCooldowns()),
        ["skillDamageAvg"] = a => DynValue.NewNumber(_api.skillDamageAvg((int)a[0].Number)),
        ["skillDamageSamples"] = a => DynValue.NewNumber(_api.skillDamageSamples((int)a[0].Number)),
        ["money"] = _ => DynValue.NewNumber(_api.money()),
        ["bagFreeSlots"] = _ => DynValue.NewNumber(_api.bagFreeSlots()),
        ["bagFull"] = _ => DynValue.NewBoolean(_api.bagFull()),
        // A `can*` name auto-stubs to false; this is a pacing gate whose idle answer is true.
        ["canPick"] = _ => DynValue.NewBoolean(_api.canPick()),
        ["inventory"] = _ => DynValue.NewTable(_api.inventory()),
        ["equipment"] = _ => DynValue.NewTable(_api.equipment()),
        ["traveling"] = _ => DynValue.NewBoolean(_api.traveling()),
        ["exp"] = _ => DynValue.NewNumber(_api.exp()),
        ["classId"] = _ => DynValue.NewNumber(_api.classId()),
        ["freeStatPoints"] = _ => DynValue.NewNumber(_api.freeStatPoints()),
        ["announce"] = a => { _api.announce(a[0].CastToString() ?? ""); return DynValue.Nil; },
        ["pendingInvite"] = _ => DynValue.NewBoolean(_api.pendingInvite()),
        ["partyAccept"] = _ => DynValue.NewBoolean(_api.partyAccept()),
        ["pendingFriend"] = _ => DynValue.NewBoolean(_api.pendingFriend()),
        ["friendAccept"] = _ => DynValue.NewBoolean(_api.friendAccept()),
        ["npcSeedCount"] = _ => DynValue.NewNumber(_api.npcSeedCount()),
        ["npcCoord"] = a => _api.npcCoord((int)a[0].Number),
        ["recentDamage"] = a => DynValue.NewNumber(_api.recentDamage(a.Count > 0 ? (int)a[0].Number : 5000)),
        ["activeQuests"] = _ =>
        {
            var t = new Table(_sim.Script);
            foreach (var q in Quests)
            {
                var row = new Table(_sim.Script);
                row["id"] = q.Id;
                row["prog"] = ProgressOf(q);
                row["need"] = q.Need;
                row["done"] = ProgressOf(q) >= q.Need;
                t.Append(DynValue.NewTable(row));
            }
            return DynValue.NewTable(t);
        },
        ["questName"] = a =>
        {
            var id = (int)a[0].Number;
            var q = Quests.FirstOrDefault(x => x.Id == id);
            return DynValue.NewString(q?.Name ?? "?");
        },
        ["questProgress"] = a =>
        {
            var q = Quests.FirstOrDefault(x => x.Id == (int)a[0].Number);
            return DynValue.NewNumber(q is null ? 0 : ProgressOf(q));
        },
        ["questDone"] = a =>
        {
            var q = Quests.FirstOrDefault(x => x.Id == (int)a[0].Number);
            return DynValue.NewBoolean(q is not null && ProgressOf(q) >= q.Need);
        },
        ["questActive"] = a => DynValue.NewBoolean(Quests.Any(x => x.Id == (int)a[0].Number)),

        // The driver's real quest path: a STATIC definition (objectives with a mob and a count) plus a
        // PULSE carrying live progress. It reads them together -- `questStatics` for the shape, `questPulse`
        // for how far along each objective is -- so a quest that exists only in `activeQuests` is invisible
        // to the part of the driver that decides what to go and kill.
        ["questStatics"] = a =>
        {
            var t = new Table(_sim.Script);
            foreach (var q in Quests) t[q.Id] = DynValue.NewTable(StaticOf(q));
            return DynValue.NewTable(t);
        },
        ["quest"] = a =>
        {
            var q = Quests.FirstOrDefault(x => x.Id == (int)a[0].Number);
            return q is null ? DynValue.Nil : DynValue.NewTable(StaticOf(q));
        },
        ["questPulse"] = _ =>
        {
            var quests = new Table(_sim.Script);
            foreach (var q in Quests)
            {
                var objProg = new Table(_sim.Script);
                objProg[1] = ProgressOf(q);
                var entry = new Table(_sim.Script);
                entry["objProg"] = DynValue.NewTable(objProg);
                quests[q.Id] = DynValue.NewTable(entry);
            }
            var pulse = new Table(_sim.Script);
            pulse["quests"] = DynValue.NewTable(quests);
            return DynValue.NewTable(pulse);
        },
        ["questObjProgress"] = a =>
        {
            var q = Quests.FirstOrDefault(x => x.Id == (int)a[0].Number);
            return DynValue.NewNumber(q is null ? 0 : ProgressOf(q));
        },
        // ⚠️ RETURN WHAT IT ACTUALLY DID. This hard-coded `DynValue.True`, so a refusal could never reach
        // the driver even after the API learned to make one — and the live `walkTo` returns false for a
        // destination the region graph cannot reach. `level_quest.lua:2679` already reads the result.
        ["walkTo"] = a => DynValue.NewBoolean(_api.walkTo((int)a[0].Number, (int)a[1].Number)),
        // ⚠️ `attack` is the LIVE signature -- (skill, target), a cast. `swing` is the sim's own
        // one-shot melee primitive. See SimBotApi.attack for what conflating them cost.
        ["attack"] = a => DynValue.NewBoolean(_api.attack(
            (int)a[0].Number, a.Count > 1 ? (int)a[1].Number : 0)),
        ["swing"] = a => DynValue.NewBoolean(_api.swing((int)a[0].Number)),
        ["canReach"] = a => DynValue.NewBoolean(_api.canReach((int)a[0].Number)),
        ["routeLength"] = a => DynValue.NewNumber(_api.routeLength((int)a[0].Number, (int)a[1].Number)),
        ["canReachPoint"] = a => DynValue.NewBoolean(_api.canReachPoint((int)a[0].Number, (int)a[1].Number)),
        // ⚠️ A MODE, NOT ONE SWING. `bot.autoAttack(h)` live sends BASHSTART and the server then streams
        // swings until the target dies; mapping it to a single `attack()` meant the driver hit a mob once
        // and stood there. See SimPlayer.AutoAttackTarget.
        ["autoAttack"] = a => DynValue.NewBoolean(_api.autoAttack((int)a[0].Number)),
        ["stopAttack"] = _ => { _api.stopAttack(); return DynValue.Nil; },
        ["bashing"] = _ => DynValue.NewBoolean(_sim.Player.AutoAttackTarget is not null),
        ["target"] = _ => _sim.Player.AutoAttackTarget is { } h ? DynValue.NewNumber(h) : DynValue.Nil,
        ["mobLocation"] = a => MobField(a, m => DynValue.NewTable(Pair(m.Mob.X, m.Mob.Y))),
        ["mobMaxHp"] = a => MobField(a, m => DynValue.NewNumber(m.MaxHp)),
        ["mobLevel"] = a => MobField(a, m => DynValue.NewNumber(m.Level)),
        ["mobAttackRange"] = a => MobField(a, m => DynValue.NewNumber(m.Arg.Combat.AttackRange)),
    };

    /// <summary>The static half of a quest: one kill objective, in the shape the driver reads.
    ///
    /// <para><c>type == 1</c> is a kill objective — `objTotal` sums `count` only for that type — and `mob`
    /// is the numeric `MobInfo.ID`, which is how the driver decides what to hunt.</para></summary>
    private Table StaticOf(KillQuest q)
    {
        var objective = new Table(_sim.Script);
        objective["type"] = 1;
        objective["mob"] = q.MobId;
        objective["count"] = q.Need;
        objective["prog"] = ProgressOf(q);
        objective["done"] = ProgressOf(q) >= q.Need;

        var objectives = new Table(_sim.Script);
        objectives.Append(DynValue.NewTable(objective));

        var mob = new Table(_sim.Script);
        mob["id"] = q.MobId;
        mob["name"] = q.MobName;
        var mobs = new Table(_sim.Script);
        mobs.Append(DynValue.NewTable(mob));

        var rec = new Table(_sim.Script);
        rec["id"] = q.Id;
        rec["name"] = q.Name;
        rec["objectives"] = DynValue.NewTable(objectives);
        rec["mobs"] = DynValue.NewTable(mobs);
        return rec;
    }

    private DynValue MobField(CallbackArguments a, Func<SimMob, DynValue> pick)
    {
        var handle = (ushort)a[0].Number;
        var mob = _sim.Mobs.FirstOrDefault(m => m.Mob.Handle == handle);
        return mob is null ? DynValue.Nil : pick(mob);
    }

    private Table Pair(int x, int y)
    {
        var t = new Table(_sim.Script);
        t["x"] = x;
        t["y"] = y;
        return t;
    }

    /// <summary>Load and run a driver, calling its `tick` (or `on_tick`) for as long as asked.
    ///
    /// <para>Errors are captured rather than thrown: a driver that dies on tick 3 has still told us which
    /// 40 functions it reached first, and that is the useful part.</para></summary>
    /// <summary>Load the script and fire its entry points. <b>Call this ONCE per run.</b></summary>
    /// <returns>false when the source would not load; the reason is in <see cref="Errors"/>.</returns>
    public bool Load(string luaSource)
    {
        try
        {
            _sim.Script.DoString(luaSource);
        }
        catch (Exception e)
        {
            Errors.Add($"load: {Summarise(e)}");
            return false;
        }

        foreach (var entry in new[] { "on_enter", "on_start" })
            Invoke(entry, optional: true);
        return true;
    }

    /// <summary>⭐ ADVANCE AN ALREADY-LOADED SCRIPT. This is what to call in a loop.
    ///
    /// <para>⚠️ <b><see cref="Run"/> RELOADS.</b> It calls `DoString` on the source and re-fires
    /// `on_enter`/`on_start` every time, so calling it in slices restarts the driver from scratch on every
    /// slice — every Lua local (its phase, its target, its cooldown and learned-damage tables) is thrown
    /// away. A scenario runner that sliced `Run` to find WHEN a character died was silently resetting the
    /// bot every five simulated seconds, and the kill counts moved between otherwise identical runs.
    /// That is the tell to watch for: a same-seed run whose numbers change.</para></summary>
    /// <returns>false if the script stopped raising or has no tick entry point.</returns>
    public bool Step(int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            _sim.Step();
            if (!Invoke("tick", optional: false) && !Invoke("on_tick", optional: false))
                return false;
        }
        return true;
    }

    /// <summary>Load and run, in one call. Convenient for a single fixed-length run; use
    /// <see cref="Load"/> + <see cref="Step"/> when the run has to be observed part-way.</summary>
    public void Run(string luaSource, int ticks)
    {
        if (Load(luaSource)) Step(ticks);
    }

    private bool Invoke(string fn, bool optional)
    {
        var f = _sim.Script.Globals.Get(fn);
        if (f.Type != DataType.Function)
            return optional;

        try
        {
            _sim.Script.Call(f);
            return true;
        }
        catch (Exception e)
        {
            var line = $"{fn}: {Summarise(e)}";
            if (!Errors.Contains(line)) Errors.Add(line);
            return Errors.Count < 25;      // keep going while the failures are still new information
        }
    }

    private static string Summarise(Exception e)
        => e is InterpreterException i ? i.DecoratedMessage ?? i.Message : e.Message;

    /// <summary>A human-readable gap report: what the driver reached for, and what was actually there.</summary>
    public string Report()
    {
        var lines = new List<string>
        {
            $"surface mentioned in script : {Surface.Count}",
            $"called at runtime           : {_calls.Count}",
            $"  backed by the simulation  : {_real.Count}",
            $"  stubbed                   : {_stubs.Count}",
            "",
            "most-leaned-on stubs (call count):",
        };
        foreach (var (name, n) in _stubs.Select(s => (s, _calls[s])).OrderByDescending(t => t.Item2).Take(20))
            lines.Add($"    {n,6}  {name}");

        if (_real.Count > 0)
        {
            lines.Add("");
            lines.Add("real calls served:");
            foreach (var (name, n) in _real.Select(s => (s, _calls[s])).OrderByDescending(t => t.Item2))
                lines.Add($"    {n,6}  {name}");
        }

        if (Errors.Count > 0)
        {
            lines.Add("");
            lines.Add($"errors ({Errors.Count}):");
            lines.AddRange(Errors.Take(10).Select(e => "    " + e));
        }
        return string.Join("\n", lines);
    }
}
