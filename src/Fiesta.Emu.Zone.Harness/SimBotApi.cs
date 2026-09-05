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

    /// <summary>The SP pool. Skills spend it, and the driver gates a whole rotation on it — so a
    /// simulation that leaves it at zero makes every caster look like it cannot cast.</summary>
    public int Sp { get; set; }

    /// <summary>SP regenerated per second while out of combat. 0 until measured — the sim does not invent
    /// a regen curve, and a caster that cannot recover SP is a visible, honest limitation rather than an
    /// invisible wrong number.</summary>
    public double SpRegenPerSecond { get; set; }

    /// <summary><c>CharacterParameters.MaxSp</c> fills this in <c>Become</c>, the same way MaxHp is
    /// filled. <b>0 means "not known"</b>, which is what makes <c>spPct()</c> return -1 rather than
    /// claiming an empty bar.</summary>
    public int MaxSp { get; set; }

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

    /// <summary>Weapon reach in world units — `so_AttackRange`, which
    /// <see cref="Combat.DamageCalculator.MeleeAttackRange"/> puts at 100 for an ordinary melee weapon.
    ///
    /// <para>⚠️ <b>It was 12, and that silently cancelled every swing.</b> `level_quest.lua` closes to
    /// <c>MELEE = 45</c> and then stands off at 0.75 of it — about 34 units — before bashing, so a reach of
    /// 12 meant the auto-attack loop found the target out of range on every tick and did nothing. The
    /// symptom was a bot logging `BASH inCombat=true` at `dist=14` for 221 seconds without the mob losing
    /// any HP, which reads as a broken rotation rather than a wrong constant.</para>
    ///
    /// <para>Third of the same family, after `walkTo`'s units-per-call and `autoAttack`'s one-shot swing:
    /// <b>a plausible small number in the wrong unit is harder to see than a missing feature.</b></para></summary>
    public int AttackRange { get; set; } = Combat.DamageCalculator.MeleeAttackRange;
    /// <summary>Movement speed in units PER SECOND, the same unit the mobs use — `MobInfo.RunSpeed`, where
    /// an Orc is 127 and a MushRoom 105.
    ///
    /// <para>⚠️ <b>It used to be units per CALL, and that was a semantic mismatch rather than a bad
    /// number.</b> Live, `bot.walkTo(x, y)` starts a walk that the character continues under its own
    /// steam; here it moved 6 units and stopped. The driver's `moving()` detects movement by looking for a
    /// position change of more than 30 units between observations, so a 6-unit step read as STANDING
    /// STILL, and the pull logic — <c>elseif not moving() and bot.now() - lastAct &gt; 400</c> — crawled
    /// the character 6 units every 400 ms toward a mob 1,475 units away.</para></summary>
    public int MoveSpeed { get; set; } = 130;

    /// <summary>Where `bot.walkTo` last told the character to go, or null when it is standing.
    ///
    /// <para>This is the whole point of the change: a walk is a DESTINATION the simulation keeps making
    /// progress toward on every tick, not an instantaneous step taken only when the script calls.</para></summary>
    public (int X, int Y)? WalkTarget { get; set; }

    /// <summary>Remaining waypoints of a routed walk. Null for a straight-line walk.</summary>
    public Queue<(int X, int Y)>? WalkPath { get; set; }

    /// <summary>Where the walk is ultimately headed, as opposed to the next waypoint. This is what the
    /// crowd and reachability columns of the combat log should judge — a kite is bad because of where it
    /// ENDS, not because of the next tile on the way.</summary>
    public (int X, int Y)? FinalWalkTarget { get; set; }

    /// <summary>The mob `bot.autoAttack` last locked on to, or null when not attacking.
    ///
    /// <para>⚠️ <b>Auto-attack is a MODE.</b> Live, `bot.autoAttack(h)` sends BASHSTART once and the
    /// SERVER then streams swings until the target dies — the script does not swing per tick and does not
    /// expect to. Modelling it as a single swing made the driver walk up to an Orc, hit it once for a
    /// fraction of its 3,562 HP, and stand there.</para></summary>
    public ushort? AutoAttackTarget { get; set; }

    /// <summary>When the next auto-attack swing lands. The real interval comes from `AttackRhythm` and the
    /// weapon's `AtkSpeed`; until that is ported (see docs/COMBAT_SIM_PLAN.md) this is a documented
    /// stand-in, not a measurement.</summary>
    public uint SwingIntervalMs { get; set; } = 1500;

    /// <summary>Simulation clock at which the next auto-attack swing is due.</summary>
    public uint NextSwingAt { get; set; }

    // ---- skills --------------------------------------------------------------------------------------
    //
    // ⚠️ Without these the simulation could only MELEE, and that is not a small gap for a caster: a
    // level-60 Enchanter rebuilt from the capture's own gear hits an Orc for 20-23 with its wand and needs
    // 154 swings, while the mob kills it in 4. Measured, not guessed. A bot evaluated in that world learns
    // to do things no real character would.

    /// <summary>Every skill this character knows. Live this is the zone-login skill list the server sends;
    /// here <c>Become</c> derives it from the class and level through
    /// <see cref="Data.SkillCatalog.LearnedBy"/> when a catalog is supplied.</summary>
    public IReadOnlyList<Data.SkillDefinition> LearnedSkills { get; set; } = [];

    /// <summary>The skill currently being cast, or null when not casting.</summary>
    public Data.SkillDefinition? CastingSkill { get; set; }

    /// <summary>The handle the in-flight cast is aimed at.</summary>
    public ushort CastTarget { get; set; }

    /// <summary>When the in-flight cast lands.</summary>
    public uint CastEndsAt { get; set; }

    /// <summary>`ZoneView.CastServerConfirmed`. Live it is set by the CAST_SUC ACK and cleared by
    /// NoteCastSent/EndCast; here acceptance is synchronous, so CastRefusal.Accepted sets it and the
    /// cast resolving or being cancelled clears it.
    ///
    /// <para>Stubbed false, level_quest.lua:2077 could never book castAt[id] for an accepted cast, so
    /// every skill ran the CAST_SPAM retry ladder to exhaustion.</para></summary>
    public bool CastServerConfirmed { get; set; }

    /// <summary>Per skill id, the clock at which it comes off cooldown.</summary>
    public Dictionary<int, uint> SkillReadyAt { get; } = [];

    // ---- inventory and purse -------------------------------------------------------------------------
    //
    // Deliberately thin. The simulation has no loot, no shops and no drops, so a character genuinely
    // carries nothing and its bag is genuinely empty — that is a FACT about this world, not a stand-in,
    // and it is why these can be answered rather than stubbed. What is NOT modelled is the economy that
    // would change them, which is why `money` starts at 0 and stays there.

    /// <summary>Cen carried. <b>0 is a real value</b>, not "unknown" — a character in a simulation with no
    /// economy has no money, and the driver is entitled to that answer.</summary>
    public int Money { get; set; }

    /// <summary>Bag capacity. The live default for a character with no expansions.</summary>
    public int BagSlots { get; set; } = 24;

    /// <summary>Slots in use. Nothing fills them yet; a loot model would.</summary>
    public int BagUsed { get; set; }

    /// <summary>Accumulated experience, awarded per kill from the mob's own `MonEXP`.</summary>
    public long Experience { get; set; }

    /// <summary>`ClassID` for the class this character was built as, or 0 when it was not built from a
    /// class table. 0 means "unknown", which is what the live call reports before the avatar list has
    /// arrived.</summary>
    public int ClassId { get; set; }

    // ---- HP soul stones ------------------------------------------------------------------------------
    //
    // ⚠️ Until this existed, survival could not be evaluated AT ALL: the integration fixture had to hand
    // the character 2,000,000 HP to keep it standing, which makes every flee, heal and stone decision in
    // level_quest.lua untestable, and `sustainableHealDps` a permanent -1.

    /// <summary>Charges left in the HP soul stone. <b>-1 is "not learned yet"</b>, which is what the live
    /// `bot.hpStones` returns before the reserve has been seen — and the driver reads it that way.</summary>
    public int HpStones { get; set; } = -1;

    /// <summary>Reserve capacity, from the soul-stone shop packet live.</summary>
    public int MaxHpStones { get; set; }

    /// <summary>HP restored by one charge. 0 means "not known", as it does live.</summary>
    public int HpStoneRestore { get; set; }

    /// <summary>⭐ SEVEN SECONDS, READ OUT OF `ShinePlayer::sp_HPStoneUse` (0x00589180):
    /// <code>
    /// mov eax, [0x14D41A70]      ; the clockwatch counter
    /// add eax, 0x46              ; +70
    /// mov [esi + 0x173B4], eax   ; the "ready to use" gate
    /// </code>
    /// and `[0x14D41A70]` counts TENTHS OF A SECOND (docs/TICKRATE.md), so 70 is <b>7,000 ms</b>. The SP
    /// stone is the same function shape with the same two constants.
    ///
    /// <para>⚠️ <b>This was 5000, a number I invented</b>, and the doc line above it admitted as much
    /// ("the live value comes off the USE/USEFAIL pair" — i.e. it had never been read). The operator's
    /// rule, and it is the right one: <b>invented, guessed or "I think" = failure.</b> The invented value
    /// was not even harmlessly wrong — it made sustained healing 40% stronger than the server allows
    /// (454 HP per 5 s = 91 dps against the true 65 dps), which is precisely the margin that decides
    /// whether a fight is survivable by standing still. It also happened to collide with the OTHER
    /// constant in the same function, so it looked plausible.</para></summary>
    public uint HpStoneCooldownMs { get; set; } = 7000;

    /// <summary>⭐ THE SECOND GATE, and it is not a cooldown — it is where a request gets QUEUED.
    ///
    /// <para>`sp_NC_SOULSTONE_HP_USE_REQ` (0x00589510) reads both timestamps:</para>
    /// <code>
    /// if ([+0x173B4] &lt;= now)      -> use it now
    /// else if ([+0x173B8] &gt; now)  -> REJECT outright
    /// else                          -> [+0x173BC] = 0x589410   ; queue a deferred use
    /// </code>
    /// <para>So a press inside the first 5 seconds is refused, and a press between 5 and 7 seconds is
    /// ACCEPTED and fires automatically when the cooldown expires. A bot may therefore pre-press and get
    /// the charge at the earliest legal instant — behaviour that would have been missed entirely by
    /// modelling one cooldown.</para></summary>
    public uint StoneQueueWindowMs { get; set; } = 5000;

    /// <summary>A use accepted inside the queue window, waiting for the cooldown to expire.</summary>
    public bool HpStoneUseQueued { get; set; }

    /// <summary>When the next charge may be used.</summary>
    public uint HpStoneReadyAt { get; set; }

    /// <summary>Set once a USE has failed for an empty reserve — the live flag the driver gates on.</summary>
    public bool HpStoneDepleted { get; set; }

    // ---- SP soul stones ------------------------------------------------------------------------------
    //
    // The exact mirror of the HP reserve. It is a separate reserve live (a separate item, a separate
    // shop line, its own USE opcode), and `level_quest.lua` reads the two independently — `spStones`
    // and `spStoneDepleted` were between them the third and fourth most-leaned-on stubs in the harness
    // report, at 11,785 calls each.

    /// <summary>Charges left in the SP soul stone. -1 is "not learned yet".</summary>
    public int SpStones { get; set; } = -1;

    public int MaxSpStones { get; set; }

    /// <summary>SP restored by one charge. 0 means "not known".</summary>
    public int SpStoneRestore { get; set; }

    /// <summary>The same 70 tenths as the HP stone — `sp_SPStoneUse` is the same shape with the same two
    /// constants (<c>add eax, 0x46</c> / <c>add ecx, 0x32</c>).</summary>
    public uint SpStoneCooldownMs { get; set; } = 7000;

    public bool SpStoneUseQueued { get; set; }

    public uint SpStoneReadyAt { get; set; }

    public bool SpStoneDepleted { get; set; }

    /// <summary>Advance toward <see cref="WalkTarget"/> for one tick. Clears the target on arrival, which
    /// is what makes `bot.walking()` go false.</summary>
    /// <summary>Whether the last walk step was refused by geometry — the character is against a wall.
    ///
    /// <para>⭐ THIS IS HALF OF WHAT MAKES A KITE BAD. The operator reports the driver "kites straight into
    /// a nearby wall"; without a walkability grid the simulation walked through mountains and the claim
    /// could not be examined at all.</para></summary>
    public bool BlockedByGeometry { get; private set; }

    public void AdvanceWalk(uint elapsedMs, WalkabilityGrid? walkable = null)
    {
        BlockedByGeometry = false;
        if (WalkTarget is not { } t) return;

        var step = Math.Max(1, MoveSpeed * (int)elapsedMs / 1000);
        double dx = t.X - X, dy = t.Y - Y;
        var d = Math.Sqrt(dx * dx + dy * dy);

        var (nx, ny) = d <= step
            ? (t.X, t.Y)
            : (X + (int)Math.Round(dx / d * step), Y + (int)Math.Round(dy / d * step));

        // Arrived at this waypoint: take the next one and keep going, so a routed walk is continuous
        // rather than stopping at every corner.
        if (d <= step && WalkPath is { Count: > 0 } queue)
        {
            X = t.X;
            Y = t.Y;
            WalkTarget = queue.Dequeue();
            return;
        }

        if (walkable is not null && !walkable.IsWalkable(nx, ny))
        {
            // ⚠️ SLIDE ALONG THE WALL rather than stopping dead.
            //
            // Refusing the step outright was tried first and it is too harsh: a character that touches
            // geometry stays touched forever, and the measured cost was severe -- a level-25 Warrior on
            // Burning Hill went from 19 kills to 1, not because it fought worse but because it wedged.
            // That is the simulation inventing a failure and then blaming the driver for it.
            //
            // One axis at a time is the cheap approximation of what the client does. It is an
            // APPROXIMATION and worth saying so: the real client resolves collision against its own mesh
            // with a proper slide vector. What matters here is that a wall stops PROGRESS TOWARD THE
            // TARGET without freezing the character, so `BlockedByGeometry` still marks a kite that ran
            // into geometry.
            BlockedByGeometry = true;

            // ⚠️ THE SLIDE MUST NOT GO WHERE THE PATHFINDER WOULD NOT. `TilePathFinder` refuses to cut a
            // corner — a diagonal needs both orthogonal neighbours open — and an unconstrained slide
            // squeezes through exactly those gaps. The character then enters pockets it cannot be routed
            // out of, which is what stranded a level-25 Warrior at (5652,8539) on Burning Hill with no
            // path to 15 of its 20 nearest mobs, one of them 223 units away on open ground.
            //
            // Movement model and pathfinder have to agree about what is passable, or the simulation
            // manufactures traps and then blames the driver for walking into them.
            if (walkable.IsWalkable(nx, Y)) { X = nx; }
            else if (walkable.IsWalkable(X, ny)) { Y = ny; }
            else { WalkTarget = null; WalkPath = null; FinalWalkTarget = null; return; }
        }
        else
        {
            X = nx;
            Y = ny;
        }

        if (d <= step)
        {
            WalkTarget = null;
            WalkPath = null;
            FinalWalkTarget = null;
        }
    }

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
    /// <summary>`bot.hpPct` — HP as a percentage. <b>-1 when the maximum is not known</b>, matching the
    /// live accessor exactly.
    ///
    /// <para>⚠️ This returned <b>0</b> for the unknown case, and 0 is a VALID percentage: it reads as
    /// "dead", and every `hpp &gt;= 0 and hpp &lt; HEAL_PCT` gate in the driver fires on it. The live
    /// call has always returned -1 there. A conformance difference of one sentinel is enough to make the
    /// simulated bot behave unlike the real one under exactly the conditions being tested.</para></summary>
    public double hpPct() => _sim.Player.MaxHp == 0 ? -1 : 100.0 * _sim.Player.Hp / _sim.Player.MaxHp;

    public int sp() => _sim.Player.Sp;
    public int maxSp() => _sim.Player.MaxSp;

    /// <summary>`bot.spPct` — SP as a percentage, -1 when the maximum is not known.</summary>
    public double spPct() => _sim.Player.MaxSp == 0 ? -1 : 100.0 * _sim.Player.Sp / _sim.Player.MaxSp;
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

    /// <summary>`bot.aggressorSpawns` - the mobs on us, with where each came from. Input to
    /// level_quest.lua's shedStep.
    ///
    /// <para>The auto-stub's empty table is not neutral: n == 0 and chasing == 0 is the shape shedStep
    /// reads as "every chaser has already dropped", so it reported a completed escape on every call.</para>
    ///
    /// <para>anchorX/anchorY and fromSpawn are measured from so_mob_LastHittedLocation, which is what
    /// MobActionChase compares against so_mob_ChaseRangeSquar. chaseLimit is the mob's FollowCha.</para></summary>
    public Table aggressorSpawns()
    {
        var t = new Table(_sim.Script);
        var i = 1;
        foreach (var m in _sim.Mobs.Where(m => m.Arg.Target is SimPlayer && m.Mob.IsAlive))
        {
            var row = new Table(_sim.Script);
            row["handle"] = (double)m.Mob.Handle;
            row["mobId"] = (double)(m.Definition?.Info.Id ?? 0);
            row["x"] = (double)m.Mob.X;
            row["y"] = (double)m.Mob.Y;
            row["anchorX"] = (double)m.SpawnX;
            row["anchorY"] = (double)m.SpawnY;
            double dx = m.Mob.X - m.SpawnX, dy = m.Mob.Y - m.SpawnY;
            row["fromSpawn"] = Math.Sqrt(dx * dx + dy * dy);
            row["chaseLimit"] = 0d;
            row["willDropIn"] = 0d;
            row["isClone"] = false;
            t.Set(DynValue.NewNumber(i++), DynValue.NewTable(row));
        }
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

    /// <summary>`bot.canPick`. A PACING gate live - ZoneView.CanPick is !PickPending || sent over 2s
    /// ago - so its idle answer is true. The `can*` auto-stub answered false and shut the loot branch.
    /// The simulation sends no picks, so nothing is ever pending.</summary>
    public bool canPick() => true;

    public double dist(int handle)
    {
        var m = _sim.Find((ushort)handle);
        return m is null ? double.MaxValue : Math.Sqrt(MobTargetSelector.SquaredDistance(_sim.Player, m.Mob));
    }

    // ---- actions -------------------------------------------------------------------------------------

    /// <summary>`bot.walkTo` — start walking to a point. NOT a step: the simulation keeps closing the
    /// distance every tick until it arrives or something else sets a new destination, which is what the
    /// live call does. See <see cref="SimPlayer.WalkTarget"/> for why the difference mattered.</summary>
    /// <summary>`bot.walkTo` — go there, routing around geometry when the map has any.
    ///
    /// <para>⚠️ <b>The live call PATHFINDS.</b> The real bot's `walkTo` runs its own
    /// `CoarsePathFinder`/`NavMeshPath`; modelling it as a straight line was fine while the simulation had
    /// no walls and became a lie the moment it did — the driver got blamed for walking into terrain that
    /// the live client would have routed around.</para>
    ///
    /// <para>⚠️ <b>A failed path still walks.</b> When no route is found inside the budget the character
    /// heads straight at the target and the wall slide handles the rest. That is deliberate: refusing to
    /// move would be a bigger fiction than moving imperfectly, and `TilePathFinder` returning null means
    /// "not found within budget", never "impossible".</para></summary>
    public bool walkTo(int tx, int ty)
    {
        var p = _sim.Player;
        p.WalkPath = null;
        _sim.WalkToCalls++;
        var logAt = _sim.WalkLog.Count;
        _sim.WalkLog.Add((_sim.Now, p.X, p.Y, tx, ty, _sim.NearestMobDistance(p.X, p.Y), false));

        if (_sim.Walkable is { } grid)
        {
            var route = TilePathFinder.FindPath(grid, p.X, p.Y, tx, ty);

            // ⭐ UNREACHABLE MEANS IT DOES NOT WALK, and it says so.
            //
            // ⚠️ Two conformance bugs in one line, and they compounded. The live `walkTo` RETURNS BOOL and
            // refuses a destination the region graph cannot reach — it logs "UNREACHABLE — no route
            // through the region graph" and returns false without moving. This returned VOID (so Lua saw
            // nil, and `walkOk = bot.walkTo(...)` at level_quest.lua:2679 read as failure even on success)
            // and, worse, fell back to walking in a STRAIGHT LINE at anything it could not route to.
            //
            // That is precisely the "driver targets unreachable mobs and walks at them" behaviour: the
            // simulation was manufacturing it. 21% of Burning Hill's mobs spawn inside geometry and 16 of
            // the 40 nearest have no route, so the fallback had plenty to chase.
            if (route is null)
            {
                _sim.WalkToNoPath++;
                return false;
            }

            if (route.Count == 0)
            {
                _sim.WalkToAlreadyThere++;
                return true;                                  // already there
            }

            _sim.WalkToRouted++;
            _sim.WalkLog[logAt] = _sim.WalkLog[logAt] with { Routed = true };
            p.WalkPath = new Queue<(int X, int Y)>(route);
            p.WalkTarget = p.WalkPath.Dequeue();
            p.FinalWalkTarget = (tx, ty);
            return true;
        }

        // No geometry loaded: the server governs walkability, so do not veto the move.
        p.WalkTarget = (tx, ty);
        p.FinalWalkTarget = (tx, ty);
        return true;
    }

    /// <summary>⭐ `bot.canReach` — is there a route to this mob at all.
    ///
    /// <para>⚠️ <b>The check the driver never makes.</b> With geometry loaded, 16 of the 40 nearest mobs
    /// on Burning Hill have NO PATH from the grind spot — 21% of that map's mobs spawn INSIDE geometry and
    /// others sit behind walls — and the driver targets them anyway, walks at them, and burns the run.
    /// A player sees this instantly and picks something else.</para>
    ///
    /// <para><b>True when the map has no geometry loaded</b>, which is the honest answer: without walls
    /// nothing is unreachable, and returning false there would invent an obstacle. The live bot can back
    /// this with its own `CoarsePathFinder`, which already exists.</para></summary>
    public bool canReach(int handle)
    {
        var m = _sim.Find((ushort)handle);
        if (m is null || !m.Mob.IsAlive) return false;
        if (_sim.Walkable is not { } grid) return true;
        return Connected(grid, m.Mob.X, m.Mob.Y);
    }

    /// <summary>Whether a route exists to a point, via the bot's own navmesh reachability.</summary>
    private bool Connected(WalkabilityGrid grid, int x, int y)
        => TilePathFinder.Reachable(grid, _sim.Player.X, _sim.Player.Y, x, y);

    /// <summary>`bot.canReachPoint` — the same question about a place rather than a mob, for a kite
    /// destination.</summary>
    public bool canReachPoint(int x, int y)
        => _sim.Walkable is not { } grid || Connected(grid, x, y);

    /// <summary>`bot.walking` — is the character still on its way somewhere.</summary>
    public bool walking() => _sim.Player.WalkTarget is not null;

    /// <summary>`bot.commitStop` / `bot.stopTravel` — stop where you are.</summary>
    public void commitStop()
    {
        _sim.Player.WalkTarget = null;
        _sim.Player.WalkPath = null;
    }

    /// <summary>`bot.attack` — swing at a mob. Returns false when out of range or the mob is gone, which
    /// is what the real API does rather than throwing.</summary>
    /// <summary>⭐ `bot.attack(skill, target)` — <b>a SKILL CAST, not a swing.</b>
    ///
    /// <para>⚠️ This is the third semantic mismatch of the same family, and the most expensive. The sim
    /// had <c>attack(int handle)</c>: one melee swing at a mob. The live API is
    /// <c>attack(int skill, int target = 0)</c> and `BotManager.AttackAsync` does nothing but fill in the
    /// nearest mob when target is 0 and <b>delegate to `CastAsync`</b>. `level_quest.lua:2076` casts every
    /// one of its damage skills through it — <c>bot.attack(id, castTarget)</c>, where <c>id</c> is a SKILL
    /// id.</para>
    ///
    /// <para>So the driver was handing a skill id to a function that looked it up as a mob handle, finding
    /// no such mob, and getting false. 931 times in a 400-second run, with <b>zero casts</b> — a level-60
    /// Warrior reduced to bare melee while its whole rotation was silently discarded. An arity that
    /// happens to accept the call is worse than one that throws.</para></summary>
    public bool attack(int skill, int target = 0)
    {
        if (target == 0)
        {
            // `AttackAsync` fills in the nearest mob when the caller passes none.
            var nearest = _sim.Mobs.Where(m => m.Mob.IsAlive)
                .OrderBy(m => MobTargetSelector.SquaredDistance(_sim.Player, m.Mob))
                .FirstOrDefault();
            if (nearest is null) return false;
            target = nearest.Mob.Handle;
        }
        return cast(skill, target);
    }

    /// <summary>One melee swing at a handle — the simulation's own primitive, with no live counterpart.
    ///
    /// <para>It was called `attack`, which collided with the live `attack(skill, target)`. Renamed rather
    /// than deleted because the sim's own scenario scripts use it to drive a plain fight without a
    /// rotation; <see cref="autoAttack"/> is what a real driver uses.</para></summary>
    public bool swing(int handle)
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

    /// <summary>`bot.autoAttack` — lock on and keep swinging. See <see cref="SimPlayer.AutoAttackTarget"/>:
    /// this starts a MODE the simulation sustains, it does not deal a hit.
    ///
    /// <para>⚠️ It does NOT close the distance, and neither does the live one. `BotManager.AutoAttackAsync`
    /// sends a short face-step and a stop before BASHSTART — that sets FACING, it does not travel. The
    /// caller must already be in weapon range; if we do not walk, nobody walks.</para></summary>
    public bool autoAttack(int handle)
    {
        var m = _sim.Find((ushort)handle);
        if (m is null || !m.Mob.IsAlive) return false;
        _sim.Player.AutoAttackTarget = (ushort)handle;
        _sim.Player.NextSwingAt = _sim.Now;      // the first swing lands on the next tick
        return true;
    }

    /// <summary>`bot.stopAttack` — drop out of auto-attack.</summary>
    public void stopAttack() => _sim.Player.AutoAttackTarget = null;

    // ---- consumables ---------------------------------------------------------------------------------

    /// <summary>`bot.soulstoneHp` — spend one HP soul-stone charge.
    ///
    /// <para>⚠️ <b>A MISSING ACK IS THE SIGNAL.</b> Live, a stone only restores near a Healer and a failed
    /// use sends `USEFAIL` (or nothing at all), which is how the driver learns the reserve is empty. So
    /// this returns false and sets <see cref="SimPlayer.HpStoneDepleted"/> rather than silently doing
    /// nothing — a stub that returned true would tell the bot it healed when it did not.</para></summary>
    public bool soulstoneHp()
    {
        var p = _sim.Player;
        if (p.HpStones <= 0) { p.HpStoneDepleted = true; return false; }

        if (_sim.Now < p.HpStoneReadyAt)
        {
            // Inside the queue window the server ACCEPTS the press and fires it when the cooldown ends;
            // before that it refuses. See StoneQueueWindowMs.
            var queueOpensAt = p.HpStoneReadyAt - (p.HpStoneCooldownMs - p.StoneQueueWindowMs);
            if (_sim.Now < queueOpensAt) return false;
            p.HpStoneUseQueued = true;
            return true;
        }

        _sim.SpendHpStone();
        return true;
    }

    /// <summary>`bot.soulstoneSp` — the SP reserve, worked exactly like the HP one.</summary>
    public bool soulstoneSp()
    {
        var p = _sim.Player;
        if (p.SpStones <= 0) { p.SpStoneDepleted = true; return false; }

        if (_sim.Now < p.SpStoneReadyAt)
        {
            var queueOpensAt = p.SpStoneReadyAt - (p.SpStoneCooldownMs - p.StoneQueueWindowMs);
            if (_sim.Now < queueOpensAt) return false;
            p.SpStoneUseQueued = true;
            return true;
        }

        _sim.SpendSpStone();
        return true;
    }

    /// <summary>`bot.skillDamageAvg` — mean damage this skill has been SEEN to deal, -1 when unmeasured.</summary>
    public double skillDamageAvg(int id) => _sim.SkillDamageAvg(id);

    /// <summary>`bot.skillDamageSamples` — landed hits sampled for this skill.</summary>
    public int skillDamageSamples(int id) => _sim.SkillDamageSamples(id);

    // ---- inventory ------------------------------------------------------------------------------------

    public double exp() => _sim.Player.Experience;
    public int classId() => _sim.Player.ClassId;

    /// <summary>`bot.freeStatPoints` — unspent stat points. 0: the simulation does not level, so none are
    /// ever awarded.</summary>
    public int freeStatPoints() => 0;

    /// <summary>`bot.announce` — the driver's own status line. Routed into the simulation log so a run can
    /// be read back, which is the whole point of the call.</summary>
    public void announce(string text) => _sim.Log.Add($"[{_sim.Now,6}] [announce] {text}");

    // ---- other players -------------------------------------------------------------------------------
    //
    // There are none. A single-character simulation has no party to be invited to and no friends to
    // accept, so these are answers rather than stubs -- and `false` here is exactly what a real bot
    // alone in a field would see.

    public bool pendingInvite() => false;
    public bool partyAccept() => false;
    public bool pendingFriend() => false;
    public bool friendAccept() => false;

    /// <summary>`bot.npcSeedCount` — how many placements this map has, from `MobCoordinate.shn`.
    ///
    /// <para>⚠️ This returned a hard 0 with a note claiming "the spawner places FIGHTABLE mobs only, so
    /// there is no NPC in the world". The first half is true and the conclusion was wrong: NPCs were never
    /// going to come from `MobRegen`, which carries spawn GROUPS — Burning Hill's eleven non-combat
    /// entries are Herb, Wood and Mine nodes. Quest givers live in `MobCoordinate.shn`, keyed by mob id
    /// and map, which is the same table the live `npcCoord` reads.</para></summary>
    public int npcSeedCount()
        => _sim.Placements is null ? 0 : _sim.Placements.OnMap(_sim.MapName ?? "").Count;

    /// <summary>`bot.npcCoord` — where an NPC stands, as `{map, x, y}`. Nil when the map does not place
    /// it, matching the live call.</summary>
    public DynValue npcCoord(int npcId)
    {
        if (_sim.Placements?.For(npcId, _sim.MapName ?? "") is not { } placement) return DynValue.Nil;

        var t = new Table(_sim.Script);
        t["map"] = placement.Map;
        t["x"] = placement.CenterX;
        t["y"] = placement.CenterY;
        return DynValue.NewTable(t);
    }

    public int money() => _sim.Player.Money;
    public int bagFreeSlots() => Math.Max(0, _sim.Player.BagSlots - _sim.Player.BagUsed);
    public bool bagFull() => _sim.Player.BagUsed >= _sim.Player.BagSlots;

    /// <summary>`bot.inventory` — what the character is carrying. Empty, and honestly so: this simulation
    /// has no loot and no shops, so nothing can have got into the bag.
    ///
    /// <para>⚠️ An empty table is a real answer here and a DANGEROUS one to copy elsewhere. A full bag
    /// halts the entire quest loop live (hand-ins refuse, the board empties, exp goes to zero), so a
    /// simulation that can never fill its bag cannot exercise that failure at all. Recorded rather than
    /// papered over.</para></summary>
    public Table inventory() => new(_sim.Script);

    /// <summary>`bot.equipment` — worn items. Empty for the same reason: the scenario equips a character
    /// through <c>Become</c>, which takes `EquipmentPiece`s rather than item ids, so there are no ids to
    /// report back.</summary>
    public Table equipment() => new(_sim.Script);

    /// <summary>`bot.traveling` — is a cross-map journey under way. Always false: the simulation is one
    /// map, with no gates and no map transitions, so nothing can be travelling.</summary>
    public bool traveling() => false;

    public int spStones() => _sim.Player.SpStones;
    public int maxSpStones() => _sim.Player.MaxSpStones;
    public int spStoneRestore() => _sim.Player.SpStoneRestore;
    public bool spStoneDepleted() => _sim.Player.SpStoneDepleted;
    public double spStoneCooldownMs() => _sim.Player.SpStoneCooldownMs;

    public double spStoneReadyIn()
    {
        var p = _sim.Player;
        if (p.SpStones <= 0) return -1;
        return _sim.Now >= p.SpStoneReadyAt ? 0 : p.SpStoneReadyAt - _sim.Now;
    }

    // ---- skills --------------------------------------------------------------------------------------

    /// <summary>`bot.learnedSkills` — the ids the character knows, as a 1-based Lua array.</summary>
    public Table learnedSkills()
    {
        var t = new Table(_sim.Script);
        var i = 1;
        foreach (var s in _sim.Player.LearnedSkills) t.Set(DynValue.NewNumber(i++), DynValue.NewNumber(s.Id));
        return t;
    }

    /// <summary>`bot.skillInfo` — the client row for a skill, field for field with the live call.
    ///
    /// <para>⚠️ It answers for ANY skill in the catalog, not only learned ones, exactly as the live one
    /// does: it reads client data, and the client has the whole file. The driver relies on that to decide
    /// what is worth training. Nil for an id the game does not have.</para></summary>
    public DynValue skillInfo(int id)
    {
        var s = _sim.Skills?.Find(id);
        if (s is null) return DynValue.Nil;

        var t = new Table(_sim.Script);
        t["id"] = s.Id;
        t["name"] = s.Name;
        t["cooldownMs"] = s.CooldownMs;
        t["sp"] = s.Sp;
        t["range"] = s.Range;
        t["castTimeMs"] = s.CastTimeMs;
        t["usableDegree"] = s.UsableDegree;
        t["useClass"] = s.UseClass;
        t["heal"] = s.IsHeal;
        t["landsOn"] = s.LandsOn;
        t["selfTargeted"] = s.LandsOn == 1;
        t["maxWc"] = (double)s.Physical.MaxFlat;
        t["maxMa"] = (double)s.Magical.MaxFlat;
        t["damage"] = (double)Math.Max(s.Physical.MaxFlat, s.Magical.MaxFlat);
        return DynValue.NewTable(t);
    }

    /// <summary>`bot.cast` — cast a learned skill at a target handle.
    ///
    /// <para>Returns whether the server ACCEPTED the cast, not whether it landed: the damage arrives when
    /// the cast bar finishes, and moving in the meantime cancels it. The refusal reason is on
    /// <see cref="CombatSimulation.LastCastRefusal"/> for tests and the report.</para></summary>
    public bool cast(int skill, int target)
        => _sim.Cast(skill, (ushort)target) == CombatSimulation.CastRefusal.Accepted;

    /// <summary>`bot.casting` - is a cast bar up right now.</summary>
    public bool casting() => _sim.Player.CastingSkill is not null;

    /// <summary>`bot.castConfirmed` - did the server take the cast we just sent. See
    /// SimPlayer.CastServerConfirmed.</summary>
    public bool castConfirmed() => _sim.Player.CastServerConfirmed;

    /// <summary>`bot.skillReadyInMs` — milliseconds until a skill comes off cooldown.
    ///
    /// <para>⚠️ <b>0 for an unknown id, matching the live call exactly</b> — which reports no phantom
    /// cooldown for a skill it has no row for. That is deliberate there and copied deliberately here.</para></summary>
    public double skillReadyInMs(int id)
    {
        if (_sim.Skills?.Find(id) is null) return 0;
        if (!_sim.Player.SkillReadyAt.TryGetValue(id, out var ready)) return 0;
        return _sim.Now >= ready ? 0 : ready - _sim.Now;
    }

    /// <summary>`bot.skillCooldowns` — every skill still cooling, as `{id=, name=, readyInMs=}` rows.</summary>
    public Table skillCooldowns()
    {
        var t = new Table(_sim.Script);
        var i = 1;
        foreach (var s in _sim.Player.LearnedSkills)
        {
            var left = skillReadyInMs(s.Id);
            if (left <= 0) continue;
            var row = new Table(_sim.Script);
            row["id"] = s.Id;
            row["name"] = s.Name;
            row["readyInMs"] = left;
            t.Set(DynValue.NewNumber(i++), DynValue.NewTable(row));
        }
        return t;
    }

    public int hpStones() => _sim.Player.HpStones;
    public int maxHpStones() => _sim.Player.MaxHpStones;
    public int hpStoneRestore() => _sim.Player.HpStoneRestore;
    public bool hpStoneDepleted() => _sim.Player.HpStoneDepleted;

    /// <summary>`bot.hpStoneReadyInMs` — milliseconds until the next charge. <b>-1 when there is nothing
    /// to wait for</b>, matching the live call.</summary>
    public double hpStoneReadyInMs()
    {
        var p = _sim.Player;
        if (p.HpStones <= 0) return -1;
        return _sim.Now >= p.HpStoneReadyAt ? 0 : p.HpStoneReadyAt - _sim.Now;
    }

    /// <summary>`bot.sustainableHealDps` — healing throughput the character can keep up indefinitely.
    ///
    /// <para>One charge per cooldown is the whole of it while soul stones are the only healing modelled.
    /// <b>-1 while the restore amount is unknown</b>, because the driver's `outmatched()` treats -1 as
    /// "not learned" and declines to judge — which is the correct behaviour for a bot that has not
    /// measured it, and is why this must not return 0.</para></summary>
    public double sustainableHealDps()
    {
        var p = _sim.Player;
        if (p.HpStoneRestore <= 0 || p.HpStones <= 0) return -1;
        return p.HpStoneRestore * 1000.0 / Math.Max(1u, p.HpStoneCooldownMs);
    }

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

    /// <summary>`bot.recentDamage(windowMs)` — total damage taken in the window, not a rate.</summary>
    public double recentDamage(int windowMs)
    {
        var dps = _sim.IncomingDps((uint)Math.Max(0, windowMs));
        return dps < 0 ? 0 : dps * windowMs / 1000.0;
    }
}
