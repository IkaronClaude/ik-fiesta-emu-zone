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
      **A cheap check is already available:** Ikaron has 25 free-stat Men and crits 13 times in 671 landed
      swings, so `FreeStatMen[25].CriRate` should come out near 19.

## Untested paths in the damage engine

- [x] **Criticals.** The `2*dmg + dmg*PassiveCriDamageRatePlus/1000` term is ported. The DAMAGE is still
      untested against a capture — every hit checked so far is `flagWord == 0` — so a crit-heavy capture
      filtered on `iscritical` is still wanted, and it would also exercise the crit RATE.
- [x] **Misses, blocks and shield block** are modelled (`ShineMobileObject.SwingDamage`). Counted on the
      capture's 750 swing frames: 658 clean, 64 missed, 15 missed+blocked, 13 critical — 10.5% miss,
      2.0% block, 19 permille crit among the 671 that landed. **Blocked-without-missed never occurs**,
      which is what the code forces. Still to do: bucket those by state, the way clean hits are, so the
      RATES are checked per bucket rather than in aggregate.
- [ ] **⚠️ An eraser-fresh container crits on every swing.** `roe_CriticalRate` sums Item.Rate,
      WeaponTitle.Rate and AbnormalState.Rate at `CriDamRate`, and `ParameterCluster.Rate()` seeds that
      slot with 1000 — so a bare container comes to 3000 against a measured 19. Either the running
      process's rate eraser holds 0 at slot 32, or something zeroes those three slots before combat. The
      offsets are not in doubt (`roe_ShieldBlock` lands on the same layer map), so **do not resolve this by
      reading a different slot**. Read a live player's container at +0x218 / +0x6E0 / +0xA10.
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

- [ ] **Abstate behaviour flags gate the tactic state machine, and nothing honours them.**
      `SubAbstateAction` 19 `SAA_NOMOVE` and 25 `SAA_NOATTACK` write bits into
      `Parameter::Container::flag` (+0xCCE), which the PDB names `cannotmove_stun`,
      `cannotmove_entangle` and `cannotattack`. `SAA_NOATTACK` sets `cannotattack`; `SAA_NOMOVE` sets
      `cannotmove_entangle`, or `cannotmove_stun` when the sub-type at +0x26 is 0x15 or 0x60 — two kinds of
      immobilisation, distinguished.

      They are no-ops for DAMAGE (a stunned mob takes and deals normal damage) and that is all
      `BucketGroundTruthTests` needs, but for 1:1 mob behaviour they are the whole point of a stun.
      `MobActionAttack`, `MobActionChase` and `MobActionTurning` must check them; none does. The capture
      already contains the ground truth — `StaBattleBlowStun` (2) and `StaCommonStun02` (307) are applied
      to mobs, and their movement and swings during those windows are on the wire.

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
