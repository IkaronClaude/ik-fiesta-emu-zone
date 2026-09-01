# Open questions

Things this project has NOT resolved, kept where they cannot be forgotten. Each entry says what is
believed, what it rests on, and what would settle it.

**Three rules.**

1. An entry leaves this file when it is *read*, not when it is guessed convincingly.
2. An entry that contradicts the operator's play experience stays open until the disagreement is actually
   resolved — the data does not automatically win.
3. **Never close an entry with an argument from absence.** This is a release C++ binary: `/OPT:REF` strips
   unreferenced code and data, so anything still in the image is referenced by *something*. "I could not
   find the caller" and "there is no caller" are different claims, and only the second is worth writing
   down. Four entries have already been closed the wrong way round by ignoring this — see Resolved.
   The fourth was manufactured by my own tool: a whole-image scan that silently covered 24.8% of `.text`
   (see `tools/disasm.py`'s `Code.sweep`). **A negative result is only as good as the sweep behind it.**

---

## 1. Mob skill attacks are not modelled

**Status:** open. The largest remaining hole in mob combat.

`MobWeapon` has 5,815 rows across 2,878 mobs; 1,324 mobs have more than one. The simulation only ever uses
weapon index 0. `AttackElement4Mob` carries a 500-entry skill sequence plus `OutOfRange` / `HPLow` /
`TargetState` change-lists, and `so_mob_SelectWeapon` chooses between them via `ae4m_NextSkill` and a
WELL512 roll against `sm_GetUseWeaponRate`. None of that is ported — the exchange-rule *order* in
`MobActionAttack` is, but its predicates are placeholders.

**Two leads picked up on 2026-08-27.** `SkillDataBox::SkillDataIndex` carries `sdi_DamageRule` at +0x70,
a `RulesOfEngagement *` — so a SKILL brings its own rule, the way a mob's normal attack gets one from
`smo_RulesOfNormalAttack`. That is where `roe_magical` / `roe_physical` enter, and therefore where caster
damage lives for players too. `sdi_Activ` (+0x04) is the `ActiveSkillInfo` row, already loaded here by
`SkillDataBox`; `sdi_AttackDist` (+0x74) is the skill's range. `ActiveSkillInfoServer` carries the
rest of what a skill needs — `AggroPerDamage`, `AbsoluteAggro`, `SkillHitType`, `SkilPyHitRate` /
`SkilMaHitRate`, `DmgIncRate` / `DmgIncValue`, `SwingTime` / `HitTime`.

---

## 2. `Damage.pcapng`'s residual — two of three parts now explained elsewhere

**Status:** open, but no longer the headline. `FighterDamageLvl60.pcapng` and `BucketGroundTruthTests`
overtook it: that capture is bucketed by state, takes the character's attack and defence from the wire,
and needs no reconstruction at all. Where nothing unmodelled is acting, the port is **exact in both
directions** there. So the old capture's residual is now mostly a story about this harness, not the engine.

**Part 1 — incoming was RECONSTRUCTION ERROR, closed.** This file inverted displayed totals to rebuild the
character, backing Con out of DEF but leaving the Str chain inside Dmg. Every displayed stat is
`ftol(roe_Xxx) + freeStat` (`so_mobile_NotifyParameterChange`), so both had to come out. Taking DEF
straight off the wire in the new capture makes incoming match at both ends — `178..220` against a predicted
`177..220`. There was never an incoming mechanism to find.

**Part 2 — outgoing was missing `JobChangeDmgUp`, closed.** Read from `roe_CalcDamage+0x5B2`, verified
under emulation, and confirmed live: outgoing jumps ~1.6x across a single 59→60 level-up while incoming
stays flat (see the Resolved entry).

**Part 3 — what is left is the ANGLE, and it is open.** With the corrected reconstruction, the job-change
multiplier and `ANGLE_MAX=1200`, this capture scores **218/219**. That is not proof: it was reached by
choosing an input, which is exactly how two earlier readings of this same constant went wrong. See
`PcapGroundTruthTests.DeployedAngleMax`, which carries the full history — including that the operator
overturned my "eliminated" verdict, and why their reasoning is better: you do not position to avoid a bonus
that is not live, and `"Forward-facing only now"` marks a change partway through, not the whole window.

**Weapon mastery cannot be the explanation here**, though it was for the new capture: `Bot2433`'s three
`NC_CHAR_CLIENT_PASSIVE_CMD` packets are `00 00`, so its mastery rate is 1000.

**What would settle it:** a capture with deliberate REAR hits, on a server whose angle table is known flat
and in force. Every zone runs one today and `tools/capture_state.py` records it. If rear and frontal hits
do equal damage there, the term's absence is established on the wire instead of argued from a file or a
recollection — and this constant can stop being a judgement call.

---

## 3. Five damage hooks are read but not modelled

**Status:** open. Each is a real branch of `roe_CalcDamage` or `roe_AttackPower`, each was found by reading
the pipeline end to end on 2026-08-27, and none is reached by a clean unbuffed swing — which is why they
cost nothing on the current capture and will cost everything on a buffed one.

| where | what | why it matters |
|---|---|---|
| `roe_CalcDamage+0x466` | `ChargedEffectContainer::cec_AttackForceRate1024` on the attacker vs `cec_DefendForceRate1024` on the defender. Attacker higher: `dmg * (1024 + diff) / 1024`. Defender higher: `dmg * 1024 / (1024 + diff)`. | A premium/charged effect scales damage in 1024ths. Mobs share one static container (`so_ply_ChargedEffectContainer` returns a fixed global), so it is a player-side term in both directions. |
| `roe_CalcDamage+0x4C2` | A critical is `2*dmg + dmg * container[+0xCDC] / 1000`, not `2*dmg`. | This port doubles and stops. The extra term is a real stat slot on the attacker's container. |
| `roe_CalcDamage+0x59E` | `so_ply_DecreaseDmgPassiveSkill(att, dmg)` on the DEFENDER. | The only modelled-nowhere hook that can reduce incoming damage. Identity for a mob. |
| `roe_AttackPower+0x129/+0x155` | `ItemActionObserveManager::EventRun_IncDmgRate` on the attacker's manager AND the defender's, then `GetRateAppliValue` applies the resulting chain of permille rates to BOTH weapon bounds. | Item actions widen or shift the attack range before the roll. Note the defender's manager is consulted too, so a player's own gear can scale the damage they RECEIVE. |
| `roe_CalcDamage+0x60A..+0x801` | Three loops over the abnormal-state list calling per-abstate virtuals with `&damage`. One of them is gated on the attacker's `so_AttackRange > 300`, i.e. a ranged weapon. | Abstates get to rewrite the damage directly. Nothing here models that. |

Skills bring one more: `MiscDataTable::mdt_ArgumentLoad` bsearches a table by skill id and writes
`EngageArgument.damagerate` (+0x1C) and `crirateadd` (+0x20) from it, plus an abstate to apply. It is
gated on `arg->sklinfo` being non-null, so a normal attack never reaches it.

---

# Resolved

## The job-change catch-up multiplier — CLOSED 2026-08-27

**A player hitting a monster deals `dmg * JobChangeDmgUp / 1000`, and this port applied none of it.**

`roe_CalcDamage+0x5B2` calls the ATTACKER's vtable slot 0xD2C with the defender and the integer damage,
one call before the level gap. For a mob that slot is `return dmg`. For a player it is
`ShinePlayer::so_ply_JobChangeDamageUp` (0x00560E80):

```
if (def == NULL || def->so_ObjectType() != 5 /*monster*/)  return dmg;
row = charClass->cc_array[level <= 150 ? level : 0];        // PrimaryParameter *[151] at charClass+0x10858
if (row == NULL) return dmg;
return (unsigned __int64)dmg * (row->JobChangeDmgUp + rndbox[2].next()) / 1000;
```

`PrimaryParameter::JobChangeDmgUp` is at +0x80 in the PDB and is column **`JobChangeDmgUp`** of
`9Data/Shine/World/Param<Class>Server.txt`. The values say plainly what it is for — a catch-up for having
just changed job:

* base classes (Fighter, Cleric, Mage, Archer, Joker) — flat **1000** at every level;
* first job — **2000** at level 20, decaying to 1190 at 59, back to 1000 at 60;
* second job — **1700** at 60, decaying to 1055 at 99, back to 1000 at 100;
* third job — **1100** at 100, decaying to 1025 at 115, 1000 from 120.

So a character in the first half of a job band hits monsters for up to twice what its stats suggest.

`rndbox[2]` is a shuffled pool of 0s and 1s — `RandomBox`'s constructor fills slot `b` with
`floor(i * b / 16384)` for i in 0..16383 — so the random term is worth 0.08%, not the 24% the capture
residual needed. Ruling that out mattered: a "random damage multiplier" is exactly the kind of thing that
gets assumed to be the answer.

**Verified by running it, not by reading it.** `tools/oracle_jobchange_dmgup.py` drives the real function
under emulation over fourteen input sets — including the `ja` on a 16-bit compare that sends level > 150 to
row 0 rather than clamping to 150, the three early returns, and a 3,000,000 damage that proves the multiply
is 64-bit and the divide unsigned. All fourteen agree with this port.

Applied to `Damage.pcapng`: the captured character is class 8 (Paladin, read off `PROTO_AVATAR_SHAPE_INFO`)
at level 82, which is 1280. Both outgoing cases went from ~20% over the predicted ceiling to under it. The
incoming cases did not move, and could not have — see §2.


## Per-mob targeting policy — CLOSED 2026-08-26

`MobInfoServer.EnemyDetectType` picks the `MobTargetSelector` subclass. All three behaviours are now read
rather than assumed:

- **`MobTargetNoBrain::mts_SelectTarget`** is a call to `mts_InitThink` and nothing else. It never acquires
  a target — not even its attacker. 764 mobs, mostly shopkeepers.
- **`MobTargetBout::mts_SelectTarget`** walks the `MobTargetStruct` hate list with an `mdb_CanIKill` check
  and **never calls `so_AllOfRange`** — it has no sight scan at all, so it only retaliates. 220 mobs,
  including everything a new character meets first (Slime, MushRoom, Imp, Crab).
- **`MobTargetAggresive::mts_SelectTarget`** adds the forward sight scan on top, since it derives from Bout.
  1,872 mobs.

That "Bout is absent from the `so_AllOfRange` caller list" is positive evidence here, not an absence
argument: the list was generated by scanning every call site in `.text`, so being absent from it is a fact
about the call graph rather than about my searching.

Ported as `TargetingPolicy` on the selector — an enum rather than a hierarchy, because only the dispatch
differs. Until this landed, **984 mobs attacked on sight that should not have.**

⚠️ The three rare values (`ED_AGGRESSIVE2`, `ED_AGGREESIVEALL`, `ED_ENEMYALLDETECT`, 22 mobs) have their own
overrides that have NOT been read; they map to Aggressive because they derive from it, which is a floor
rather than a claim.


## The angle question — CLOSED 2026-08-26. The operator was right.

**A mob's detection circle is not centred on the mob.** `ShineMob::so_mob_SightCenter` (0x004ABCD0) seeds
the output with the mob's own position and then calls
`ddt_GetFoward(facing, detectRange * 205 / 512, out)`, pushing the centre **forward along the mob's facing
by about 40% of its detect range**. The base `ShineObject` version is three instructions and returns the
position unchanged, so this is mob-specific.

It is reached through vtable slot **0x8F4**, which is exactly the virtual `MobTargetAggresive::mts_SelectTarget`
calls at +0x3DB — and its return value is the `loc` argument handed to `so_AllOfRange`. So the scan really
is a circle, centred somewhere the mob is not.

With range r, the reach is about **1.40 r ahead and 0.60 r behind** — a front-to-back ratio near 2.3. That
is large enough to be obvious in play, which is exactly how it was reported.

**This project argued the wrong way for weeks.** The reasoning was "the scan is a circle, no angular term
appears, therefore orientation cannot matter". Every step was true except the conclusion: the shape is a
circle and there is no angular term, and detection is still direction-dependent because the circle moves.
The `FanFormSectorArgument` that `so_AllOfRange` accepts — which I had also missed — turned out to be a red
herring for aggro: the call sites pass NULL.

What actually found it was reading `mts_SelectTarget`'s arguments **one at a time** instead of scanning for
a keyword. The `loc` argument was never the mob's position; nobody had looked.

Two consequences fell out while porting it, both previously invisible:

- The simulation kept a second copy of facing on the selector that nothing updated, so directional
  detection silently degraded to a concentric circle. One mob has one facing.
- `mts_GetTopAggroTarget` was never called, so **a mob struck from behind never fought back** — its
  attacker sits outside the forward circle and acquisition found nothing. `mab_Think` calls both.

⚠️ `Direction.Forward` computes cos/sin where the server reads a table built by `ddt_Initialize`; the two
agree to within table rounding. Recorded in docs/UNVALIDATED.md §6.


## Where `MobWeapon` enters the damage formula — CLOSED 2026-08-26

**A mob's weapon is its GEAR.** `ShineMob::sm_PrepareWeapon` (0x004A9D50) writes the selected weapon's
`MinWC`/`MaxWC`/`TH`/`MinMA`/`MaxMA`/`MH` to `mob + 0x10A0 … 0x10C0`. The embedded container
(`ShineMobileObject::smo_Param`) is at `+0x0FC0`, and the difference is `0xCC` — the **`Item.Plus`** cluster.

1. `c_StoreMob` zeroes those slots at spawn — no weapon chosen yet.
2. `so_mob_SelectWeapon` → `so_mob_SkillParameterSet_WeaponIndex` → `sm_PrepareWeapon` fills them.
3. `c_MakeTotal` folds `Item.Plus` in as its second operation.
4. `roe_MinWC` reads the container and finds the weapon already there — **which is why it has no mob
   branch**, the detail that made this look mysterious.

Closed the wrong way once: "nothing folds `MobWeapon` into a cluster" was an argument from absence, and the
writer was two calls down a path that had not been walked.

## Item rate columns go to `ItemPowerRate`, not `Item.Rate` — CLOSED 2026-08-26

`ItemInfo`'s `WCRate`/`MARate`/`ACRate`/`MRRate` land on **`ItemPowerRate.Rate`**.
`ShinePlayer::so_RecalcEquipParam` writes that cluster for exactly AC, MR, WCmin, WCmax, MAmin and MAmax,
and `roe_AC` / `roe_MinWC` / `roe_MR` read it and never read `Item.Rate`.

This port had them on `Item.Rate` — one of the five clusters `c_MakeTotal` skips — inferred from the shared
"Item" prefix. **Item rate bonuses therefore did nothing at all**, and a test asserted that broken behaviour
as though it were the finding.

`Item.Rate` is not unused either: `so_RecalcEquipParam` writes `Item.Rate.CriDamRate`.

## Does anything add the cluster's own `MaxHP` slot? — CLOSED 2026-08-26

Yes. `ShinePlayer::sp_MaxHP` (0x0054A670) reads `Item.Plus.MaxHP`, `Item.Plus.MaxHP_2`,
`AbnormalState.Plus.MaxHP` and `AbnormalState.Rate.MaxHP` on top of the class virtual. `CharClass::MaxHP`
is one term of the answer, not the whole of it — which is why it never reads the cluster itself.

## Where `so_DamagedBy`'s aggro rate comes from — CLOSED 2026-08-27

`so_DamagedBy(attacker, damage, ratePermille, flag)` turns damage into hate as `damage * rate / 1000`
(+0x147). The rate has two sources, and the split is clean:

**An ordinary attack passes the literal 1000.** All four combat call sites push `0x3E8`:
`so_attack+0x117`, `so_smash+0x115`, `so_skillsmash+0x151`, `DamageAbsorbAction::exe+0xCC`. One point of
damage is one point of hate. So the simulation's 1000 is exact rather than an assumption — which is the
part that mattered, since nothing here casts.

**A skill varies it.** `sds_TemplateStore` takes the rate as its 4th parameter (`[ebp+0x14]`, pushed at
+0x19A), and `smo_SkillBlast+0x5BD` builds it from the skill's server row:

```c
const ActiveSkillInfoServer* serv = skill->sdi_ServInf;
absolute += serv->AbsoluteAggro;                                   // +0x43, accumulated flat
rate      = <global at 0x1325EDE0> + serv->AggroPerDamage - 1000;  // +0x3F, relative to 1000
```

`AggroPerDamage` differs from 1000 for **2,580 of 2,791 skills** — `TripleHit` is 9000, nine times the
hate per point of damage, which is what a taunt looks like in this engine. `AbsoluteAggro` is non-zero for
251 and includes **negative** values (down to −2000), a flat hate DROP; `SnearKick01`–`05` climb 750 →
18,000 with `AggroPerDamage` 0, so they are pure flat-threat skills that generate no damage-scaled hate at
all.

**Still unread, and small:** the global at `0x1325EDE0`. It is runtime-allocated, so it is not in the image
and cannot be read statically — it needs a live process or its initialiser traced. Given the `- 1000`, it
is almost certainly a server-wide baseline of 1000, which would make the rate simply `AggroPerDamage`; that
is a lean, not a reading.

A near-miss worth keeping: `so_mobile_MobAggroRate` looks like the answer and is not. It returns a BYTE —
`ShinePlayer`/`ShineMover` → 1, `ShineMob` → `sm_Flag & 1`, base → 0. It is a yes/no "does this object draw
mob aggro at all", not a permille rate.

## The third term of `nextAttackAt` — CLOSED 2026-08-27

`mab_Think` computes `now + AtkDly + swing + <local>`, and the local is
`ShineMob::sm_GetWeaponCastTime()` (0x004B88E0) — **the cast time of the SKILL named on the weapon row**:

```c
ushort skillId = box->weapon[currentIndex].skill;        // _MobWeaponIndex.skill, +0x04
const SkillDataIndex* s = SkillDataBox[skillId];
if (!s) return 0;                                        // the null pointer IS the return value
return s->sdi_Activ->CastTime * 10 / 1000;               // ActiveSkillInfo+0xCB, tenths
```

Two details worth keeping. The cast is added at `+0x8BA`, **after** the three `test/jns/xor` clamps at
`+0x8A3`, so it is not clamped. And it is **not** scaled by the attack-speed rate that swing, hit and delay
all pass through — haste does not shorten a cast.

**Why the question was invisible rather than hard.** `MobWeapon.Skill` is `-` on weapon row 0 of all 2,834
mobs that have weapons, and row 0 is the row `mab_Think` forces when the target is a player. So the term is
zero for every attack a character can receive, and the old two-term interval was not a floor — it was
exact for that case. It starts to matter as soon as mob skills are modelled (question 1).

The join is now loaded (`SkillDataBox` over `ActiveSkill.shn`): all 2,974 weapon rows that name a skill
match a row, none unmatched, and 478 of them carry a non-zero cast time.

## `AggroInitialize` — CLOSED 2026-08-27. It SUBTRACTS, and it is a boss mechanic.

The column is parsed, and its reader is `ShineMobileObject::smo_SwingDamage+0x4B5`, at the tail of a hit
that connected:

```c
if (aggroInitialize > 0)
    attacker->so_mob_DecreaseAggro(target, aggroInitialize);   // vtable +0x704
```

**The direction is the opposite of what the name suggests.** It does not give the victim hate; it takes
hate for that victim OFF the attacker's own list. A mob that lands a blow becomes less interested in whoever
it just hit — which is how aggro rotates instead of locking on. `ShineMob::so_mob_DecreaseAggro`
(0x0042D380) then walks `sm_FamilyList` (+0x24B1), a circular list, applying the same decrease to **every
family member**, so a linked pack sheds together.

It is a **boss** mechanic in practice: 139 of 5,815 weapon rows carry a non-zero value, at 400–1000, and
they are `GobleKing`, `GhostKnight`, `Marlone`, `LegendaryTree` and the KQ bosses. An ordinary field mob has
0 and never rotates.

Two more facts fell out of the same read:

- A **miss** still generates hate: `smo_SwingDamage+0x1D2` does `target->so_mob_AppendAggro(attacker, 1)`.
  Not portable here yet — `roe_HitRate` is not implemented, so nothing in this simulation ever misses.
- Slot `+0x700` is `so_mob_AppendAggro` and `+0x704` is `so_mob_DecreaseAggro`. On `ShinePlayer` both are
  the ICF-folded empty stub, which is the mechanical statement that **players have no hate list**.

Also corrected here: the field at +0x24AE is `sm_CurrentTarget` in the PDB, not "first attacker". Within
`so_DamagedBy` it is only SEEDED (`if (== 0xFFFF) = attacker->handle`), so the behaviour previously
described was right; whatever clears it has not been found, so it is still named for the read behaviour.

## Which rules a normal attack uses — CLOSED 2026-08-27

`MobWeapon.HitType` had been decoded and connected to nothing, and the wizard's 1-kill grind had been
written up as "magic classes cannot fight". Both are now read.

`ShineMobileObject::smo_RulesOfNormalAttack` (`+0x1E74`) is the field. `ShineMob::so_mob_Regenerate+0x543`
sets it from weapon row 0's `HitType`; everything else defaults to `roe_normalPY` from the
`ShineMobileObject` constructor. **A player is always physical, every class** — the only other writer is
`sp_SetRulesOfEngagement`, whose one caller is the GM `&allcritical`. So the wizard result was the game,
not a gap. Ported as `MobParameters.NormalAttackRule`; 334 fightable mobs now attack against MR.

**How it was nearly missed, and the lesson.** The first pass concluded "nothing writes `+0x1E74` except a
GM command" — which was an argument from absence, exactly what rule 3 forbids, and it was produced by a
broken tool rather than by sloppy reasoning. `capstone`'s `disasm()` stops at the first undecodable byte
and returns what it has; on this binary that is 0x004ADFEF, so every whole-image scan in `tools/` had been
seeing **228,684 of 922,891 instructions (24.8%)** while looking exhaustive. The constructor's write sits
at 0x559284, well past the cut. `Code.sweep()` now resynchronises. Every earlier "no callers" answer from
`xref_vcall.py` should be re-run before being trusted.

Left open on this path: `roe_HitRate` and `roe_CriticalRate` are per-rule and neither is implemented, so a
swing in this port never misses and crits only when told to.

## Pinky: physical, not magic — CLOSED 2026-08-26

Was open as a disagreement: the operator recalled Pinkies as ranged **magic** attackers, while the table
declared `HT_PY` with a single weapon row, WC 520–792 above MA 72–110, and range 350.

**The operator checked in game and confirmed physical.** The data was right.

Kept because the shape of the near-miss is worth remembering: a ranged attacker carrying a non-zero magic
attack value is very easy to remember as a magic attacker. Had this been resolved by inference instead of a
check, the tempting move would have been to "fix" the port to match the recollection — and
`MobWeapon.IsMagical` would now be lying about 2,878 mobs to accommodate one.
