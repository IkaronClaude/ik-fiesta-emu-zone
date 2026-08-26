using Fiesta.Emu.Zone.Mob;
using MoonSharp.Interpreter;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>The player the Lua driver is controlling, inside the simulation.</summary>
public sealed class SimPlayer : IShineObject
{
    public ushort Handle { get; init; } = 1;
    public int X { get; set; }
    public int Y { get; set; }
    public bool IsAlive => Hp > 0;

    public int Hp { get; set; } = 1000;
    public int MaxHp { get; set; } = 1000;
    public int Level { get; set; } = 1;

    /// <summary>Damage this player deals per attack. A stand-in until the damage engine is migrated in
    /// from ik-fiesta-bots.</summary>
    public int AttackDamage { get; set; } = 40;

    /// <summary>Aggro generated per point of damage dealt, in permille — the third argument of
    /// `so_DamagedBy`.</summary>
    public int AggroRatePermille { get; set; } = 1000;

    public int AttackRange { get; set; } = 12;
    public int MoveSpeed { get; set; } = 6;
}

/// <summary>The `bot.*` table the driver scripts expect, backed by the simulation instead of a live
/// connection.
///
/// <para>This is the point of the whole exercise: the same Lua that drives a real bot can be run against
/// simulated mobs, so a change to combat logic can be evaluated in seconds instead of a live session.</para>
///
/// <para>⚠️ <b>The combat subset only.</b> The real driver also calls quest, inventory, navigation and
/// vendor functions (`bot.questName`, `bot.bagFreeSlots`, `bot.npcByMob`, `bot.money`, `bot.mounted`, …)
/// which this simulation does not model. Scripts that need those will fail here, and that is the honest
/// boundary — the alternative is stub functions returning plausible values, which would let a script run
/// while quietly simulating nothing.</para></summary>
public sealed class SimBotApi
{
    private readonly CombatSimulation _sim;

    public SimBotApi(CombatSimulation sim) => _sim = sim;

    // ---- clock and self ------------------------------------------------------------------------------

    /// <summary>`bot.now` — milliseconds since the simulation began. The driver's single most-used call,
    /// and the reason the simulation owns the clock rather than reading a wall clock: time only advances
    /// when a tick is run, so a fight can be replayed exactly.</summary>
    public uint now() => _sim.Now;

    public int x() => _sim.Player.X;
    public int y() => _sim.Player.Y;
    public int hp() => _sim.Player.Hp;
    public int maxHp() => _sim.Player.MaxHp;
    public double hpPct() => _sim.Player.MaxHp == 0 ? 0 : 100.0 * _sim.Player.Hp / _sim.Player.MaxHp;
    public int level() => _sim.Player.Level;
    public bool alive() => _sim.Player.IsAlive;

    /// <summary>`bot.inCombat` — is anything currently targeting the player. In the bot this means "was
    /// hit recently"; here it is exact, because the simulation can see every mob's target.</summary>
    public bool inCombat() => _sim.Mobs.Any(m => m.Arg.Target is SimPlayer);

    /// <summary>`bot.aggressorHandles` — the handles of every mob currently targeting the player.</summary>
    public Table aggressorHandles()
    {
        var t = new Table(_sim.Script);
        var i = 1;
        foreach (var m in _sim.Mobs.Where(m => m.Arg.Target is SimPlayer))
            t[i++] = (double)m.Mob.Handle;
        return t;
    }

    // ---- the world -----------------------------------------------------------------------------------

    /// <summary>`bot.nearbyMobs` — every living mob, as tables with the fields the driver reads.</summary>
    public Table nearbyMobs()
    {
        var t = new Table(_sim.Script);
        var i = 1;
        foreach (var m in _sim.Mobs.Where(m => m.Mob.IsAlive))
        {
            var e = new Table(_sim.Script)
            {
                ["handle"] = (double)m.Mob.Handle,
                ["x"] = (double)m.Mob.X,
                ["y"] = (double)m.Mob.Y,
                ["hp"] = (double)m.Hp,
                ["maxHp"] = (double)m.MaxHp,
                ["dist"] = Math.Sqrt(MobTargetSelector.SquaredDistance(_sim.Player, m.Mob)),
                ["targetingMe"] = m.Arg.Target is SimPlayer,
            };
            t[i++] = e;
        }
        return t;
    }

    public double dist(int handle)
    {
        var m = _sim.Find((ushort)handle);
        return m is null ? double.MaxValue : Math.Sqrt(MobTargetSelector.SquaredDistance(_sim.Player, m.Mob));
    }

    // ---- actions -------------------------------------------------------------------------------------

    /// <summary>`bot.walkTo` — step toward a point at the player's move speed. One tick of movement, not
    /// a blocking walk: the driver is expected to call it repeatedly, as it does against a real server.</summary>
    public void walkTo(int tx, int ty)
    {
        var p = _sim.Player;
        double dx = tx - p.X, dy = ty - p.Y;
        var d = Math.Sqrt(dx * dx + dy * dy);
        if (d <= p.MoveSpeed) { p.X = tx; p.Y = ty; return; }
        p.X += (int)Math.Round(dx / d * p.MoveSpeed);
        p.Y += (int)Math.Round(dy / d * p.MoveSpeed);
    }

    /// <summary>`bot.attack` — swing at a mob. Returns false when out of range or the mob is gone, which
    /// is what the real API does rather than throwing.</summary>
    public bool attack(int handle)
    {
        var m = _sim.Find((ushort)handle);
        if (m is null || !m.Mob.IsAlive) return false;

        var p = _sim.Player;
        if (MobTargetSelector.SquaredDistance(p, m.Mob) > (long)p.AttackRange * p.AttackRange)
            return false;

        _sim.PlayerAttack(m);
        return true;
    }

    public bool isAlive(int handle) => _sim.Find((ushort)handle)?.Mob.IsAlive ?? false;
}
