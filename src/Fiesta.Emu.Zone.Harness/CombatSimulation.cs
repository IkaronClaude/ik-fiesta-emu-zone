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

    /// <summary>The map the simulation is on, as `MobRegen/&lt;name&gt;.txt` names it — "Urg" for Uruga.
    ///
    /// <para>⚠️ <b>`bot.map()` returns a NAME, and the harness's auto-stub returned a number.</b> It is the
    /// single most-called stub in the driver — 45,849 calls in a 4,000-tick run — and every one of them
    /// fed a comparison against a map name that could never match. `SpawnAll` sets this from the regen
    /// data it was handed, so it is a reading rather than a setting.</para></summary>
    public string MapName { get; set; } = "";

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

    // ---- casting -------------------------------------------------------------------------------------

    /// <summary>Why a cast was refused. Every one of these is a refusal the real server also makes, and
    /// the point of naming them is that a bot which cannot tell "out of range" from "no SP" cannot fix
    /// either.</summary>
    public enum CastRefusal
    {
        Accepted,
        NotLearned,
        OnCooldown,
        NotEnoughSp,
        NoTarget,
        OutOfRange,
        AlreadyCasting,
        Dead,
    }

    /// <summary>The skill catalog, when one has been loaded. Null means the scenario supplied no client
    /// data, and `skillInfo` then answers nil for every id rather than inventing rows.</summary>
    public Data.SkillCatalog? Skills { get; set; }

    /// <summary>The last cast refusal, for the harness report and for tests.</summary>
    public CastRefusal LastCastRefusal { get; private set; }

    /// <summary>How many casts have landed this run.</summary>
    public int Casts { get; private set; }

    /// <summary>Damage observed per skill — the total and the number of landed hits.
    ///
    /// <para>⭐ THE DRIVER RANKS ITS ROTATION ON THIS, and it is a genuine feedback loop rather than a
    /// lookup: `level_quest.lua` sorts under-sampled skills FIRST so every rank gets its turn, then
    /// prefers the best measured damage-per-second. Stubbed, `measuredDps` returned nil for every skill
    /// and the rotation fell back to the static table forever — the bot could never learn that a skill
    /// its data rates highly performs badly against a particular mob.</para></summary>
    private readonly Dictionary<int, (long Total, int Count)> _skillDamage = [];

    /// <summary>Mean damage a skill has actually dealt. <b>-1 when nothing has landed yet</b>, matching
    /// the live accessor — and the driver reads it that way, treating an unmeasured skill as unknown
    /// rather than as zero damage.</summary>
    public double SkillDamageAvg(int skillId)
        => _skillDamage.TryGetValue(skillId, out var d) && d.Count > 0 ? (double)d.Total / d.Count : -1;

    /// <summary>How many landed hits have been sampled for this skill. 0 is a real answer here.</summary>
    public int SkillDamageSamples(int skillId)
        => _skillDamage.TryGetValue(skillId, out var d) ? d.Count : 0;

    /// <summary>Start a cast. SP is spent NOW and the damage lands when the cast bar finishes — which is
    /// what makes a cast interruptible, and what makes cast time cost anything.</summary>
    public CastRefusal Cast(int skillId, ushort target)
    {
        if (!Player.IsAlive) return LastCastRefusal = CastRefusal.Dead;
        if (Player.CastingSkill is not null) return LastCastRefusal = CastRefusal.AlreadyCasting;

        var skill = Player.LearnedSkills.FirstOrDefault(s => s.Id == skillId);
        if (skill is null) return LastCastRefusal = CastRefusal.NotLearned;
        if (Player.SkillReadyAt.TryGetValue(skillId, out var ready) && Now < ready)
            return LastCastRefusal = CastRefusal.OnCooldown;
        if (Player.Sp < skill.Sp) return LastCastRefusal = CastRefusal.NotEnoughSp;

        // ⭐ TARGETING OURSELVES IS A SELF-CAST, whatever `LandsOn` says.
        //
        // ⚠️ This is why a Cleric died at full SP with `Heal10` ready on every incoming hit. A heal is
        // `LandsOn 3` (ALLY), not 1 (self), and the driver casts it at its OWN handle — correctly, and
        // with a comment saying so. The simulation then looked the target up among MOBS, found nothing,
        // and refused the cast. Every self-heal and every ally-buff was silently impossible.
        var selfCast = skill.LandsOn == 1 || target == Player.Handle;

        if (!selfCast)
        {
            var m = Find(target);
            if (m is null || !m.Mob.IsAlive) return LastCastRefusal = CastRefusal.NoTarget;

            // ⚠️ `Range` 0 on an OFFENSIVE skill means melee reach, not "infinite". Every Fighter skill in
            // the file has Range 0 -- `TripleHit`, `PowerHit`, `RedSlash` -- because they are swung with
            // the weapon that is already in reach. Treating 0 as unlimited would let the bot open a fight
            // from across the map and never learn to close.
            var reach = (long)(skill.Range > 0 ? skill.Range : Player.AttackRange);
            if (MobTargetSelector.SquaredDistance(Player, m.Mob) > reach * reach)
                return LastCastRefusal = CastRefusal.OutOfRange;
        }

        Player.Sp -= skill.Sp;
        Player.SkillReadyAt[skillId] = Now + (uint)Math.Max(0, skill.CooldownMs);
        Player.CastingSkill = skill;
        Player.CastTarget = target;
        Player.CastEndsAt = Now + (uint)Math.Max(0, skill.CastTimeMs);
        return LastCastRefusal = CastRefusal.Accepted;
    }

    /// <summary>⭐ MOVING CANCELS A CAST. Not a simplification — the cast bar (`0x2047`/`0x2048`) is
    /// cancelled by movement on the real server, and this project has already paid for not modelling it
    /// once: the bot's own re-pathing cancelled every mount summon it tried.
    ///
    /// <para>The SP is NOT refunded and the cooldown is NOT rolled back, because the server does not roll
    /// them back either. That asymmetry is the entire cost of a cancelled cast, and a bot that walks
    /// while casting has to feel it or it will never stop doing it.</para></summary>
    private void AdvanceCast()
    {
        if (Player.CastingSkill is not { } skill) return;

        if (!Player.IsAlive) { Player.CastingSkill = null; return; }
        if (Player.WalkTarget is not null && skill.CastTimeMs > 0)
        {
            Log.Add($"[{Now,6}] cast of {skill.InxName} CANCELLED by movement (sp {skill.Sp} spent, still cooling)");
            Player.CastingSkill = null;
            return;
        }
        if (Now < Player.CastEndsAt) return;

        Player.CastingSkill = null;
        Casts++;

        if (skill.LandsOn == 1 || Player.CastTarget == Player.Handle) { ApplySelfCast(skill); return; }

        var m = Find(Player.CastTarget);
        if (m is null || !m.Mob.IsAlive) return;      // it died mid-cast; the SP is still gone

        var damage = SkillDamage(skill, m);
        if (damage <= 0) return;

        var seen = _skillDamage.GetValueOrDefault(skill.Id);
        _skillDamage[skill.Id] = (seen.Total + damage, seen.Count + 1);

        LandOnMob(m, damage);
    }

    /// <summary>A heal is the only self-cast whose effect is modelled. Anything else lands, costs its SP
    /// and does nothing observable — which is honest: a buff the simulation does not model must not
    /// silently grant its bonus, and must not silently refuse to be cast either.</summary>
    private void ApplySelfCast(Data.SkillDefinition skill)
    {
        if (!skill.IsHeal) return;

        // ⚠️ FROM `SpecialValueA`, NOT the damage columns. `Heal10` has MinMA/MaxMA of zero and heals for
        // 1,100; reading the MA pair returned 0 and the heal landed for nothing.
        var amount = skill.HealAmount;
        if (amount <= 0) return;
        Player.Hp = Math.Min(Player.MaxHp, Player.Hp + amount);
        Log.Add($"[{Now,6}] {skill.InxName} healed for {amount} (hp {Player.Hp}/{Player.MaxHp})");
    }

    /// <summary>The skill damage roll — the same engine the capture-backed bucket tests exercise, reached
    /// through the SKILL rules object rather than the plain-swing one.</summary>
    private int SkillDamage(Data.SkillDefinition skill, SimMob target)
    {
        var mods = new AttackModifiers
        {
            RollPermille = (int)Rng.well512_GetRandom(1001),
            LevelGapRatePermille = LevelGapRate(Player, target),
            JobChangeDamageUpPermille = Player.JobChangeDamageUpPermille,
            Skill = skill.IsMagical ? skill.Magical : skill.Physical,
        };
        return DamageCalculator.ResolveDamage(Player, target, mods,
            rule: skill.IsMagical ? EngagementRule.MagicalSkill : EngagementRule.PhysicalSkill);
    }

    public void PlayerAttack(SimMob target)
        => LandOnMob(target, SwingDamage(Player, target, Player.AttackDamage));

    /// <summary>Apply damage the player dealt, whatever produced it. Shared by the swing and the cast so
    /// that aggro, the move interrupt and the death bookkeeping cannot drift apart between them.</summary>
    private void LandOnMob(SimMob target, int damage)
    {
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

            // ⭐ EXPERIENCE IS THE LEVELLING BOT'S ENTIRE SCOREBOARD, and `MonEXP` was already loaded
            // and never read. Without it `bot.exp()` is a stub, and a driver whose whole job is to gain
            // experience cannot tell a good grind from a bad one -- which makes the search in stage 5 of
            // docs/BOT_SIM_INTEGRATION.md score nothing.
            if (target.Definition is { } def) Player.Experience += def.Server.MonExp;
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
        Player.AdvanceWalk(TickMs, Walkable);
        AdvanceQueuedStones();
        AdvanceAutoAttack();
        AdvanceCast();

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
                    RecordHit(m, p, landed.Damage);

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

    /// <summary>Set to capture a <see cref="CombatLogEntry"/> per incoming hit. Null (the default) costs
    /// nothing — a full scenario matrix does not want a readiness list per skill per hit.</summary>
    public CombatLog? CombatLog { get; set; }

    /// <summary>Spend one HP charge now. Shared by an immediate use and by a queued one firing.</summary>
    public void SpendHpStone()
    {
        var p = Player;
        if (p.HpStones <= 0) { p.HpStoneDepleted = true; return; }
        p.HpStones--;
        p.HpStoneReadyAt = Now + p.HpStoneCooldownMs;
        p.Hp = Math.Min(p.MaxHp, p.Hp + p.HpStoneRestore);
        if (p.HpStones == 0) p.HpStoneDepleted = true;
    }

    public void SpendSpStone()
    {
        var p = Player;
        if (p.SpStones <= 0) { p.SpStoneDepleted = true; return; }
        p.SpStones--;
        p.SpStoneReadyAt = Now + p.SpStoneCooldownMs;
        p.Sp = Math.Min(p.MaxSp, p.Sp + p.SpStoneRestore);
        if (p.SpStones == 0) p.SpStoneDepleted = true;
    }

    /// <summary>Fire any queued stone use whose cooldown has now expired — the deferred action the server
    /// parks at `[player + 0x173BC]`.</summary>
    private void AdvanceQueuedStones()
    {
        if (Player.HpStoneUseQueued && Now >= Player.HpStoneReadyAt)
        {
            Player.HpStoneUseQueued = false;
            SpendHpStone();
        }
        if (Player.SpStoneUseQueued && Now >= Player.SpStoneReadyAt)
        {
            Player.SpStoneUseQueued = false;
            SpendSpStone();
        }
    }

    /// <summary>Diagnostics for `walkTo` under geometry — how often a route was found, refused, or the
    /// caller was already there. A driver that cannot move is usually a driver whose every route was
    /// refused, and that is invisible without counting.</summary>
    /// <summary>Every `walkTo` the driver asked for: when, from where, to where, and how far the nearest
    /// live mob was at that moment. A driver that walks away from its targets is invisible in a kill count
    /// and obvious here.</summary>
    public List<(uint At, int FromX, int FromY, int ToX, int ToY, int NearestMob, bool Routed)> WalkLog { get; } = [];

    public int WalkToCalls { get; set; }
    public int WalkToRouted { get; set; }
    public int WalkToNoPath { get; set; }
    public int WalkToAlreadyThere { get; set; }

    /// <summary>NPC and mob placements for this map, from `MobCoordinate.shn`. Null until a scenario
    /// supplies one, and `npcCoord` then answers nil exactly as the live call does without client data.</summary>
    public Data.MobCoordinateCatalog? Placements { get; set; }

    /// <summary>The map's `.shbd` walls, when loaded. Null means open ground everywhere, which is what the
    /// simulation did before this existed — and a kite validated against open ground is not validated.</summary>
    /// <para>⚠️ <b>THE PLAYER ONLY. Mobs do not pathfind — once aggro'd they phase straight through
    /// walls</b> (operator, from play). That is why <see cref="Walkable"/> is passed to the player's
    /// `AdvanceWalk` and to nothing else, and it is deliberate rather than an oversight: constraining
    /// mobs would make geometry a shield the real game does not give you, and every kite measured behind
    /// a wall would be a fiction.</para>
    ///
    /// <para>It also means an UNREACHABLE mob is not a harmless one — it cannot be walked to, but it can
    /// still come to you.</para>
    public WalkabilityGrid? Walkable { get; set; }

    /// <summary>⭐ THE ROW THE OPERATOR ASKED FOR: on each enemy hit, every stone and skill cooldown.
    ///
    /// <para>Captured HERE, at the moment the damage lands, because that is the only instant at which the
    /// question "what could it have done instead" has a definite answer. Reconstructing it afterwards from
    /// the tick loop would be guessing at state that has already moved on.</para></summary>
    private void RecordHit(SimMob attacker, SimPlayer p, int damage)
    {
        if (CombatLog is null) return;

        var skills = new List<SkillReadiness>(p.LearnedSkills.Count);
        foreach (var s in p.LearnedSkills)
        {
            var ready = p.SkillReadyAt.TryGetValue(s.Id, out var at) && Now < at ? at - Now : 0;
            skills.Add(new SkillReadiness(s.Id, s.InxName, ready, p.Sp >= s.Sp));
        }

        double StoneReady(int charges, uint readyAt)
            => charges <= 0 ? -1 : Now >= readyAt ? 0 : readyAt - Now;

        var dx = attacker.Mob.X - p.X;
        var dy = attacker.Mob.Y - p.Y;

        CombatLog.Add(new CombatLogEntry(
            At: Now,
            Attacker: attacker.Name,
            AttackerHandle: attacker.Mob.Handle,
            Damage: damage,
            Hp: p.Hp, MaxHp: p.MaxHp, Sp: p.Sp,
            DistanceToAttacker: Math.Sqrt((double)dx * dx + (double)dy * dy),
            Walking: p.WalkTarget is not null,
            Casting: p.CastingSkill is not null,
            AutoAttacking: p.AutoAttackTarget is not null,
            Aggressors: _mobs.Count(x => x.Mob.IsAlive && x.Arg.Target is SimPlayer),
            HpStones: p.HpStones,
            HpStoneReadyInMs: StoneReady(p.HpStones, p.HpStoneReadyAt),
            SpStones: p.SpStones,
            SpStoneReadyInMs: StoneReady(p.SpStones, p.SpStoneReadyAt),
            MobsNearby: Crowd(p.X, p.Y),
            // ⚠️ The FINAL destination, not the next waypoint. A routed walk's next corner is always a
            // few tiles away and always reachable, so judging that would report every kite as perfect.
            MobsNearWalkTarget: (p.FinalWalkTarget ?? p.WalkTarget) is { } w ? Crowd(w.X, w.Y) : -1,
            WalkTargetReachableFraction: (p.FinalWalkTarget ?? p.WalkTarget) is not { } wt || Walkable is null
                                         ? 1
                                         : Walkable.ReachableFraction(p.X, p.Y, wt.X, wt.Y),
            Skills: skills));
    }

    /// <summary>Distance to the nearest living mob, or -1 when the map is empty.</summary>
    public int NearestMobDistance(int x, int y)
    {
        var best = -1.0;
        foreach (var m in _mobs)
        {
            if (!m.Mob.IsAlive) continue;
            double dx = m.Mob.X - x, dy = m.Mob.Y - y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (best < 0 || d < best) best = d;
        }
        return (int)best;
    }

    /// <summary>Live mobs within <see cref="CombatLogEntry.CrowdRadius"/> of a point. Used to compare where
    /// the character IS against where it is walking TO — see `CombatLogEntry.KitingIntoACrowd`.</summary>
    private int Crowd(int x, int y)
    {
        var r = (long)CombatLogEntry.CrowdRadius * CombatLogEntry.CrowdRadius;
        var n = 0;
        foreach (var m in _mobs)
        {
            if (!m.Mob.IsAlive) continue;
            long dx = m.Mob.X - x, dy = m.Mob.Y - y;
            if (dx * dx + dy * dy <= r) n++;
        }
        return n;
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
