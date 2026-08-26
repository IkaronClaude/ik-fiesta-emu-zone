# Unvalidated: invented constants, unconnected data, and theories

Every value in this project falls into one of three states:

- **read** — taken from the binary or a game table, with the reading recorded;
- **decoded but unconnected** — parsed out of a table and then not used by anything;
- **invented** — a number this project chose, which the server gets from somewhere else.

This file lists everything that is *not* in the first state. It exists because the third category is
invisible in normal use: a made-up constant produces plausible behaviour and never announces itself.

Ordered worst-first. "Worst" means *most likely to be silently wrong*, not most work to fix.

> **A tooling bug that manufactured false absences.** The scan used to find field readers only matched
> `disp8` addressing, and `disp8` is SIGNED — so any struct offset at or above `0x80` is encoded as
> `disp32` and was invisible to it. `TurnSpeed` at `+0x9C` looked unreferenced for that reason alone. If an
> older note in this file says "nothing reads X", check whether X sits past `0x7F` before believing it.
>
> **Rule 3, carried from OPEN_QUESTIONS.md:** this is a release C++ binary, so `/OPT:REF` strips
> unreferenced code and data. Anything still in the image is referenced by something. Every "nothing uses
> this" claim below is therefore **a report on my searching, not a property of the server** — three such
> claims have already turned out wrong (`MobWeapon` into a cluster, item rate columns, the cluster `MaxHP`
> slot). Treat §2 and §4 as leads to chase, not conclusions.

---

## 1. Invented constants that SHADOW a real data column

**The most dangerous category.** The simulation makes a number up while the server reads a per-mob value
from a table we already parse. Behaviour looks reasonable and is uniformly wrong.

| invented | where | the real source |
|---|---|---|
| `TurnRateUnitsPerSecond = 150` | `MobActionTurning` | **`MobInfoServer.TurnSpeed`** — now decoded and its ZERO branch ported (60 mobs turn instantly). What a NON-zero value means is still unread: only four values exist (100, 0, 300, 500) and the distribution cannot say whether bigger is faster or slower, so non-zero mobs still use the invented rate |
| `RespawnSeconds = 25` default | `SimMob` | `MobRegen.RegStandard` is wired, but `RegMin`/`RegMax`/the delta schedule are not, and `MobInfoServer.RegenInterval` / `ResetInterval` are not even decoded |
| `AttackRange = 10` default | `MobCombatState` | `MobWeapon.Range` — wired *only* when a definition is applied |
| `FacingToleranceUnits = 5` | `MobCombatState` | unknown; no obviously matching column found, but note rule 3 below before treating that as settled |

Chase speed is now per-mob from `MobInfo.RunSpeed`. Turning is half-done: the instant-turn branch is
ported, the rate for the other 2,818 mobs is not.

`WalkChase` was listed here as a chase-speed source and that was wrong — it is **zero for 2,862 of 2,878
mobs**, so it is a rare special case rather than the general speed, even though `MobActionChase::mab_Think`
is what reads it.

---

## 2. Decoded but connected to nothing

Parsed into a record, then never read. Harmless today, but each one is a behaviour the simulation cannot
have.

**`MobInfo`** — `WalkSpeed` (decoded; `RunSpeed` is what chase uses), `Size`, `Id`
**`MobInfoServer`** — `MonExp`, `DetectCha`, `FollowCha`, `MaxSp`, `Rank`, `Id`
**`MobWeapon`** — `BlastRate`, `Id`, and `Skill` (used only as a marker for "is this the ordinary swing",
never to actually cast anything).

`Th`, `MinMa`, `MaxMa` and `Mh` were on this list and are now **connected**: `sm_PrepareWeapon` stages them
into the mob's `Item.Plus` cluster alongside `MinWc`/`MaxWc`.

---

## 3. Present in the file, not decoded at all

Columns the tables carry that this project does not read. `MobInfoServer` has **49 columns and 16 are
captured**.

**`MobInfoServer`** — `WalkChase` and `RegenInterval` are now decoded but unused; `Visible`, `MobKillInx`, `EXPRange`, `ResetInterval`,
`CutInterval`, `CutNonAT`, `PceHPRcvDly`, `PceHPRcv`, `AtkHPRcvDly`, `AtkHPRcv`, `MobRaceType`,
`FamilyArea`, `FamilyRescArea`, `FamilyRescCount`, `BloodingResi`, `StunResi`, `MoveSpeedResi`,
`FearResi`, `ResIndex`, `KQKillPoint`, `Return2Regen`, `IsRoaming`, `RoamingNumber`, `RoamingDistance`,
`RoamingRestTime`, `BroadAtDead`, `AllCanLoot`, `DmgByHealMin`, `DmgByHealMax`

`DmgByHealMin`/`DmgByHealMax` are read by `RuleOfEngagementHealAttack::roe_CalcDamage` — mobs that take
damage from healing. `RegenInterval` is read by `ShineMob::so_MapMarking`.

**`MobInfo`** — `WeaponType`, `ArmorType`, `GradeType`, `IsPlayerSide`, `AbsoluteSize`

**`MobWeapon`** — `AtkType`; `MopAttackTarget` (read in `mab_Think` as a 5-way switch — the switch is
*seen* but not ported); and `StaName`, `StaStrength`, `StaRate`, `AggroInitialize`, whose MEANING is now
known even though the columns are still not parsed: `ShineMob::smo_SwingDamage` passes the weapon's
abstate, `StaStrength`, `StaRate` and `AggroInitialize` into the shared swing, so they are the weapon's
status-effect strength and chance plus its aggro contribution. `AggroInitialize` is a live lead on the
open "hate points per hit" question in §5.

**`Param<Class>Server`** — `Wizdom` (no cluster slot exists for it), `AtkPerAP`, `DmgPerAP`, all twelve
stone columns, `PainRes`, `RestraintRes`, `CurseRes`, `ShockRes`, `CharTitlePt`, `SkillPwrPt`,
`JobChangeDmgUp`

**`Parameter::Container` second tier** — the ~20 fields past `Total`: `DotDamagePlus`, `SPRate`,
`RangeEvasion`, `flag`, `MissPercentFix`, `DamageReflection`, `ChangeAbilityInfo`, `HealRate`,
`PassiveBuffKeepTimeUPRate`, `PassiveHealRate`, `PassiveCriDamageRatePlus`,
`PassiveHPDownRate{WCMin,WCMax,MAMin,MAMax,AC,MR}`, `PassiveMovingTBPlus`, `PhysicalImmuneRate`,
`MagicalImmuneRate`, `RangeOver`, `DMGMinusRate`

---

## 4. Cluster slots nothing ever populates

Of the 51 `Stat` slots, these are never written by any code path here, for players or mobs:

`AbsoluteAttack`, `AbsoluteDefend`, `AbsoluteHit`, `AbsoluteBlock`, `CastingTime`, `CriDamRate`,
`MagCriDamRate`, `AttSpeed` (read for mob timing, never *set*), `MaxHP_2`, `HPAbsorption_Hitted`,
`SPAbsorption_Hitted`, `HPAbsorption_Hit`, `SPAbsorption_Hit`, `RegistNone`, `ResistPoison`,
`ResistDeaseas`, `ResistCurse`, `ResistMoveSpdDown`, `ResistGTI`, `MaxLP`, `LPRecover`

`MaxHP_2` is worth singling out: a second max-HP slot whose purpose is unknown.

---

## 5. Rules based on theory rather than a reading

These are *my* inferences. Each produces correct-looking behaviour and none was read out of the binary.

| rule | where | basis |
|---|---|---|
| `IsRanged => Range >= 100` | `MobWeapon` | **Invented threshold.** Melee sit at 10–40 and ranged at 250+, so the gap is wide, but the server's own cutoff (if it has one) is unread |
| Which `MobType`s are fightable | `MobInfo.IsFightable` | Inferred from the enum names. `MT_NONAME` is *not* excluded, on no evidence either way |
| `AtkSpd == SwingTime` is a tendency | timing port | Measured (2,242/2,263), not read. The port does not rely on it |
| Aggro tie-breaking | `MobTargetSelector` | Explicitly a guess — the server walks an intrusive list |
| Hate points per hit | aggro | The call that converts damage to aggro is not identified |
| `SkillChancePermille`, `HpLowThresholdPermille`, `TargetStateWantsSkill` | `MobCombatState` | Placeholders. The `sm_SkillExchange_*` ORDER is ported; the predicates are not |

---

## 6. Knowingly approximated, with the real version identified

Honest stand-ins where the real mechanism is known but not ported.

- **Circular spawn placement** (`MobRegenLoc.Circular`) — the server indexes a precomputed word table with a
  wrapping index; this uses a uniform disc sample. Same distribution, different sequence, so circular
  spawns are *not* reproducible against the server.
- **`CrtRandom`** — the MSVC LCG taken from published behaviour, **not verified against the binary**. It is
  CRT code rather than game code, but it drives spawn placement, so a mismatch would desynchronise every
  spawn.
- **The unresolved term in `nextAttackAt`** — `mab_Think` computes `now + AtkDly + swing + <local>`.
  `IntervalTenths` is the floor of the real interval.
- **Action states as shared singletons** — the original embeds a per-mob instance of most action states
  inside each `MobActionArgument`. Fine while states hold no per-mob data; wrong the moment one does.
- **`Direction.Forward`** — the port of `ddt_GetFoward` computes cos/sin where the server reads a table
  built at start-up by `ddt_Initialize`. The direction quantum is 2°, so the two agree closely, but the
  table's own rounding will differ by a unit here and there. Same situation as the circular spawn sampler.
- **`Item.Rate` and friends** — clusters `c_MakeTotal` skips. `Item.Rate` is written with `CriDamRate` by
  `so_RecalcEquipParam`; where the formula reads it is untraced.

---

## 7. Simulation-only values (fine, but not game facts)

Defaults that exist to make an unconfigured object usable. They are *not* claims about the game, and every
one is overwritten by `Become()` or `Define()` when real data is applied. Listed so they are never mistaken
for read values: `SimPlayer` Hp/MaxHp 1000, AttackDamage 40, AttackRange 12, MoveSpeed 6,
AggroRatePermille 1000; `SimMob` Hp/MaxHp 300, AttackDamage 25, SwingIntervalMs 2000, SwingLandDelayMs 400;
`TickMs` 100 (this one *does* match the server's ClockWatch resolution — see docs/TICKRATE.md).

---

## How to use this file

When something in the simulation behaves oddly, check here **before** re-reading the binary. The odds
strongly favour a §1 entry: the value exists in a table, we parse the table, and the code ignores it in
favour of a constant.
