# Character parameters — how a stat becomes a number

Everything here was read out of `Zone.exe`. Where something has not been read, it says so.

## The shape: clusters, not a stat block

A character's stats are not one table. The server keeps a **`Parameter::Cluster`** — 51 consecutive
`int32` slots — per *source*, and combines them only at the end.

**This layout is now READ from the PDB's TPI type stream, not inferred.** `tools/pdb_types.py --struct
"Parameter::Container"` prints it directly:

```
+0x0000  PureCharParam   Cluster          +0x0594  WeaponTitle    {Plus, Rate}
+0x00CC  Item            {Plus, Rate}     +0x072C  PassiveSkill   {Plus, Rate}
+0x0264  ItemPowerRate   {Plus, Rate}     +0x08C4  AbnormalState  {Plus, Rate}
+0x03FC  Upgrade         {Plus, Rate}     +0x0A5C  LastTune       {Plus, Rate}
                                          +0x0BF4  Total          Cluster
```

Each pair is an anonymous struct of two `Parameter::Cluster`s named literally `Plus` (+0x00) and `Rate`
(+0xCC). `Parameter::Cluster` is 204 bytes and its 51 `int` fields are exactly the `Stat` enum, in order,
misspellings included (`PhisycalWeaponMastery`, `ResistDeaseas`).

The rest of this section records how the same layout was worked out BEFORE the type stream was parsed. It is
kept because the method generalises to the many structures whose types are not this convenient, and because
it agreed with the declaration in every particular:

`Parameter::Container::c_clear` (0x0043C370) seeds fifteen clusters at a stride of `0xCC` (51 dwords),
each from one of two eraser templates. **Which eraser it uses is what identifies the cluster:**

| offset | eraser | cluster | | offset | eraser | cluster |
|---|---|---|---|---|---|---|
| +0x000 | plus | base | | +0x594 | plus | WeaponTitle.Plus |
| +0x0CC | plus | Item.Plus | | +0x660 | rate | WeaponTitle.Rate |
| +0x198 | rate | Item.Rate | | +0x72C | plus | PassiveSkill.Plus |
| +0x264 | plus | ItemPowerRate.Plus | | +0x7F8 | rate | PassiveSkill.Rate |
| +0x330 | rate | ItemPowerRate.Rate | | +0x8C4 | plus | AbnormalState.Plus |
| +0x3FC | plus | Upgrade.Plus | | +0x990 | rate | AbnormalState.Rate |
| +0x4C8 | rate | Upgrade.Rate | | +0xA5C | plus | LastTune.Plus |
| | | | | +0xB28 | rate | LastTune.Rate |
| | | | | +0xBF4 | — | the total |

One base cluster, then seven `(Plus, Rate)` pairs.

> **The eraser CONTENTS cannot be read from the file** — both globals live in the executable's huge
> uninitialised section (VA 0x0074C000 upward) and are filled at start-up, so the image holds nothing.
>
> They were read out of a **running zone server** instead (`/proc/<pid>/mem`; Wine maps the image at its
> preferred 0x400000, so static addresses apply directly):
>
> - `parameter_eraser_plus` (0x0DA3FB48) — 51 zeros.
> - `parameter_eraser_rate` (0x0DA3FA78) — **1000 except slots 42..48, which are 0.** That run is
>   `CriticalTB`, `RegistNone`, `ResistPoison`, `ResistDeaseas`, `ResistCurse`, `ResistMoveSpdDown`,
>   `ResistGTI`.
>
> Before that read, this document inferred *uniform* 1000 from `operator*=` skipping on 1000. Right in
> general, wrong in the tail — and it matters, because `*=` skips only on exactly 1000, so a 0 in a rate slot
> multiplies that stat by **zero** rather than leaving it alone.

## The two operators

- **`Cluster::operator+=`** (0x004C9040) — field-wise addition, unrolled over all 51 slots.
- **`Cluster::operator*=`** (0x004C9160) — field-wise **permille** scaling, unrolled the same way:
  - a slot of exactly `1000` is **skipped** (`cmp eax, 0x3E8; je`), not multiplied and divided;
  - otherwise `dst = dst * rate / 1000`, via the `0x10624DD3` / `sar 6` magic divide plus the
    `shr eax,31; add eax,edx` correction — a signed divide that **truncates toward zero**. C# integer
    division does the same, so the port is a translation, not a reproduction of the idiom.

## Combining: `c_MakeTotal` (0x004C9A40)

**The order is the formula.** Addition and truncating permille scaling do not commute.

```
total  = 0                       total += Upgrade.Plus
total += base                    total += PassiveSkill.Plus
total += Item.Plus               total += AbnormalState.Plus
total *= ItemPowerRate.Rate      total += LastTune.Plus
total *= AbnormalState.Rate      total *= LastTune.Rate
total *= PassiveSkill.Rate
```

then the **first five slots only** are floored at 1.

Two consequences worth stating plainly:

- **Gear's flat bonus is scaled by buffs; an upgrade's is not.** Item.Plus goes in before the rate steps,
  Upgrade.Plus after. That asymmetry is why this is a ported sequence and not a tidy sum.
- **Five clusters never reach the total**: Item.Rate, ItemPowerRate.Plus, Upgrade.Rate, and both
  WeaponTitle halves. They are not dead — the damage formula reads several layers directly rather than
  reading the total. A stat system that collapsed everything into one number would lose them.

The cross-check on the whole mapping: every `+=` above lands on a plus-seeded cluster and every `*=` on a
rate-seeded one. Ten operations, no exceptions. A wrong assignment would have mismatched.

## Base stats: `c_Storepure` (0x00517B60)

Called from `ShinePlayer::so_allparametercalculate` (0x0054F190), which is only two steps: rebuild the base
cluster, then `c_MakeTotal`.

1. Get the level (a **byte**), clamp against `0x96` = **150**, and index the class's per-level row array at
   `charClass + 0x10858`. Above the cap it falls back to row 0.

   The PDB names that array: **`CharClass::cc_array`, a `PrimaryParameter *[151]`** — 151 entries for levels
   0..150, which is where the `0x96` clamp comes from. `PrimaryParameter` is the row, and it confirms every
   offset derived below:

   ```
   +0x00 level        +0x0C intelligence   +0x18 mentalpower   +0x74 LevelHP  (unsigned short)
   +0x04 strength     +0x10 wizdom                             +0x76 LevelSP  (unsigned short)
   +0x08 constitution +0x14 dexterity
   ```

   `LevelHP`/`LevelSP` are the table's `MaxHP`/`MaxSP` columns, and their `unsigned short` type is why
   `CharClass::MaxHP` reads them with `movzx eax, word ptr [ecx+0x74]`.
2. `rep movsd` the plus eraser over the base cluster.
3. Fill the five primaries as **row value + allocated free stat points** (a virtual call per stat).
4. Fill slots 5..14 from **eight virtual methods on the class object** — see below; they all return 0.
5. Write **1000** into MoveSpeed, HPRecover and SPRecover (`mov eax, 0x3E8`), then explicitly zero slots
   22..31 (CastingTime through MagCriDam), and stop. **It never touches the MaxHP or MaxSP slots.**

### The eight weapon/armour virtuals all return zero

`CharClass::WC`, `::AC`, `::TH`, `::TB`, `::MA`, `::MR`, `::MH`, `::MB` resolve to the *same address*,
0x00449600, and the entire body there is:

```asm
xor eax, eax
ret 8
```

They share an address because **Identical Code Folding** merges functions with byte-identical bodies — eight
`return 0`s collapse into one. (Same mechanism as the universal empty-body function at 0x00549070, just with
a different body.)

And **no player class overrides them** — every one of the 27 player-class vtables holds `0x449600` in slots
0..7 (`tools/vtables.py --family CharClass --overrides`). So this is not an unread gap: **a player's base
weapon and armour values ARE zero, and every point of them comes from equipment.**

The complete set of stat virtuals, all twelve:

| slot | method | overridden by |
|---|---|---|
| 0–7 | `WC` `AC` `MA` `MR` `TH` `TB` `MH` `MB` | nobody — all folded at 0x00449600, `return 0` |
| 8 | `MaxHP` | `CharClassMob` (0x004496F0) |
| 9 | `MaxSP` | `CharClassSentinel` **and `CharClassSavior`** (both 0x0064F610) |

`CharClassMob::MaxHP` ignores the cluster entirely and reads the mob's own info record at `+0x46`.
The Sentinel/Savior `MaxSP` is `mov eax, 1; ret 8` — a flat 1 SP, whatever the level.

> ⚠️ **Do not enumerate overrides by symbol search.** Grepping the PDB for `?MaxSP@CharClass*` reports only
> Sentinel: ICF folded Savior's byte-identical body to the same address and only one name survives there.
> A symbol search also answers by *absence*, which cannot distinguish "inherits the base slot" from "no
> symbol was emitted". Reading the vtable slot finds both classes, because a slot holds an address whether
> or not anything was named after it.

### MaxHP and MaxSP are computed, not stored

```
MaxHP = row.MaxHP + (cluster.Con - row.Constitution) * 5
MaxSP = row.MaxSP + (cluster.Men - row.MentalPower) * 5
```

Because `c_Storepure` sets `cluster.Con = row.Constitution + freeStatPoints`, the second term is exactly the
player's **spent Constitution points, worth 5 HP each**. The row's `MaxHP` is read as a *word* from `+0x74`,
which is column 29 — and column 29 is `MaxHP`, typed `Word` in the header. Offset and schema agree, which is
what confirms the column identification rather than assuming it.

### The Dex/Int crossover

The primaries are stored in *cluster* order, which is **not** the table's column order:

| cluster slot | 0 Str | 1 Con | 2 **Dex** | 3 **Int** | 4 Men |
|---|---|---|---|---|---|
| row offset | +0x04 Strength | +0x08 Constitution | +0x14 Dexterity | +0x0C Intelligence | +0x18 MentalPower |

Dexterity and Intelligence swap places. Cleric at level 1 (Int 1, Dex 3) makes a swapped mapping visible
rather than coincidental, which is what `DexterityAndIntelligenceLandInTheRightClusterSlots` pins.

## The class tables — `9Data/Shine/World/Param<Class>Server.txt`

27 files, one per class, one `#record` row per level. 34 columns:

```
Level, Strength, Constitution, Intelligence, Wizdom, Dexterity, MentalPower,
SoulHP, MAXSoulHP, PriceHPStone, SoulSP, MAXSoulSP, PriceSPStone,
AtkPerAP, DmgPerAP, MaxPwrStone, NumPwrStone, PricePwrStone, PwrStoneWC, PwrStoneMA,
MaxGrdStone, NumGrdStone, PriceGrdStone, GrdStoneAC, GrdStoneMR,
PainRes, RestraintRes, CurseRes, ShockRes, MaxHP, MaxSP, CharTitlePt, SkillPwrPt, JobChangeDmgUp
```

**`MaxHP` and `MaxSP` are stored columns**, so there is no HP *curve* to reverse-engineer — but they are not
the final answer either: `CharClass::MaxHP` adds 5 per spent Constitution point on top. See above.

`Wizdom` is present in every table and zero in the ones checked; there is no cluster slot for it.

The set of classes is discovered by globbing this directory. No class list is written into this repo.

## Gear — `ItemInfo.shn`

The item columns name their own half, which is the strongest independent confirmation that the Plus/Rate
split is real:

| goes to Item.Plus | goes to Item.Rate |
|---|---|
| `MinWC` `MaxWC` `AC` `MinMA` `MaxMA` `MR` `TH` `TB` `ShieldAC` `CriRate` `CrlTB` `HitRatePlus` `EvaRatePlus` `MACriPlus` `CriDamPlus` `MagCriDamPlus` | `WCRate` `MARate` `ACRate` `MRRate` |

## Mobs — `c_StoreMob` (0x0043C550)

A mob's base cluster comes from `MobInfoServer`, and the function is the structural twin of `c_Storepure`:
seed from the plus eraser, write primaries and defences, then the *same* tail — 1000 into MoveSpeed,
HPRecover and SPRecover, slots 22..31 zeroed.

| cluster slot | 0 Str | 1 Con | 2 Dex | 3 Int | 4 Men | 7 AC | 9 TB | 12 MR | 14 MB |
|---|---|---|---|---|---|---|---|---|---|
| `MobInfoServer` | +0x50 | **+0x54** | **+0x52** | +0x56 | +0x58 | +0x25 | +0x27 | +0x29 | +0x2B |

**The Con/Dex crossover is here too.** `MobInfoServer` declares Str, Dex, Con, Int, Men in that order, but
the cluster's order is Str, Con, Dex, Int, Men — so the original reads +0x54 into slot 1 and +0x52 into
slot 2. Copying the file's order across would silently swap every mob's Constitution and Dexterity.

**`WCmin`, `WCmax`, `TH`, `MAmin`, `MAmax` and `MH` are written as ZERO**, not skipped. A mob's attack values
are not stat-cluster entries — they live in `MobWeapon`.

`CharClassMob::MaxHP` (0x004496F0) returns `MobInfo[+0x46]` and never touches the cluster: unlike the
player's version there is no Constitution term. A mob's HP is simply what the table says.

### The three tables

| file | rows | what it carries |
|---|---|---|
| `MobInfo.shn` | 2,878 | `ID`, `InxName`, `Name`, `Level`, `MaxHP` (+0x46), walk/run speed, size, types |
| `MobInfoServer.shn` | 2,878 | `AC` `TB` `MR` `MB`, the five primaries, `MonEXP`, detection, resistances, `MaxSP` |
| `MobWeapon.shn` | 5,815 | one row per ATTACK: `MinWC`/`MaxWC`/`TH`, `MinMA`/`MaxMA`/`MH`, `Range`, timings |

`MobInfo` and `MobInfoServer` join on `ID`; `MobWeapon` joins on `InxName`, which is also the key the
`MobRegen` spawn tables use. A mob may have several weapon rows; the ordinary swing is the one whose
`Skill` is `-`.

### Swing timings — read from `mab_Think`, not guessed

`MobActionAttack::mab_Think` (0x004BBA00) is the only function that reads all four timing columns, and it
settles what each one is. **All of them are converted to tenths of a second** (`× 10 / 1000`) — the
resolution the zone's timed logic runs at.

```
atkSpd     = AtkSpd != 0 ? AtkSpd : 1
normalised = SwingTime * 128 / atkSpd            floored at 1
swing      = (SwingTime/100) * 128 / normalised
swing      = swing * AbnormalState.Rate[AttSpeed] / 1000
hit        = (HitTime/100)  * AbnormalState.Rate[AttSpeed] / 1000
delay      = (AtkDly/100)   * AbnormalState.Rate[AttSpeed] / 1000
```

then, with each clamped at zero:

```
nextAttackAt = now + delay + swing + <one further term, unresolved>
masd_SetDelay(MobActionSwingDamage, swing - hit)
mawse_SetNextAction(MobActionWaitSkillEnd, hit)
```

Three things fall out, and two of them contradict earlier readings in this document:

- **`AtkDly` is an extra delay ADDED ON TOP of the swing**, not the interval. This is why `AtkDly`
  exceeding `SwingTime` in 1,387 of 2,841 rows was never the contradiction it appeared to be: they add.
- **`AtkSpd` is the real swing duration.** The two `128` scales cancel algebraically, leaving
  `swing ≈ AtkSpd / 100`; `SwingTime` is only the reference it is normalised against. That is why the two
  are equal for most mobs and differ only for ones deliberately quickened or slowed. They are *not*
  collapsed in the port — each step truncates and the intermediate floor-at-1 is observable, so the
  two-step form is kept (it matches `AtkSpd/100` in 2,242 of 2,263 rows; the 21 exceptions are the point).
- **`HitTime` is the damage offset within the swing**, confirmed by the `masd_SetDelay(swing - hit)` /
  `mawse_SetNextAction(hit)` pair.

`AbnormalState.Rate[AttSpeed]` is container offset `+0xA18`, so a haste or slow scales all three together.

The complete sum is at `mab_Think+0x8B5`:

```c
if (swing < 0) swing = 0;  if (hit < 0) hit = 0;  if (delay < 0) delay = 0;   // +0x8A3
nextAttackAt = delay + swing + sm_GetWeaponCastTime() + clockwatchNow;        // +0x8B8..+0x8C3
```

- **The fourth term is the weapon row's SKILL cast time** — `sm_GetWeaponCastTime` (0x004B88E0) looks the
  row's skill up in `SkillDataBox` and returns `sdi_Activ->CastTime * 10 / 1000`, or 0 on a miss.
- It is added **after** the three clamps, so it is not clamped; and it does **not** pass through
  `AbnormalState.Rate[AttSpeed]`, so **haste does not shorten a cast**.
- It is zero for every attack a player can receive: `MobWeapon.Skill` is `-` on weapon row 0 of all 2,834
  mobs, and row 0 is the row forced against a player. It begins to matter with mob skills.

## Not everything a map spawns is an enemy

`MobInfo.Type` is a `MobType`, and gathering nodes come out of the same `MobRegen` machinery as monsters:

```
0 HUMAN  1 MAGICLIFE  2 SPIRIT  3 BEAST  4 ELEMENTAL  5 UNDEAD  6 NPC
7 OBJECT  8 MINE  9 HERB  10 WOOD  11 NONAME  12 NOTARGET  13 NOTARGET2
```

**Ten of Uruga's twenty spawn types are not enemies** — `MUSHROOM7/8/9` and `HERB7/8/9` are `MT_HERB`,
`WOOD7/8/9` are `MT_WOOD`, plus a present box. The level-2 enemy called `MushRoom` is a *different entry*
and Uruga never spawns it. A simulation that spawns the map naively has its character walking up to a
mushroom and swinging at something it can never kill.

### Which rules a normal attack resolves through

`ShineMobileObject::smo_RulesOfNormalAttack` (`+0x1E74`) holds a pointer to one of the eight
`RulesOfEngagement` singletons, and it governs the WHOLE swing: `smo_SwingDamage` calls `roe_HitRate`
(slot 6), `roe_HitRateByGlobalAction` (slot 11) and `roe_CalcDamage` (slot 7) through it.

Every object starts physical — `ShineMobileObject::ShineMobileObject+0xC4` writes `&roe_normalPY`. A mob
then overwrites it once, at regeneration:

```c
// ShineMob::so_mob_Regenerate+0x543
const MobDataBoxIndex* box = mob->sm_MobDataBox;         // +0x1F90
if      (box->weapon == NULL)                  roe = &roe_normalPY;   // no weapon array
else if (box->weapon[0].weapon == NULL)        roe = &roe_normalMA;   // (counterintuitive, but measured)
else if (box->weapon[0].weapon->HitType != 0)  roe = &roe_normalMA;   // HitType at MobWeapon+0x6D
else                                           roe = &roe_normalPY;
```

Three consequences:

- **Weapon row 0 decides for the whole mob**, once, at spawn. 304 mobs are physical at row 0 and magical
  further down the list; none of them ever swings magically at a player.
- **The test is `!= HT_PY`, not `== HT_MA`.** `HT_NONE` also selects magic. 708 mobs carry it at row 0 —
  629 not fightable, the rest props with WC 0-0 — so nothing behaves differently for it today.
- **A player is ALWAYS physical, every class.** The only writer that could change it for a player is
  `sp_SetRulesOfEngagement`, whose one caller is the GM command `&allcritical`. Caster damage comes from
  skills, not from the auto-attack.

Of the 2,059 fightable mobs with a weapon row, **334 attack magically**: resisted by MR rather than AC,
and unblockable, since `RulesOfEngagementNormalMA` leaves `roe_ShieldBlock` at the base return-0 stub
where `NormalPY` overrides it.

Read `HitType` rather than inferring it from MA exceeding WC. Mobs that really are `HT_MA` do carry MA far
above WC (GoblinMage: WC 19–30, MA 273–415), but the converse fails: `Pinky` has MA 72–110 alongside WC
520–792 and is declared `HT_PY` — which the operator confirmed in game.

## Open, and known to be open

1. **Does anything add the cluster's own MaxHP slot?** `CharClass::MaxHP` returns
   `row.LevelHP + (Con - row.constitution) * 5` and never reads `cluster[MaxHP]`, yet gear with a flat +HP
   bonus has to land somewhere. Either a caller adds the slot afterwards, or +HP gear works differently. The
   callers have not been traced. (The cluster also has a second `MaxHP_2` slot whose purpose is unknown.)

2. **The container's second tier is entirely unmodelled.** Past `Total` at +0xBF4 there are ~20 more fields
   that are not clusters and never pass through `c_MakeTotal`:

   ```
   DotDamagePlus  SPRate  RangeEvasion  flag  MissPercentFix  DamageReflection
   ChangeAbilityInfo  HealRate  PassiveBuffKeepTimeUPRate  PassiveHealRate
   PassiveCriDamageRatePlus  PhysicalImmuneRate  MagicalImmuneRate  RangeOver  DMGMinusRate
   PassiveHPDownRate{WCMin,WCMax,MAMin,MAMax,AC,MR}  PassiveMovingTBPlus
   ```

   The `PassiveHPDownRate*` group is typed `Parameter::ChangeByConditionParam` — stats that scale with how
   low your HP is. None of this is ported.

2. **`ItemInfo` has `WCRate`/`MARate`/`ACRate`/`MRRate`, but `c_MakeTotal` never folds Item.Rate into the
   total.** The "or the naming is off by a pair" half of this is **answered: it is not.** The PDB declares
   the container's pairs in exactly `StatModifier` order, so `Item.Rate` really is a cluster that never
   reaches the total. What remains is the behavioural half — the damage formula must read it directly, and
   that path has not been traced.

3. **Where `MobWeapon` enters the damage formula.** `c_StoreMob` leaves a mob's WC/MA/TH/MH slots at zero
   and nothing folds `MobWeapon` into a cluster, so a mob's attack values are read from somewhere else at
   attack time. Until that path is traced the simulation rolls mob damage between `MinWC` and `MaxWC`
   directly — the mob's real attack input, but it means **the defender's AC is not applied to mob damage**.
   Player damage does go through the full formula.

4. **`AtkDly`** — not an interval (see the mob section); actual meaning unread.

5. **`AtkPerAP` / `DmgPerAP`** and the stone columns (`PwrStoneWC`, `GrdStoneAC`, ...) are unused so far.
