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
   down. Four entries have already been closed the wrong way round by ignoring this — see Resolved.
   The fourth was manufactured by my own tool: a whole-image scan that silently covered 24.8% of `.text`
   (see `tools/disasm.py`'s `Code.sweep`). **A negative result is only as good as the sweep behind it.**

---

## 1. Mob skill attacks are not modelled

**Status:** open. The largest remaining hole in mob combat.

`MobWeapon` has 5,815 rows across 2,878 mobs; 1,324 mobs have more than one. The simulation only ever uses
weapon index 0. `AttackElement4Mob` carries a 500-entry skill sequence plus `OutOfRange` / `HPLow` /
`TargetState` change-lists, and `so_mob_SelectWeapon` chooses between them via `ae4m_NextSkill` and a
WELL512 roll against `sm_GetUseWeaponRate`. None of that is ported — the exchange-rule *order* in
`MobActionAttack` is, but its predicates are placeholders.

**Two leads picked up on 2026-08-27.** `SkillDataBox::SkillDataIndex` carries `sdi_DamageRule` at +0x70,
a `RulesOfEngagement *` — so a SKILL brings its own rule, the way a mob's normal attack gets one from
`smo_RulesOfNormalAttack`. That is where `roe_magical` / `roe_physical` enter, and therefore where caster
damage lives for players too. `sdi_Activ` (+0x04) is the `ActiveSkillInfo` row, already loaded here by
`SkillDataBox`; `sdi_AttackDist` (+0x74) is the skill's range. `ActiveSkillInfoServer` carries the
rest of what a skill needs — `AggroPerDamage`, `AbsoluteAggro`, `SkillHitType`, `SkilPyHitRate` /
`SkilMaHitRate`, `DmgIncRate` / `DmgIncValue`, `SwingTime` / `HitTime`.

---

## 2. `Damage.pcapng`'s residual — two of three parts now explained elsewhere

**Status:** open, but no longer the headline. `FighterDamageLvl60.pcapng` and `BucketGroundTruthTests`
overtook it: that capture is bucketed by state, takes the character's attack and defence from the wire,
and needs no reconstruction at all. Where nothing unmodelled is acting, the port is **exact in both
directions** there. So the old capture's residual is now mostly a story about this harness, not the engine.

**Part 1 — incoming was RECONSTRUCTION ERROR, closed.** This file inverted displayed totals to rebuild the
character, backing Con out of DEF but leaving the Str chain inside Dmg. Every displayed stat is
`ftol(roe_Xxx) + freeStat` (`so_mobile_NotifyParameterChange`), so both had to come out. Taking DEF
straight off the wire in the new capture makes incoming match at both ends — `178..220` against a predicted
`177..220`. There was never an incoming mechanism to find.

**Part 2 — outgoing was missing `JobChangeDmgUp`, closed.** Read from `roe_CalcDamage+0x5B2`, verified
under emulation, and confirmed live: outgoing jumps ~1.6x across a single 59→60 level-up while incoming
stays flat (see the Resolved entry).

**Part 3 — what is left is the ANGLE, and it is open.** With the corrected reconstruction, the job-change
multiplier and `ANGLE_MAX=1200`, this capture scores **218/219**. That is not proof: it was reached by
choosing an input, which is exactly how two earlier readings of this same constant went wrong. See
`PcapGroundTruthTests.DeployedAngleMax`, which carries the full history — including that the operator
overturned my "eliminated" verdict, and why their reasoning is better: you do not position to avoid a bonus
that is not live, and `"Forward-facing only now"` marks a change partway through, not the whole window.

**Weapon mastery cannot be the explanation here**, though it was for the new capture: `Bot2433`'s three
`NC_CHAR_CLIENT_PASSIVE_CMD` packets are `00 00`, so its mastery rate is 1000.

**What would settle it:** a capture with deliberate REAR hits, on a server whose angle table is known flat
and in force. Every zone runs one today and `tools/capture_state.py` records it. If rear and frontal hits
do equal damage there, the term's absence is established on the wire instead of argued from a file or a
recollection — and this constant can stop being a judgement call.

**⭐ 2026-09-03: that control now exists, from a capture we already had — and it says FLAT.**
`MageDamageLvl60.pcapng` carries off-angle hits without anyone having set out to make them.
Reconstructing every hit's index (see §4 for the method and its one assumption) gives 83 of 86 hits an
index, spread from 0 to 78, and the correlation between the stock table's rate and the multiplier each hit
actually needs is **-0.011 over 43 clean hits**. Hits at index 0 need up to 1.240; hits at index 73-78,
where the stock table pays 1.126-1.131, need only 1.12-1.13. On THAT deployment, on that date, the angle
term is not acting.

⚠️ **This does not retroactively settle `Damage.pcapng`.** Different session, different date, and the
operator's recollection of positioning during that fight is first-hand evidence about that window which
this measurement cannot touch. `DeployedAngleMax` stays 1000 and keeps its history; what has changed is
that the flat reading now has wire evidence behind it for at least one deployment, where before it had
only a file and a process dump.

---

## 3. Five damage hooks are read but not modelled

**Status:** open. Each is a real branch of `roe_CalcDamage` or `roe_AttackPower`, each was found by reading
the pipeline end to end on 2026-08-27, and none is reached by a clean unbuffed swing — which is why they
cost nothing on the current capture and will cost everything on a buffed one.

| where | what | why it matters |
|---|---|---|
| `roe_CalcDamage+0x466` | `ChargedEffectContainer::cec_AttackForceRate1024` on the attacker vs `cec_DefendForceRate1024` on the defender. Attacker higher: `dmg * (1024 + diff) / 1024`. Defender higher: `dmg * 1024 / (1024 + diff)`. | A premium/charged effect scales damage in 1024ths. Mobs share one static container (`so_ply_ChargedEffectContainer` returns a fixed global), so it is a player-side term in both directions. |
| `roe_CalcDamage+0x4C2` | A critical is `2*dmg + dmg * container[+0xCDC] / 1000`, not `2*dmg`. | This port doubles and stops. The extra term is a real stat slot on the attacker's container. |
| `roe_CalcDamage+0x59E` | `so_ply_DecreaseDmgPassiveSkill(att, dmg)` on the DEFENDER. | The only modelled-nowhere hook that can reduce incoming damage. Identity for a mob. |
| `roe_AttackPower+0x129/+0x155` | `ItemActionObserveManager::EventRun_IncDmgRate` on the attacker's manager AND the defender's, then `GetRateAppliValue` applies the resulting chain of permille rates to BOTH weapon bounds. | Item actions widen or shift the attack range before the roll. Note the defender's manager is consulted too, so a player's own gear can scale the damage they RECEIVE. |
| `roe_CalcDamage+0x60A..+0x801` | Three loops over the abnormal-state list calling per-abstate virtuals with `&damage`. One of them is gated on the attacker's `so_AttackRange > 300`, i.e. a ranged weapon. | Abstates get to rewrite the damage directly. Nothing here models that. |

Skills bring one more: `MiscDataTable::mdt_ArgumentLoad` bsearches a table by skill id and writes
`EngageArgument.damagerate` (+0x1C) and `crirateadd` (+0x20) from it, plus an abstate to apply. It is
gated on `arg->sklinfo` being non-null, so a normal attack never reaches it.

---

## 4. `smo_SkillBlast`'s post-`roe_CalcDamage` cascade — MODELLED 2026-09-03; two inputs still missing

**Status:** the six damage steps are ported and composed; what is left is FEEDING two of them.

`ShineMobileObject::smo_SkillBlast` (0x00581160) is the function that builds the `EngageArgument` for a
skill, calls `roe_CalcDamage` through rules vtable slot 0x1C at +0x939, and then does six more things to
the integer it gets back before it reaches the `SkillDamage` record. `DamageCalculator` stops at
`roe_CalcDamage`, so the port's skill damage was the INPUT to this cascade rather than its output.

`Skill/SkillBlastCascade.cs` now runs all six, in order, with `Skill/SkillSpecialArguments.cs` supplying
the per-skill parameters. Both bucket tests put their predicted bands through it, so its neutrality on the
two captures is demonstrated by construction instead of asserted in prose — 545/545 and 37/37 both survive
the change.

| # | where | what | modelled by |
|---|---|---|---|
| 1 | +0x93B | `dmg = (uint)(dmg * se_Argument[SET_DAMEGERATE]) / 1000` — UNSIGNED divide | `MultiHit.HitDamage`, called through the cascade |
| 2 | +0x94D | `dmg = (dmg * mha_DamageRate) / 1000` — SIGNED, truncating toward zero | as above |
| 3 | +0x97B | the floor at 1, and the `isdamage` CLEAR when the rate is zero | `SkillBlastOutcome.IsDamage` |
| 4 | +0x9A9 | `SS_TARGETHPDOWNDMGUPRATE` — an execute bonus on the target's missing HP | `SkillBlastCascade` |
| 5 | +0xA3C | `SS_DMGDOWNRATE` x the blast's wave counter, capped by `SS_MAXDMGDOWNRATE`. It SUBTRACTS | `SkillBlastCascade` |
| 6 | +0xAAF | `SS_UNDEADTODMG` against a `MobType.Undead` target | `SkillBlastCascade` |
| 7 | +0xC98 | the caster's `so_smo_StaticDamage` slot, which REPLACES the result | `SkillBlastCascade` |

Three things worth keeping from reading it:

- **Step 5 divides by MINUS a thousand.** The magic constant at +0xA5D is `0xEF9DB22D`, which is
  `-0x10624DD3` — the /1000 magic negated, same shift, same truncate-toward-zero fixup. That is what makes
  "damage DOWN rate" go down, and reading the constant as unsigned makes it go up by a factor of 68.
- **Step 4's configured rate cancels out of its own arithmetic** — `((missing*rate)/1000 * dmg) / rate` —
  so it changes nothing except through the truncations it sits between, and a rate of 0 would divide by
  zero. That reads as a bug in the original; the shape is preserved rather than simplified.
- **The names line up with the data and confirm each other.** `LightningWave01` is the skill carrying
  `DMGDOWNRATE` 100 capped at 900, and the counter that multiplies it is `sbe_nLightningWaveCnt` — each
  bounce of the chain loses another 10%, to a floor of 90% off. `HolySmite01..08` carry `UNDEADTODMG`
  150 to 500. `Judge01` carries the execute bonus. Six skills use step 4, eight use step 5, thirteen use
  step 6.

The `SpecialIndex` -> `EnumStruct` map is read out of `sdi_SetArgument`'s 73-entry jump table (0x00584800),
not computed: **the obvious arithmetic is wrong.** `offset = 0x78 + (index-1)*8` holds for nine entries and
then breaks — `SS_THHPUP` (10) writes +0x0F0 where it predicts +0x0C0. Ten indices store nothing at all,
and `SS_DASH` aliases `SS_WARPING`'s slot. Across `ActiveSkill.shn` that matters on real rows: 114 of the
file's (index, value) pairs name an index the table discards.

### What is still missing

1. **Nothing loads a character's set-item damage rate.** `SetItemSkillEffect` models `siel_AppendEffect`'s
   accumulate rule (`arg[index] += Argument - 1000` over a 1000 seed, from `se_Clear`) and the cascade
   consumes the result, but no loader produces the list it accumulates. Both bucket tests pass the neutral
   1000, which is right for both captures — neither character's equipment carries a `SetItemIndex` — and
   wrong for anyone in a set.

   **The chain is now mapped end to end, which is most of the work:**

   ```
   sp_SetItemCheck (0x00510B50, on equipment change)
     -> sic_SetItemDefine(SetItemClassifier, &player.PlayerSetEffect)      0x00510370
          PlayerSetEffect is {ushort effectarray[10]; byte effectnumber} at player+0x2A7E0,
          which is why nothing appears to WRITE the count at +0x2A7F4: it is written through
          this pointer, not by that offset. SetItemClassifier (0x1326AE00) holds
          EffectByPiece[256] and sic_ItemSetPiece[256] -- pieces worn per set type.
     -> smo_ply_SetItemEffect(skillId)                                     0x00510D20
          se_Clear() seeds all 17 slots to 1000, then one siel_AppendEffect per handle.
     -> siel_AppendEffect(effectHandle, skillId)                           0x005104C0
          entry = setitemeffectlist + handle*0x30            (EffectDescription, 48 bytes)
          if (entry->skilllist && !bsearch(&skillId, entry->skilllist, entry->skillnumber, 2))
              return;                                        // null list = applies to EVERY skill
          se_Argument[entry->seteffect] += entry->setargument - 1000;
   ```

   **Two links are still unread, and both are in the DATA path rather than the damage path:**

   - **Who fills `setitemeffectlist` (0x1325EDF8) from `SetItemEffect.shn`.** A whole-image sweep finds
     exactly one code reference to that address, in `smo_ply_SetItemEffect`. ⚠️ That is an argument from
     absence and this file forbids acting on one — the `PlayerSetEffect` case above is the same shape and
     did have a writer, through a pointer argument. Look for a method taking `SetItemEffectList*`.
   - **How `EffectDescription.skilllist` is built from the row's `SkillGroup` / `From` / `To`.** The DATA
     shows two shapes: 222 of 235 `SkillGroup` values are an `InxName` prefix (`IceBolt` ->
     `IceBolt01`..`IceBolt99`, and `From`/`To` are `01`/`99` throughout), and the other 13 are four-letter
     codes that appear in `SkillClassifierA` (`SBBF`, `GDHM`, `HHMM`, `HDMC`, `BBDI`). Which the builder
     uses, and whether it uses both, is an inference from the data and not a reading.

2. **A bucket cannot feed step 4.** The execute bonus needs the target's HP AT THE STRIKE, and a bucket
   aggregates its hits. Both tests now REFUSE such a bucket rather than predicting without the term —
   correct, and it means a future capture containing `Judge01` will report a refusal instead of a
   confident wrong answer. Fixing it means `damage_buckets.py` keeping `restHp` per hit, which it already
   parses.

3. **Two branches are deliberately absent.** The damage REFLECT at +0xAF3 (`Parameter::Container+0xCD2`,
   returning a share of the damage to the caster, range-gated by +0xDA8) and the HP drain at +0xBC0 both
   read the running damage and act on the CASTER. Neither changes what the target takes, so neither is in
   the cascade — but both are real and neither is modelled anywhere.

---


## 5. The capture's character carries base stats its class row does not produce

**Status:** the ARMOUR half is CLOSED (2026-09-04). What is left is smaller and different from what
this section used to claim.

### What this section said, and why it was wrong

It said the rebuild's DEF 327 fell nine short of the capture's 336 because `NC_ITEM_EQUIPCHANGE_CMD`
carries no `+N`, so an upgraded Nature piece contributed armour `ItemInfo` could not see — and pointed at
`NC_CHAR_CLIENT_ITEM_CMD` (0x1047) as the packet that would settle it.

**That is refuted.** Enhancement moves AC and MR and nothing else, and the capture's character is off the
level-60 class row on *every* primary stat:

| | Str | Con | Dex | Int | Men | AC | MaxHp |
|---|---|---|---|---|---|---|---|
| class row 60 | 43 | 138 | 157 | 273 | 186 | — | 1245 |
| + the Mini Phino pet | 45 | 140 | 159 | 275 | 188 | 329 | 1255 |
| capture, at the hits | 47 | 147 | 166 | 288 | 197 | 336 | 1290 |

Armour was never the odd one out. Reading only the AC column made a whole-vector difference look like an
equipment problem, and sent the next step after the wrong packet.

### What the capture proves instead

`MageDamageLvl60.pcapng`'s first `NC_CHAR_CHANGEPARAMCHANGE_CMD` after zone login, **before any equipment
packet arrives**, reports `Str 43, Con 138, Dex 157, Int 273, Men 186, MaxHp 1245, MaxSp 1826, AC 138` —
the level-60 Enchanter class row to the unit, and the rebuild reproduces all eight exactly. Later, wearing
nine items worth 189 armour, it reports `Con 147, AC 336`. So:

```
AC = Con + sum(item AC)          138 = 138 + 0        336 = 147 + 189
```

Two independent points in one session, and the unequipped one is decisive: with nothing worn, reported AC
EQUALS Constitution. **The engine's armour chain is correct, and the nine points of DEF are nine points of
Con.** `AnUnequippedCharacterHasArmourEqualToItsConstitution` pins the login state and
`AnOrcHitsTheCapturesCharacterAsHardAsTheCaptureSaysItDid` now asserts the identity itself rather than a
tolerance around 336.

### What is still open

Why the base stats are elevated at all. The wire rules out the obvious answers:

- **Not a level difference.** All five `NC_CHAR_CLIENT_BASE_CMD` blocks in the capture report level **60**.
  (Level 65's row happens to match Str/Con/Dex exactly, which is a coincidence worth knowing about — it is
  the trap this question is most likely to fall into next.)
- **Not free-stat allocation.** All 93 hits at DEF 336 share one free-stat block, `Int 50 / Men 25` and
  nothing in Str, Con or Dex — yet Str, Con and Dex all moved.
- **Not equipment.** `ItemInfo.shn` has no Str/Con/Dex/Int/Men columns at all.
- **Not set effects.** None of the nine items appears in `SetItemCatalog.SetOfItem`.
- **Not passives.** All 18 learned passives are Staff/Wand mastery, Wisdom (`MaxSP`) and `PowerofLove`;
  none carries a stat column.
- **Not a visible buff.** `damage_buckets.py` records no self-abstate on any of the 93 hits.

**The leading explanation is that it is an ADMIN character** — the capture carries
`NC_ACT_NOTICE_CMD` "Admin level is 100", and `NC_CHAR_STAT_REMAINPOINT_CMD` reports 75 unspent points at
login. Hand-set stats would explain it and are not derivable from any table. **This is a plausible cause,
not a proven one, and it is recorded as such.**

### The pet — found, and how it was missed

The **Mini Phino pet** grants **+2 to every primary stat**, through `GradeItemOption.shn`. The wire shows
it directly: `EQUIP slot 25 <- 30817` is followed 35 ms later by a CHANGEPARAM moving Str 43->45, Con
138->140, Dex 157->159, Int 273->275, Men 186->188, with AC following Con (+2) and MaxHp following it by
five (+10). `EquipmentCatalog` now joins that table and reproduces all seven exactly.

**The operator supplied this from play experience after the analysis here had ruled it out.** The ruling
was: "No Str/Con/Dex/Int/Men columns exist in `ItemInfo.shn` at all. So items cannot add base stats."
The first sentence is true; the second does not follow. `ItemInfo.shn` has no primary-stat column for ANY
item in the game — which is the signature of reading the wrong table, not of the answer being no.
**An absent COLUMN is evidence about a schema; only an absent VALUE, in the right table, is evidence
about the entity.** The same four cosmetics had already caught this project once before, dismissed as
"cosmetics" after a check of their `MinMA` when they carry `CriRate`.

Fixed structurally rather than by resolution: `ItemTableDiscovery` scans every server table for columns
that resolve to item ids or `InxName`s — it finds **52** — and `ItemTableCoverageTests` fails the build
unless every one is named as either modelled or, with a statement about the data, not combat-relevant.
`EquipmentCatalog` reading `ItemInfo.shn` alone had the same blind spot as the reasoning did, so nothing
could have caught it.

### What is still open: a uniform +5% on every stat

With the class row and the pet both modelled the rebuild reaches `Str 45, Con 140, Dex 159, Int 275,
Men 188`. The capture reports `47, 147, 166, 288, 197` — each one the rebuilt value plus
**floor(value x 5%)**:

```
Str  45 + floor( 2.25) = 47      Int  275 + floor(13.75) = 288
Con 140 + floor( 7.00) = 147     Men  188 + floor( 9.40) = 197
Dex 159 + floor( 7.95) = 166
```

Five exact hits from a one-parameter model, and the truncation pins it: 7.95 gives 7, not 8. **The source
multiplies; it does not add.** DEF follows Con, so the remaining armour gap is 7 points and is entirely
this.

Ruled out, each checked rather than assumed:

- **Level** — all five `NC_CHAR_CLIENT_BASE_CMD` blocks report 60. (Level 65's row matches Str/Con/Dex
  exactly by coincidence; that is the trap this question is most likely to fall into next.)
- **Free-stat allocation** — all 93 hits share one block, `Int 50 / Men 25`, nothing in Str/Con/Dex.
- **Remaining equipment** — `GradeItemOption` has a row for the pet and for none of the other eight.
- **`ChargedEffect.shn`** — the four cosmetics carry `EffectEnum 9`, which is 3,692 of 4,546 rows and
  takes only the values 0 and 1: a costume flag. Its rate-bearing enums (13 HPIncrease, 14 SPIncrease,
  15 DIncrease, 10-12 A/D/AD Power) are permille and single-purpose; none touches all five primaries.
- **`MinimonInfo.shn`** — `{MinimonEquipPos, MinimonRole}` only; a pet's stats are in `GradeItemOption`.
- **Set effects, the 18 learned passives, `StateItem.shn`, and the character title** (the client reports
  `CurrentTitle 0, NumOfTitle 0`).
- **The only self-abstate in the capture** — index 291, `StaImmortal`, a GM state. (Index 731,
  `StaE_UserNewbie04`, is on handle 9280, not the player's 9282.)

The character IS an admin — `NC_ACT_NOTICE_CMD` "Admin level is 100" — so a hand-applied buff remains the
leading guess and stays a guess. `TheResidualAgainstTheCaptureIsAUniformFivePercentOnEveryStat` asserts
the shape, so the next session inherits a signature to search for rather than a number to nudge.

### A second thing this turned up

`CharacterParameters.StorePure` adds free-stat points to the base stat 1:1 (`b[Stat.Int] = row.Int +
free.Int`), ported statement for statement from `c_Storepure`. The capture reports base Int **288** with
50 points allocated against a row of 273 — neither 273 nor 323. Either the 0x1035 builder transforms the
value on its way out (which is exactly what the MA pair does — see the closed section below), or the
allocation reaches the base stat by some other route. **Nothing has been changed on the strength of this**;
the ported statement stands until something reads the builder. It matters because `Con.ACAbsoulte` is
`ceil(n/2)` while a 1:1 base-stat add would give `n`, so the two models disagree about what a point of Con
is worth in armour — and no capture to hand allocates Con.

---

# Resolved

## `MageDamageLvl60`'s magical residual — CLOSED 2026-09-03. The wire's MA pair is not the accessor's output.

**37 of 37 clean hits and 8 of 8 criticals now sit inside their predicted bands**, and the whole suite is
green: 548 pass, 0 fail, 4 skip.

`so_mobile_NotifyParameterChange@ShinePlayer` (0x00503C10) does NOT report `roe_MinMA`/`roe_MaxMA` the way
it reports every other stat. Read side by side:

```
MinWC (6)  = ftol(roe_MinWC) + FreeStatStr.WCAbsolute
MaxWC (7)  = ftol(roe_MaxWC) + FreeStatStr.WCAbsolute
MinMA (11) = ftol(roe_MinMA) + FreeStatInt.MAAbsolute - [this+0x10B4]
MaxMA (12) = ftol(roe_MaxMA) + FreeStatInt.MAAbsolute - [this+0x10B8]
AC    (8)  = ftol(roe_AC)    + FreeStatCon.ACAbsoulte
MR    (13) = ftol(roe_MR)    + FreeStatMen.MRAbsolute
```

and `so_RecalcEquipParam@ShinePlayer` (0x004CAB70) fills those two slots by walking the equipped items:
`[+0x10B4] += ItemInfo.MinMA` (+0x93), `[+0x10B8] += ItemInfo.MaxMA` (+0x97), beside `[+0x10A0] += MinWC`
and `[+0x10A8] += AC`. **The magic-attack pair is the only reported stat carrying a third term, and that
term is the equipped weapon's own magic attack.**

**The server subtracts it because `roe_MinMA` counts the weapon twice.** It arrives once through `Chain`
over `Item.Plus[bound]` and again through the scaled item bonus — `WeaponTitle.rate[MAmax] *
Item.Plus[bound] * PassiveSkill.rate[MagicalWeaponMastery] / 1e6`, which at the neutral 1000/1000 is that
same number over again. The display takes one copy off so the client can show the weapon's line
separately; the damage engine keeps both. For the capture's Enchanter it falls out to the unit:

    roe_MinMA = 288 Int + 240 wand + 240 again + 34 WandMastery05 = 802,  reported 802 + 60 - 240 = 622
    roe_MaxMA = 288      + 320     + 320       + 34               = 962,  reported 962 + 60 - 320 = 702

⭐ **The operator was right, and had been for weeks.** "Free stat STR applies like twice in 2 separate
situations, I don't trust you've fully wired INT free stat correctly." The doubling is real; it is the
WEAPON rather than the free stat; and it is on the magical side. Every search inside the formula failed
because **the formula was already correct** — `roe_AttackPower`, `roe_MinMA`, `roe_MR`, `roe_Damage` and
`roe_CalcDamage` had all been verified against the real functions under emulation, and all of them were
right. The error was one layer out, in what this harness believed the wire was telling it.

**How it hid for three sessions.** The physical reconstruction — take the displayed value, subtract the
free stat, treat the rest as the accessor's output — is exactly right, is verified by 545 hits, and is
wrong for magic and only for magic. Every check that compared the port against the server passed, because
the port was never the problem; and every check of an INPUT passed too, because each input was individually
correct. What was wrong was a step that nothing tested: the inversion from the wire back to a container.
The lesson is the one this file keeps relearning in new clothes — **a chain of individually verified links
does not verify the thing they are chained to.**

Two smaller things fell out on the way and are worth keeping:

- **The band has to pin the engine's own coin flip.** `so_ply_JobChangeDamageUp` multiplies by
  `rate + rndbox(0..1)`, so the widest clean outcome uses `rate + 1`. `Through` was letting a seeded
  `Random` decide, which put the ceiling 0.06% low whenever it drew 0 — one whole point of damage, and
  exactly what left `FireBolt08`'s 908 sitting one over a 907 ceiling after everything else was fixed.
- **Criticals are a second sample of the same roll.** `damage_buckets.py` now emits each one individually
  and the magical test predicts a `ForceCritical` band for it. Worth 8 more observations here, and they
  are what pinned `ChainLightning01` tightly enough to prove no constant could ever reconcile it with
  `FireBolt08` — which is what said the miss was an INPUT rather than a coefficient.

**Closed since:** `damage_buckets.py` records the whole equipment map from `NC_ITEM_EQUIPCHANGE_CMD`, and
the test sums `MinMA`/`MaxMA` over every equipped item the way `so_RecalcEquipParam` does. The result is
unchanged at 37/37, which is the point — it is now a reading rather than an approximation that happened to
be right.

⚠️ **And it corrected the picture of this character.** The capture equips NINE items, not the five this
project had been reasoning about all along: the wand, four Nature armour pieces, and four cosmetics
(`Cos_Sakura01_9`, `AngelWing09_4`, `Hat_Rabbitear01_4`, `MiniPino01_7`). They carry no magic attack, so
the damage work stands — but **three of them carry `CriRate` 50, and the operator caught that the crit
chance was wrong because of it.** The character's item critical rate is 180; the wand alone gives 30. Two
of the three are literally named `[Critical]` in `ItemInfo.Name`.

The lesson is the narrow-check one this file keeps relearning: the four extra items were dismissed as
"cosmetics with zero MA" after checking the ONE column the magic-attack question needed. Dismissing an
input requires looking at what it does, not at what you were looking for.


## The job-change catch-up multiplier — CLOSED 2026-08-27

**A player hitting a monster deals `dmg * JobChangeDmgUp / 1000`, and this port applied none of it.**

`roe_CalcDamage+0x5B2` calls the ATTACKER's vtable slot 0xD2C with the defender and the integer damage,
one call before the level gap. For a mob that slot is `return dmg`. For a player it is
`ShinePlayer::so_ply_JobChangeDamageUp` (0x00560E80):

```
if (def == NULL || def->so_ObjectType() != 5 /*monster*/)  return dmg;
row = charClass->cc_array[level <= 150 ? level : 0];        // PrimaryParameter *[151] at charClass+0x10858
if (row == NULL) return dmg;
return (unsigned __int64)dmg * (row->JobChangeDmgUp + rndbox[2].next()) / 1000;
```

`PrimaryParameter::JobChangeDmgUp` is at +0x80 in the PDB and is column **`JobChangeDmgUp`** of
`9Data/Shine/World/Param<Class>Server.txt`. The values say plainly what it is for — a catch-up for having
just changed job:

* base classes (Fighter, Cleric, Mage, Archer, Joker) — flat **1000** at every level;
* first job — **2000** at level 20, decaying to 1190 at 59, back to 1000 at 60;
* second job — **1700** at 60, decaying to 1055 at 99, back to 1000 at 100;
* third job — **1100** at 100, decaying to 1025 at 115, 1000 from 120.

So a character in the first half of a job band hits monsters for up to twice what its stats suggest.

`rndbox[2]` is a shuffled pool of 0s and 1s — `RandomBox`'s constructor fills slot `b` with
`floor(i * b / 16384)` for i in 0..16383 — so the random term is worth 0.08%, not the 24% the capture
residual needed. Ruling that out mattered: a "random damage multiplier" is exactly the kind of thing that
gets assumed to be the answer.

**Verified by running it, not by reading it.** `tools/oracle_jobchange_dmgup.py` drives the real function
under emulation over fourteen input sets — including the `ja` on a 16-bit compare that sends level > 150 to
row 0 rather than clamping to 150, the three early returns, and a 3,000,000 damage that proves the multiply
is 64-bit and the divide unsigned. All fourteen agree with this port.

Applied to `Damage.pcapng`: the captured character is class 8 (Paladin, read off `PROTO_AVATAR_SHAPE_INFO`)
at level 82, which is 1280. Both outgoing cases went from ~20% over the predicted ceiling to under it. The
incoming cases did not move, and could not have — see §2.


## Per-mob targeting policy — CLOSED 2026-08-26

`MobInfoServer.EnemyDetectType` picks the `MobTargetSelector` subclass. All three behaviours are now read
rather than assumed:

- **`MobTargetNoBrain::mts_SelectTarget`** is a call to `mts_InitThink` and nothing else. It never acquires
  a target — not even its attacker. 764 mobs, mostly shopkeepers.
- **`MobTargetBout::mts_SelectTarget`** walks the `MobTargetStruct` hate list with an `mdb_CanIKill` check
  and **never calls `so_AllOfRange`** — it has no sight scan at all, so it only retaliates. 220 mobs,
  including everything a new character meets first (Slime, MushRoom, Imp, Crab).
- **`MobTargetAggresive::mts_SelectTarget`** adds the forward sight scan on top, since it derives from Bout.
  1,872 mobs.

That "Bout is absent from the `so_AllOfRange` caller list" is positive evidence here, not an absence
argument: the list was generated by scanning every call site in `.text`, so being absent from it is a fact
about the call graph rather than about my searching.

Ported as `TargetingPolicy` on the selector — an enum rather than a hierarchy, because only the dispatch
differs. Until this landed, **984 mobs attacked on sight that should not have.**

⚠️ The three rare values (`ED_AGGRESSIVE2`, `ED_AGGREESIVEALL`, `ED_ENEMYALLDETECT`, 22 mobs) have their own
overrides that have NOT been read; they map to Aggressive because they derive from it, which is a floor
rather than a claim.


## The angle question — CLOSED 2026-08-26. The operator was right.

**A mob's detection circle is not centred on the mob.** `ShineMob::so_mob_SightCenter` (0x004ABCD0) seeds
the output with the mob's own position and then calls
`ddt_GetFoward(facing, detectRange * 205 / 512, out)`, pushing the centre **forward along the mob's facing
by about 40% of its detect range**. The base `ShineObject` version is three instructions and returns the
position unchanged, so this is mob-specific.

It is reached through vtable slot **0x8F4**, which is exactly the virtual `MobTargetAggresive::mts_SelectTarget`
calls at +0x3DB — and its return value is the `loc` argument handed to `so_AllOfRange`. So the scan really
is a circle, centred somewhere the mob is not.

With range r, the reach is about **1.40 r ahead and 0.60 r behind** — a front-to-back ratio near 2.3. That
is large enough to be obvious in play, which is exactly how it was reported.

**This project argued the wrong way for weeks.** The reasoning was "the scan is a circle, no angular term
appears, therefore orientation cannot matter". Every step was true except the conclusion: the shape is a
circle and there is no angular term, and detection is still direction-dependent because the circle moves.
The `FanFormSectorArgument` that `so_AllOfRange` accepts — which I had also missed — turned out to be a red
herring for aggro: the call sites pass NULL.

What actually found it was reading `mts_SelectTarget`'s arguments **one at a time** instead of scanning for
a keyword. The `loc` argument was never the mob's position; nobody had looked.

Two consequences fell out while porting it, both previously invisible:

- The simulation kept a second copy of facing on the selector that nothing updated, so directional
  detection silently degraded to a concentric circle. One mob has one facing.
- `mts_GetTopAggroTarget` was never called, so **a mob struck from behind never fought back** — its
  attacker sits outside the forward circle and acquisition found nothing. `mab_Think` calls both.

⚠️ `Direction.Forward` computes cos/sin where the server reads a table built by `ddt_Initialize`; the two
agree to within table rounding. Recorded in docs/UNVALIDATED.md §6.


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

## Where `so_DamagedBy`'s aggro rate comes from — CLOSED 2026-08-27

`so_DamagedBy(attacker, damage, ratePermille, flag)` turns damage into hate as `damage * rate / 1000`
(+0x147). The rate has two sources, and the split is clean:

**An ordinary attack passes the literal 1000.** All four combat call sites push `0x3E8`:
`so_attack+0x117`, `so_smash+0x115`, `so_skillsmash+0x151`, `DamageAbsorbAction::exe+0xCC`. One point of
damage is one point of hate. So the simulation's 1000 is exact rather than an assumption — which is the
part that mattered, since nothing here casts.

**A skill varies it.** `sds_TemplateStore` takes the rate as its 4th parameter (`[ebp+0x14]`, pushed at
+0x19A), and `smo_SkillBlast+0x5BD` builds it from the skill's server row:

```c
const ActiveSkillInfoServer* serv = skill->sdi_ServInf;
absolute += serv->AbsoluteAggro;                                   // +0x43, accumulated flat
rate      = <global at 0x1325EDE0> + serv->AggroPerDamage - 1000;  // +0x3F, relative to 1000
```

`AggroPerDamage` differs from 1000 for **2,580 of 2,791 skills** — `TripleHit` is 9000, nine times the
hate per point of damage, which is what a taunt looks like in this engine. `AbsoluteAggro` is non-zero for
251 and includes **negative** values (down to −2000), a flat hate DROP; `SnearKick01`–`05` climb 750 →
18,000 with `AggroPerDamage` 0, so they are pure flat-threat skills that generate no damage-scaled hate at
all.

**Still unread, and small:** the global at `0x1325EDE0`. It is runtime-allocated, so it is not in the image
and cannot be read statically — it needs a live process or its initialiser traced. Given the `- 1000`, it
is almost certainly a server-wide baseline of 1000, which would make the rate simply `AggroPerDamage`; that
is a lean, not a reading.

A near-miss worth keeping: `so_mobile_MobAggroRate` looks like the answer and is not. It returns a BYTE —
`ShinePlayer`/`ShineMover` → 1, `ShineMob` → `sm_Flag & 1`, base → 0. It is a yes/no "does this object draw
mob aggro at all", not a permille rate.

## The third term of `nextAttackAt` — CLOSED 2026-08-27

`mab_Think` computes `now + AtkDly + swing + <local>`, and the local is
`ShineMob::sm_GetWeaponCastTime()` (0x004B88E0) — **the cast time of the SKILL named on the weapon row**:

```c
ushort skillId = box->weapon[currentIndex].skill;        // _MobWeaponIndex.skill, +0x04
const SkillDataIndex* s = SkillDataBox[skillId];
if (!s) return 0;                                        // the null pointer IS the return value
return s->sdi_Activ->CastTime * 10 / 1000;               // ActiveSkillInfo+0xCB, tenths
```

Two details worth keeping. The cast is added at `+0x8BA`, **after** the three `test/jns/xor` clamps at
`+0x8A3`, so it is not clamped. And it is **not** scaled by the attack-speed rate that swing, hit and delay
all pass through — haste does not shorten a cast.

**Why the question was invisible rather than hard.** `MobWeapon.Skill` is `-` on weapon row 0 of all 2,834
mobs that have weapons, and row 0 is the row `mab_Think` forces when the target is a player. So the term is
zero for every attack a character can receive, and the old two-term interval was not a floor — it was
exact for that case. It starts to matter as soon as mob skills are modelled (question 1).

The join is now loaded (`SkillDataBox` over `ActiveSkill.shn`): all 2,974 weapon rows that name a skill
match a row, none unmatched, and 478 of them carry a non-zero cast time.

## `AggroInitialize` — CLOSED 2026-08-27. It SUBTRACTS, and it is a boss mechanic.

The column is parsed, and its reader is `ShineMobileObject::smo_SwingDamage+0x4B5`, at the tail of a hit
that connected:

```c
if (aggroInitialize > 0)
    attacker->so_mob_DecreaseAggro(target, aggroInitialize);   // vtable +0x704
```

**The direction is the opposite of what the name suggests.** It does not give the victim hate; it takes
hate for that victim OFF the attacker's own list. A mob that lands a blow becomes less interested in whoever
it just hit — which is how aggro rotates instead of locking on. `ShineMob::so_mob_DecreaseAggro`
(0x0042D380) then walks `sm_FamilyList` (+0x24B1), a circular list, applying the same decrease to **every
family member**, so a linked pack sheds together.

It is a **boss** mechanic in practice: 139 of 5,815 weapon rows carry a non-zero value, at 400–1000, and
they are `GobleKing`, `GhostKnight`, `Marlone`, `LegendaryTree` and the KQ bosses. An ordinary field mob has
0 and never rotates.

Two more facts fell out of the same read:

- A **miss** still generates hate: `smo_SwingDamage+0x1D2` does `target->so_mob_AppendAggro(attacker, 1)`.
  Not portable here yet — `roe_HitRate` is not implemented, so nothing in this simulation ever misses.
- Slot `+0x700` is `so_mob_AppendAggro` and `+0x704` is `so_mob_DecreaseAggro`. On `ShinePlayer` both are
  the ICF-folded empty stub, which is the mechanical statement that **players have no hate list**.

Also corrected here: the field at +0x24AE is `sm_CurrentTarget` in the PDB, not "first attacker". Within
`so_DamagedBy` it is only SEEDED (`if (== 0xFFFF) = attacker->handle`), so the behaviour previously
described was right; whatever clears it has not been found, so it is still named for the read behaviour.

## Which rules a normal attack uses — CLOSED 2026-08-27

`MobWeapon.HitType` had been decoded and connected to nothing, and the wizard's 1-kill grind had been
written up as "magic classes cannot fight". Both are now read.

`ShineMobileObject::smo_RulesOfNormalAttack` (`+0x1E74`) is the field. `ShineMob::so_mob_Regenerate+0x543`
sets it from weapon row 0's `HitType`; everything else defaults to `roe_normalPY` from the
`ShineMobileObject` constructor. **A player is always physical, every class** — the only other writer is
`sp_SetRulesOfEngagement`, whose one caller is the GM `&allcritical`. So the wizard result was the game,
not a gap. Ported as `MobParameters.NormalAttackRule`; 334 fightable mobs now attack against MR.

**How it was nearly missed, and the lesson.** The first pass concluded "nothing writes `+0x1E74` except a
GM command" — which was an argument from absence, exactly what rule 3 forbids, and it was produced by a
broken tool rather than by sloppy reasoning. `capstone`'s `disasm()` stops at the first undecodable byte
and returns what it has; on this binary that is 0x004ADFEF, so every whole-image scan in `tools/` had been
seeing **228,684 of 922,891 instructions (24.8%)** while looking exhaustive. The constructor's write sits
at 0x559284, well past the cut. `Code.sweep()` now resynchronises. Every earlier "no callers" answer from
`xref_vcall.py` should be re-run before being trusted.

Left open on this path: `roe_HitRate` and `roe_CriticalRate` are per-rule and neither is implemented, so a
swing in this port never misses and crits only when told to.

## Pinky: physical, not magic — CLOSED 2026-08-26

Was open as a disagreement: the operator recalled Pinkies as ranged **magic** attackers, while the table
declared `HT_PY` with a single weapon row, WC 520–792 above MA 72–110, and range 350.

**The operator checked in game and confirmed physical.** The data was right.

Kept because the shape of the near-miss is worth remembering: a ranged attacker carrying a non-zero magic
attack value is very easy to remember as a magic attacker. Had this been resolved by inference instead of a
check, the tempting move would have been to "fix" the port to match the recollection — and
`MobWeapon.IsMagical` would now be lying about 2,878 mobs to accommodate one.

## LongCaptureNoDc misses by ~10%, on SWINGS as well as skills — OPEN, and probably the fixture

Ran as an independent control on the skill work: a second, unrelated physical capture (levels 5–13,
skills `TripleHit01` / `SeverBone01` / `RedSlash01`) against a model that scores 545/545 on
`FighterDamageLvl60`. It does **not** reproduce that result — 19 inside, 169 over, 119 unpredictable.

**But the same capture's plain SWINGS miss too**: 119 of 354 hits in 35 overshooting buckets, where the
swing check is 510/510 on the level-60 capture. A miss that lands on both paths equally is not in the
skill path; it is in an input they share.

Two candidates, neither yet tested:

- **Level.** `roe_Damage` scales by `(attackerLevel + 1)`, and this capture runs at levels 5–13 where
  being one level low is an 8–17% under-prediction. The observed median overshoot is 7–10%. The level is
  tracked as an absolute from `NC_CHAR_CLIENT_BASE_CMD` re-asserted per login and incremented per
  `NC_BAT_LEVELUP_CMD`; a missed level-up at the front of the capture would do exactly this.
- **Gear the capture never announced.** It starts mid-session: 119 of 307 skill hits and 138 swings are
  refused outright for having no known weapon, so the equipment state is admittedly incomplete. What is
  missing for those is missing for the rest too.

⚠️ **This does not put the 545/545 in doubt, and it must not be used to.** The level-60 capture carries
complete state — every hit is predicted, none refused — while this one cannot say whether the model or
its own inputs are wrong. Worth resolving because a control that only works on one capture is a weak
control; not worth "fixing" the model against until the level and the gear are pinned down first.
