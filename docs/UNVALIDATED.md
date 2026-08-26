# Unvalidated: invented constants, unconnected data, and theories

Every value in this project falls into one of three states:

- **read** — taken from the binary or a game table, with the reading recorded;
- **decoded but unconnected** — parsed out of a table and then not used by anything;
- **invented** — a number this project chose, which the server gets from somewhere else.

This file lists everything that is *not* in the first state. It exists because the third category is
invisible in normal use: a made-up constant produces plausible behaviour and never announces itself.

Ordered worst-first. "Worst" means *most likely to be silently wrong*, not most work to fix.

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
| `TurnRateUnitsPerSecond = 150` | `MobActionTurning` | **`MobInfoServer.TurnSpeed`** |
| `SpeedPerSecond = 50` | `MobActionChase` | **`MobInfo.WalkSpeed` / `RunSpeed`** (both decoded, both unused), and `MobInfoServer.WalkChase` |
| `RespawnSeconds = 25` default | `SimMob` | `MobRegen.RegStandard` is wired, but `RegMin`/`RegMax`/the delta schedule are not, and `MobInfoServer.RegenInterval` / `ResetInterval` are not even decoded |
| `AttackRange = 10` default | `MobCombatState` | `MobWeapon.Range` — wired *only* when a definition is applied |
| `FacingToleranceUnits = 5` | `MobCombatState` | unknown; no obviously matching column found, but note rule 3 below before treating that as settled |
| **one targeting policy for every mob** | `MobTargetSelector` | **`MobInfoServer.EnemyDetectType`** — selects the selector SUBCLASS, mapping one-for-one onto the RTTI hierarchy. 220 mobs are passive (`ED_BOUT`) and 764 are `ED_NOBRAIN`; here they all attack on sight |

Every mob currently turns at the same rate and chases at the same speed. `TurnSpeed` in particular matters
for the open **angle question**: if turn rate varies per mob, a uniform constant would mask exactly the
effect being argued about.

---

## 2. Decoded but connected to nothing

Parsed into a record, then never read. Harmless today, but each one is a behaviour the simulation cannot
have.

**`MobInfo`** — `WalkSpeed`, `RunSpeed`, `Size`, `Id`
**`MobInfoServer`** — `MonExp`, `DetectCha`, `FollowCha`, `MaxSp`, `Rank`, `Id`, and `DetectType` (decoded
and its meaning read — it selects the targeting policy — but the simulation does not act on it yet)
**`MobWeapon`** — `BlastRate`, `Id`, and `Skill` (used only as a marker for "is this the ordinary swing",
never to actually cast anything).

`Th`, `MinMa`, `MaxMa` and `Mh` were on this list and are now **connected**: `sm_PrepareWeapon` stages them
into the mob's `Item.Plus` cluster alongside `MinWc`/`MaxWc`.

---

## 3. Present in the file, not decoded at all

Columns the tables carry that this project does not read. `MobInfoServer` has **49 columns and 16 are
captured**.

**`MobInfoServer`** — `Visible`, `MobKillInx`, `EXPRange`, `ResetInterval`,
`CutInterval`, `CutNonAT`, `PceHPRcvDly`, `PceHPRcv`, `AtkHPRcvDly`, `AtkHPRcv`, `MobRaceType`,
`FamilyArea`, `FamilyRescArea`, `FamilyRescCount`, `BloodingResi`, `StunResi`, `MoveSpeedResi`,
`FearResi`, `ResIndex`, `KQKillPoint`, `Return2Regen`, `IsRoaming`, `RoamingNumber`, `RoamingDistance`,
`RoamingRestTime`, `BroadAtDead`, **`TurnSpeed`**, **`WalkChase`**, `AllCanLoot`, `DmgByHealMin`,
`DmgByHealMax`, **`RegenInterval`**

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
- **`Item.Rate` and friends** — five clusters `c_MakeTotal` skips. Confirmed skipped; where the damage
  formula reads them instead is untraced.

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
