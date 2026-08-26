# Open questions

Things this project has NOT resolved, kept where they cannot be forgotten. Each entry says what is
believed, what it rests on, and what would settle it.

**Two rules.** An entry leaves this file when it is *read*, not when it is guessed convincingly. And an
entry that contradicts the operator's play experience stays open until the disagreement is actually
resolved — the data does not automatically win.

---

## 1. Pinky: declared physical, played as ranged magic — OPERATOR TO CHECK

**Status:** open, disagreement unresolved. Operator to verify in game.

The operator states Pinkies are ranged **magic** attackers. `Z:/ServerSource` says ranged **physical**.

What the data says, from `MobWeapon.shn`:

| | Skill | HitType | WC | MA | Range |
|---|---|---|---|---|---|
| `Pinky` | `-` (only row) | **`HT_PY`** | 520–792 | 72–110 | 350 |
| `GoblinMage` (a real magic mob) | `-` | `HT_MA` | 19–30 | 273–415 | 300 |

Pinky has exactly **one** weapon row, its WC exceeds its MA, and its declared hit type is physical. Uruga
spawns the plain `Pinky` (23 of them); none of the 21 `Pinky`/`Lips` variants appear there.

**Possible reconciliations, none verified:**

1. The live server's `MobWeapon.shn` differs from the `Z:/ServerSource` tree. *Settled by reading the file
   out of a zone pod and diffing.*
2. The magic comes from a **skill** attack chosen by `MobAttackSequence`, not from the normal-attack row —
   `NormalAttackOf()` only ever returns the `Skill == "-"` row, so a mob whose actual behaviour is
   skill-driven would look physical here. *Settled by finishing the `AttackElement4Mob` read.*
3. The attack looks magical (ranged projectile, non-zero MA) while resolving as physical.

**Do not encode either answer until this is resolved.** The port currently follows the declared `HitType`
because that is what was read, and `MobWeapon.IsMagical` carries a comment saying why it is not inferred
from MA-versus-WC.

---

## 2. Where `MobWeapon` enters the damage formula

**Status:** open. Partially traced.

`roe_MinWC` reads *only* the attacker's `Parameter::Container` — one virtual call, no weapon pointer, no
type branch. But `c_StoreMob` leaves a mob's `WCmin`/`WCmax`/`TH`/`MAmin`/`MAmax`/`MH` slots at **zero**,
and nothing found so far folds `MobWeapon` into a cluster.

So either something writes those slots between spawn and attack, or mob damage takes a different path
entirely. `so_mob_RegenComplete` does `c_clear` → `c_StoreMob` → `c_MakeTotal` with no weapon step.

**Consequence today:** the simulation rolls mob damage directly between `MinWC` and `MaxWC`. That uses the
mob's real attack input, but **the defender's AC is not applied to mob damage**. Player damage does go
through the full formula.

**Next step:** `MobAttackSequence::mas_Find` → `AttackElement4Mob`, reached from `so_mob_RegenComplete`.

---

## 3. The unresolved term in `nextAttackAt`

**Status:** open, minor.

`mab_Think` computes `now + AtkDly + swing + <local>`. The two named terms are ported; the third is a
local this port has not resolved, so `MobAttackTiming.IntervalTenths` is the **floor** of the real interval
rather than certainly the whole of it.

---

## 4. Does anything add the cluster's own `MaxHP` slot?

**Status:** open.

`CharClass::MaxHP` returns `row.LevelHP + (Con − row.constitution) * 5` and never reads `cluster[MaxHP]`,
yet flat +HP gear has to land somewhere. Either a caller adds the slot afterwards, or +HP gear works
differently. The cluster also has a second `MaxHP_2` slot of unknown purpose.

---

## 5. `Item.Rate` never reaches the total

**Status:** half answered.

The "maybe the `StatModifier` naming is off by a pair" half is **closed** — the PDB declares the container's
pairs in exactly that order. So `Item.Rate` really is a cluster `c_MakeTotal` skips, and `ItemInfo`'s
`WCRate`/`MARate`/`ACRate`/`MRRate` must be read directly by the damage formula somewhere not yet traced.

---

## 6. The angle question

**Status:** open debt, documented at length in `docs/AGGRO.md` and `PROJECT_PLAN.md`.

The operator reports aggro range depends on orientation. Three functions read, none contains an angle term.
Neither that absence nor the plausible turn-cost mechanism is proof, and neither outranks the observation.

---

## 7. Mob skill attacks are not modelled

**Status:** open.

`MobWeapon` has 5,815 rows across 2,878 mobs, so most mobs have skill attacks beyond their ordinary swing.
The simulation only uses the `Skill == "-"` row. `AttackElement4Mob` carries a 500-entry skill sequence plus
`OutOfRange` / `HPLow` / `TargetState` change-lists, none of it ported — the exchange-rule *order* in
`MobActionAttack` is ported but its predicates are placeholders.
