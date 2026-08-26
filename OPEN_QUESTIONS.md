# Open questions

Things this project has NOT resolved, kept where they cannot be forgotten. Each entry says what is
believed, what it rests on, and what would settle it.

**Two rules.** An entry leaves this file when it is *read*, not when it is guessed convincingly. And an
entry that contradicts the operator's play experience stays open until the disagreement is actually
resolved — the data does not automatically win.

---

## 1. The unresolved term in `nextAttackAt`

**Status:** open, minor.

`mab_Think` computes `now + AtkDly + swing + <local>`. The two named terms are ported; the third is a
local this port has not resolved, so `MobAttackTiming.IntervalTenths` is the **floor** of the real interval
rather than certainly the whole of it.

---

## 2. Does anything add the cluster's own `MaxHP` slot?

**Status:** open.

`CharClass::MaxHP` returns `row.LevelHP + (Con − row.constitution) * 5` and never reads `cluster[MaxHP]`,
yet flat +HP gear has to land somewhere. Either a caller adds the slot afterwards, or +HP gear works
differently. The cluster also has a second `MaxHP_2` slot of unknown purpose.

---

## 3. `Item.Rate` never reaches the total

**Status:** half answered.

The "maybe the `StatModifier` naming is off by a pair" half is **closed** — the PDB declares the container's
pairs in exactly that order. So `Item.Rate` really is a cluster `c_MakeTotal` skips, and `ItemInfo`'s
`WCRate`/`MARate`/`ACRate`/`MRRate` must be read directly by the damage formula somewhere not yet traced.

---

## 4. The angle question

**Status:** open debt, documented at length in `docs/AGGRO.md` and `PROJECT_PLAN.md`.

The operator reports aggro range depends on orientation. Three functions read, none contains an angle term.
Neither that absence nor the plausible turn-cost mechanism is proof, and neither outranks the observation.

---

## 5. Mob skill attacks are not modelled

**Status:** open.

`MobWeapon` has 5,815 rows across 2,878 mobs, so most mobs have skill attacks beyond their ordinary swing.
The simulation only uses the `Skill == "-"` row. `AttackElement4Mob` carries a 500-entry skill sequence plus
`OutOfRange` / `HPLow` / `TargetState` change-lists, none of it ported — the exchange-rule *order* in
`MobActionAttack` is ported but its predicates are placeholders.

---

# Resolved

## Where `MobWeapon` enters the damage formula - CLOSED 2026-08-26

**A mob's weapon is its GEAR.** `ShineMob::sm_PrepareWeapon` (0x004A9D50) writes the selected weapon's
`MinWC`/`MaxWC`/`TH`/`MinMA`/`MaxMA`/`MH` to `mob + 0x10A0 ... 0x10C0`. The embedded container
(`ShineMobileObject::smo_Param`) is at `+0x0FC0`, and the difference is `0xCC` - the **`Item.Plus`**
cluster. All six land exactly on its weapon slots.

So the whole sequence is ordinary:

1. `c_StoreMob` zeroes those slots at spawn - the mob has not chosen a weapon yet.
2. `so_mob_SelectWeapon` -> `so_mob_SkillParameterSet_WeaponIndex` -> `sm_PrepareWeapon` fills them on
   selection. Weapon choice runs through `ae4m_NextSkill` plus a WELL512 roll against `sm_GetUseWeaponRate`.
3. `c_MakeTotal` folds `Item.Plus` in as its second operation.
4. `roe_MinWC` reads the container and finds the weapon already there - **which is why it has no mob
   branch**, the thing that made this look mysterious.

The earlier note in this project claiming "nothing folds `MobWeapon` into a cluster" was simply wrong.

Ported, and mob damage now goes through `DamageCalculator` like everything else - so **the defender's AC
applies to mob damage**, which it previously did not.

Resolved on the way: `ShineMob::smo_SwingDamage` passes the weapon's abstate, `StaStrength`, `StaRate` and
`AggroInitialize` into the shared swing, explaining four fields the audit listed as decoded-but-unconnected.
`AggroInitialize` is also a lead on the open "hate points per hit" question.


## Pinky: physical, not magic — CLOSED 2026-08-26

Was open as a disagreement: the operator recalled Pinkies as ranged **magic** attackers, while the table
declared `HT_PY` with a single weapon row, WC 520–792 above MA 72–110, and range 350.

**The operator checked in game and confirmed physical.** The data was right.

Kept because the *shape* of the near-miss is worth remembering: Pinky is a ranged attacker carrying a
non-zero magic attack value, which is a very easy thing to remember as a magic attacker. Had this been
resolved by inference instead of a check, the tempting move would have been to "fix" the port to match the
recollection — and `MobWeapon.IsMagical` would now be lying about 2,878 mobs to accommodate one.

The rule that kept it honest stays: **an entry contradicting play experience stays open until actually
resolved.** Not deferring to the operator, and not deferring to the file — just not encoding either until
someone looks.
