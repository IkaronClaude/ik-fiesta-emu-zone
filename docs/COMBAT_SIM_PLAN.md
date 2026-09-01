# From a damage formula to a combat simulation

`DamageCalculator` answers one question exactly: *given two entities in a known state, what number does a
normal physical swing produce?* Verified 556/556 against `FighterDamageLvl60.pcapng`.

A combat simulation has to answer a much larger one: *what happens?* Everything below is between the two.

Ordered by **how many already-simulated events it affects**, not by how interesting it is. Each task says
what to read, and what would prove it — because on this project a port is not done when it compiles, it is
done when it disagrees with nothing.

---

## 1. Hit, block and critical rolls — the biggest gap

Every swing the simulator already makes is affected. Today swings never miss, never block and never crit;
`AttackModifiers.CriticalChancePermille` is an *input* defaulting to 0 and nothing derives it from stats.

- [x] **`roe_HitRate`** (`NormalPY` 0x005011F0) and **`roe_HitRateByGlobalAction`** (0x005051C0).
- [x] **`roe_CriticalRate`** (`NormalPY` 0x00501700) and **`roe_CriticalRateByGlobalAction`** (0x00504B10).
- [x] **`roe_ShieldBlock`** (`NormalPY` 0x004FF860) and **`roe_ShieldBlockByGlobalAction`** (0x00505090).
- [x] **`roe_FreeStatHitRate`** (0x00500010) / **`roe_FreeStatCriRate`** (0x004FFDB0) — the free-stat halves.
      The record shapes are now READ from the PDB rather than inferred, and Dex and Men carry a second
      `u16` nobody had looked at: `FreeStatDex {Stat, THRate, TBRate}` (6 bytes),
      `FreeStatMen {Stat, MRAbsolute, CriRate, MaxSP}` (8), `FreeStatCon {Stat, ACAbsoulte, BlockRate,
      MaxHP}` (8), `FreeStatStr {Stat, WCAbsolute}` and `FreeStatInt {Stat, MAAbsolute}` (4 each).
- [x] **Critical damage is `2*dmg + dmg*PassiveCriDamageRatePlus/1000`** (container +0xCDC), not `2*dmg`.
      `roe_CalcDamage+0x4C2`.
- [x] **`roe_CriticalStun`** (0x00500070) and `roe_CriticalStunRate` — a flat **200** permille, and the
      abstate it applies is hard-coded `0x133` = **307** = `StaCommonStun02`.
- [ ] **The tables behind the free-stat terms are still unread.** `FreeStatDex` and `FreeStatMen` have
      never been dumped the way `FreeStatStr` was (181 entries, live, at 0x0DA50BC4), so
      `ICombatant.FreeStatDexTHRate` and friends have to be supplied by the caller. See `FUTURE_TESTS.md`.
- [ ] **Item actions.** `ItemActionObserveManager::EventRun` is not modelled; `ItemActionRates.None` keeps
      the roll ORDER honest (the extra draws are skipped exactly where the server skips them) without
      pretending to compute anything.

### Started 2026-09-01 — where the rolls actually live

**The `RulesOfEngagement` vtable, resolved** (from `??_7RulesOfEngagementNormalPY@@6B@` at 0x006D009C):

```
+0x00 roe_CriticalStunRate  +0x14 roe_ShieldBlock       +0x28 roe_ShieldBlockByGlobalAction
+0x04 roe_CriticalRate      +0x18 roe_HitRate           +0x2C roe_HitRateByGlobalAction
+0x08 roe_AttackPower       +0x1C roe_CalcDamage        +0x30 roe_FreeStateAttackPower
+0x0C roe_DefendPower       +0x20 roe_IsDamageSkill     +0x34 roe_FreeStateDefendPower
+0x10 roe_Damage            +0x24 roe_CriticalRateByGlobalAction   +0x38 roe_IsDamageImmune
```

⚠️ Written down because a first pass through `smo_SwingDamage` guessed slot 0x2C was shield block. It is
`roe_HitRateByGlobalAction`. Resolve slots against the vtable, never against what the neighbouring code
looks like it is doing.

**Where the rolls are:**

* **`smo_SwingDamage` rolls, `roe_CalcDamage` does not.** It calls slot **0x18** `roe_HitRate` at `+0x15F`,
  draws `well512_GetRandom(1000)` and compares; then slot **0x2C** `roe_HitRateByGlobalAction` at `+0x1B1`
  with a second draw. Both happen BEFORE `roe_CalcDamage`, which is why a missed or blocked swing never
  enters the damage pipeline — and why filtering the captures to `flagWord == 0` was sound.
* **`roe_HitRate@NormalPY` (0x005011F0) does the BLOCK check first.** It calls slot 0x14
  `roe_ShieldBlock`, draws its own `well512_GetRandom(1000)`, and if the draw comes in under the block
  rate it writes `mov byte [arg+0x13], 1` — `isshieldblock` — and returns `fldz`. Only if the swing is not
  blocked does it go on (via 0x005013FF, which first consults the defender's `so_GetItemActionObserves`,
  so an item action can influence the outcome) to produce the hit rate.
* **`EngageArgument`'s outcome bytes** are `iscritical@0x10, ismiss@0x11, isdead@0x12,
  isshieldblock@0x13` — the same bits the wire reports in `NC_BAT_SWING_DAMAGE_CMD.flag`, so the capture's
  flag word checks this code directly.

**So a swing resolves in this order**, and the port models none of it: shield block → hit → global-action
hit → (damage pipeline) → critical. Three independent WELL512 draws before any damage is computed.

**`roe_ShieldBlock@NormalPY` (0x004FF860), read.** Reads the DEFENDER's container (`arg+4`, vtable 0x430):

```
block = (Upgrade.Plus[ShieldAC] + Item.Plus[ShieldAC]) * AbnormalState.Rate[ShieldAC] / 1000
          +0x464                   +0x134                 +0x9F8
```

All three are <see cref="Stat"/> slot **26** (`ShieldAC`) in their respective halves, and `+0x9F8` is
exactly the address `SAA_SHIELDACRATE` writes — the abstate decode and the block formula meet at the same
offset, which checks both. A non-positive result branches out early (no block possible).

Note the shape: gear contributes a PLUS and only the abnormal-state layer contributes a RATE. `ShieldAC`
is also `ItemInfo.ShieldAC`, a column already loaded by `EquipmentCatalog` and currently unused —
`Kaineneceshield` has 70.

### Done 2026-09-01 — the rolls, ported

**`roe_HitRate@NormalPY`, complete.** The tail past 0x501582 was the missing half:

```
if (defender.MissPercentFix != 0)                       // +0x0CD0, an unsigned short
    return MissPercentFix > 1000 ? 0 : 1000 - MissPercentFix;   // no block flag on the >1000 path
th  = roe_TH(attacker) + attacker.FreeStatDex.THRate
tb  = roe_TB(defender) + defender.FreeStatDex.TBRate
tb += defender.PassiveMovingTBPlus[0]      when so_mobile_IsInMoving   // +0x0D88, INDEX 0, not the HP key
hit = (int)(th * 850.0 / tb)
hit -= defender.RangeEvasion               when the attack's range > 300   // roe_FreeStatHitRate
```

**850, not 1000** (the constant at 0x006D03E0). Equal Aim and Evasion is an 85% chance, and hit rate only
reaches certainty at about 18% above. `so_AttackRange@ShinePlayer` (0x00559930) is branchless and blunt —
**450 for the Archer family, 100 for everyone else** — keyed on `cc_BaseClass` (Fighter 1, Cleric 6,
Archer 11, Mage 16, Joker 21, Sentinel 26), so only archers ever pay `RangeEvasion`.

**The three comparisons do not agree with each other**, and the asymmetry is real:

| roll | passes when |
| --- | --- |
| shield block | `draw < rate` |
| hit | `draw <= rate` — a rate of 500 hits on 501 of the 1000 outcomes |
| `HitRateByGlobalAction` | `value >= draw`, and the value is `max(1000 - sum, 0)` |
| critical | `draw < rate` |
| critical stun | `draw < 200` |

**`roe_CriticalRate@NormalPY`** sums three ATTACKER layers at `CriDamRate` (Item.Rate +0x218,
WeaponTitle.Rate +0x6E0, AbnormalState.Rate +0xA10), subtracts two DEFENDER layers at `CriticalTB`
(AbnormalState.Rate +0xA38, Item.Rate +0x240), adds `FreeStatMen.CriRate`, and floors at 1.

⚠️ **OPEN, and it matters: an eraser-fresh container makes everything crit.** `ParameterCluster.Rate()`
seeds `CriDamRate` (slot 32) with 1000, so the three attacker terms alone come to **3000** for a character
with no gear at all. The capture says otherwise — of `FighterDamageLvl60.pcapng`'s 750 swing frames, 671
landed and **13** carry `iscritical`, which is **19 permille**. Ikaron has 25 free-stat Men, so ~19
permille is about what the `FreeStatMen.CriRate` term alone would contribute, and the three layer terms
must be **0** in a live container rather than the eraser's 1000. Either the rate eraser's slot 32 is not
1000 in the running process — the eraser was read out of live memory, but a static template is not a live
character, which is the same trap `capture_state.py` exists for — or something zeroes those three slots
before combat. **Do not "fix" this by reading a different slot**: the offsets are unambiguous and
`roe_ShieldBlock` lands on the same layer map. Settle it by reading a live player's container at +0x218 /
+0x6E0 / +0xA10, the way `FreeStatStr` was settled.

**Where the ground truth already is.** Flag byte of the capture's 750 `NC_BAT_SWING_DAMAGE_CMD` frames
(bit0 `iscritical`, bit2 `ismissed`, bit3 `isshieldblock`):

```
0x00  658   clean hit
0x04   64   missed                    -> 10.5% of all swings miss
0x0C   15   missed AND shield-blocked ->  2.0% blocked
0x01   13   critical                  -> 19 permille of the 671 that landed
```

**Not one frame is blocked without also being missed.** That is not a coincidence in the data, it is
forced by the code: `roe_HitRate` sets `isshieldblock` and then returns `0.0`, and `smo_SwingDamage` rolls
against that zero like any other rate — a block that still hit would need the draw to come up exactly 0.
The port reproduces it, and this is the first prediction from the roll phase that the wire confirms.

**Proof:** the captures already carry it. `flagWord` bit 2 is `ismissed` and bit 3 `isshieldblock`; the
bucket tool currently discards every non-zero flag. Extend it to bucket MISSES and BLOCKS as counts, and
check the observed rate per bucket against the predicted probability. The crit branch is checkable the same
way via bit 1, and its damage against the doubled band.

## 2. Abstates at runtime — currently only in the test's prediction path

The simulator has no abstate module. `BucketGroundTruthTests` reads which abstates the wire says are active
and applies their parameter effects; nothing can apply, tick or expire one.

- [ ] **An abstate list on a simulated object**, with `restKeeptime` expiry. `AbstateElementInObject`,
      `AbstateListInObject::asl_Abstate_IsSet`, `aeo_GetRestTime`, `ASE_Tick`.
- [ ] **`aeo_ParameterEnchant`** (0x004079F0) moved out of the test and into the parameter layer, so a
      container reflects live buffs. The jump table and its `SAA_*` handlers are already read.
- [ ] **The cancel hooks**: `aeo_Attack` (0x004A42A0) and `aeo_Attacked` (0x004A4310) end a state when the
      owner attacks / is hit, gated on bits 0x04 / 0x08 of a definition byte at `[element+0x70]+2`.
      ⚠️ **Name that struct first** — it is not `AbnormalStateInfo`.
- [ ] **The behaviour flags gate the mob state machine.** `Parameter::Container::flag` (+0xCCE) —
      `cannotmove_stun`, `cannotmove_entangle`, `cannotattack`. `MobActionAttack`, `MobActionChase` and
      `MobActionTurning` must honour them; none does. This is what a stun IS.
- [ ] **`SubAbnormalStateActor` subclasses** — poison/DoT (`sasa_GetDamage`, `sasa_Routine`), shields and
      absorbs (`sasa_Act_DamegeAbsorpt`), damage-minus (`sasa_Act_DamageMinusRate`), heal-over-time.
- [ ] **The three abstate damage callbacks** in `roe_CalcDamage`'s tail (+0x60A..+0x801), one gated on the
      attacker's `so_AttackRange > 300`.

**Proof:** `damage_buckets.py` already reconstructs abstate state from the wire. Have the simulator
reconstruct the same timeline from the same events and diff the two. Expiry needs real timestamps — frame
order is not a clock (see task 6).

## 3. Abstate application and resistance — who gets one, and how often

- [ ] **`cpl_SetAbstate`** (0x00446A10), **`so_ply_PassiveSetAbstate`** (0x005798E0),
      **`sasa_ApplyAbstate`**, **`PSkillSetAbstate.shn`**, and `PassiveDataBox::sdb_GetSetAbstate`.
- [ ] **`AbStateStr.debuffresist`** (+0x08) and the four resistance stats already in the container
      (`ResistPoison`, `ResistDeaseas`, `ResistCurse`, `ResistMoveSpdDown`) — plus `PainRes` /
      `RestraintRes` / `CurseRes` / `ShockRes` from the class tables, which are loaded and unused.
- [ ] **`mdt_ArgumentLoad`'s abstate half** — a skill's row also carries an `ABSTATEINDEX` to apply
      (row +0x0C), alongside the `damagerate` and `crirateadd` it writes.

**Proof:** count applications per attempt in a capture and compare to the predicted chance. Needs a capture
with a debuff used repeatedly against the same target.

## 4. Skills — the whole subsystem

Nothing exists. `SkillDataBox` reads `CastTime`/`DlyTime` for attack intervals only.

- [ ] **`SkillDataIndex::sdi_DamageRule`** (+0x70) — a skill brings its own `RulesOfEngagement`, which is
      where `roe_magical` / `roe_physical` enter and therefore where all caster damage lives.
      `sdi_Activ` (+0x04) is the `ActiveSkillInfo` row, `sdi_AttackDist` (+0x74) the range.
- [ ] **`MiscDataTable::mdt_ArgumentLoad`** (0x004A6110) — the ONLY writer of `EngageArgument.damagerate`
      (+0x1C) and `crirateadd` (+0x20); a normal attack leaves them at 1000/0. Keyed by skill id via
      `bsearch` over a table at `this+0x3460`.
- [ ] **`ActiveSkillInfoServer`** — `DmgIncRate` / `DmgIncValue`, `SkillHitType`, `SkilPyHitRate` /
      `SkilMaHitRate`, `AggroPerDamage` / `AbsoluteAggro`, `SwingTime` / `HitTime`.
- [ ] **`smo_SkillBlast`** (0x00581452 constructs the EngageArgument) and the multi-hit path
      (`MultiHitArgument`, `EngageArgument+0x24`).
- [ ] **Mob skill use** — `OPEN_QUESTIONS.md` §1. `AttackElement4Mob`'s 500-entry sequence,
      `so_mob_SelectWeapon`'s WELL512 roll against `sm_GetUseWeaponRate`, and the `sm_SkillExchange_*`
      predicates that are currently placeholders. **1,324 mobs have more than one weapon and the simulation
      only ever uses index 0.**

**Proof:** `FighterDamageLvl60.pcapng` contains **633 `NC_BAT_SKILLBASH_HIT_DAMAGE_CMD` frames** that have
never been looked at, plus `SKILLBASH_CAST_SUC_ACK` / `CAST_FAIL_ACK` / `HIT_OBJ_START` / `HIT_BLAST`.
Extend `damage_buckets.py` to bucket skill hits by (skill id, rank, state) exactly as it does swings.

## 5. The five damage hooks that no clean swing reaches

`OPEN_QUESTIONS.md` §3, unchanged: `ChargedEffectContainer` attack/defence force rates, the crit damage
bonus (also task 1), `so_ply_DecreaseDmgPassiveSkill`, `EventRun_IncDmgRate` item actions, and the abstate
damage callbacks (also task 2). Each needs a capture that actually triggers it.

## 6. Instrument work these depend on

- [ ] **Timestamps.** `damage_buckets.py` cannot expire anything because the decoded dump carries per-
      direction byte offsets, not a clock. Needed for abstate expiry, cast/hit timing and swing cadence.
- [ ] **Bucket misses and blocks**, not just clean hits — the rates are the ground truth for task 1.
- [ ] **Bucket skill hits** by skill and rank — the ground truth for task 4.
- [ ] **Free-stat tables for Dex / Int / Men**, read in full the way Str was (0x0DA50BC4) and Con should be
      (0x0DA50BD0). See `FUTURE_TESTS.md`.
