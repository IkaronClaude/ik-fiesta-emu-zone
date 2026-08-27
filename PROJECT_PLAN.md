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

Currently red, deliberately: **one.**

| red test | what it states |
|---|---|
| `PcapGroundTruthTests.TheCeilingIsExact_KNOWN_RED` | Against the capture, using the angle the SERVER applies (flat 1000), agreement is **127/219 (58%)**. Every FLOOR holds; every CEILING is ~20% low. The gap is a continuous per-swing multiplier of up to **1.24x** that applies to a MOB attacker as well as the character — so not gear, not item actions, not anything character-side. |

⚠️ **A previously reported 98.6% was wrong.** It used `Z:/ServerSource`'s angle table (1000-1200); the
deployed server is flat 1000. The 1.2x from a file the server does not use happened to cover the real
spread. See the log entry below.
would bracket each outgoing case gives **R ≥ 1142** (Orc) and **R ≥ 1122** (Pinky) — two mobs, two armour
values, one overlapping answer — and **1150 is a WCRate that exists in `ItemInfo.shn`**. It stays red
because a bound and a candidate are not a reading. Closing it means decoding
`NC_CHAR_CLIENT_ITEM_CMD`'s variable-length `PROTO_ITEMPACKET_INFORM` records (103-byte
`SHINE_ITEM_STRUCT`, 101-byte union) for the equipped gear's actual rate.
(`NC_CHAR_CLIENT_ITEM_CMD`, 39 items) to split those layers; decoding it is the next step.
residual so it cannot grow; this one says it should not exist.
worth, not what it is.

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

### 2026-08-27 — checked against a capture for the first time

The goal moved to "full 1:1 combat sim, checked against a pcapng". Three tables that were never loaded
are now loaded, and the port has its first check against a live server rather than against the binary.

**`DamageLvGapPVE.shn` / `DamageLvGapEVP.shn`** — the level-difference rate, previously defaulted to 1000
and documented as "the one approximation in the pipeline". `GetLevelCapRate` (0x005C8220) scans for the
first row whose signed `LvGap` is at least `defenderLevel - attackerLevel`. A player gets 1000 at or above
the target's level, ramping to a flat 1500 five levels down; **every EVP row is 1000**, so a monster's
damage is never level-adjusted — assumed before, read now.

**`DamageByAngle.txt`** — six anchors expanded by `dt_Load` into a 91-entry table indexed in direction
units. Not a lerp: each slot recomputes its step from the value just written and truncates, so the ramp
hugs the lower anchor. **The whole range is 1000-1200**, which is the number that matters when reading a
capture — a backstab explains at most 20% of a damage spread, so anything wider is something else.

**`tools/pcap_combat_truth.py`** — per-swing ground truth (attacker, defender, damage, decoded flags, the
player's server-reported stats) as JSON. It shells out to `pcap_decode.py` and `analyse_damage.py` rather
than reimplementing framing, the XOR and the briefinfo array.

Where the port now stands against `Damage.pcapng`, one clean window, level 82 character:

| direction | mob | observed | predicted | inside |
|---|---|---:|---:|---:|
| monster to player | Orc | 55-118 | ceiling 120 | ceiling holds |
| player to monster | Orc | 1128-1401 | 879-1136 | 2/34 |
| player to monster | Pinky | 964-1174 | 739-954 | 0/7 |

The monster-to-player CEILING holds, which is a real result. The floor does not, and the player-to-monster
band is short by a consistent factor — filed as the repo's one deliberate red test.

**A trap worth recording.** The first read of this capture merged all three conversations in the file and
the observed range for one mob widened from 55-118 to 55-213, which looked like our formula being wildly
wrong. A relog opens a new conversation and the character's configuration can differ across it; the
extractor now stamps a `conv` on every swing and the harness filters on it.

215 tests: 214 green, 1 deliberately red.

### 2026-08-27 — the HP-down passives, found by chasing the capture's gap

Reading `roe_AttackPower@RulesOfEngagementNormalPY` (0x00506660) to find what the port was missing turned
up a whole family of stat modifiers it had never applied. `Parameter::ChangeByConditionParam` — a bucketed
bonus keyed on **how much HP the owner is MISSING, in permille**:

| container offset | PDB name | read by |
|---|---|---|
| +0x0CE0 / +0x0CFC | `PassiveHPDownRateWCMin` / `Max` | every physical `roe_AttackPower`, both bounds |
| +0x0D18 / +0x0D34 | `PassiveHPDownRateMAMin` / `Max` | every magical `roe_AttackPower` |
| +0x0D50 / +0x0D6C | `PassiveHPDownRateAC` / `MR` | `roe_DefendPower` |

Eleven `cbcp_GetValue` call sites, all in those two functions. The lookup is
`value[key / condition]` with **zero** returned both when nothing is configured and when the key is past
the last bucket — never a clamp to the nearest, which is what makes it safe to add: an unconfigured block
changes no existing result.

⚠️ **`roe_AC` and `roe_MR` have no `cbcp` call at all** — it is `roe_DefendPower` that adds it. So armour
read directly is correct as armour and incomplete as defence, and the port now keeps that distinction.

This does NOT close the red test: the capture's character has no such passive configured as far as we can
tell, and the comparison bypasses `AttackPower` anyway. What it did produce is a much sharper hypothesis
for the red — `roe_AttackPower` ends with `value * container[+0x858] / 1000`, and +0x858 is
`PassiveSkill.Rate[PhisycalWeaponMastery]` (slot 24, arithmetic checked). If the client's displayed attack
excludes weapon mastery, that is exactly the mob-independent multiplicative factor measured. Recorded as a
lean on the test itself, not as a reading.

Also still open: the two `EventRun_IncDmgRate` item-action observers `roe_AttackPower` runs on both
attacker and defender, which can put both bounds through `GetRateAppliValue`. Not ported.

221 tests: 220 green, 1 deliberately red.

### 2026-08-27 (later) — the operator was right about free stats, and the harness was the bigger bug

**`roe_Damage` is overridden per rule.** The port had the base function and treated it as the whole story.
Five of the eight rules override slot 4 and add a FLAT pair on top of the base result:

```
damage = base(arg, attack, defend) + attacker->FreeStatStr() - defender->FreeStatCon()   (NormalPY,
                                   + attacker->FreeStatInt() - defender->FreeStatMen()    PhisycalSkill,
                                                                                          AlwaysCritical)
```

NormalMA and MagicalSkill take the Int/Men pair; CureSkill, AlwaysHit and HealAttack keep the base.
**AlwaysCritical taking the physical override is the one nobody would have guessed.**

Verified by EXECUTION, not by reading: `tools/oracle_free_stat_damage.py` stubs the two accessors and runs
the real function under emulation over six input sets, every one exact. The accessors return level-keyed
records out of runtime-allocated globals, so the VALUES are invisible to a static read — the 1:1 scale
comes from the operator measuring it in-client on 2026-07-29 (30 points into END gave a clean -30). That
measurement was already in the agent memory and was not applied when the harness was built.

**The bigger finding is that the harness was wrong, not the engine.**
`Parameter::Cluster::c_compare` builds the CHANGEPARAM packet by walking the cluster slot by slot, so the
client's displayed "Dmg 1709-1840" is the WCmin/WCmax SLOT, not `roe_MinWC` — which adds the Str chain on
top. The harness had been feeding the displayed number straight into `CoreDamage` as attack power:

| | feeding the displayed value | feeding a rebuilt container |
|---|---:|---:|
| OUT Orc | 2/34 inside | 20/22 |
| OUT Pinky | 0/7 | 6/7 |
| IN Orc | — | 105/121 |
| IN Pinky | — | 67/69 |

Reported earlier in this session as "the engine under-predicts player damage by ~30%". It did not. That is
the second time this session a confident finding turned out to be the instrument (the first was the
capstone sweep covering 25% of `.text`).

Two more harness defects fixed on the way: swings were selected across the whole session while the stat
snapshot came from one clean window (now delimited by the operator's own chat annotations), and "clean hit"
was decided by the decoded flag NAME list being empty — which is true for flag words like `0x2800` whose
bits have no names, and those are exactly the swings that exceed a maximum roll.

**Where it stands.** Every observed minimum sits at or above our predicted floor, in all four
direction/mob cases, with no tolerance at all. The ceiling is over by 2.4-11.5%, pinned by one test and
stated as wrong by another.

231 tests: 230 green, 1 deliberately red.

### 2026-08-27 (later still) — the capture DOES carry it; 2/34 to 213/219

The operator pushed back twice, and both times the assertion being corrected was mine.

**"The wire definitely carries it all, we might not read it all."** Correct, and the client PDB maps it.
`PROTO_NC_CHAR_MAPLOGIN_ACK` is size 242 — exactly the 0x1802 payload — and every stat inside
`CHAR_PARAMETER_DATA` is a `SHINE_CHAR_STATVAR { base, change }`, EIGHT bytes. The existing tooling reads
only the `change` half:

| stat | base | change |
|---|---:|---:|
| Strength | 315 | 371 |
| Constitute | 236 | 302 |
| Dexterity | 208 | 246 |
| Intelligence | 156 | 201 |
| MentalPower | 224 | 255 |
| WClow / WChigh / AC / TH / TB / MR | 0 | the value |

`Wizdom`'s slot holds ASCII heap garbage (`" awaits "`) — the server never fills it, which independently
confirms the "no cluster slot for Wizdom" note.

**"My assumption is highest level mastery passive skill."** Ruled out for THIS capture by the wire, not by
argument: all three `NC_CHAR_CLIENT_PASSIVE_CMD` packets are `00 00`. The character has zero passives, so
there is no mastery bonus here. The mechanic is real; it is not what moves these numbers.

**What did.** The displayed DEF is not the AC slot. `roe_AC` is `Con + AC`, and one free point of Con was
measured to move the displayed DEF by **+0.5** while moving MaxHp by +5 — a cluster slot cannot gain half a
point from another slot, so the display carries its own Con/2 term:

```
AC slot = displayedDEF - Con/2     =>     roe_AC = displayedDEF + Con/2
```

Putting the displayed value straight into `Base[AC]` double-counts Con. With the correction the Orc
incoming case brackets **121 of 121** observed hits between a minimum roll and a maximum roll from directly
behind, no tolerance, both bounds from game data and the wire.

**Where the harness has travelled**, same capture throughout:

| | first attempt | now |
|---|---:|---:|
| OUT Orc | 2/34 | 20/22 |
| OUT Pinky | 0/7 | 6/7 |
| IN Orc | — | **121/121** |
| IN Pinky | — | 66/69 |
| overall | — | **213/219 (97.3%)** |

Every one of those gains was an instrument defect, not an engine fix: feeding the displayed attack straight
into the core formula, merging conversations, merging windows, deciding "clean hit" from an empty flag-NAME
list, and the AC double-count. The engine changed once in all of it — the per-rule `roe_Damage` override —
and that was verified against the real function under emulation rather than against the capture.

231 tests: 230 green, 1 deliberately red.

### 2026-08-27 (final) — 190/190 incoming, exact, with nothing fitted

Read the free-stat accessors to the end instead of stopping at the first branch. `so_ply_FreeStatStr` does
NOT return the allocation — it indexes a per-points table with it:

```c
rec = this->so_GetStatDistStr();          // vtable +0x424, a CHARSTATDISTSTR
byte n = rec[0] (Str) / rec[1] (Con);
if (n > cap) return table[0];
return table[n];                          // Str 0xDA50BC4, Con 0xDA50BD0; value is a u16 at record+1
```

`CHARSTATDISTSTR` (client PDB, 6 bytes) is on the wire in `NC_CHAR_CLIENT_BASE_CMD` at **+0x57**. Offset
pinned against the operator's chat rather than by counting: byte[1] reads 3 → 50 across the three sessions
and the narration is "END was +3" → "END to 50". The obvious-looking six bytes at +0x56 are off by one.

The tables were read out of a live zone (one exec, three small reads, no loop) and are **not identity**:

| | value |
|---|---|
| `Str[0]`, `Con[0]` | 0 — and index 0 is what a MOB gets, so monsters contribute nothing either side |
| `Str[2]` | 2 — Str is 1:1 |
| `Con[19]`, `Con[20]`, `Con[21]`, `Con[50]` | 10, 10, 11, 25 — Con is `ceil(n/2)` |

And the defence reading was corrected twice: the displayed DEF **is** what `roe_AC` returns, so the Con
term has to be backed out of the slot. Treating DEF as the raw AC slot under-predicts; a `Con/2`
correction fitted the Orc case by coincidence and was structurally wrong.

With all of that read rather than fitted:

| | first attempt | now |
|---|---:|---:|
| IN Orc | — | **121/121** |
| IN Pinky | — | **69/69** |
| OUT Orc | 2/34 | 20/22 |
| OUT Pinky | 0/7 | 6/7 |
| overall | — | **216/219 (98.6%)** |

**Every incoming hit is bracketed exactly, no tolerance** — mob container from the game tables, character
defence from the login burst, level gap from `DamageLvGapEVP`, angle cap from `DamageByAngle`, free-stat
term from the live tables with the allocation reconstructed from the wire (login value plus the
`NC_CHAR_STAT_INCPOINTSUC_ACK` events before the window).

Three outgoing hits remain, and the cause is named rather than guessed. Three other leads were eliminated
by checking: weapon enhancement cancels in the max bound, zero clean outgoing hits landed while the target
had an abstate, and this character has no passive skills at all.

231 tests: 230 green, 1 deliberately red.

### 2026-08-27 (correction) — retracting the 98.6%, and what the oracle did settle

**The oracle cleared the accessors.** `tools/oracle_accessors.py` builds the same containers the harness
builds, runs the REAL `roe_MinWC` / `roe_MaxWC` / `roe_AC` under emulation, and returns 2080 / 2211 / 1215
for the character and 1569 / 1959 / 242 for the Orc — identical to this port to the digit. Containers and
accessors are correct; the residual is downstream. (Two harness traps worth keeping: the rule singleton's
vtable pointer must be written by hand because the ctor never runs, and the default virtual stub must
return a POINTER TO ZEROS, not NULL — with NULL the accessors take another branch and `roe_MinWC` comes
back as 208.0 instead of 2080.0, a clean factor of ten that looks like a unit bug.)

**Then the disassembly overturned a headline result of my own.** The deployed server's `DamageByAngle`
tables are **flat 1000**, verified three ways: the live expanded arrays at `damagebyangle_Ply` and
`damagebyangle_Mob` in two separate zone processes, and both on-disk copies (`/fiesta/9Data` and
`/source/9Data`), whose file is dated an hour BEFORE the capture. `Z:/ServerSource`'s copy expands to
1000-1200 and is not what runs.

| angle used | agreement |
|---|---|
| 1200, from `Z:/ServerSource` | 216/219 (98.6%) — **what was reported** |
| 1000, what the server applies | **127/219 (58%)** |

So "190 of 190 incoming hits exact, nothing fitted" was false. The 1.2x came from a file the server does
not use and happened to be close enough to the real, still-unexplained spread to cover it. A number that
good, resting on an input that wrong, is precisely what this suite exists to catch — and it caught it only
because the angle was checked against the live process instead of trusted.

**What is actually established:** every FLOOR holds, in all four cases, and the floor is angle-independent
— a minimum roll, the EVP level-gap rate, and the free-stat term, with the mob container from game data
and the character defence from the login burst. Every ceiling is ~20% low.

**The open question, stated precisely:** a continuous per-swing multiplier of up to 1.24x, applying to a
MOB attacker as well as to a player. Outgoing damage spans 1.24x on a smooth distribution while the weapon
range spans 1.06x, so the attack BOUNDS vary per swing. Ruled out by measurement: the accessors (oracle),
weapon mastery, target debuffs (including `ABSTATE_CHANGE`), attacker buffs, weapon enhancement, the
equipment layer split, random options, the HP-down passive (r = +0.26, wrong sign), handle reuse, and any
difference between deployed and reference mob/item data (`MobInfoServer`, `MobWeapon`, `MobInfo` and the
five equipped items are byte-identical; only unrelated `ItemInfo` rows differ).

⚠️ **`Z:/ServerSource` is STOCK data, not what is deployed.** This is the first proven case and it cost a
false headline. Where a prediction is checked against a capture, read the live value.

231 tests: 230 green, 1 deliberately red.
