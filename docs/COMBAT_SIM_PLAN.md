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
- [x] **The tables behind the free-stat terms are READ, all five, all 181 entries** — dumped live from the
      pointer arrays at 0x0DA50BC4 (Str), BC8 (Int), BCC (Dex), BD0 (Con), BD4 (Men) and embedded verbatim
      as `FreeStatTables`. The curves the earlier version computed are kept only as comments, held against
      the data by a test, because the operator asked for the tables themselves rather than a fit to them.
- [x] **Item actions — the DAMAGE half is READ and PORTED** (`Combat/ItemActionResults.cs`).
      `EventRun_IncDmgRate` (0x005D2010) is three null gates and then a fixed argument: event selector
      **9**, slot mask **0xFFFF**, `subject` = attacker, `object` = defender.

      What the damage path consumes is `ActionResults::GetRateAppliValue` (0x005D0C20), and its shape was
      the surprise:

      ```
      if (value == 0) return 0;
      for (i = 0; i < count; i++)
          value = (uint)(value * result[i].rate) / 1000;     // UNSIGNED, truncating, EVERY step
      ```

      ⭐ **The results COMPOUND and each truncates on its own.** Three results of 1100 are not one rate of
      1331 and are certainly not a sum: the intermediate is floored after every step, so summing or
      pre-multiplying both drift, and drift further the more results there are. **Order matters too** —
      `[300, 7000]` on a value of 5 gives 7 while `[7000, 300]` gives 10, because the small rate first
      floors 1.5 to 1 and the chain never recovers. That is why `AttackModifiers` now carries an
      `ItemActionResults` sequence rather than a single permille.

      ⚠️ **"No actions" is a different PATH from "a neutral rate".** `roe_AttackPower` gates the whole
      block on an action having fired, and inside the block each bound is truncated to an integer on the
      way past — so skipping it also skips a truncation.

      Still unmodelled: `_EventRun`'s dispatcher, i.e. WHICH items produce results and with what rates.
      That is item-and-condition data, not damage arithmetic, and nothing in the damage path needs it —
      the fold above is the whole interface between the two.

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

- [x] **The remaining actors — READ and PORTED** (`Abstate/AbstateDamageCallbacks.cs`). Each was small,
      as expected; what was NOT expected is that they do not share a convention.

      | actor | action | behaviour |
      | --- | --- | --- |
      | `ShieldHPRate::AllDamageAbsorb` | — | an **HP-POOL**: `pool <= dmg` -> the overflow passes THROUGH and the state ends; otherwise the hit is zeroed |
      | `RangeIntercept::DamegeIntercept` | — | a **COUNTED** absorb: the hit is zeroed whatever its size, one charge spent |
      | `DamageDownRate::NormalDamageDown` | `SAA_DMGDOWNRATE` 115 | `dmg -= dmg*rate/1000`, and `rate >= 1000` -> 0 |
      | `DamageDownRate::DotDamageDown` | `SAA_DOTDMGDOWNRATE` 111 | same body, different action |
      | `MinHP::MinHP` | `SAA_MINHP` 114 | RAISES a floor, so the highest among the states wins and visit order does not matter |
      | `UseSPDown::UseSPDown` | `SAA_USESPDOWN` 100 | `cost -= cost*rate/1000`, **no `>= 1000` clamp** — a rate above 1000 goes negative |
      | `SelfRevive::Killed` | `SAA_REVIVEHEALRATE` 40 | `setIsRebirth(1)` then `setHealRate(rate)` |
      | `PartyRecharge::Killed` | `SAA_DEADHPSPRECOVRATE` 24 | combines with a set-item bonus — **not fully read** |

      ⚠️ **Two shields, two behaviours.** Against a big hit the HP-pool shield breaks and lets the
      remainder through while the counted absorb eats it whole. Indistinguishable on small hits.

      ⚠️ **The absent-action convention is NOT uniform**, and it has to be read per slot. Damage-down,
      MinHP and UseSPDown call `assa_IsHaveEffect` first and leave the value ALONE when the action is
      missing. `LastDamegeInterceptByAtk` and `SelfRevive` call `assa_FindEffect` directly and use its
      missing-value of **0** — zeroing the damage in one case and reviving at rate 0 in the other.

      ⭐ `UseSPDown` closes a loop: `StaMagicDanceUseSPDown01`..`04`, the abstates the shipped
      `MagicDance` / `PointAttack` passive rows apply, carry exactly this action. The passive path in task
      3 and this actor are two ends of one mechanic.

      "The heals" in the original wording resolve to the HP/SP-over-time path already ported as
      `DotDamage` — there is no separate heal actor slot in the image.
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
- [x] **Port them — DONE** (`Abstate/AbstateDamageCallbacks.cs`). `sasa_Act_LastDamegeInterceptByAtk`
      (0x00407570) turned out short once the table walk is unpicked:

      ```
      rate   = assa_FindEffect(element.row[strength].args, SAA_TOTALDAMAGERATE /*50*/);
      damage = damage * rate / 1000;                      // signed, truncating
      ```

      It runs on the **ATTACKER's** states — a state that scales down everything its owner deals — and the
      `LastDmgRatio` and `HideDamage` actors share this one body verbatim.

      ⚠️ **`assa_FindEffect` (0x00416160) returns 0 when the action is absent.** It scans the row's FOUR
      (action, argument) slots and falls out with `xor eax, eax`, so an element reaching this callback
      without a `SAA_TOTALDAMAGERATE` slot **zeroes the damage** rather than leaving it alone. Ported that
      way rather than defaulting to a neutral 1000: it is only survivable because the two actors are bound
      to rows carrying the action, and a neutral default would hide a mis-built row instead of failing on
      it. It also means "absent" and "present, valued 0" are indistinguishable — to the original as much
      as to this port.

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

- [x] **The application path is READ and its decision rule ported.** `so_AbnormalState_Set` is object
      vtable **+0x638** (`so_AbnormalState_IsSet` is **+0x3E4**) and it does two things:

      ```
      map = so_CurMap();
      if (map && map->fm_IsRefuseAbstate(index)) return FALSE;   // A MAP CAN REFUSE AN ABSTATE OUTRIGHT
      return this->abstateList /* object+0x1CC */ ->asl_AbstateSet(...);
      ```

      `asl_AbstateSet` (0x0040D020) then walks: `asl_Abstate_IsSet` → **`bp_AbStateChange`** → `aeo_Set` →
      `ase_AfterEnchant` → `sasa_AttachObject`, and calls `sbec_SkillCancel` on the way — **applying a
      state can cancel a cast in progress.**

      **`SubAbstatePriority::PriorityBase::bp_AbStateChange` (0x00591CE0) is the interesting part**, and it
      is richer than the "re-applying replaces" the wire suggests. It compares the two `SubAbState` rows'
      four (ActionIndex, ActionArg) pairs and returns a `StateExchange`:

      ```
      for i in 0..3:
          if incoming.action[i] != existing.action[i]  return SAP_NORELATION;  // unrelated, both stay
          if incoming.arg[i]    <  existing.arg[i]     return SAP_SUBSCRIPT;   // weaker: declined
          if incoming.arg[i]    >  existing.arg[i]     return SAP_VANISH;      // stronger: existing goes
          if incoming.arg[i]    >  0                   sawPositive = true;
      return sawPositive ? SAP_SUBSCRIPT : SAP_VANISH;                         // identical rows
      ```

      ⚠️ **Identical rows are SUBSCRIPT** — re-casting the same buff does NOT refresh its keeptime. That is
      the one case the wire could never show, because it is exactly what the server re-sends constantly and
      declines to act on. Ported as `SubAbstatePriority` + `AbstateListInObject.SetWithPriority`, which
      returns the displaced states because each is an `ABSTATERESET` the server owes its clients.

- [x] **The passive-skill half — READ and PORTED** (`Abstate/PassiveSetAbstate.cs`).
      `cpl_SetAbstate` accepts exactly FOUR conditions (`cmp [ebp+8], 4; jge`, then a four-entry jump
      table), so that is the complete set of hooks passives have into the abstate system:

      ```
      foreach (row in passiveList)
          if (row.PS_Condition != condition)                                   continue;
          if (condition == PS_CBOWATKRATEKNOCKBACK &&
              (!flag || source.weapon.WeaponType != WT_CROSSBOW /*10*/))       continue;
          if (condition == PS_MEDMGMISSCRIUPRATE && !flag)                     continue;
          if (row.PS_ConditioRate <= rb_1000())                                continue;
          abstate = as_FromName(row.PS_AbStateInx);            // by NAME, not index
          if (!abstate || target->so_AbnormalState_Resist(abstate) == 1)       continue;
          target->so_AbnormalState_Set_Simple(source, abstate.index, row.Strength, 1);
      ```

      Reached through `so_ply_PassiveSetAbstate`, whose base `ShineObject` implementation does nothing —
      **only a player applies an abstate this way; a mob never does.** The abstate lands on the SECOND
      argument (both the resist check and the set are called on it); the first is only the source. The
      loop does not stop at the first match, so each matching row rolls independently.

      ⭐ **The shipped data does not exercise all of it.** All 16 rows of `PSkillSetAbstate.shn` use
      conditions 1, 2 and 3 — **condition 0 has no rows at all**, so the crossbow weapon gate is reachable
      code with nothing behind it. Rows: cond 1 = `MagicDance01`..`04` + `PointAttack01`..`04` ->
      `StaMagicDanceUseSPDown01`..`04`; cond 2 = `DeepFear01`..`04` -> `StaDeepFearMenDownRate01` at
      strengths 1..4; cond 3 = `Shame01`..`04` -> `StaShameCRIUp01`..`04`. Every rate is **1000**, which
      with the strict-greater rule always fires. And rank is expressed two different ways — `DeepFear` as
      four strengths of one abstate, the others as four abstates at strength 1 — so rank does not reliably
      live in `Strength`.
- [x] **`mdt_ArgumentLoad`'s abstate half — READ.** `mdvba_NewState` (row +0x0C) applies when it is
      **below 0x318** (a `jge`, so 0x318 itself applies nothing — out of range is how a row says "modify
      the damage but apply no state", not an error value):

      ```
      abstate = as_FromIndex(row.mdvba_NewState);          // by INDEX here, unlike the passive path
      if (defender->so_AbnormalState_Set(attacker, row.mdvba_NewState, 1, abstate, 0,-1,0, 6, 0))
          defender->so_AbnormalState_BitSet(abstate.index);
      ```

      It lands on the DEFENDER; **strength is a hard-coded 1** (the row has no strength field, so a
      skill's misc-data abstate is always rank 1); and it uses the FULL `so_AbnormalState_Set`
      (vtable +0x638), not the `_Simple` overload (+0x644) the passive path uses. ⚠️ Unlike the passive
      path it does **not** roll resistance first — it hands the decision to `so_AbnormalState_Set` and
      only bit-sets on success.

      *Tooling note, cost half an hour:* `disasm`'s VA->name map keeps ONE name per address, and this
      binary folds identical COMDATs hard — ~70 symbols share 0x00615E70, so vtable slot +0x634 prints as
      `GDTSO_SetDiceFix@CGambleObject` when it is also `so_AbnormalState_Resist@ShineObject`. A slot that
      resolves to an absurd name is the lookup, not the binary; list every symbol at the VA before
      concluding anything.

**Proof:** count applications per attempt in a capture and compare to the predicted chance. Needs a capture
with a debuff used repeatedly against the same target.

## 4. Skills — the whole subsystem

Nothing exists. `SkillDataBox` reads `CastTime`/`DlyTime` for attack intervals only.

- [x] **`SkillDataIndex::sdi_DamageRule`** (+0x70) — confirmed in use, and it is the ONLY rules pointer
      the skill path touches. `smo_SkillBlast` loads `[sklinfo+0x70]` and calls its `roe_HitRate` (slot 6),
      `roe_HitRateByGlobalAction` (slot 11) and `roe_CalcDamage` (slot 7) through it — so which rule a
      skill uses is per-skill DATA, not a property of the caster's class. `sdi_Activ` (+0x04) is the
      `ActiveSkillInfo` row (read by `roe_AttackPower`), `sdi_ServInf` (+0x00) the `ActiveSkillInfoServer`
      row (read by `roe_HitRate`), `sdi_AttackDist` (+0x74) the range.
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

- [x] **Where the empower term lands in the attack-power chain — READ, at +0x17E..+0x1E6.** ONE lookup,
      added to **both bounds**, after the skill row and before the item-action rates and the roll. So
      empowering a cast for damage SHIFTS the range and does not widen it. This agrees with the earlier
      oracle result (additive on both bounds, before the roll, before the mastery rate) and now has the
      instruction sequence behind it rather than a bracketing measurement.

- [x] **`ActiveSkillInfoServer` — and the item in this list named the WRONG TABLE.** The expectation was
      that a skill's damage bonus comes from `DmgIncRate` / `DmgIncValue`. It does not. `roe_AttackPower`
      dereferences `sdi_Activ` (+0x04, the CLIENT `ActiveSkillInfo` row) and reads four columns there:

      ```
      physical   MinWC +0xDB  MinWCRate +0xDF  MaxWC +0xE3  MaxWCRate +0xE7
      magical    MinMA +0xEB  MinMARate +0xEF  MaxMA +0xF3  MaxMARate +0xF7

      low  = low  + low  * MinRate/1000 + MinFlat;
      high = high + high * MaxRate/1000 + MaxFlat;
      ```

      **The bounds are scaled INDEPENDENTLY** — a skill can widen its damage range, not merely shift it.
      Measured on the real function with the RNG pinned to each end (`tools/oracle_skill.py`), all five
      cases exact. Nothing clamps `low <= high`: `MinWCRate` alone inverts the range and the server hands
      the negative span to the RNG without noticing.

      `ActiveSkillInfoServer` IS read, by the HIT path only — `roe_HitRate@PhisycalSkill` (0x00502D30)
      takes exactly two columns from `sdi_ServInf`:
        - **`SkilPyHitRate` / `SkilMaHitRate`** (+0x23 / +0x27) replaces the plain swing's hard-coded
          **850** in the same position and units, so a row holding 850 is exactly as accurate as an
          ordinary attack.
        - **`SkillHitType`** (+0x3A) selects the formula, and the branch is `!= 0`, not `== 1`:
          `AsNormal` is the ordinary `rate * TH / TB` contest; anything else is
          `rate * attackerLevel / defenderLevel`, which **ignores aim, evasion, Dex and the defender's
          movement entirely**. Verified by moving the attacker's Dex from 20 to 2000: the normal branch
          went 850 -> 1700000 and the level branch did not move off 1142.

      Also read while there: the skill hit function does its OWN shield-block roll (well512 against vtable
      slot 5) BEFORE the MissPercentFix short-circuit — the opposite order from the plain swing.

      `AggroPerDamage` / `AbsoluteAggro` / `SwingTime` / `HitTime` / `AddSoul` are real columns but no
      damage-path function reads them; they belong to aggro and action timing. Deliberately absent from
      `ActiveSkillInfoServer` rather than modelled as zero.

- [x] **The item-action rate was in the WRONG PLACE in this port, and the skill read is what exposed it.**
      `GetRateAppliValue` is applied to **each bound, before the roll** (`roe_AttackPower@NormalPY`
      +0x163..+0x1BF, PhisycalSkill +0x272..+0x2D6), with each bound TRUNCATED to an integer on the way
      in — not to the rolled figure, which is where the port had it. Invisible until now because the whole
      block is gated shut when no item action fires, so the default neutral rate never exercised it, and
      the 556/556 ground truth is unchanged either way. Fixed, with the gate modelled as a gate rather
      than as a multiply by 1000.
- [x] **`smo_SkillBlast` and the multi-hit path — READ and PORTED** (`Skill/MultiHit.cs`).

      `EngageArgument+0x24` is `pMultiHitArg`. The surprise is where it is NOT used: **`roe_CalcDamage`
      never scales damage by the multi-hit rate.** It reads the pointer exactly once, at +0x1AE, to gate
      the CRITICAL STUN:

      ```
      arg->iscritical = 1;                                    // set BEFORE the branch
      if (pMultiHitArg == null)              roe_CriticalStun(arg);   // a plain swing always tries
      else if (pMultiHitArg->mha_DamageRate > 0) roe_CriticalStun(arg);
      // otherwise skipped entirely
      ```

      So a filler strike carrying no damage rate can be flagged critical and never stun, while an ordinary
      swing always makes the attempt — a property of the individual STRIKE, not of the skill. Critical
      damage is unaffected either way.

      The scaling is the CALLER's, at `smo_SkillBlast+0x93B`, on the finished integer:

      ```
      dmg = sdi_DamageRule->roe_CalcDamage(arg);
      dmg = (uint)(dmg * serverRate) / 1000;      // UNSIGNED  (mul 0x10624DD3; shr edx,6)
      dmg = (dmg * mha_DamageRate) / 1000;        // SIGNED, truncating (imul; sar; sign fixup)
      if (scaled > 0 && dmg == 0 && mha_DamageRate > 0) dmg = 1;
      ```

      **The two divides are different divides** and disagree on a negative product, and the floor to 1 is
      conditional on three things — so a low-rate tick can never be rounded away while a zero-rate tick
      stays at zero. `tools/oracle_multihit.py` drives that exact instruction block: **12 of 12 cases
      agree** with the port.

      The sequence itself is `MultiHitData::MultiHitElement` — up to 160 `OneHit` rows with `mhe_ArrayCnt`
      live, each carrying `oh_HitTimeRate`, `oh_DamageRate`, `oh_AreaStep` and an abstate triple; loaded by
      `mht_Load` from `9Data/Shine/MultiHitType.shn` into `_MultiHitTable` (0x0DA29384).

- [x] **The two "unnamed globals" are IDENTIFIED, and they are not server config.** They are members of
      `?setitemskilleffect@@3VSkillEffect@SetItemData@@A` at 0x1325EDB0 — a `SetItemData::SkillEffect`,
      which is `unsigned long se_Argument[17]`, so 0x1325EDB8 is `se_Argument[2]` and 0x1325EDE4 is
      `se_Argument[13]`. The nearest-symbol lookup that produced "+0x8 / +0x34" was right about the
      symbol and wrong to treat the offsets as a gap: they are field indices.

      It is a **staging buffer for the current object's matched-equipment-SET bonuses**, rebuilt by
      `smo_ply_SetItemEffect` (`ShinePlayer`'s override walks the player's set list at +0x2A7E0, count at
      +0x2A7F4) and accumulated by `siel_AppendEffect`, which does `add [idx*4 + base], value`. So these
      are a per-caster GEAR bonus, not a server-wide rate — a distinction that matters because a server
      config would apply to everyone and this applies only to whoever is wearing the set.

      `smo_SkillBlast` reads five of the seventeen slots: **[2]** scales skill damage and **[13]** scales
      skill hit rate (both permille, both against a 1000 neutral); **[1]**, **[9]** and **[12]** are also
      permille rates combined against 1000 — [12] as `a + slot - 1000` — and are **not** pinned to a
      specific effect yet.

      Read live from `zone00` (pid 248, mapping `0074c000-1550e000`): all seventeen slots hold **1000**,
      i.e. no set bonus staged at rest. That is why the port defaults them to 1000, and it is now a
      measured default rather than an assumed one.

- [ ] **The rest of `smo_SkillBlast`** — 6.6KB, of which this read covers the EngageArgument construction
      (+0x52), the hit/miss rolls through `sdi_DamageRule`, and the damage arithmetic. Target selection
      (`alsst_SkillBlast`, `alsst_SkillBlast_RandomTarget`), the effect container
      (`SkillBlastEffectContainer::sbec_Store` / `sbec_Routine`, which is what makes a sequence land over
      TIME) and `mdt_PostSkillBlast` are not read.
- [x] **Mob weapon selection — READ and PORTED** (`Mob/MobWeaponSelection.cs`).
      `so_mob_SelectWeapon` (0x004AB720) walks the weapon list from the **HIGHEST index DOWN to 0** and
      takes the first that passes, so higher indices are the special attacks and get first refusal; index
      0 catches what none of them wanted. Each candidate clears three gates BEFORE any roll — its skill
      resolves in `SkillDataBox`, the mob's SP covers `ActiveSkillInfo.SP`, and the weapon is off cooldown
      — and only then rolls `well512_GetRandom(1000) <= sm_GetUseWeaponRate(i)`.

      `sm_GetUseWeaponRate` (0x004AA060) reads a **per-instance override vector first** and falls back to
      `MobWeapon.BlastRate` (+0x47), so two mobs of the same kind can disagree about their weights without
      the data changing.

      ⚠️ **The roll is INCLUSIVE**, so a `BlastRate` of 0 is not "never" — a draw of 0 still selects, one
      time in a thousand. And "nothing selected" returns **-1**, not 0, because 0 is a real index.

      ⚠️ **This qualifies the old complaint rather than answering it.** "The simulation only ever uses
      index 0" is CORRECT against a player: `mab_Think` dynamic-casts the target to `ShinePlayer` and
      zeroes the index, which a pre-existing test in this repo already pinned. The descending walk governs
      mob-versus-NON-player attacks. `smo_SkillBlastOption() == 2` (blocked from casting) also forces 0.

- [x] **`ae4m_NextSkill` — READ and PORTED** (`Mob/MobAttackSequence.cs`). The "500-entry sequence" is a
      flat `u16[]` of SKILL IDS at `this+0x04`, and the mob does not own its position in it: the caller
      passes an `int*` step and the function reads `sequence[step]` without advancing it, which is what
      lets two mobs share one sequence.

      Resolving a step to a weapon is a **SEARCH, not an index** — the weapon list is scanned for the
      entry naming that skill, **starting at index 1**, so a sequence can never propose the basic swing.
      A blank record is skipped before its skill is compared.

      ⚠️ **`0xFFFF` is the terminator and "nothing queued", and it is NOT a skill id** — which is exactly
      what leaves skill id 0 free to be a real one.

      There is also a **one-shot override**: with the caller's flag set, `sm_GetNextSkillID` is consulted
      and anything but 0xFFFF is CONSUMED (`sm_SetNextSkillID(0xFFFF)` runs immediately, BEFORE the
      search) — so a script that queues a skill the mob has no weapon for loses it silently rather than
      having it retried. That is the hook for making a boss cast a specific thing next.

      The function returns -1 for a target that is not a `ShineMob`: it opens with a type walk against a
      specific RTTI pointer.

- [ ] **The `sm_SkillExchange_*` predicates** — `HPLow` (0x004BB100), `HPLow_ChangeOrder` (0x004BB600),
      `OutOfRange` (0x004BAEF0), `TargetState` (0x004BB390): when a mob abandons the sequence's current
      entry. `so_mob_SelectWeapon` calls `sm_SkillExchange_OutOfRange` at +0x14D, so at least that one is
      on the selection path. Still placeholders.

**Proof:** `FighterDamageLvl60.pcapng` contains **633 `NC_BAT_SKILLBASH_HIT_DAMAGE_CMD` frames** that have
never been looked at, plus `SKILLBASH_CAST_SUC_ACK` / `CAST_FAIL_ACK` / `HIT_OBJ_START` / `HIT_BLAST`.
Extend `damage_buckets.py` to bucket skill hits by (skill id, rank, state) exactly as it does swings.

## 5. The five damage hooks that no clean swing reaches

Four of the five are now read; the fifth is the item-action machinery in task 1's list.

- [x] **The crit damage bonus** — `2*dmg + dmg*PassiveCriDamageRatePlus/1000` (container +0xCDC). Task 1.
- [x] **`ChargedEffectContainer` attack/defence force rates** — `ApplyChargedEffect`. Task 1.
- [x] **The abstate damage callbacks** — all read and ported. Task 2.
- [x] **`so_ply_DecreaseDmgPassiveSkill`** (0x005651E0) — READ and PORTED
      (`Combat/DecreaseDamagePassive.cs`). A POSITIONAL reduction: the defender takes less from a nearby
      monster that is facing away from them. The base `ShineObject` returns the damage unchanged, so only
      a player is ever protected.

      Four gates first: attacker non-null; `so_GetKind() == 5` (a MONSTER — PvP is unaffected);
      `DMGMinusRate > 0` (container +0x0DAC); both objects have a position. Then:

      ```
      if (DMG_MinusArea² < distanceSquared)  return damage;   // squared, no sqrt
      bearing = ddt_DirectSR(defender - attacker);
      diff    = fold(|attacker.facing - bearing|)             // into 0..90 units
      if (diff <= 45) return damage;                          // inside its forward cone
      return damage - damage * DMGMinusRate / 1000;
      ```

      `DMG_MinusArea` comes from `CSingleDataMap` under that key, read once and cached in a global — so it
      is server-wide, not per character.

      ⚠️ **The angular unit is DIRECTION UNITS of 2° each** (the bearing halved), which is the whole
      difficulty of this function. Confirmed against this repo's existing `sr_degree2sr` port
      (`DamageByAngleTable.DegreesToUnits`, `(deg % 360) * 180 / 360`) and `DamageByAngle`'s `uint16[91]`
      covering 0–90 units = 0–180°. So the fold's modulus of **180 units is a FULL TURN (360°)** and the
      **45-unit cone is 90°**: you are protected when the monster faces more than 90° away from you.

      Reading the units wrong flips the meaning entirely and silently — as plain degrees the fold looks
      like a 90° axis and the rule like a perpendicularity test; as half-degrees the cone becomes 22.5°.
      I went through both before the ported `sr_degree2sr` settled it. **The numeric tests were correct
      under all three readings**, because the arithmetic never changed — only the prose was ever at risk,
      which is exactly why the unit is now stated with its evidence.

- [x] **`EventRun_IncDmgRate` item actions** — done; see task 1's entry for the compounding fold. **All
      five hooks are now read.**

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
- [x] **Bucket misses and blocks** — already done and the item was stale. `damage_buckets.py` keeps two
      accumulators per bucket: `damage` (clean hits, the damage-band check) and `outcomes` (EVERY swing
      whatever its flags, the rate denominator). Measured on `FighterDamageLvl60.pcapng`, split per
      direction because pooling them describes neither: **OUT 234 swings, 0 missed, 0 blocked, 13 critical
      of 234 landed (5.56%); IN 516 swings, 79 missed (15.3%), 15 blocked (2.9%), 0 critical.**
      Rates are now printed as PERCENT — "55.6 permille" reads as a percentage at a glance and has been
      misread as one.
- [x] **Bucket skill hits** — done, and it is the ground truth task 4 was missing.
      `NC_BAT_SKILLBASH_HIT_DAMAGE_CMD` is `{index, caster, targetnum}` + `targetnum * SkillDamage(14)`,
      and the skill id is NOT in it: it comes from `NC_BAT_SKILLBASH_HIT_OBJ_START_CMD` (0x244E)
      `{skill, targetobj, index}`, carried forward by index per conversation.

      ⚠️ **A skill hit's flags are NOT laid out like a swing's.** `SkillDamage::flag` opens with an
      `isdamage` bit that `SWING_DAMAGE::flag` does not have, so every name after it shifts by one —
      critical is bit 1 here and bit 0 there. Using the swing mask marks every damaging skill hit critical
      and reports a rate near 100%.

      ⚠️ **One packet, MANY targets.** An AoE lands on ten mobs in a single frame and each target is its
      own outcome; counting packets rather than records undercounts this capture roughly twofold.

      Measured: **706 attributed skill hits over 11 skills, 54 critical of 706 landed (7.65%)** — against
      5.56% on plain swings in the same capture. 16 hits unattributed (no roster entry, or the opening
      cast not captured) and reported rather than dropped silently. Two skills (143, 60 hits; 261, 2 hits)
      carry `isdamage` CLEAR on every hit — buffs/heals/enchants, reported as such so they do not read as
      a parser hole.

      Rank needs no separate field: `ActiveSkill.shn` rows are per-rank, so a rank is already a distinct
      id. None is invented.
- [x] **Free-stat tables for Dex / Int / Men / Con**, read in full the way Str was — done, see task 1.

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

✅ **SETTLED on a live character.** What puts a number in those slots is the equipped gear, and the
in-game crit figure is the SUM of weapon, jewellery, armour and costume crit plus the MEN free-stat term
(operator, from play).

ArcherZero's container was located in zone02 by matching its whole displayed stat block against the
container's `Total` — WC 89-103, MA 27, AC 83, MR 70, Aim 137, Evasion 123, primaries 54/40/79/26/36, every
one matching — and it holds:

```
Item.Rate[CriDamRate] = 40        <- 4%, the equipped sources summed
Item.Plus[Critical]   = 0
Total[Critical]       = 0         <- slot 23 is NOT the crit path
```

So `CharacterParameters.Equip` writes `ItemInfo.CriRate` into `Item.Rate[CriDamRate]`, and the `Critical`
slot this port used to write is dead. A displacement scan still does not find the WRITER — a loop with a
computed slot index does not appear in one — but what lands there is measured now, not inferred.

⚠️ **A retraction of my own retraction, and the lesson is in the middle step.** I first inferred this
routing from the capture, then RETRACTED it when a write scan came up empty and a model comparison over 234
swings looked ambiguous. Both halves of that were bad: "I cannot find the writer" is not "there is no
writer" — a trap this project has a named rule about — and the capture comparison ignored the 633
`SKILLBASH_HIT_DAMAGE` frames and had no accounting for jewellery, armour or costume crit at all. It could
never have adjudicated the model, and the model did not need adjudicating: one live container answered it.

Modelled as `ParameterCluster.RateFor(source)`, which is the eraser plus the per-cluster clearing. The
556/556 damage check is unaffected — no damage accessor reads those slots.

⚠️ **What is still unread is WHICH code does the clearing.** It is not `c_clear` and not
`so_RecalcEquipParam` — both re-seed from the eraser wholesale. The behaviour is measured on live
containers, so the port is right about the STATE; the mechanism is a loose end.
