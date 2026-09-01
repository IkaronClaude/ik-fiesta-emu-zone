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

✅ **SETTLED by live memory, 2026-09-01 — and not the way the evidence was pointing.** Old text follows for
the record; the resolution is at the end of this section.

⚠️ ~~OPEN, and it matters: an eraser-fresh container makes everything crit.~~ `ParameterCluster.Rate()`
seeds `CriDamRate` (slot 32) with 1000, so the three attacker terms come to **3000** before any gear. The
capture says 55.6 permille. `CharacterParameters.Equip` was also filing a weapon's `CriRate` into
`Item.Plus[Critical]`, which nothing in the roll reads — now fixed to `Item.Rate[CriDamRate]`. The SEED is
what remains. Full evidence, both competing measurements and the experiment that settles it are in
`FUTURE_TESTS.md`; **do not resolve it by reading a different slot**, the offsets are confirmed twice.

**Where the ground truth already is.** `damage_buckets.py` now counts every swing's outcome per state
bucket, not only the clean hits, and reports the rates per DIRECTION — pooling the two gives a denominator
that describes neither side:

```
OUT  234 swings:   0 missed (0.0%),  0 blocked (0.0%),  13 critical of 234 landed (55.6 permille)
IN   516 swings:  79 missed (15.3%), 15 blocked (2.9%),  0 critical of 437 landed ( 0.0 permille)
```

The player never misses this mob tier and is never blocked — mobs carry no shield, so `roe_ShieldBlock`
returns 0 for them. The mobs miss 15.3% of the time and do not crit once in 516 swings.

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

- [x] **An abstate list on a simulated object**, with `restKeeptime` expiry — `AbstateListInObject` /
      `AbstateElementInObject`, with `IsSet` / `Set` / `Reset` / `SetAll` / `Tick` / `RestTimeMs`.
- [x] **`aeo_ParameterEnchant`** moved out of the test and into `AbstateListInObject.ParameterEnchant`,
      which REBUILDS the AbnormalState layer rather than adjusting it — the only way removing a state can
      take its bonus with it. The effect table is now GENERATED from the binary for all 120 actions
      (`tools/abstate_actions.py`), not the nine that one capture happened to exercise.
- [x] **The cancel hooks** `aeo_Attack` / `aeo_Attacked` are wired, taking a PREDICATE — the definition
      byte at `[element+0x70]+2` lives in a struct that is still not named, so the caller supplies the rule
      rather than the port guessing at it.
- [x] **The behaviour flags**, and the assumption in this list was wrong. See `FUTURE_TESTS.md`: the
      server's only two readers are `so_ReinforceMove@ShineMobileObject` (entangle) and
      `sp_Schedule_SwingStart@ShinePlayer` (attack). Not the tactic states. And `cannotmove_stun` has no
      reader at all.
- [x] **The `SubAbnormalStateActor` family is MAPPED.** 31 subclasses over a 27-slot vtable of which the
      base implements four and stubs the rest; each subclass overrides one or two. The slots that matter:

      ```
       3 +0x0C sasa_Routine                       Poison Disease HPHeal SPHeal HPSPHeal AreaDamage MagicField DmgState
       4 +0x10 sasa_Act_DamegeAbsorpt             Shield ManaShield
       5 +0x14 sasa_Act_Killed                    PartyRecharge SelfRevive
      10 +0x28 sasa_Act_DamegeIntercept           RangeIntercept
      11 +0x2C sasa_Act_LastDamegeInterceptByAtk  LastDmgRatio HideDamage
      12 +0x30 sasa_Act_LastDamegeInterceptByDef  LastDmgRatio_DefSide
      21 +0x54 sasa_Act_AllDamageAbsorb           ShieldHPRate
      22 +0x58 sasa_Act_NormalDamageDown          DamageDownRate
      25 +0x64 sasa_Act_MinHP                     MinHP
      ```

- [x] **Damage over time is ported** (`DotDamage`), which is `sasa_Routine@Poison` (0x0040D4C0) plus
      `sasa_GetDamage@Poison` (0x0040B360) and `smo_DotDamageAppend` (0x00408A60):

      ```
      if (target holds abstate 291 or 499) return;        // 291 is StaImmortal
      if (!subState has SAA_DOTDAMAGE) return 0;
      damage = SAA_DOTDAMAGE arg + DotDamagePlus[member for subState.Type]
      if (damage < 1) damage = 1                          // AFTER the append
      damage = min(damage, current HP)                    // a DoT does not overkill
      ```

      The type byte at +0x26 selects the member, off `smo_DotDamageAppend`'s jump table:
      `0x16 Blooding, 0x21 Poison, 0x22 Desease, 0x53 Burn, 0x54 PitBlooding`, everything else nothing.
      **The action names and the member names then agree** — `SAA_ADDPOISONDMG` writes `Poison` and a
      type-0x21 state reads it back — which is the cross-check on both readings.

      The append comes off the **TARGET's** container, so a `SAA_SUBTRACTPOISONDMG` buff on the victim
      softens their own poison and the floor of 1 stops it reaching zero.

      The same type byte is what picks `cannotmove_stun` over `cannotmove_entangle`, so it is now a field
      on `AbstateElementInObject` and the alt-branch rule is the list's own rather than a caller's delegate.

- [ ] **The remaining actors.** Shields and absorbs (`sasa_Act_DamegeAbsorpt`, `sasa_Act_AllDamageAbsorb`),
      damage-down (`sasa_Act_NormalDamageDown`, `sasa_Act_DotDamageDown`), `sasa_Act_MinHP`, the heals, and
      `sasa_Act_Killed`. All are one-subclass slots, so each is a small read.
- [x] **The three abstate damage callbacks** in `roe_CalcDamage`'s tail are READ. Each walks one side's
      `so_mobile_AbstateList` (object vtable +0x52C) and calls one `SubAbnormalStateActor` slot on every
      element:

      | at | list | actor slot | method | implemented by |
      | --- | --- | --- | --- | --- |
      | +0x60A | DEFENDER | 0x28 | `sasa_Act_DamegeIntercept` | `SubAbnormalStateActorRangeIntercept` |
      | +0x76F | ATTACKER | 0x2C | `sasa_Act_LastDamegeInterceptByAtk` | `LastDmgRatio`, `HideDamage` (same body) |
      | +0x8D0 | DEFENDER | 0x50 | — | **nobody** |

      The first is gated on the SAME range test as `roe_FreeStatHitRate` — `so_AttackRange` (or the skill's
      range at `sklinfo->[4]->[0xB4]`) strictly over **300** — so it only ever sees ranged hits, which is
      what "RangeIntercept" means.

      Its body (0x004074F0) is short enough to state in full:

      ```
      charges = element[0x54]
      if (charges > 0) { element[0x54] = charges - 1; *damage = 0; }   // absorbs the hit ENTIRELY
      else             { element[0x20] = clockwatch; }                 // out of charges: end the state
      ```

      So it is a counted absorb, not a percentage — N ranged hits taken to zero, and the state expires
      itself on the first hit after the last charge.

      ⚠️ **The third callback is dead in this build.** Actor slot 0x50 is `ret 0x10` on the base and no
      subclass in the image overrides it, so that whole pass calls nothing. Worth knowing before anyone
      spends time looking for its effect.
- [ ] **Port them.** Only `sasa_Act_DamegeIntercept` is fully read; `sasa_Act_LastDamegeInterceptByAtk`
      (0x00407570) is longer and its body is not read yet. Nothing can exercise either until the actor
      subclasses exist, which is the item above.

**Proof:** `damage_buckets.py` already reconstructs abstate state from the wire. Have the simulator
reconstruct the same timeline from the same events and diff the two. Expiry needs real timestamps — frame
order is not a clock (see task 6).

## 3. Abstate application and resistance — who gets one, and how often

- [x] **The resistance roll is READ and ported** — `so_AbnormalState_Resist`, object vtable **+0x634**,
      which `roe_CriticalStun` consults before applying its stun. Mobs and players do entirely different
      things:

      **`ShineMob` (0x00416660)** — twelve `u16` permille on the mob's own record at `+0x252D`, indexed by
      the abstate's resist type (`AbnormalStateInfo+0xF8`, valid 1..12). No stats, no container, nothing
      the mob is wearing: a mob's resistance is a property of the species. A record of `0xFFFF` or a type
      outside 1..12 — 0 included — resists nothing.

      **`ShinePlayer` (0x00416730)** — `Item.Rate[slot] + AbnormalState.Rate[slot]`, where the slot comes
      from **`AbStateStr.debuffresist`**. That field is declared `int *` and is not a value at all: it is
      the BYTE OFFSET of a stat slot, so the abstate definition names which resistance stat applies and
      the player's own two rate halves supply the number. (`0x1158 - 0xFC0 = 0x198` is Item.Rate;
      `0x1950 - 0xFC0 = 0x990` is AbnormalState.Rate.) A null definition returns RESISTED, so an unknown
      abstate is refused rather than applied blind.

      Both roll `well512_GetRandom(1000)` and resist on a STRICT less-than.

      **This also corroborates the CriDamRate anomaly.** The player's two halves are SUMMED, and every slot
      `debuffresist` can point at is in the rate-eraser's zero run (42..48: `CriticalTB`, `RegistNone`, the
      four `Resist*`, `ResistGTI`). That is exactly the set of rate slots the engine reads ADDITIVELY —
      `roe_CriticalRate` subtracts `CriticalTB`, this sums a `Resist*` — and they are all seeded 0.
      `CriDamRate` is read additively too and is seeded **1000**. Two independent cases now say additive
      rate slots have a zero identity, which makes that seed an anomaly with evidence against it rather
      than a lone puzzle. See `FUTURE_TESTS.md`.

- [ ] **The application path itself** — `cpl_SetAbstate` (0x00446A10), `so_ply_PassiveSetAbstate`
      (0x005798E0), `sasa_ApplyAbstate`, `PSkillSetAbstate.shn` and `PassiveDataBox::sdb_GetSetAbstate`.
      `so_AbnormalState_Set` is object vtable **+0x638** and `so_AbnormalState_IsSet` is **+0x3E4**.
- [ ] **`mdt_ArgumentLoad`'s abstate half** — a skill row also carries an `ABSTATEINDEX` to apply
      (row +0x0C), alongside the `damagerate` and `crirateadd` it writes.

**Proof:** count applications per attempt in a capture and compare to the predicted chance. Needs a capture
with a debuff used repeatedly against the same target.

## 4. Skills — the whole subsystem

Nothing exists. `SkillDataBox` reads `CastTime`/`DlyTime` for attack intervals only.

- [ ] **`SkillDataIndex::sdi_DamageRule`** (+0x70) — a skill brings its own `RulesOfEngagement`, which is
      where `roe_magical` / `roe_physical` enter and therefore where all caster damage lives.
      `sdi_Activ` (+0x04) is the `ActiveSkillInfo` row, `sdi_AttackDist` (+0x74) the range.
- [x] **`MiscDataTable::mdt_ArgumentLoad`** (0x004A6110) — read and ported, and it is **CONDITIONAL**,
      which the plan did not anticipate. The row is keyed by skill id (`bsearch` over 20-byte
      `MiscData_VarifyByAbstate` rows at `this+0x3460`) but it only fires when the DEFENDER currently
      satisfies the row's `mdvba_Condition`:

      ```
      mdvba_Skill u16 @0 | mdvba_Condition @4 | mdvba_DamageRate s16 @8
      mdvba_NewState ABSTATEINDEX @0xC (applied only below 0x318) | mdvba_Crirate s16 @0x10
      ```

      So a skill's damage rate is **not a property of the skill**: it is a "hits harder against a stunned /
      slowed / armour-broken target" bonus, and against an unafflicted target a skill leaves the argument
      at the same 1000/0 a normal swing does.

      `so_smo_AbnormalStateAttribute@ShineMobileObject` (0x004A8010) is the gate, and each of its three
      attributes is a specific test rather than a name:

      | attribute | satisfied by |
      | --- | --- |
      | `STUN` | a sub-state whose TYPE byte at +0x26 is **0x15** — the same value that makes `SAA_NOMOVE` set `cannotmove_stun` |
      | `SLOW` | any state carrying `SAA_SPEEDDOWNRATE` (88) |
      | `ACMRMINUS` | `SAA_ACMINUS` (73), `SAA_ACDOWNRATE` (74), `SAA_MRMINUS` (86) or `SAA_MRDOWNRATE` (87) |

- [x] **Skill empower.** `EngageArgument.empower` (+0x0C) is a `SKILL_EMPOWER`: two bytes holding FOUR
      4-bit fields — `damage`, `sp`, `keeptime`, `cooltime` — allocated per cast and requested by the
      client through `PROTO_NC_SKILL_EMPOWALLOC_REQ`. `roe_AttackPower` reads it in all three physical
      variants (NormalPY +0x658, PhisycalSkill and MagicalSkill +0x178):

      ```
      level = empower.damage;                       // 0 short-circuits to no term at all
      term  = *(u32*)(sdi_Activ + level*4 + 0x1BB); // = nT0[level-1]
      ```

      `nT0`..`nT3` are four contiguous `unsigned long[5]` from +0x1BF, and the engine walks them as **ONE
      flat run of twenty** — level 6 reads `nT1[0]`, not `nT0[5]`. Reading them as four separate tables
      would be wrong for every level above 5.

- [ ] **Where the empower term lands in the attack-power chain.** The lookup is exact; what
      `roe_AttackPower` then does with it — added before or after which rate, inside or outside which
      truncation — is unread, and the port deliberately stops at the lookup rather than guessing.

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

- [x] **Timestamps.** `pcap_decode.py --timestamps` now prints each frame's capture time, in seconds from
      the CONVERSATION's first frame (a relog opens a new conversation and the two decode independently).
      Opt-in, so the default output and the decode-the-whole-thing workflow are unchanged.

      `damage_buckets.py` passes it and expires an abstate when its `restKeeptime` runs out instead of
      holding it until an explicit `ABSTATERESET`. That closed a real correctness hole: 6 of 248 state
      buckets were being split by a debuff that had already lapsed. **The 556/556 damage check still
      passes on the corrected state**, which is the useful result — the hole was real but had not been
      changing any prediction.

      A state with no expiry (keeptime 0, or a dump made without the flag) is KEPT. "We do not know when
      this ends" must not quietly become "it already ended".
- [ ] **Bucket misses and blocks**, not just clean hits — the rates are the ground truth for task 1.
- [ ] **Bucket skill hits** by skill and rank — the ground truth for task 4.
- [ ] **Free-stat tables for Dex / Int / Men**, read in full the way Str was (0x0DA50BC4) and Con should be
      (0x0DA50BD0). See `FUTURE_TESTS.md`.

## The critical-rate seed: settled by reading the live server

The longest-running open question of the swing-roll work, and the answer is a measurement rather than an
argument. Three reads, in order:

**1. The oracle confirms the formula.** `roe_CriticalRate@NormalPY` run under emulation on an
eraser-seeded container returns exactly **3000.0**, and poking one slot at a time identifies all five
offsets independently of the disassembly: `Item.Rate[CriDamRate]`, `WeaponTitle.Rate[CriDamRate]` and
`AbnormalState.Rate[CriDamRate]` add; `AbnormalState.Rate[CriticalTB]` and `Item.Rate[CriticalTB]`
subtract. The reading was right.

**2. The live eraser confirms the seed.** All 51 slots read out of zone02's running `Zone.exe`
(`/proc/<pid>/mem` at 0x0DA3FA78): 1000 everywhere except slots **42..48** — `CriticalTB`, `RegistNone`
and the four `Resist*` plus `ResistGTI`. `CriDamRate` (32) really is **1000**. The plus eraser at
0x0DA3FB48 is 51 zeros. `ParameterCluster` was right too, and `so_RecalcEquipParam` re-seeds from those
same two erasers.

So both halves of the contradiction were solid, which is why arguing about it was going nowhere.

**3. A live CONTAINER settles it.** Scanning zone00's `Zone.exe` for the rate-cluster signature and
reading what a real container holds:

```
Item.Rate            slots not 1000: 32, 33, 42..48
WeaponTitle.Rate     slots not 1000: 32, 33, 42..48
AbnormalState.Rate   slots not 1000: 32,     42..48
ItemPowerRate.Rate   slots not 1000:         42..48
Upgrade.Rate         slots not 1000:         42..48
PassiveSkill.Rate    slots not 1000:         42..48
LastTune.Rate        slots not 1000:         42..48
```

**The crit slots are cleared AFTER the erase, and only in the three clusters the crit formula adds.**
Every other rate cluster keeps the eraser's 1000 at slot 32. So a live character's crit rate starts at 0
and is floored at 1 — the 3000 was the bug, and it is gone.

⚠️ **CORRECTED: what then PUTS a number in those slots is NOT known.** This section first claimed a
weapon's `ItemInfo.CriRate` is the chance, because the capture's weapons (Splitter 30, Kaineneceflight 70,
Kainenecefury 90) predicted 51.0 permille against 55.6 measured. That was an inference dressed as a
finding. Two things undercut it:

* a scan for writes to those displacements finds only `c_clear` — no equipment code. (A weak negative on
  its own: a generic loop with a computed slot index would not show up.)
* the capture cannot separate the models. Over 234 landed swings, MEN-only predicts **11.7** criticals and
  weapon-only **11.9**, against **13** observed. Both fit; their SUM (23.6) does not.

The operator's play figures point the same way: weapons carry roughly 3–9% (30–90 permille, exactly
`ItemInfo.CriRate`'s range), 25 MEN adds 5%, and a normal player is **5–10% in total** — which a straight
sum of the two would already exceed. So the two terms are probably not both feeding this function, and the
`CharacterParameters.Equip` routing that assumed they were has been removed.

The one term pinned end to end is `roe_FreeStatCriRate`: `FreeStatMen.CriRate[25] = 50` permille from the
live table, which alone accounts for the 13 criticals observed.

**Settling it** needs a live EQUIPPED player's container read at +0x218. The containers found so far were
idle pool entries; a scan filtered on populated `Total` slots produced only false positives.

Modelled as `ParameterCluster.RateFor(source)`, which is the eraser plus the per-cluster clearing. The
556/556 damage check is unaffected — no damage accessor reads those slots.

⚠️ **What is still unread is WHICH code does the clearing.** It is not `c_clear` and not
`so_RecalcEquipParam` — both re-seed from the eraser wholesale. The behaviour is measured on live
containers, so the port is right about the STATE; the mechanism is a loose end.
