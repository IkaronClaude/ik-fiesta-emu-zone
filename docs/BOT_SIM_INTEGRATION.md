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
