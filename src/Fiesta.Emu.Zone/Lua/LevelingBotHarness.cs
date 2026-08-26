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
        "skillCooldowns", "selfAbstates", "aggressors", "aggressorSpawns", "gates", "instanceDoors",
        "scenarioAreas", "scenarioAckedAreas", "npcSeedList", "knownShopsOfKind", "npcLocation",
        "npcCoord", "mobLocation", "entityPos", "itemInfo", "skillInfo", "coveragePath",
    };

    private DynValue NeutralFor(string name, Script script)
    {
        if (KnownTables.Contains(name) || _tableShaped.Contains(name))
            return DynValue.NewTable(new Table(script));

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
        ["nearbyMobs"] = _ => DynValue.NewTable(_api.nearbyMobs()),
        ["killsByMe"] = _ => DynValue.NewNumber(_sim.Kills),
        ["walkTo"] = a =>
        {
            _api.walkTo((int)a[0].Number, (int)a[1].Number);
            return DynValue.True;
        },
        ["attack"] = a => DynValue.NewBoolean(_api.attack((int)a[0].Number)),
        ["autoAttack"] = a => DynValue.NewBoolean(_api.attack((int)a[0].Number)),
        ["mobLocation"] = a => MobField(a, m => DynValue.NewTable(Pair(m.Mob.X, m.Mob.Y))),
        ["mobMaxHp"] = a => MobField(a, m => DynValue.NewNumber(m.MaxHp)),
        ["mobLevel"] = a => MobField(a, m => DynValue.NewNumber(m.Level)),
        ["mobAttackRange"] = a => MobField(a, m => DynValue.NewNumber(m.Arg.Combat.AttackRange)),
    };

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
    public void Run(string luaSource, int ticks)
    {
        var script = _sim.Script;
        try
        {
            script.DoString(luaSource);
        }
        catch (Exception e)
        {
            Errors.Add($"load: {Summarise(e)}");
            return;
        }

        foreach (var entry in new[] { "on_enter", "on_start" })
            Invoke(entry, optional: true);

        for (var i = 0; i < ticks; i++)
        {
            _sim.Step();
            if (!Invoke("tick", optional: false) && !Invoke("on_tick", optional: false))
                return;
        }
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
