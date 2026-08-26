# Mob combat decisions

Read from `Zone.exe`. As with `AGGRO.md`, everything here is a **reading**, not a measurement — nothing has
been executed under the oracle yet.

## Where the decision lives

`MobTacticElement::MobActionAttack::mab_Think` (`0x004BBA00`, 0x9A0 bytes) is the attack decision. Like
every other state, it returns the next `MobActionBase*`.

## Facing is checked here, not during acquisition

The attack state calls:

* `DirectDistanceTable::ddt_DirectSR(dx, dy)` — the direction from the mob to its target, as a
  **direction byte** (one unit = 2 degrees; see `DAMAGE_FORMULA.md` for the derivation).
* `DirectDistanceTable::ddt_ShineRadianDiff(a, b, out)` — the angular difference between two directions.
* `MobActionTurning::mat_Reserv(...)` — reserve a **turn** action.

So a mob that is not facing its target **turns first**, and turning is its own state that costs ticks.

**This is a hypothesis, not an explanation, and it is currently losing to the operator's experience.**

It is tempting to say turning explains the directional feel — approach from behind, the mob spends ticks
turning, and that reads like reduced rear aggro. But "a mechanism exists that could produce the symptom"
is not evidence that it *does*, and the operator reports the effect from play while this is inferred from
a call list. **Do not cite it as the answer.** `AGGRO.md` sets out what would actually settle the angle
question; until one of those is done, treat the shape as unresolved.

## Auto-attack versus skill

Skill choice is a set of "exchange" rules, each a predicate on the mob's or target's state:

| function | trigger |
|---|---|
| `ShineMob::sm_SkillExchange_HPLow` | the mob's own HP is low |
| `ShineMob::sm_SkillExchange_HPLow_ChangeOrder` | reorders the skill list in the low-HP case |
| `ShineMob::sm_SkillExchange_TargetState(ShineObject*)` | the target's state |
| `ShineMob::sm_SkillExchange_OutOfRange` | the target is out of range |
| `MobActionAttack::maa_SkillFromScriptClear` | a Lua script supplied the skill |

and a random draw — `cWell512Random::well512_GetRandom` — so the choice is **not deterministic**. A
simulator therefore needs the same RNG if runs are to be reproducible, and reproducibility here is the
whole point of simulating.

Range is tested with `ShineObject::so_DistanceSquar(other)`, squared, never rooted — the same convention
as acquisition.

## Other states reached from combat

`MobActionChase`, `MobActionInChase`, `MobActionBackStep`, `MobActionAvoidOverlap`, `MobActionSwingDamage`,
`MobActionWaitSkillEnd`, `MobActionTurning`, `MobActionWander`, `MobActionRoaming`, `MobAction2Region`,
`DuringReturn2Regen`, `MobActionNoBrain`, `MobActionInMove(_Cancelable)` — seventeen `mab_Think`
implementations in total.

`NormalAttackDamageDelay` (with a `NormalAttackDamageTick` and a `deque` of
`NormalAttackDamageElement`) is the swing-timing machinery: damage is queued and applied on a delay rather
than at the moment of the swing.

## Not yet read

* The cooldown representation. `NormalAttackDamageDelay` is a delay queue, not obviously a cooldown table;
  per-skill cooldowns may live in `CharaterSkillList` / `SkillDataBox`.
* `MobActionSwingDamage::mab_Think` — how a swing turns into a damage event.
* `MobActionChase` — pursuit, and how it interacts with the regen point (`DuringReturn2Regen` suggests
  mobs return home).
* Whether `so_CanSeeOtherObject` tests occlusion, facing, or both.
