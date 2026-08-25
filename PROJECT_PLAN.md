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
| damage (`RulesOfEngagement`) | in ik-fiesta-bots, to migrate here | fuzz-exact; branch coverage not yet measured |
| mob targeting / aggro (`MobTargetSelector` family) | not started | — |
| mob tactics (`MobTacticElement::MobAction*`) | not started | — |
| everything else | not started | — |

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
