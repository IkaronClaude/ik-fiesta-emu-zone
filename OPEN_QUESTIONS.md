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
   down. Three entries have already been closed the wrong way round by ignoring this — see Resolved.

---

## 1. The unresolved term in `nextAttackAt`

**Status:** open, minor.

`mab_Think` computes `now + AtkDly + swing + <local>`. The two named terms are ported; the third is a
local this port has not resolved, so `MobAttackTiming.IntervalTenths` is the **floor** of the real interval
rather than certainly the whole of it.

---

## 2. The angle question

**Status:** open, but materially narrowed — and one of my supporting claims was wrong.

The operator reports from play that aggro range depends on orientation.

**What was wrong:** this document previously said "three functions have been read and none contains an
angle term." `so_AllOfRange` — the range scan the whole targeting path runs through — takes a
**`FanFormSectorArgument`**:

```
FanFormSectorArgument { int ffsa_shineradian; VectorClass::UnitVector ffsa_chardirect; }
```

A half-angle and a facing vector. The engine has first-class support for angular sector queries, and
`so_AllOfRangeNomal` calls `sr_cos1024` to evaluate them. An angle term absolutely exists.

**What is now positively established:** the aggro call sites pass **NULL** for it.
`MobTargetAggresive::mts_SelectTarget` pushes seven arguments and the fourth — the fan — is literally `0`;
so does the per-tick `ShineMobileObject::so_Routine` scan. Those are *readings*, not failures to find
something.

So acquisition is a circle, while the fan mechanism is real and used elsewhere (skills, AoE, blasts).

**Still unexplained**, and worth checking before this closes:

- the other `mts_SelectTarget` overrides (`AggresiveALL`, `PlayerCaptivate`, the base `MobTargetSelector`)
  — only `MobTargetAggresive` has been decoded argument-by-argument;
- `so_CanSeeOtherObject`, never read;
- `MobInfoServer.TurnSpeed`, which this project replaces with a uniform invented constant. If turn rate
  varies per mob, a uniform constant would mask exactly the effect under argument (docs/UNVALIDATED.md §1).

Nothing here may describe the detection shape as settled, and the turn-tick story must not be cited as the
explanation.

---

## 3. Mob skill attacks are not modelled

**Status:** open.

`MobWeapon` has 5,815 rows across 2,878 mobs; 1,324 mobs have more than one. The simulation only ever uses
weapon index 0. `AttackElement4Mob` carries a 500-entry skill sequence plus `OutOfRange` / `HPLow` /
`TargetState` change-lists, and `so_mob_SelectWeapon` chooses between them via `ae4m_NextSkill` and a
WELL512 roll against `sm_GetUseWeaponRate`. None of that is ported — the exchange-rule *order* in
`MobActionAttack` is, but its predicates are placeholders.

---

## 4. Per-mob targeting policy is not modelled

**Status:** open, newly identified.

`MobInfoServer.EnemyDetectType` selects which `MobTargetSelector` subclass a mob uses, and its values map
one-for-one onto the RTTI hierarchy. It is now decoded (`EnemyDetect`), but the simulation still gives
every mob the same selector.

**220 mobs are `ED_BOUT` — passive, retaliate-only** (Slime, MushRoom, Imp, Crab), and 764 are `ED_NOBRAIN`
(shopkeepers). Both currently attack on sight here.

---

## 5. `AggroInitialize`, and hate points per hit

**Status:** open, with a lead.

`ShineMob::smo_SwingDamage` passes the weapon's `AggroInitialize` into the shared swing, so that column is
the mob's per-attack aggro contribution. The column is not yet parsed, and the call that converts damage to
hate is still unidentified.

---

# Resolved

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

## Pinky: physical, not magic — CLOSED 2026-08-26

Was open as a disagreement: the operator recalled Pinkies as ranged **magic** attackers, while the table
declared `HT_PY` with a single weapon row, WC 520–792 above MA 72–110, and range 350.

**The operator checked in game and confirmed physical.** The data was right.

Kept because the shape of the near-miss is worth remembering: a ranged attacker carrying a non-zero magic
attack value is very easy to remember as a magic attacker. Had this been resolved by inference instead of a
check, the tempting move would have been to "fix" the port to match the recollection — and
`MobWeapon.IsMagical` would now be lying about 2,878 mobs to accommodate one.
