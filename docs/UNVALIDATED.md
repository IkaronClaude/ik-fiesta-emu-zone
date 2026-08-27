# Unvalidated: invented constants, unconnected data, and theories

Every value in this project falls into one of three states:

- **read** — taken from the binary or a game table, with the reading recorded;
- **decoded but unconnected** — parsed out of a table and then not used by anything;
- **invented** — a number this project chose, which the server gets from somewhere else.

This file lists everything that is *not* in the first state. It exists because the third category is
invisible in normal use: a made-up constant produces plausible behaviour and never announces itself.

Ordered worst-first. "Worst" means *most likely to be silently wrong*, not most work to fix.

> **TWO tooling bugs that manufactured false absences.**
>
> 1. The scan used to find field readers only matched `disp8` addressing, and `disp8` is SIGNED — so any
>    struct offset at or above `0x80` is encoded as `disp32` and was invisible to it. `TurnSpeed` at
>    `+0x9C` looked unreferenced for that reason alone.
> 2. **Worse:** every whole-image scan looped over `capstone.Cs.disasm(raw, base)` directly. That
>    generator **STOPS at the first byte it cannot decode and returns what it had** — on this binary,
>    0x004ADFEF, 44% of the way through `.text`. The loop looks exhaustive and saw **228,684 of 922,891
>    instructions, 24.8%**. It hid the `ShineMobileObject` constructor's write to
>    `smo_RulesOfNormalAttack` through a whole pass of "nothing sets this field". Fixed by `Code.sweep()`
>    in `tools/disasm.py`, which resynchronises; `xref_vcall.py` and `xref_call.py` now use it. **Any
>    negative result from before 2026-08-27 was worth a quarter of a binary.**
>
> If an older note in this file says "nothing reads X", it predates both fixes.
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
| `RespawnSeconds = 25` default | `SimMob` | `MobRegen.RegStandard` is wired, but `RegMin`/`RegMax`/the delta schedule are not, and `MobInfoServer.RegenInterval` / `ResetInterval` are not even decoded |
| `AttackRange = 10` default | `MobCombatState` | `MobWeapon.Range` — wired *only* when a definition is applied |
| `FacingToleranceUnits = 5` | `MobCombatState` | unknown; no obviously matching column found, but note rule 3 below before treating that as settled |

Chase speed is now per-mob from `MobInfo.RunSpeed`, and turning from `MobInfoServer.TurnSpeed` — including
the fact that **`TurnSpeed` is a duration, not a rate**: `elapsedTenths * 18000 / TurnSpeed` units turned,
so a full turn lands at `TurnSpeed / 100` tenths. Bigger is slower. Zero is instant.

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
*seen* but not ported); `StaName`; and `StaStrength`/`StaRate`, now PARSED but still unconnected — they are
the weapon's status-effect strength and chance, and abnormal states are not modelled here at all, so
nothing consumes them.

`AggroInitialize` has left this list: it is parsed and connected. It SUBTRACTS — a mob that lands a hit
sheds that much hate for its victim, across its whole family — and it is a boss mechanic (139 of 5,815
rows, values 400–1000). See OPEN_QUESTIONS.md.

`HitType` has also left this list: it selects the mob's `RulesOfEngagement` singleton at spawn.

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

## 4b. Magic classes cannot auto-attack — RESOLVED 2026-08-27, and it is the game, not a gap

**This entry was wrong and is kept as a correction.** It read the wizard's grind result as a missing
feature. It is not: `smo_RulesOfNormalAttack` (`ShineMobileObject+0x1E74`) is the field that chooses a
normal attack's rules, and **a player's is always `roe_normalPY`.** Every writer of that field, over a
full-coverage instruction scan:

| writer | value |
|---|---|
| `ShineMobileObject::ShineMobileObject+0xC4` (0x559284) | `&roe_normalPY` — the default for everything |
| `ShineMob::so_mob_Regenerate+0x55D/+0x569` | `&roe_normalMA` / `&roe_normalPY` from `MobWeapon.HitType` |
| `ShinePet::spt_Regenerate+0x2B1` | `&roe_normalPY` |
| `ShinePlayer::sp_SetRulesOfEngagement` | whatever it is passed — **one caller: the GM `&allcritical`** |

So a wizard swinging a wand for almost nothing is correct behaviour. Caster damage arrives through
SKILLS (`roe_magical`, `roe_normalMA` for skill casts), which this project does not model at all — that is
the real gap, and it is §2's "mob skill attacks" problem seen from the player's side.

The grind table stands as measured; only its interpretation changes:

| class | WC | orcs killed |
|---|---:|---:|
| Ranger | 950 | 137 |
| Warrior / Knight | 581 | 45 |
| HighCleric | 509 | 37 |
| **Wizard / Enchanter** | **128** | **1** |

**`MobWeapon.HitType` is now connected** (`MobParameters.NormalAttackRule`). Of the 2,059 fightable mobs
with a weapon row, **334 attack magically** — resisted by MR, not AC, and unblockable, because
`RulesOfEngagementNormalMA` leaves `roe_ShieldBlock` at the base return-0 stub. 708 mobs carry `HT_NONE`
at row 0, which the server's `!= HT_PY` test also routes to the magical rule; 629 of those are not
fightable and the rest are props with WC 0-0.

⚠️ Still unread on this path: `roe_HitRate` and `roe_CriticalRate` differ per rule and this port does not
implement either — a swing here never misses and crits only when told to.

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
