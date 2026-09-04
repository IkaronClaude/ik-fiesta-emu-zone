using Fiesta.Emu.Zone.Mob;
using MoonSharp.Interpreter;

namespace Fiesta.Emu.Zone.Lua;

/// <summary>The player the Lua driver is controlling, inside the simulation.</summary>
public sealed class SimPlayer : IShineObject, Combat.ICombatant
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
    /// `so_DamagedBy`, which turns it into hate as <c>damage * rate / 1000</c>.
    ///
    /// <para><b>1000 is exact for an ordinary attack, not a placeholder.</b> Every combat caller of
    /// `so_DamagedBy` pushes the literal <c>0x3E8</c>: `so_attack+0x117`, `so_smash+0x115`,
    /// `so_skillsmash+0x151`, `DamageAbsorbAction::exe+0xCC`. One point of damage is one point of hate.</para>
    ///
    /// <para>Only the SKILL path varies it. `sds_TemplateStore` takes the rate as a parameter, and
    /// `smo_SkillBlast+0x5BD` builds it from `ActiveSkillInfoServer.AggroPerDamage` (+0x3F) — 2,580 of
    /// 2,791 skills differ from 1000, and `TripleHit` is 9000, nine times the hate per point. Skills are
    /// not modelled here, so nothing sets this to anything else yet.</para></summary>
    public int AggroRatePermille { get; set; } = 1000;

    /// <summary>`JobChangeDmgUp` for this character's class at this character's level, in permille — the
    /// catch-up multiplier every hit ON A MONSTER goes through.
    ///
    /// <para><c>null</c> until <c>CharacterSheet.Become</c> reads it out of the class table, which is
    /// honest: a player whose class is unknown has no rate, and defaulting to 1000 would silently claim
    /// "base class" for a first-job character that should be hitting twice as hard.</para></summary>
    public int? JobChangeDamageUpPermille { get; set; }

    public int AttackRange { get; set; } = 12;
    public int MoveSpeed { get; set; } = 6;

    /// <summary>The character's stat layers. Always present — an empty container is a real character with
    /// no stats, which is different from "not configured", and keeping it non-null means the damage formula
    /// never has to ask.</summary>
    public Parameter.ParameterContainer Parameters { get; set; } = new();

    /// <summary>Whether swings are resolved through the real damage formula from <see cref="Parameters"/>,
    /// or by the flat <see cref="AttackDamage"/>.
    ///
    /// <para>Set by <c>CharacterSheet.Become</c>. It is an explicit switch and not a check like
    /// "WCmax &gt; 0" on purpose: zero weapon damage is a legitimate value for an unarmed character, and
    /// inferring the mode from it would make that character silently deal flat damage instead.</para></summary>
    public bool UsesStatFormula { get; set; }
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

    /// <summary>`bot.nearbyMobs` — every living mob, in the SAME SHAPE the live `BotApi` produces.
    ///
    /// <para>⚠️ <b>The field names are a contract, and a missing one is not a missing feature — it is a
    /// nil.</b> This method used to emit seven fields; the live one emits fifteen. `level_quest.lua:1360`
    /// does <c>mobs[m.mobId] = (mobs[m.mobId] or 0) + 1</c>, and a nil `mobId` is not a zero, it is
    /// "table index is nil" — the driver died there, 1360 lines in, having never reached combat.</para>
    ///
    /// <para>⚠️ <b>And the spelling is part of the contract.</b> The live entry is <c>maxhp</c>, lower
    /// case, while every other entity table in the API uses <c>maxHp</c>. The scripts read both — 10 of
    /// one, 7 of the other — so emitting the tidier name silently returns nil to seven call sites. Keep
    /// the server's inconsistency; it is what the scripts were written against.</para>
    ///
    /// <para>Fields with no meaning in the simulation are still EMITTED, with the type the script expects
    /// and a value it reads as absent. Omitting the key is what produces the nil-index class of bug.</para></summary>
    public Table nearbyMobs()
    {
        var t = new Table(_sim.Script);
        var i = 1;
        foreach (var m in _sim.Mobs.Where(m => m.Mob.IsAlive))
            t[i++] = MobEntry(m);
        return t;
    }

    /// <summary>One mob as the live `BotApi` shapes it. See <see cref="nearbyMobs"/> for why every field
    /// is present even when the simulation has nothing to put in it.</summary>
    private Table MobEntry(SimMob m)
    {
        var info = m.Definition?.Info;
        return new Table(_sim.Script)
        {
            ["handle"] = (double)m.Mob.Handle,
            ["x"] = (double)m.Mob.X,
            ["y"] = (double)m.Mob.Y,
            ["hp"] = (double)m.Hp,
            // ⚠️ lower-case `hp`, matching the live API. Not a typo.
            ["maxhp"] = (double)m.MaxHp,
            ["dist"] = Math.Sqrt(MobTargetSelector.SquaredDistance(_sim.Player, m.Mob)),
            ["mobId"] = (double)(info?.Id ?? 0),
            ["name"] = m.Name,
            ["level"] = (double)m.Level,
            // `IsFightable` already excludes gathering nodes, scenery and NPCs.
            ["isHuntable"] = info?.IsFightable ?? true,
            ["isNpc"] = info?.IsNpc ?? false,
            // The simulation has no gates, no polymorph clones and no facing/battle-mode broadcast yet.
            // Emitted anyway, at the value the scripts treat as "not one of those".
            ["isGate"] = false,
            ["isClone"] = false,
            ["linkMap"] = "",
            ["dir"] = 0d,
            ["mode"] = 0d,
            // ⚠️ SIM-ONLY: the live API has no such field. A script that came to depend on it would work
            // here and read nil live, so it stays documented rather than quietly useful.
            ["targetingMe"] = m.Arg.Target is SimPlayer,
        };
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

    // ---- what the driver measures about a fight -------------------------------------------------------

    /// <summary>`bot.incomingDps(windowMs)` — damage taken per second over the last window.
    ///
    /// <para>⚠️ <b>This gates a combat decision, so it must not be stubbed.</b> `level_quest.lua`'s
    /// `outmatched()` compares it against `sustainableHealDps()` to decide whether to flee, and the
    /// harness's auto-stub returned a TABLE — <c>inDps &lt;= 0</c> then raised "attempt to compare table
    /// with number" and killed the driver 2516 lines in.</para>
    ///
    /// <para><b>-1 is the honest unknown</b>, and the script is written for it: "Both must be MEASURED.
    /// -1 is 'not learned yet' (never 0-as-sentinel); an unknown must not fake a verdict." Zero would
    /// tell it the fight costs nothing.</para></summary>
    public double incomingDps(int windowMs) => _sim.IncomingDps((uint)Math.Max(0, windowMs));

    /// <summary>`bot.sustainableHealDps` — the healing throughput the character can keep up.
    ///
    /// <para>⚠️ <b>-1, because the simulation does not model healing yet</b> — no HP stones, no heal
    /// skills, no regen. That is a REFUSAL, not a value: paired with <see cref="incomingDps"/> it makes
    /// `outmatched()` return false and the driver never flees on this basis, which is the correct
    /// behaviour for a bot that has not learned the number.</para>
    ///
    /// <para>It becomes a real number when stage 2 of `docs/BOT_SIM_INTEGRATION.md` lands consumables and
    /// heal skills. Until then this is one of the calls that makes a flee decision untestable here, and
    /// it is listed as such rather than faked.</para></summary>
    public double sustainableHealDps() => -1;

    /// <summary>`bot.recentDamage(windowMs)` — total damage taken in the window, not a rate.</summary>
    public double recentDamage(int windowMs)
    {
        var dps = _sim.IncomingDps((uint)Math.Max(0, windowMs));
        return dps < 0 ? 0 : dps * windowMs / 1000.0;
    }
}
