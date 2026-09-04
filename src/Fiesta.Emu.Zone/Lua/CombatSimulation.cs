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

    /// <summary>`ShineMobileObject::smo_RulesOfNormalAttack` (+0x1E74) — which rules of engagement this
    /// mob's ordinary swing resolves through.
    ///
    /// <para>Physical until <see cref="Define"/> reads it from game data, which is the SERVER'S default too:
    /// the `ShineMobileObject` constructor writes <c>&amp;roe_normalPY</c> into the field and
    /// `so_mob_Regenerate` only overwrites it for a mob whose weapon row 0 says otherwise.</para></summary>
    public Combat.EngagementRule NormalAttackRule { get; private set; } = Combat.EngagementRule.NormalPhysical;

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
        NormalAttackRule = definition.NormalAttackRule;
        Mob.Selector.Policy = definition.Policy;
        Arg.Combat.RunSpeed = definition.Info.RunSpeed;
        Arg.Combat.WalkSpeed = definition.Info.WalkSpeed;
        Arg.Combat.WalkChaseDistance = definition.Server.WalkChase;
        Arg.Combat.TurnSpeed = definition.Server.TurnSpeed;

        if (definition.NormalAttack is not { } w) return;

        Timing = MobAttackTimingCalculator.Compute(
            w, MobAttackTimingCalculator.AttackSpeedRate(definition.Parameters),
            definition.NormalAttackCastTimeMs);

        // The interval is DELAY + SWING + the skill's CAST TIME, which is why AtkDly exceeding SwingTime was
        // never the contradiction it looked like: they add rather than compete. The cast term is zero for
        // every anti-player attack in the data -- row 0 names no skill.
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

    private readonly List<(uint At, int Damage)> _incoming = new();

    /// <summary>Damage the player has taken in the last <paramref name="windowMs"/>, per second.
    ///
    /// <para><b>-1 means "not learned yet", and that is a real answer.</b> The live `bot.incomingDps`
    /// derives this from observed damage packets, so before anything has hit the player there is nothing
    /// to derive it from — and `level_quest.lua` reads -1 as "unknown" and declines to judge. Returning 0
    /// instead would tell it the fight is free.</para></summary>
    public double IncomingDps(uint windowMs)
    {
        if (windowMs == 0) return -1;
        var from = Now > windowMs ? Now - windowMs : 0;
        var total = 0;
        var seen = false;
        foreach (var (at, damage) in _incoming)
            if (at >= from) { total += damage; seen = true; }
        return seen ? total * 1000.0 / windowMs : -1;
    }

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
        var roll = new AttackModifiers
        {
            RollPermille = (int)Rng.well512_GetRandom(1001),
            LevelGapRatePermille = LevelGapRate(attacker, defender),
            // `so_ply_JobChangeDamageUp` runs on the ATTACKER and returns early unless the DEFENDER is a
            // monster, so both halves of this condition are the server's, not a simplification.
            JobChangeDamageUpPermille = attacker is SimPlayer p && defender is SimMob
                ? p.JobChangeDamageUpPermille
                : null,
        };
        return DamageCalculator.ResolveDamage(attacker, defender, roll, rule: NormalAttackRuleOf(attacker));
    }

    /// <summary>`DamageLvGapPVE.shn` / `DamageLvGapEVP.shn`, when a table has been supplied.
    ///
    /// <para>Without one this returns <see cref="LevelGapTable.NoAdjustment"/>, which is correct for a
    /// monster hitting a player (every EVP row is 1000) and <b>wrong by up to 50%</b> for a player hitting
    /// something below their level. Set <see cref="LevelGaps"/> whenever game data is available.</para></summary>
    private int LevelGapRate(ICombatant attacker, ICombatant defender)
    {
        if (LevelGaps is null) return LevelGapTable.NoAdjustment;
        return LevelGaps.Rate(KindOf(attacker), attacker.Level, KindOf(defender), defender.Level);
    }

    private static CombatantKind KindOf(ICombatant c)
        => c is SimMob ? CombatantKind.Monster : CombatantKind.Player;

    /// <summary>The level-difference damage tables. Null means "no adjustment", which is honest rather than
    /// invented — see <see cref="LevelGapRate"/> for what it costs.</summary>
    public LevelGapTable? LevelGaps { get; set; }

    /// <summary>Which rules of engagement an attacker's NORMAL swing goes through — the ported
    /// `smo_RulesOfNormalAttack`.
    ///
    /// <para>Deliberately not a member of <see cref="ICombatant"/>. On the server the rule is a field of the
    /// mobile OBJECT (`ShineMobileObject+0x1E74`), not of its stat container, and the damage functions take
    /// it as the `this` of a `RulesOfEngagement` singleton rather than reading it off the combatant. Keeping
    /// it out here preserves that: the calculator still needs a level and a container, and nothing else.</para>
    ///
    /// <para><b>A player is ALWAYS physical, whatever their class.</b> That is not an approximation and not
    /// a gap in this port — it is the only thing the binary can do. The `ShineMobileObject` constructor sets
    /// <c>roe_normalPY</c>, and a full-coverage scan of every instruction touching +0x1E74 finds exactly four
    /// writers: that constructor, `so_mob_Regenerate` (mobs), `spt_Regenerate` (pets, always physical), and
    /// `sp_SetRulesOfEngagement` — whose ONE caller is the GM command `&amp;allcritical`. So a wizard's
    /// auto-attack swinging a wand for almost nothing is CORRECT behaviour, not missing magic support;
    /// caster damage arrives through skills, which use `roe_magical` and are not modelled here.</para></summary>
    private static EngagementRule NormalAttackRuleOf(ICombatant attacker) => attacker switch
    {
        SimMob mob => mob.NormalAttackRule,
        _ => EngagementRule.NormalPhysical,
    };

    /// <summary>The player swings at a mob: damage now, aggro now, both through the ported paths.</summary>
    /// <summary>Sustain `bot.autoAttack`: swing at the locked target whenever one is due and it is in
    /// range, and drop the lock when it dies or walks out of reach.
    ///
    /// <para>This is the server's half of an auto-attack — the client sends BASHSTART once and the server
    /// streams the swings. Without it the driver's one call produced one hit.</para></summary>
    private void AdvanceAutoAttack()
    {
        if (Player.AutoAttackTarget is not { } handle) return;

        var m = Find(handle);
        if (m is null || !m.Mob.IsAlive) { Player.AutoAttackTarget = null; return; }
        if (!Player.IsAlive) { Player.AutoAttackTarget = null; return; }

        // Out of reach is not "stop attacking" -- live the swings simply do not connect, and the driver
        // is the thing that decides to close or give up.
        var range = (long)Player.AttackRange * Player.AttackRange;
        if (MobTargetSelector.SquaredDistance(Player, m.Mob) > range) return;

        if (Now < Player.NextSwingAt) return;
        Player.NextSwingAt = Now + Math.Max(1u, Player.SwingIntervalMs);
        PlayerAttack(m);
    }

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

        // The player keeps walking toward wherever `bot.walkTo` last pointed. Live, a walk continues
        // without the script asking again; a simulation that only moves when called makes the driver's own
        // movement detector read STANDING STILL. See SimPlayer.WalkTarget.
        Player.AdvanceWalk(TickMs);
        AdvanceAutoAttack();

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
                    // Every hit the player takes, with when it landed. `bot.incomingDps` is a MEASURED
                    // quantity in the live bot -- it learns it from the damage packets -- so the
                    // simulation has to learn it the same way rather than being told.
                    _incoming.Add((Now, landed.Damage));

                    // `smo_SwingDamage+0x4B5`, the tail of a hit that connected: the ATTACKER sheds hate for
                    // whoever it just hit. Guarded on > 0 there, so a zero column is not "shed nothing by
                    // accident" -- it is the branch not being taken.
                    var shed = m.NormalAttack?.AggroInitialize ?? 0;
                    if (shed > 0) m.Mob.so_mob_DecreaseAggro(p, shed);

                    Log.Add($"[{Now,6}] mob {m.Mob.Handle} hits for {landed.Damage} (player {p.Hp}/{p.MaxHp})"
                            + (shed > 0 ? $" [sheds {shed} aggro]" : ""));
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
