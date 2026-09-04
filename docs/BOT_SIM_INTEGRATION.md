# Driving the real bot script against the simulation

**Goal:** run `ik-fiesta-bots/scripts/level_quest.lua` — the actual production driver, unmodified —
against this project's combat simulation, iterate its combat logic at 100x realtime, and ship the improved
script back to the bots project with confidence that what got better in the sim is better live.

`docs/COMBAT_SIM_PLAN.md` is the other half of this: it plans the ENGINE (rolls, abstates, skills). This
file is about the HARNESS — the contract between the script and the world it thinks it is in. The two are
independent: a perfect engine behind a wrong `bot.*` surface produces a bot that plays a game nobody ships.

---

## Where this actually stands, measured

Run `LevelingBotHarness.Attach(sim, src)` over the real driver, 2000 ticks:

| | |
|---|---|
| speed | **~128x realtime** (2000 ticks x 100 ms = 200 simulated seconds in 1.56 s wall) |
| distinct `bot.*` reached at runtime | **50** |
| — backed by the simulation | **14** |
| — **auto-stubbed** | **36** |
| `bot.*` mentioned anywhere in the source | **181** |
| script errors | **1** — `chunk_0:(1360,9-49): table index is nil` |
| kills | **0** |

**Since then** (see stages 1 and 2 below): 0 errors, 57 calls reached, 20 backed, **2-9 kills** depending
on gear, ~200x realtime.

⭐ **Speed is not the problem and never was.** 128x is already past the ask; a batch of 100 seeds is
minutes, not hours. The problem is that the driver crashes 1360 lines in and never throws a punch. Every
stage below is about FIDELITY, and the speed work at the end is only about turning fidelity into search.

⚠️ **36 silent stubs is the headline risk.** `LevelingBotHarness` auto-stubs any `bot.*` the simulation
does not back, guessing a shape from the call site. That is what lets the driver run at all — and it means
**a green run proves nothing on its own**. `bot.map` is called 28,122 times and stubbed as a *number*; the
real one returns a map NAME. Every comparison against it is quietly false.

---

## Stage 0 — one contract, two backings

`Fiesta.Bot.Scripting.BotApi` (251 public methods) and `Fiesta.Emu.Zone.Lua.SimBotApi` (16) are unrelated
C# types that MoonSharp happens to expose under the same global. Nothing makes them agree, so they drift
silently and the drift shows up as a bot that behaves differently in the sim for no visible reason.

**Do the cheap thing first: a conformance test, not a shared interface.** Reflect over `BotApi`'s public
methods and assert that each one is either implemented on `SimBotApi` with a compatible signature, or
present in an explicit `NotSimulated` list with a reason. Extracting a shared `IBotApi` into a package both
repos reference is the tidier end state and it couples two build graphs; earn that later.

The test's value is the RATCHET: "36 stubbed" becomes a number in a failing assertion that can only go
down, and a new `bot.*` added to the bots project shows up here as a build break rather than as a stub.

**Exit:** every `bot.*` is classified — implemented, explicitly not-simulated, or failing the build.

---

## Stage 1 — make the driver survive

It crashes at `level_quest.lua:1360`:

```lua
for _, m in ipairs(bot.nearbyMobs()) do
  if m.isGate then gates[#gates+1] = (m.linkMap or "?")
  else mobs[m.mobId] = (mobs[m.mobId] or 0) + 1 end     -- m.mobId is nil -> "table index is nil"
```

`SimBotApi.nearbyMobs()` emits `{handle, x, y, hp, maxHp, dist, targetingMe}`. The real one emits more,
and the script indexes a table with a field the sim never set.

**This is the whole failure class in one line: the sim's ENTITY SHAPES must match the real ones
field-for-field, not just the function names.** A missing field is `nil`, and `nil` in Lua is a valid
value right up until it is a table key.

- Enumerate the fields the scripts actually read off each entity table (`nearbyMobs`, `aggressors`,
  `inventory`, `equipment`, `activeQuests`, `shopItems`, `drops`, `gates`). Grep for `m.<field>` at each
  call site rather than guessing from `BotApi`.
- Emit all of them from the sim. Where the sim genuinely cannot know one, emit it with the same TYPE and
  a value the script treats as absent — never omit the key.
- Add a harness check: any `nil` field read off a sim-produced table is recorded, and the run reports it.

**Exit:** 0 script errors over 10,000 ticks from 20 seeds.

---

## Stage 2 — make it fight

0 kills. The driver never reaches combat because it is stuck upstream, but once it does, the combat calls
have to mean something:

| call | today | needed |
|---|---|---|
| `attack` / `autoAttack` | one swing, range-gated | the real swing loop: `BASHSTART` cadence, `AttackRhythm`, damage through `DamageCalculator` |
| `cast` / `castGround` / `casting` / `castConfirmed` | stubbed | cast bar, cast time, cooldown, SP cost, the `SkillBlastCascade` |
| `skillInfo` / `skillCooldowns` / `skillReadyInMs` | stubbed | from `ActiveSkill.shn`, which this repo already loads |
| `hpStones` / `soulstoneHp` / `useItem` | stubbed | consumables with real restore amounts and cooldowns |
| `flee` / `kiteLoop` / `kiteStepPoint` | stubbed | movement against the real `.shbd` walkability |
| `mobHitAvg` / `incomingDps` / `mobHitsToKillUs` | stubbed | derived from the sim's own damage log, which already exists |

⭐ The engine work these need is mostly DONE or planned in `COMBAT_SIM_PLAN.md` — hit/crit/block rolls are
ported, the skill cascade is ported. This stage is wiring, not research.

**Exit:** the driver kills mobs unaided, and `kills`, `deaths` and `exp/hour` are non-zero and stable
across seeds.

### Progress, and what it exposed

`walkTo` and `autoAttack` were both modelled as EVENTS where the live ones are MODES, and both failures
looked like healthy behaviour:

- **`walkTo`** live starts a walk the character continues on its own. The simulation moved it six units and
  stopped — and six was units-per-CALL where every mob in the same simulation moves in units-per-SECOND
  (`MobInfo.RunSpeed`; an Orc is 127). The driver's `moving()` reads a position change under 30 units as
  standing still, so its pull logic crawled six units every 400 ms toward a mob 1,475 units away.
- **`autoAttack`** live sends BASHSTART once and the SERVER streams swings until the target dies. The
  simulation dealt exactly one hit, against an Orc with 3,562 HP.

Fixing both took kills from 0 to 1.

⭐ **Then 1 was the answer no matter what — and reading that as a bottleneck was WRONG.** Three scenarios,
same seed, same 400 simulated seconds:

| character | swing vs Orc (61, 3562 hp) | time-to-kill | kills BEFORE | kills AFTER |
|---|---|---|---|---|
| lvl 40, weapon 60-95 | 34-40 | ~144 s | 1 | **2** |
| lvl 60, weapon 60-95 | 71-80 | ~70 s | 1 | **5** |
| lvl 60, weapon 300-420 | 131-162 | ~36 s | 1 | **9** |

A 4x improvement in kill speed had bought nothing, and I concluded from that "the limit is the driver's
loop, not combat — the remaining work is quest state". **That was the wrong conclusion from the right
observation.**

The cause was a third instance of the same units family: `SimPlayer.AttackRange` was **12** where a melee
weapon reaches **100** (`DamageCalculator.MeleeAttackRange`). `level_quest.lua` closes to `MELEE = 45` and
stands off at about 34 before bashing, so every swing found the target out of range and did nothing. The
one kill was the bot happening to end up inside 12 units. The symptom was a log line reading
`BASH inCombat=true` at `dist=14` for 221 seconds with the mob at full HP — which looks like a broken
rotation, not a wrong constant.

⚠️ **A flat response to an input is evidence of a BROKEN input, not of a different bottleneck.** Treating
"more damage changes nothing" as information about the rest of the system, rather than about the damage
never being applied, sent the plan off after quest state. The phase flip-flopping is real and still there;
it simply was not what was stopping the bot.

With the reach corrected, kills track `1/ttk` at about 80% of theory (2.8, 5.7, 11.1 predicted), and the
gap is travel and retargeting — real behaviour. `MoreDamageMeansMoreKills` now asserts the ordering, so a
regression of this kind fails instead of being interpreted.

⚠️ It also shows how easily the two get confused. The first scenario — a level-40 Cleric in Uruga, a
level-61 zone — takes 639-746 damage per mob swing against a real max HP of 879, and only survives because
the fixture hands it 2,000,000 HP. Read without the level-gap numbers, "the bot barely kills anything and
is always nearly dead" reads as a bad bot. It is a badly chosen fixture, and the engine reporting it
faithfully.

---

## Stage 3 — the fidelity ratchet

Turn the stub list from a silent default into an explicit, shrinking allowlist.

Three legitimate outcomes per `bot.*`, and no fourth:

1. **Simulated** — backed by real data or real engine code.
2. **Declared inert** — stubbed with a documented constant AND an argument for why it cannot change a
   combat decision (`bot.announce`, `bot.whisper`, `bot.friendAdd`).
3. **Fatal** — the run ABORTS. For anything that gates a combat decision and is not yet simulated,
   aborting is the honest answer; a stub makes the bot look like it handled a case it never saw.

⚠️ **A stub returning 0 is a decision, not a neutral.** `LevelingBotHarness.StubDefaults` already exists
for exactly this — `bagFreeSlots = 0` means "bag full", which stops the quest loop dead. Every default
needs that reasoning written next to it or it is a guess with a number on it.

**Exit:** the stub count is asserted in a test and can only go down.

---

## Stage 4 — the oracle: check the sim against reality

The step that stops this becoming a fast wrong answer. **A simulation that agrees with itself is worth
nothing**; this project has the captures to check it against something else.

- **Damage distribution.** Put the capture's own character and mobs into the sim, make the same number of
  swings and casts, and compare the damage HISTOGRAM against `MageDamageLvl60` / `FighterDamageLvl60`.
  The band tests already prove single hits; this proves the distribution — the roll, the crit rate, the
  miss rate.
- **Crit rate.** Already a worked example: the Enchanter's gear predicts 230 permille and the capture
  shows 224 over 67 hits. The sim must reproduce that from the same equipment.
- **Time-to-kill.** The capture says how long a mob took to die. The sim should agree within the
  variance the roll allows.
- **Incoming damage.** `NoBucketExceedsWhatAMaximumRollCanProduce` already validates mob→player swings;
  the sim should reproduce the same distribution over many ticks.

**Exit:** a `SimulationAgreesWithTheCapture` test that fails when the sim drifts, using the captures the
repo already has.

---

## Stage 5 — turn fidelity into search

Only worth building once stage 4 passes.

- **Headless batch runner.** N seeds x M script variants, in parallel. 128x realtime and ~8 cores makes a
  1-hour grind session about 30 seconds.
- **A scoring function**, decided BEFORE looking at results: exp/hour, deaths/hour, downtime %, stones
  spent/hour. Write it down first or every run becomes a story about why the number that moved is the
  number that mattered.
- **Variance discipline.** One seed proves nothing. Report the median and spread across seeds, and treat
  a change smaller than the seed-to-seed spread as noise.
- **A/B diff.** Two script versions, same seeds, paired comparison.

**Exit:** a one-command run that reports "variant B beats A by X +/- Y over N seeds".

---

## Stage 6 — reintegration into the bots project

The script already lives in `ik-fiesta-bots/scripts/` and the harness reads it from there, so there is no
port step — which is the point. What can still go wrong:

- **Sim-only assumptions.** A script tuned against a stub is tuned against a constant. The stage 3
  allowlist is the defence: if a decision depended on a stubbed call, the run should have aborted.
- **Tick-rate coupling.** The sim ticks a fixed 100 ms; the live bot's loop does not. A cadence tuned to
  the tick will drift live. Run the final candidate at several `TickMs` values and require the score to
  hold.
- **The live smoke run is mandatory.** One real session on the live server, watched, before the script is
  left running. The sim is evidence, not proof — see `fiesta-deployed-data-vs-running-process`: this
  project has already been burned once by reference data that was not what the server ran.
- **Ship the harness too.** The conformance test belongs in whichever repo is easiest to run in CI, so a
  change to `BotApi` breaks it immediately rather than at the next sim run.

---

## Traps, from this project's own scar tissue

- **`Table[int]` loses a 0 or negative key in MoonSharp**, and then `pairs()` throws for the whole table.
  Use `Table.Set(DynValue.NewNumber(id))`. This broke level-1 levelling entirely once.
- **`select('#', ...)` returns 1 for zero arguments** in MoonSharp, which silently killed a per-tick memo.
- **`bot.now` is called 589 times in the scripts** — more than anything else. If simulated time does not
  advance the way the live bot's does, every cooldown, every retry and every timeout is wrong, and none of
  it looks wrong.
- **0 is a valid value, never a sentinel.** `x ~= 0 and x or fallback` makes zero untestable, and half the
  stub defaults are zeros.
- **A green run with 36 stubs is a fast wrong answer.** The stub count belongs in the test output next to
  the score, always.

---

## ⚠️ 2026-09-04 — EVERY DRIVER MEASUREMENT IN THIS DOCUMENT IS CONFOUNDED

`level_quest.lua` thrashes `PHASE => xpgrind` / `PHASE => kill (quest mobs)` on **every tick of every
run**, against its own crutch line:

> `no active quests KNOWN yet (n=0, lists still loading or genuinely none) — xp-grinding until they
> arrive. NOT the same as 'all my quests are unsolvable'.`

`activeQuests` is backed and always empty; `availableQuests`, `eligibleQuests`, `npcCoord` and `canPick`
are still stubs, because the spawner places **fightable mobs only** and there is no NPC to take a quest
from. So the phase machine that decides WHERE to grind and WHAT to kill spends the entire run in a crutch
loop.

**What that invalidates.** The driver behaviours measured here — ping-ponging between two fixed
destinations, 1,370-unit legs where walls-off does 293-unit hops, spending the run in transit, and
probably the 6-kills-with-walls against 19-without gap — are downstream of a MISSING SUBSYSTEM, not of
combat or navigation. It also explains why the driver looked equally odd with walls off, which no
navigation theory accounted for.

**What it does not invalidate.** The combat engine (capture-exact on every stat), the soul-stone model
(cooldown read from the binary), the navmesh (0 blocked ticks, every route found), and the API
conformance fixes — `attack(skill, target)`, `walkTo` returning bool, `hpPct` returning -1 — are all
independently verified and stand.

**Judging a quest-driven bot in a world with no quests measures the crutch, not the bot.** The quest
surface is the next thing to back, ahead of the kiting work, which cannot be honestly assessed until the
driver's own decision loop is being fed.

### Hypotheses this session tested and DISPROVED — do not repeat without new evidence

| Hypothesis | Killed by |
|---|---|
| The stone cooldown is wrong / too short | It was: 5,000 ms was invented. The real 7,000 is in `sp_HPStoneUse` — but the operator's rule stands, mobs are MEANT to outdamage in dungeons |
| The driver dies because it cannot out-heal | It was healing at its ceiling; it never kited — 3 of 48 hits moving, median attacker range 0u |
| Missing armour enhancement explains the DEF gap | The whole stat vector was short, not just AC; it was "Power of Love" |
| Items cannot grant primary stats | `GradeItemOption.shn`, and a pet granting +2 to all five |
| The driver chases mobs it cannot reach | `walkTo` refused **0 of 12** calls once the navmesh landed |
| Walls-on kills collapse because of geometry | Leg length: ~1,370u legs against ~293u, so the run is spent travelling |

### Follow-up: NPCs are in `MobCoordinate.shn`, and the grind maps have none

`npcCoord` and `npcSeedCount` are now backed by `MobCoordinateCatalog` (3,523 placements), which is the
same table the live `npcCoord` reads. The note they used to carry — "the spawner places fightable mobs
only, so there is no NPC in the world" — had a true first half and a wrong conclusion: NPCs were never
going to come from `MobRegen`, which carries spawn GROUPS. Burning Hill's eleven non-combat entries are
Herb, Wood and Mine nodes.

**But the grind maps still have no quest givers, and that is the game, not a gap:**

| map | placements | of them NPCs |
|---|---|---|
| `RouVal02` Burning Hill (field) | 38 | **0** |
| `ValDn01` Marlone Clan's Hideout (dungeon) | 62 | **0** |
| `Urg` Uruga (town-ish) | 121 | 25 |

Quest NPCs live in towns. A character dropped into a field or a dungeon has nobody to take a quest from,
so `activeQuests` is legitimately empty and the crutch loop is the driver doing what it says it does.

That splits the remaining work cleanly, and the split matters:

- **The matrix is a GRIND benchmark and should stay one.** The operator specified it as one field map and
  one dungeon per level band; measuring combat there is the point. What the driver should do with no
  quests is settle into `xpgrind`, not thrash `xpgrind` / `kill (quest mobs)` every tick — that thrash is
  a real driver inefficiency and it is measurable here.
- **Measuring QUEST decision-making needs towns and cross-map travel**, which is a subsystem of its own
  and pairs with the P3 manual quest-difficulty table.

### 2026-09-04 (later) — walls are ON by default, and the harness stopped lying

The `.shbd` reader and navmesh are now the BOT'S OWN, copied verbatim into `Pathfinding/` on the
operator's instruction. `walkTo` refusals went from **394 of 395 to 0 of 27**, and geometry is on by
default because a real zone has it.

That reverses the earlier decision to keep walls opt-in, and the measurements reverse with it:

| cell | walls off | walls on |
|---|---|---|
| Warrior L25 dungeon | **DIED at 125s** | survived 150s |
| HighCleric L25 dungeon | **DIED at 45s** | survived 150s |
| Warrior L75 field | 10 kills | 10 kills |
| HighCleric L75 field | 7 kills | 7 kills |
| Warrior L25 field | 19 kills | 6 kills |

Geometry **prevents** deaths at low level and costs nothing at all at high level. It does cost kills at
level 25, which is the price of walking real terrain instead of through it — a number to improve, not to
hide by switching the walls off.

⚠️ **This also invalidates the earlier "dies in five of six dungeon cells" figure**, which was measured
with walls off and a straight-line `walkTo`. Re-measure before quoting it.

The genuinely open driver defect is unchanged and now measurable on honest ground: it does not kite, and
HighCleric L75 still dies in Trumpy Remains at 10 seconds with or without geometry.
