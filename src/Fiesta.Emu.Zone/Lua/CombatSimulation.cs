using Fiesta.Emu.Zone.Mob;
using Fiesta.Emu.Zone.Random;
using MoonSharp.Interpreter;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>One simulated mob: the server-side object, its AI argument, and its combat bookkeeping.</summary>
public sealed class SimMob
{
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

    /// <summary>Simulated milliseconds since the start. Advanced only by <see cref="Tick"/>.</summary>
    public uint Now { get; private set; }

    /// <summary>Milliseconds per tick.</summary>
    public uint TickMs { get; init; } = 100;

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
        _mobs.Add(sim);
        return sim;
    }

    /// <summary>The player swings at a mob: damage now, aggro now, both through the ported paths.</summary>
    public void PlayerAttack(SimMob target)
    {
        target.Hp -= Player.AttackDamage;
        target.Mob.so_DamagedBy(Player, Player.AttackDamage, Player.AggroRatePermille);

        // Being hit interrupts a cancelable move, exactly as MobActionInMove_Cancelable::mab_Damaged does.
        target.Arg.Current.mab_Damaged(target.Arg);

        if (target.Hp <= 0)
        {
            target.Mob.IsAlive = false;
            Log.Add($"[{Now,6}] mob {target.Mob.Handle} died");
        }
    }

    /// <summary>Advance one tick: run every mob's AI, land any damage that has come due, then let mobs
    /// that are in range and off cooldown swing.
    ///
    /// <para>The order matters. Damage due this tick lands BEFORE new swings are queued, so a hit thrown
    /// last tick resolves against this tick's state rather than being overtaken.</para></summary>
    public void Tick()
    {
        Now += TickMs;

        foreach (var m in _mobs.Where(m => m.Mob.IsAlive))
        {
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
                    m.Swings.nadt_PushBack(m.AttackDamage, Now + m.SwingLandDelayMs, target);
                    m.NextSwingAt = Now + m.SwingIntervalMs;
                    Log.Add($"[{Now,6}] mob {m.Mob.Handle} swings ({decision.Choice}, {decision.Reason})");
                }
            }
        }
    }

    /// <summary>Load a driver script. It should define a global `on_tick()`.</summary>
    public void LoadScript(string lua) => Script.DoString(lua);

    /// <summary>Run until the script stops, everything dies, or the tick budget runs out.
    /// Returns the number of ticks actually run.</summary>
    public int Run(int maxTicks)
    {
        var onTick = Script.Globals.Get("on_tick");
        for (var i = 0; i < maxTicks; i++)
        {
            Tick();
            if (onTick.Type == DataType.Function && Player.IsAlive)
                Script.Call(onTick);
            if (!Player.IsAlive || _mobs.All(m => !m.Mob.IsAlive))
                return i + 1;
        }
        return maxTicks;
    }
}
