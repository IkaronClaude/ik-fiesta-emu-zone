# PROJECT_PLAN — ik-fiesta-emu-zone

The authoritative log: decisions, method, and per-task results. Read this first.

---

## Goal

**Now:** a small mob / combat simulator that behaves like the zone - movement, aggro, targeting, skills,
cooldowns - good enough to run the bot's Lua driver against and iterate orders of magnitude faster than
against a live server. **No S2S/S2C networking**, and none planned for now.

**Later:** grow the same code into a full emulator that can run in place of `Zone.exe`. That is why every
function is translated 1:1 from the binary rather than approximated - an approximation would have to be
thrown away at that point.

## Two lists, kept separate

**Ported** and **covered** are not the same thing and this plan never merges them. A function that has been
translated but has no branch-complete test suite is a hypothesis. The status tables below carry both.


---

## Method

### 1. Read the structure from the binary, not from naming

Class hierarchy comes from MSVC **RTTI** in the image — `??_R3` ClassHierarchyDescriptor → BaseClassArray
→ BaseClassDescriptors → TypeDescriptors — which gives exact bases, in order, with their offsets inside the
derived object. Method lists come from PDB public symbols. Neither is inferred from what a name looks like.

### 2. Translate the function, keeping the shape

Names stay as the server has them, including its spellings (`roe_MinWC`, `PhisycalWeaponMastery`), so a
function can be diffed against its disassembly directly. Operation ORDER is preserved even where it looks
redundant, because floating-point association and integer truncation points are observable in the output.

### 3. Fuzz to DISCOVER, not to certify

Fuzzing against the original answers three questions: which inputs are load-bearing, where the branches
are, and which reading of the disassembly is right. It is exploratory and throwaway.

### 4. Write the tests - that is the deliverable

Ordinary per-function tests aiming for **100% branch coverage**, every expected value read from the oracle
rather than computed by hand (hand-derived expectations have twice been wrong while the translation was
right). These are the compliance and anti-drift artifact and they run anywhere. See `docs/METHOD.md`.


---

## Hard-won rules (carried from the damage-engine work in ik-fiesta-bots)

These cost real time to learn. They are not general advice; each one produced a confidently wrong result.

1. **When a differential test disagrees, the harness is a suspect too.** Of the first four "port bugs"
   found on the damage engine, three were defects in the measuring instrument.
2. **A negative control that does not change the semantics proves nothing.** Sabotaging a function to check
   the fuzz can catch it only works if the sabotage is real — one "sabotage" reproduced the exact behaviour
   it was meant to break, and the resulting green run looked like the fuzz was blind.
3. **An untouched input is not a passing input.** A generator that never sets a field cannot fail on it.
   Widening from 10 stat fields to all 51 turned a 12,600/12,600 pass into 121/180.
4. **A multiplicative factor is invisible when the value it multiplies is zero.** Dependency probes over an
   empty object find only the additive terms.
5. **A single failing case cannot distinguish a law from a coincidence.** Candidate formulas must be scored
   across hundreds of cases; twice, the variant that matched one minimised case lost decisively over a
   population.
6. **Emulator defaults are not process defaults.** Unicorn boots the x87 at 24-bit precision and caches
   translated blocks, so values baked into instructions can never change. Both produced fictitious findings.
7. **State what was measured, not what follows from it.** "The binary has an SSE branch" is a reading;
   "therefore the live server takes it" is an inference, and that one was wrong.

---

## Known-red tests are a convention here, not neglect

Where the simulator has to pick a behaviour the binary has not been read for, the gap gets a **failing
test**, not a passing one.

A green test over an unverified guess is worse than no test at all: it makes the guess look established,
it survives review, and it actively resists correction because changing the code now "breaks a test".
A red one states the debt in the one place nobody can ignore.

Currently red, deliberately: **none.**

Closed so far:

| was red | how it was closed |
|---|---|
| `TargettingSuccessTransition_IsUnverifiedAgainstTheBinary` | read all six returns of `mab_Think`; the guessed handoff to Attack turned out correct, and the test now asserts it *because it was read* |
| `BaseWeaponAndArmourStatsAreUnreadPerClassVirtuals` | the premise was wrong. The eight virtuals are one ICF-folded `return 0` with no player-class overrides, so base weapon/armour values really are zero. Closed by reading, in the opposite direction from the guess |

**Do not make these pass by asserting current behaviour.** Close them by reading the binary — as that one
was. The guess being right is not the point; knowing it is right is.

## The angle question — settled 2026-08-26

The operator's report was right and this plan's reasoning was wrong. A mob's detection circle is displaced
**forward along its facing by 40% of its detect range** (`ShineMob::so_mob_SightCenter`), giving roughly
1.40r ahead and 0.60r behind. The shape is a circle and contains no angular term — both things this plan
insisted on — and detection is direction-dependent anyway, because the circle is not centred on the mob.

The lesson is recorded in OPEN_QUESTIONS.md: the argument "no angle term appears, therefore orientation
cannot matter" was an argument from absence wearing a proof's clothing.

---

## Status

### Infrastructure

| piece | state |
|---|---|
| repo scaffolding | done |
| RTTI hierarchy + method extraction | done (`tools/pdb_hierarchy.py` in ik-fiesta-bots) |
| generalised function oracle | done, verified end-to-end |
| discovery/fuzz harness | next |
| branch-coverage measurement | next |

### Subsystems

| subsystem | ported | branch-covered |
|---|---|---|
| damage (`RulesOfEngagement`) | **migrated here** (`src/Fiesta.Emu.Zone/Combat/`) | fuzz-exact in the bots repo; branch coverage not yet measured |
| character parameters (`Parameter::Container`) | done — layout verified against the PDB type stream | yes, incl. compliance tests against the declared fields |
| class/level base stats (`c_Storepure`, `CharClass::MaxHP`) | done | yes |
| mob targeting / aggro (`MobTargetSelector` family) | done | yes |
| mob tactics (`MobTacticElement::MobAction*`) | partial — Targetting/Attack/Chase/Turning | yes for what is ported |
| mob stats (`MobInfoServer.shn`) | done — `c_StoreMob` + `sm_PrepareWeapon`; a mob's weapon is its gear | yes |
| normal-attack rules (`smo_RulesOfNormalAttack`) | done — chosen from `MobWeapon.HitType` at spawn | yes |
| hit / critical rolls (`roe_HitRate`, `roe_CriticalRate`) | **not started** — swings never miss here | — |
| skills (`roe_magical`, `roe_physical`) | **not started** — no caster has an attack. `SkillDataIndex::sdi_DamageRule` (+0x70) is the entry point | — |
| active-skill table (`ActiveSkill.shn`) | partial — `CastTime`/`DlyTime` only, for the attack interval | yes |
| everything else | not started | — |

The damage engine now reads its inputs from `Parameter::Container` rather than a duplicate `CombatStats`,
so a character's swing is driven by its class table, level, spent points and worn gear. The bots repo still
carries the original copy; migrating it there is follow-up work, and it has a filed P1 for the rate-eraser
bug this move exposed.

---

## Log

### 2026-08-25 — repo created

Split out of `ik-fiesta-bots` at the operator's direction: the bots repo is a client, this is a server, and
the two should not share a build. The damage engine currently lives in the bots repo and will migrate here
once this repo's harness can host it.

Class hierarchy for the mob AI domain extracted from RTTI. Confirmed shape:

```
AxialListIterator
  └── MobTargetSelector
        └── MobTargetBout
              ├── MobTargetNoBrain
              └── MobTargetAggresive
                    ├── MobTargetAggresive2
                    ├── MobTargetAggresiveALL
                    ├── MobTargetAggresiveNoLevel
                    └── MobTargetPlayerCaptivate

MobTacticElement::MobActionBase
  ├── MobActionNeededTarget ── MobActionAttack, MobActionChase, MobActionBackStep, MobActionAvoidOverlap
  ├── MobActionInMove ── MobActionInMove_Cancelable
  └── MobActionInChase, MobActionRoaming, MobActionWander, MobActionTurning, MobActionSwingDamage,
      MobActionTargetting, MobActionWaitSkillEnd, MobActionNoBrain, MobAction2Region, DuringReturn2Regen
```

`MobTargetSelector` deriving from `AxialListIterator` is the important one: target selection IS a spatial
scan callback, so detection shape and range live in the per-candidate `ali_Work` override rather than in
any table lookup.

### 2026-08-27 — how a normal attack picks its rules, and a tool that was lying

`MobWeapon.HitType` is connected. `ShineMobileObject::smo_RulesOfNormalAttack` (`+0x1E74`) is the field
that selects one of the eight `RulesOfEngagement` singletons for an ordinary swing, and it governs the
whole swing — `smo_SwingDamage` reaches `roe_HitRate`, `roe_HitRateByGlobalAction` and `roe_CalcDamage`
through it. `ShineMob::so_mob_Regenerate+0x543` sets it from weapon row 0; everything else keeps the
constructor's `&roe_normalPY`. 334 fightable mobs now attack against MR instead of AC.

**A player is always physical, whatever their class.** That retires the "magic classes cannot fight"
finding: the wizard's one-orc grind is the game, not a missing feature. Caster damage is a skill, and
skills are not modelled here at all.

**The tooling failure is the more valuable half.** The first pass concluded no code writes `+0x1E74` — an
argument from absence, which rule 3 in `OPEN_QUESTIONS.md` exists to forbid. The cause was not reasoning:
`capstone`'s `disasm()` stops at the first undecodable byte and returns what it already had. On this binary
that is 0x004ADFEF, so every whole-image scan in `tools/` had been walking **228,684 of 922,891
instructions — 24.8% — while looking exhaustive.** The write that settled the question is at 0x559284,
past the cut. `Code.sweep()` in `tools/disasm.py` now resynchronises past bad bytes; `xref_vcall.py` and
the new `xref_call.py` use it.

The rule for this repo: **a negative result is a claim about the sweep, not about the binary.** Any
"no callers" answer produced before this date is worth re-running.

193 tests, 0 red.

### 2026-08-27 — `AggroInitialize` subtracts, and it is a boss mechanic

Chased straight after the sweep fix, because the open question had been parked on a negative result.

`MobWeapon.AggroInitialize` is read at `smo_SwingDamage+0x4B5`, at the tail of a hit that connected, and it
calls `attacker->so_mob_DecreaseAggro(target, value)`. **It takes hate away from the attacker**, it does not
give it to the victim — which is how a boss rotates targets instead of locking onto one. 139 of 5,815
weapon rows carry it (400–1000) and they are all bosses; an Orc has 0.

`ShineMob::so_mob_DecreaseAggro` walks `sm_FamilyList`, so a linked pack sheds together. Ported with the
family walk, plus `StaStrength`/`StaRate` parsed but left unconnected (abnormal states are not modelled).

Corrected while reading: the +0x24AE field is `sm_CurrentTarget`, not "first attacker" — the behaviour
previously written down was right (`so_DamagedBy` only seeds it while unset), the name was not.

Still open: what supplies `so_DamagedBy`'s permille rate. `so_mobile_MobAggroRate` is NOT it — it returns a
boolean "does this object draw aggro at all".

197 tests, 0 red.

### 2026-08-27 — the third term of `nextAttackAt` is a skill cast time

The one open item that had been parked on "a local this port has not resolved". Reading `mab_Think` to its
full 0x9A0 extent — which the dump could only do after the resync fix earlier today — gives:

```c
nextAttackAt = delay + swing + sm_GetWeaponCastTime() + clockwatchNow;   // +0x8B8..+0x8C3
```

`sm_GetWeaponCastTime` (0x004B88E0) resolves the weapon row's skill through `SkillDataBox` and returns
`sdi_Activ->CastTime * 10 / 1000`, or 0 when the lookup misses. Added AFTER the three zero-clamps, and not
scaled by `AbnormalState.Rate[AttSpeed]` — **haste does not shorten a cast.**

It was invisible rather than difficult: `MobWeapon.Skill` is `-` on weapon row 0 of all 2,834 mobs, and row
0 is the row forced against a player, so the term is zero for every attack a character can receive. The old
two-term interval was exact for that case, not a floor. `SkillDataBox` now loads `ActiveSkill.shn`; the
join is clean (2,974 skill-naming weapon rows, 0 unmatched, 478 with a non-zero cast time).

Picked up on the way: `SkillDataIndex::sdi_DamageRule` (+0x70) is a `RulesOfEngagement *`, so a SKILL
carries its own rule. That is the entry point for `roe_magical` — and therefore for caster damage on the
player side, which the normal-attack path structurally cannot provide.

Open questions are down to two: mob skill attacks, and the source of `so_DamagedBy`'s aggro rate.

201 tests, 0 red.

### 2026-08-27 — `WalkChase` is a distance, and the last stale "not read" note

Re-ran the field-reader scans under full coverage, as the sweep fix required. Two survived unchanged (the
angle question's single call site at vtable +0x8F4; the `TurnSpeed` readers). One did not.

`MobInfoServer.WalkChase` has exactly one reader, `MobActionChase::mab_Think+0x895`, and it is **a distance
threshold, not a speed**:

```c
if (serv->WalkChase >= ddt_Distance(dx, dy)) mab_WalkTo(...);   // inside it: walk
else                                          mab_RunTo(...);   // beyond it: run
```

Zero means always run — 2,862 of 2,878 mobs, which is why chasing at `RunSpeed` unconditionally looked
correct. The 16 exceptions are the `B_SubHel` family at 400 (run 400, walk 115), the golems at 150/300,
`Anvil` at 100 and `KQ_SK_Dash` at 1. Ported with `MobInfo.WalkSpeed`.

The column sits between two speed columns, which is the whole reason a speed was the obvious guess — twice.

Also cleared a stale doc comment claiming `TurnSpeed`'s non-zero meaning was unread. It was resolved on
2026-08-26; the comment had not caught up.

203 tests, 0 red.

### 2026-08-27 — the aggro rate, and the last of the small open questions

`so_DamagedBy`'s permille rate splits cleanly. **An ordinary attack passes the literal 1000** — all four
combat call sites push `0x3E8` — so one point of damage is one point of hate, and this simulation's 1000 is
exact rather than an assumption. Only skills vary it: `smo_SkillBlast+0x5BD` builds the rate from
`ActiveSkillInfoServer.AggroPerDamage` (2,580 of 2,791 skills differ from 1000; `TripleHit` is 9000 — a
taunt) and accumulates `AbsoluteAggro` separately as flat threat, which can be negative.

One term of that is still unread and is genuinely small: a runtime-allocated global at `0x1325EDE0`, which
the `- 1000` suggests is a server-wide baseline of 1000. Not in the image, so it needs a live process.

That leaves **one** open question: mob skill attacks. Three closed today.

203 tests, 0 red.
