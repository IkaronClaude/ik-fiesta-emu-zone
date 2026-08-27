using Fiesta.Emu.Zone.Combat;
using Fiesta.Emu.Zone.Mob;
using Fiesta.Emu.Zone.Parameter;
using Fiesta.Emu.Zone.Random;
using MoonSharp.Interpreter;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>One simulated mob: the server-side object, its AI argument, and its combat bookkeeping.</summary>
public sealed class SimMob : ICombatant
{
    /// <summary>The mob's stat layers, so it can be a defender in the real damage formula — its AC and MR
    /// are what an attacker's power is measured against. Filled by <see cref="Define"/> from
    /// `MobInfoServer.shn` via the ported `c_StoreMob`.</summary>
    public ParameterContainer Parameters { get; private set; } = new();

    /// <summary>The mob's level, for the attacker-vs-defender level gap. From `MobInfo.Level`.</summary>
    public int Level { get; set; } = 1;

    /// <summary>The joined game-data definition, once one has been applied.</summary>
    public MobCombatant? Definition { get; private set; }

    /// <summary>The mob's ordinary swing, from `MobWeapon.shn`. Null means no data was applied and
    /// <see cref="AttackDamage"/> is in force.</summary>
    public Data.MobWeapon? NormalAttack { get; private set; }

    /// <summary>The ported attack timings, in the server's tenths of a second.</summary>
    public MobAttackTiming Timing { get; private set; }

    /// <summary>Adopt a real mob definition: stats, level, HP and swing timings all from game data.
    ///
    /// <para>The timings come from <see cref="MobAttackTimingCalculator"/>, which is the ported timing block
    /// of `mab_Think` — not a field-by-field mapping. All four columns are used and none means what its
    /// name suggests; two earlier guesses here were wrong before the function was read.</para>
    public void Define(MobCombatant definition)
    {
        Definition = definition;
        // The NAME comes with the definition. Without this a mob defined outside MapSpawner has an empty
        // name, and anything that counts kills BY NAME -- a kill quest, a drop table -- silently never
        // matches. It cost a test failure that looked like the quest was broken.
        Name = definition.Info.InxName;
        Parameters = definition.Parameters;
        Level = definition.Level;
        MaxHp = definition.MaxHp;
        Hp = MaxHp;
        NormalAttack = definition.NormalAttack;
        Mob.Selector.Policy = definition.Policy;
        Arg.Combat.RunSpeed = definition.Info.RunSpeed;
        Arg.Combat.TurnSpeed = definition.Server.TurnSpeed;

        if (definition.NormalAttack is not { } w) return;

        Timing = MobAttackTimingCalculator.Compute(
            w, MobAttackTimingCalculator.AttackSpeedRate(definition.Parameters));

        // The interval is the DELAY PLUS THE SWING, which is why AtkDly exceeding SwingTime was never the
        // contradiction it looked like: they add rather than compete.
        SwingIntervalMs = Math.Max(100u, Timing.IntervalMs);
        SwingLandDelayMs = Timing.HitMs;
        if (w.Range > 0) Arg.Combat.AttackRange = w.Range;
    }

    public required ShineMob Mob { get; init; }
    public required MobActionArgument Arg { get; init; }
    public required NormalAttackDamageTick Swings { get; init; }

    public int Hp { get; set; } = 300;
    public int MaxHp { get; set; } = 300;
    public int AttackDamage { get; set; } = 25;

    /// <summary>When this mob may swing again. Attack speed, kept as a ready-time like the cooldowns.</summary>
    public uint NextSwingAt { get; set; }
    public uint SwingIntervalMs { get; set; } = 2000;

    /// <summary>How long after a swing its damage lands — the reason `NormalAttackDamageTick` exists.</summary>
    public uint SwingLandDelayMs { get; set; } = 400;

    /// <summary>The mob's name in `MobRegen`, e.g. "Orc". Carried so a trace names what died.</summary>
    public string Name { get; set; } = "";

    /// <summary>The spawn group it belongs to, e.g. "Urg07".</summary>
    public string SpawnGroup { get; set; } = "";

    /// <summary>Where it spawned, so a respawn puts it back in the same area.</summary>
    public int SpawnX { get; set; }
    public int SpawnY { get; set; }

    /// <summary>`RegStandard` from the table — the respawn delay in seconds.
    ///
    /// <para>⚠️ The table also carries RegMin/RegMax and a delta schedule that is not understood yet, so
    /// this uses the flat standard value. Respawn timing is therefore approximate, and deliberately so
    /// rather than acting on a guess about the schedule.</para></summary>
    public int RespawnSeconds { get; set; } = 25;

    /// <summary>When this corpse comes back, or null if it is alive.</summary>
    public uint? RespawnAt { get; set; }
}

/// <summary>A deterministic mob-combat simulation the bot's Lua driver can be run against.
///
/// <para>Time only moves when <see cref="Tick"/> is called, and every random decision comes from the
/// oracle-verified <see cref="cWell512Random"/>, so a run is reproducible from its seed. That is the whole
/// value proposition: a fight that takes 100 seconds against a live server can be replayed in
/// milliseconds, and replayed <em>identically</em> when a driver change needs evaluating.</para></summary>
public sealed class CombatSimulation
{
    private readonly List<SimMob> _mobs = new();

    public CombatSimulation(uint seed = 1)
    {
        Script = new Script(CoreModules.Preset_SoftSandbox);
        var state = new uint[16];
        for (var i = 0; i < 16; i++) state[i] = seed + (uint)i * 2654435761u;
        Rng = new cWell512Random(state);

        // MoonSharp will not expose a CLR object to Lua until its type is registered -- without this the
        // constructor throws "cannot convert clr type" and every script fails before it runs a line.
        UserData.RegisterType<SimBotApi>();

        Api = new SimBotApi(this);
        Script.Globals["bot"] = UserData.Create(Api);
        Script.Globals["log"] = (Action<string>)(m => Log.Add($"[{Now,6}] {m}"));
    }

    public Script Script { get; }
    public cWell512Random Rng { get; }
    public SimBotApi Api { get; }
    public SimPlayer Player { get; } = new();
    public IReadOnlyList<SimMob> Mobs => _mobs;

    /// <summary>Take a mob out of the world entirely — used to drop gathering nodes from a spawned map.</summary>
    public bool Remove(SimMob mob) => _mobs.Remove(mob);

    /// <summary>Simulated milliseconds since the start. Advanced only by <see cref="Tick"/>.</summary>
    public uint Now { get; private set; }

    /// <summary>Milliseconds of simulated time each <see cref="Tick"/> advances.
    ///
    /// <para>Settable at any point, so a run can be coarse while nothing is happening and fine-grained
    /// through a fight. Nothing in the simulation reads a real clock — this value is the ONLY source of
    /// time — so halving it doubles the resolution of every timing decision (swing intervals, respawn
    /// timers, delayed damage) without changing any of their durations in simulated milliseconds.</para>
    ///
    /// <para>⚠️ It is not free of consequences: durations are stored in milliseconds and compared against
    /// <see cref="Now"/>, so a tick coarser than an interval makes that interval effectively round up to
    /// the tick. A 2000 ms swing at TickMs 3000 fires every 3000 ms.</para></summary>
    public uint TickMs { get; set; } = 100;

    public List<string> Log { get; } = new();

    public SimMob? Find(ushort handle) => _mobs.FirstOrDefault(m => m.Mob.Handle == handle);

    public SimMob AddMob(ushort handle, int x, int y, Action<SimMob>? configure = null)
    {
        var mob = new ShineMob { Handle = handle, X = x, Y = y, so_getDetectRange = 60 };
        var sim = new SimMob
        {
            Mob = mob,
            Swings = new NormalAttackDamageTick(),
            Arg = new MobActionArgument
            {
                Actor = mob,
                Selector = mob.Selector,
                Combat = new MobCombatState { AttackRange = 12, FacingToleranceUnits = 8 },
                Rng = Rng,
            },
        };
        configure?.Invoke(sim);
        sim.SpawnX = x;
        sim.SpawnY = y;
        _mobs.Add(sim);
        return sim;
    }

    /// <summary>How much damage one swing does.
    ///
    /// <para>Two models, chosen EXPLICITLY by the attacker rather than inferred from whether a stat happens
    /// to be non-zero. A "use the formula if WCmax &gt; 0" rule would make a genuinely unarmed character
    /// silently fall back to a flat number, and zero is a real weapon value, not a marker for "unset".</para></summary>
    private int SwingDamage(ICombatant attacker, ICombatant defender, int flat)
    {
        // A mob with a real definition goes through the SAME formula as the player.
        //
        // That is not a convenience: `sm_PrepareWeapon` writes a mob's chosen weapon into its Item.Plus
        // cluster — a mob's weapon is its gear — so by the time anything reads its stats the weapon is
        // simply present, and `roe_MinWC` has no mob branch at all. An earlier version of this method rolled
        // mob damage directly between MinWC and MaxWC because that path had not been traced yet, which meant
        // the DEFENDER'S AC WAS NEVER APPLIED to mob damage.
        if (attacker is not (SimMob { Definition: not null } or SimPlayer { UsesStatFormula: true }))
            return flat;

        // The damage roll is drawn from the SERVER'S generator, not System.Random. The calculator will
        // happily make its own roll, but combat randomness on the real server comes from WELL512, and this
        // simulation's whole reproducibility story is "same seed, same run" -- a second, unrelated RNG
        // inside the damage path would quietly break that.
        var roll = new AttackModifiers { RollPermille = (int)Rng.well512_GetRandom(1001) };
        return DamageCalculator.ResolveDamage(attacker, defender, roll);
    }

    /// <summary>The player swings at a mob: damage now, aggro now, both through the ported paths.</summary>
    public void PlayerAttack(SimMob target)
    {
        var damage = SwingDamage(Player, target, Player.AttackDamage);
        target.Hp -= damage;
        target.Mob.so_DamagedBy(Player, damage, Player.AggroRatePermille);

        // Being hit interrupts a cancelable move, exactly as MobActionInMove_Cancelable::mab_Damaged does.
        target.Arg.Current.mab_Damaged(target.Arg);

        if (target.Hp <= 0)
        {
            target.Mob.IsAlive = false;
            target.RespawnAt = Now + (uint)(target.RespawnSeconds * 1000);
            target.Arg.Target = null;
            Kills++;
            _killsByName[target.Name] = _killsByName.GetValueOrDefault(target.Name) + 1;
            Log.Add($"[{Now,6}] {Describe(target)} died (respawn at {target.RespawnAt})");
        }
    }

    /// <summary>Advance one tick: run every mob's AI, land any damage that has come due, then let mobs
    /// that are in range and off cooldown swing.
    ///
    /// <para>The order matters. Damage due this tick lands BEFORE new swings are queued, so a hit thrown
    /// last tick resolves against this tick's state rather than being overtaken.</para></summary>
    /// <summary>How many mobs the player has killed this run.</summary>
    public int Kills { get; private set; }

    private readonly Dictionary<string, int> _killsByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kills broken down by the mob's `MobRegen` name — what a kill quest counts.</summary>
    public IReadOnlyDictionary<string, int> KillsByName => _killsByName;

    private static string Describe(SimMob m)
        => string.IsNullOrEmpty(m.Name) ? $"mob {m.Mob.Handle}" : $"{m.Name}#{m.Mob.Handle}";

    public void Tick()
    {
        Now += TickMs;

        // Respawn anything whose timer has come up, back in its own spawn area.
        foreach (var dead in _mobs.Where(m => !m.Mob.IsAlive && m.RespawnAt is not null && Now >= m.RespawnAt))
        {
            dead.Hp = dead.MaxHp;
            dead.Mob.IsAlive = true;
            dead.Mob.X = dead.SpawnX;
            dead.Mob.Y = dead.SpawnY;
            dead.Mob.Selector.mts_AggroClear();
            dead.Arg.Current = MobActionBase.Actor_Targetting;
            dead.RespawnAt = null;
            Log.Add($"[{Now,6}] {Describe(dead)} respawned");
        }

        foreach (var m in _mobs.Where(m => m.Mob.IsAlive))
        {
            m.Arg.ElapsedMs = TickMs;
            m.Arg.NowTenths = (int)(Now / 100);        // the server's clockwatch resolution
            m.Arg.Nearby = Player.IsAlive ? new IShineObject[] { Player } : Array.Empty<IShineObject>();

            // The AI driver, as the server runs it: think returns the next state, we adopt it.
            m.Arg.Current = m.Arg.Current.mab_Think(m.Arg);

            // Damage queued earlier that has now come due.
            foreach (var landed in m.Swings.nadt_Routine(Now))
            {
                if (landed.Target is SimPlayer p && p.IsAlive)
                {
                    p.Hp -= landed.Damage;
                    Log.Add($"[{Now,6}] mob {m.Mob.Handle} hits for {landed.Damage} (player {p.Hp}/{p.MaxHp})");
                    if (p.Hp <= 0) Log.Add($"[{Now,6}] player died");
                }
            }

            // Swing if in range, facing, and off cooldown -- the damage lands later.
            if (m.Arg.Current is MobActionAttack attack && Now >= m.NextSwingAt)
            {
                var decision = attack.Decide(m.Arg);
                if (decision.NextState == m.Arg.Current && m.Arg.Target is SimPlayer target && target.IsAlive)
                {
                    m.Swings.nadt_PushBack(SwingDamage(m, target, m.AttackDamage),
                                           Now + m.SwingLandDelayMs, target);
                    m.NextSwingAt = Now + m.SwingIntervalMs;
                    Log.Add($"[{Now,6}] mob {m.Mob.Handle} swings ({decision.Choice}, {decision.Reason})");
                }
            }
        }
    }

    /// <summary>Load a driver script. It should define a global `on_tick()`.</summary>
    public void LoadScript(string lua) => Script.DoString(lua);

    /// <summary>Advance the world one tick and then give the driver its turn — one full simulated step.
    ///
    /// <para>Separate from <see cref="Tick"/> so a caller can drive the world without a script, and from
    /// <see cref="Run"/> so a caller can step manually and inspect between steps. Everything the
    /// simulation does is reachable this way; <see cref="Run"/> is only a loop over this.</para></summary>
    public void Step()
    {
        Tick();
        var onTick = Script.Globals.Get("on_tick");
        if (onTick.Type == DataType.Function && Player.IsAlive)
            Script.Call(onTick);
    }

    /// <summary>Has the run reached a natural end — the player is dead, or nothing is left that will
    /// ever act again?
    ///
    /// <para>⚠️ A mob that is dead but has a respawn pending is NOT gone. An earlier version tested only
    /// `IsAlive` and so declared a one-mob world finished the moment that mob died, ending the run before
    /// its respawn could happen — which made every respawn timing measurement return the first tick.</para></summary>
    public bool IsFinished
        => !Player.IsAlive || _mobs.All(m => !m.Mob.IsAlive && m.RespawnAt is null);

    /// <summary>Run until the script stops, everything dies, or the tick budget runs out.
    /// Returns the number of ticks actually run.</summary>
    public int Run(int maxTicks)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            Step();
            if (IsFinished)
                return i + 1;
        }
        return maxTicks;
    }

    /// <summary>Step until a condition holds, or the tick budget runs out. Returns the ticks used.
    ///
    /// <para>Preferable to running a fixed number of ticks and then asserting on the final state: it says
    /// what the run is waiting FOR, and it stops there instead of letting the world keep evolving past
    /// the moment of interest.</para></summary>
    public int RunUntil(Func<CombatSimulation, bool> condition, int maxTicks)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            Step();
            if (condition(this) || IsFinished)
                return i + 1;
        }
        return maxTicks;
    }
}
