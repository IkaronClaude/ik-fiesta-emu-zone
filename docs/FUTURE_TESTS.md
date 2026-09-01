# Future tests runbook

Things the damage work has NOT exercised. Each is a place where the port could be confidently wrong and
nothing would currently catch it. Ordered roughly by how likely they are to bite.

The pattern to distrust, since it has now bitten twice: **a plausible formula fitted to a handful of
samples.** `FreeStatStr` was "1:1" from two samples that happened to be the two where the record's two
fields agree; it is actually `points + points/5`. Read the whole table, or say out loud that you did not.

## Known-shaky inputs

**The record shapes are now READ, not inferred** (PDB, `ShineCommonParameter::FreeStat*`). Three of the
five are bigger than this file assumed, and two of the extra fields are read by the roll phase:

```
FreeStatStr  4  { Stat u8, WCAbsolute u16, checksum u8 }
FreeStatInt  4  { Stat u8, MAAbsolute u16, checksum u8 }
FreeStatDex  6  { Stat u8, THRate u16, TBRate u16, checksum u8 }
FreeStatCon  8  { Stat u8, ACAbsoulte u16, BlockRate u16, MaxHP u16, checksum u8 }
FreeStatMen  8  { Stat u8, MRAbsolute u16, CriRate u16, MaxSP u16, checksum u8 }
```

`roe_HitRate` reads Dex's THRate AND TBRate; `roe_FreeStatCriRate` reads Men's CriRate. `MaxHP` / `MaxSP`
are where 0x1035's params 16 and 17 pick up their free-stat halves. What remains unread is the TABLES.

- [ ] **`FreeStatCon` — the same trap, unsprung.** Currently `ceil(n/2)`, sampled at four points
      (Con[19]=10, Con[20]=10, Con[21]=11, Con[50]=25) and never exercised: `FighterDamageLvl60.pcapng`'s
      character has **zero** Con allocated, so the term is 0 whatever the table says. Read all 181 entries
      out of the live table at **0x0DA50BD0** the way `FreeStatStr` was read at 0x0DA50BC4 — pointer array,
      and note it is an **8-byte** record, so `BlockRate` and `MaxHP` come out of the same read for free.
      Then capture a character that HAS spent Con points.
- [ ] **`FreeStatDex` / `FreeStatInt` / `FreeStatMen` tables.** Never read at all, and now they have
      callers: `ICombatant.FreeStatDexTHRate` / `FreeStatDexTBRate` / `FreeStatMenCriRate` have to be
      supplied by hand because nothing can compute them. Meanwhile the wire gives the SUM — 0x1035's `Aim`
      is `roe_TH + THRate` and `Evasion` is `roe_TB + TBRate` — so a captured character can be reconstructed
      without them, but a synthetic one cannot.
      **The Men table has a calibration waiting for it:** the operator puts 25 points of MEN at about
      +5%, so `FreeStatMen[25].CriRate` should read near 50. The capture cannot confirm that on its own —
      13 outgoing criticals in 234 landed swings fit the weapon term alone — so the table read is what
      would settle it. See "The critical rate, measured" below.

## Untested paths in the damage engine

- [x] **Criticals, misses and blocks are modelled** (`ShineMobileObject.SwingDamage`) and counted on the
      wire. The numbers, the bug they found and the input still unresolved are below under
      "The critical rate, measured". Still open as a TEST: crit DAMAGE has never been checked against a
      capture -- every hit validated so far is `flagWord == 0` -- so a crit-heavy capture filtered on
      `iscritical` is still wanted.
- [ ] **Magical damage.** `roe_magical` / `roe_normalMA` never exercised — no caster has attacked in any
      capture. Needs a Mage/Cleric capture.
- [ ] **Skills.** `FighterDamageLvl60.pcapng` contains **633** `NC_BAT_SKILLBASH_HIT_DAMAGE_CMD` frames,
      entirely unanalysed; only normal swings (`NC_BAT_SWING_DAMAGE_CMD`) are bucketed. Skills bring their
      own rule via `SkillDataIndex::sdi_DamageRule` and their own `damagerate`/`crirateadd` via
      `MiscDataTable::mdt_ArgumentLoad`.

## Untested table coverage

- [ ] **Level-gap rows other than 1000.** Every gap in every capture so far lands on the flat side. The
      1100/1200/1300/1400/1500 rows (attacking something well below you) have never run.
- [ ] **`JobChangeDmgUp` bands other than 59→60.** The 1000→1700 boundary is confirmed live; the
      **2000 at level 20** first-job band, and the 4th-job 1100→1025 band, are not.
- [ ] **Mastery columns other than `Sword1` / `Axe2`.** Hammer, mace, bow, crossbow, claw, two-hand sword
      and the magical routing (`WeaponType` 3/11 → `MagicalWeaponMastery`) are all unexercised, as is the
      `MstRtTmp` out-of-range fallback.
- [ ] **`SubAbstateAction`s beyond the eight read.** Only 4/18/19/21/25/73/74/81/94 are known; everything
      else makes a bucket unpredictable rather than wrong, which is the right failure but still a gap.

## Mob behaviour — the state machine, not the damage formula

- [x] **Abstate behaviour flags — found their real readers, and the assumption here was wrong.**
      This entry used to say `MobActionAttack`, `MobActionChase` and `MobActionTurning` "must check them;
      none does". The second half was true of this port. The first half is **not true of the server**.

      The flag byte can be reached two ways — `+0xCCE` from the container, and `+0x1C8E` from the object,
      since `so_parameter@ShineMobileObject` is `lea eax,[ecx+0xFC0]`. Scanning the image for both gives
      **exactly two readers in the whole binary**, and neither is a tactic state:

      ```
      so_ReinforceMove@ShineMobileObject+0x90   test byte [edi+0x1C8E], 2   ; cannotmove_entangle, then return
      sp_Schedule_SwingStart@ShinePlayer+0xAF   test byte [eax+0xCCE],  4   ; cannotattack
      ```

      Movement is gated on the MOVE function, so it covers every caller at once — porting the check into
      `MobActionChase` would have been more work and wrong. Attacking is gated in the PLAYER's swing
      scheduler. Both are now ported (`MobActionArgument.MoveToward`, `IShineObject.Flags`).

- [ ] **⚠️ `cannotmove_stun` is written by two actions and read by NOTHING.** `SAA_NOMOVE`'s alternative
      branch and `SAA_AWAY` both set bit 1, and no instruction in the image tests it at either
      displacement. So the two immobilisations the PDB names separately are not both enforced server-side,
      and **how a stunned MOB is actually stopped is still unread — it is not this bit.** `cannotattack`'s
      only reader is a `ShinePlayer` method, so it does not stop a mob either, yet the capture clearly
      applies `StaBattleBlowStun` (2) and `StaCommonStun02` (307) to mobs.
      The port deliberately does NOT gate anything on `CannotMoveStun`; inventing that mechanic is exactly
      the kind of plausible fabrication that is hardest to find later. `AbstateRuntimeTests` pins the
      finding, so if a reader turns up the test fails and gets rewritten.
      **Where to look next:** `MobActionSwingDamage::mab_Think`, `AttackRhythm_Mob`, and the
      `SubAbnormalStateActor` subclasses.

- [ ] **`StaImmortal` (291) is SPAWN INVULNERABILITY, and the state machine has to implement it.**
      Confirmed on the wire in `FighterDamageLvl60.pcapng`: **82 mob handles** carry it, always applied
      exactly one frame after the spawn and released a short time later, and every player login gets it too
      (handles 8214 / 8215 / 8217 at orders 74→113, 289→316, 624→649).

      ```
      handle 7057 (Pinky)  1919 SPAWN -> 1920 LIST ab=291 -> 1978 RESET -> ... 3792 first swing
      handle 6880 (Orc)    2079 SPAWN -> 2080 LIST ab=291 -> 2140 RESET -> ... 4750 first swing
      ```

      Operator's description, to test against: for a PLAYER it blocks being targeted or damaged and blocks
      attacking or casting, but movement is allowed; for a MOB it blocks attacking and casting and ends as
      soon as it takes damage.

      Its sub-state `SubStaKeepTime_Eternal` carries **no actions**, so none of that is implemented through
      `aeo_ParameterEnchant` — it is enforced elsewhere, and that is precisely the code the mob tactic
      state machine needs to mirror. Find it.

      **How it ends — read, after an apparent contradiction turned out to be packet ordering.** Two swings
      in this capture have an attacker still holding 291, which looks like a mob attacking while immortal.
      It is not: the `ABSTATERESET` arrives **2 and 4 frames after the swing**, and two hooks explain why —

      ```
      aeo_Attack   (0x004A42A0)  test byte [element+0x70 -> def +2], 4   ; cancel when the owner ATTACKS
      aeo_Attacked (0x004A4310)  test byte [element+0x70 -> def +2], 8   ; cancel when the owner IS HIT
      ```

      Both stamp the current tick into the element's timer at +0x20, ending the state. So the mob's own
      attack is what ended its invulnerability, and the swing broadcast simply precedes the reset
      broadcast. The operator's account holds; the tracker was reading an ordering artefact.

      Independently, the state also expires on its own `restKeeptime` — 5000 ms in every LIST here — and
      the reset lands ~57 frames after the LIST whether or not a swing happened, which is what a plain
      timer looks like.

      - [ ] **Which struct holds those cancel flags is NOT pinned.** `[element+0x70]` is not an
            `AbnormalStateInfo` (its +2 is `InxName`), so bits 0x04/0x08 live in something else. Name it
            before porting, or the cancel rules will be guesses.
      - [ ] **`damage_buckets.py` never expires an abstate.** It only removes on an explicit
            `ABSTATERESET`, so a state that lapses on `restKeeptime` is held forever. It happens not to
            matter for StaImmortal (no actions) and rarely for combat debuffs (the server re-applies them
            constantly), but `SubStaMoraleDecreaseWC` has a 15 s keeptime and a real WC effect. Frame
            ORDER is not a clock — this needs pcap timestamps, which the decoded dump does not carry.

      It also arrives at **strength 1** while its only table row is **Strength 999**, so the server's
      row-selection rule when the strength does not match is unread. `BucketGroundTruthTests` sidesteps that
      by checking whether ANY row for the sub-state carries an action, which is sound only because none
      does.

- [ ] **Every other non-parameter `SubAbstateAction`.** The eight read so far were chosen because they
      moved damage. The behavioural half of that enum is unexplored and is where mob AI fidelity lives.

## Open questions with a test attached

- [ ] **The angle table.** Take a capture with deliberate REAR hits on a server whose `DamageByAngle` is
      known flat and in force (`tools/capture_state.py` records both). Equal damage front and back settles
      it on the wire; see `PcapGroundTruthTests.DeployedAngleMax` for why it is still open.
- [ ] **The five unmodelled hooks** in `OPEN_QUESTIONS.md` §3 — `ChargedEffectContainer` force rates, the
      crit damage bonus, `so_ply_DecreaseDmgPassiveSkill`, `EventRun_IncDmgRate` item actions, and the
      abstate damage callbacks in `roe_CalcDamage`'s tail. None is reached by a clean unbuffed swing, so
      none has ever been under test.

## Instrument hygiene

- [ ] **Run `tools/capture_state.py --character <name>` beside every capture.** See
      `docs/CAPTURE_PROTOCOL.md`. A capture whose server state was not recorded has already cost this
      project one unanswerable question.
- [ ] **Read the chat first.** One line (`"Forward-facing only now"`) eliminated a hypothesis three
      sessions had been circling, and two more named the residual.

### The critical rate, measured — and the one input still unresolved

**Corrected from the first pass:** the flag counts were pooled across both directions, which is the wrong
denominator for every rate in them. Split by side, `damage_buckets.py` now reports:

```
OUT  234 swings:   0 missed (0.0%),  0 blocked (0.0%),  13 critical of 234 landed (55.6 permille)
IN   516 swings:  79 missed (15.3%), 15 blocked (2.9%),  0 critical of 437 landed ( 0.0 permille)
```

The player never misses this mob tier and is never blocked (mobs carry no shield, so
`roe_ShieldBlock` returns 0 for them); the mobs miss 15.3% of the time and **never crit once in 516
swings**. Pooling those gave "10.5% miss, 19 permille crit", which describes neither side.

**`ItemInfo.CriRate` is already permille on the roll's own scale** — it runs 10..90 across the file, and the
capture's three weapons are Splitter 30, Kaineneceflight 70, Kainenecefury 90. Operator calibration, from
play: a level-50 blue is around 80 (8%), and 25 points of MEN are worth about +50 (5%).

Outgoing criticals per weapon, against those two candidate models:

| weapon | CriRate | landed | crits | measured | `CriRate` | `CriRate + 50` |
| --- | --- | --- | --- | --- | --- | --- |
| 257 Splitter, lv59 | 30 | 79 | 6 | 75.9‰ | | |
| 257 Splitter, lv60 | 30 | 43 | 2 | 46.5‰ | | |
| 40001 Kaineneceflight | 70 | 90 | 2 | 22.2‰ | | |
| 40101 Kainenecefury | 90 | 22 | 3 | 136.4‰ | | |
| **all** | | **234** | **13** | **55.6‰** | **11.9 expected** | **23.6 expected** |

13 observed against 11.9 expected fits the weapon term alone; against 23.6 it does not (Poisson
P(≤13 | 23.6) ≈ 1.4%). **13 events is thin** — the per-weapon column is far too noisy to rank the models on
its own, and the Kaineneceflight window is the one that disagrees with both. So this points at the weapon
term carrying the rate, and it does not settle where the operator's MEN contribution goes.

**What is now fixed.** `CharacterParameters.Equip` was filing `CriRate` into `Item.Plus[Critical]` and
`CrlTB` into `Item.Plus[CriticalTB]`. `roe_CriticalRate` reads neither: it reads `Item.Rate[CriDamRate]`
(+0x218) and `Item.Rate[CriticalTB]` (+0x240), confirmed both by reading the function and independently by
`tools/cluster_xref.py --sym roe_CriticalRate`. A weapon's crit rate did nothing at all. Those Plus writes
stay, because `Total[Critical]` has its own readers and is where the client's displayed figure comes from.

- [ ] **⚠️ THE SEED IS THE OPEN INPUT, and it makes every swing a critical.** `roe_CriticalRate` ADDS three
      rate-half slots (`Item.Rate`, `WeaponTitle.Rate`, `AbnormalState.Rate`, all at `CriDamRate`), and
      `ParameterCluster.Rate()` seeds `CriDamRate` with 1000 — so a container built by
      `CharacterParameters.Build` comes to 3000 before any gear. 234 outgoing swings say the real figure is
      55.6. Two measurements disagree and neither is weak:
      * the rate eraser was read out of a LIVE zone at 0x0DA3FA78 and had 1000 at slot 32;
      * the wire says a level-60 warrior crits 5.6% of the time.

      `c_clear`'s alternation is not in doubt — it is a literal sequence of `rep movsd` from 0x0DA3FB48
      (plus, 51 zeros) and 0x0DA3FA78 (rate), and Item.Rate / WeaponTitle.Rate / AbnormalState.Rate all
      take the rate one. **Corroboration for the wire side:** the rate eraser's zero run starts at
      `CriticalTB` — which is exactly the slot `roe_CriticalRate` SUBTRACTS. A rate slot used additively
      needs a 0 identity, so the zero run looks like it marks "additive rate slots", and `CriDamRate` is
      used additively too.

      **The experiment that settles it:** re-read the 51 dwords at 0x0DA3FA78 out of a running zone
      (`/proc/<pid>/mem`, the technique that settled `FreeStatStr`) and print them against slot NAMES
      rather than indices. If slot 32 really is 1000, the zeroing happens in whatever rebuilds the
      clusters per recalculation, and that is the next thing to read.
      **Do not resolve this by having `roe_CriticalRate` read a different slot** — the offsets are
      confirmed twice and `roe_ShieldBlock` lands on the same layer map.
- [ ] **Then re-check the MEN term.** If the seed turns out to be 0, the weapon term alone predicts the
      capture and the operator's +5% from 25 MEN has to arrive somewhere this port has not modelled —
      `roe_FreeStatCriRate` reads `FreeStatMen.CriRate`, so the table read (see above) would give it
      directly.
