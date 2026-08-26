# Character parameters — how a stat becomes a number

Everything here was read out of `Zone.exe`. Where something has not been read, it says so.

## The shape: clusters, not a stat block

A character's stats are not one table. The server keeps a **`Parameter::Cluster`** — 51 consecutive
`int32` slots — per *source*, and combines them only at the end.

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

> **The eraser CONTENTS cannot be read from the file.** Both globals live in the executable's huge
> uninitialised section (VA 0x0074C000 upward) and are filled at start-up. The identity values come from the
> operators instead, which is a better source: `operator*=` compares each slot against 1000 and skips it, so
> 1000 is provably the no-op, and 0 is provably the no-op for a field-wise add.

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
2. `rep movsd` the plus eraser over the base cluster.
3. Fill the five primaries as **row value + allocated free stat points** (a virtual call per stat).
4. Fill slots 5..14 from **eight virtual methods on the class object**.

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

**`MaxHP` and `MaxSP` are stored columns.** There is no HP curve to reverse-engineer and nothing to infer
from Constitution — any formula would be a guess competing with a number the server already has.

`Wizdom` is present in every table and zero in the ones checked; there is no cluster slot for it.

The set of classes is discovered by globbing this directory. No class list is written into this repo.

## Gear — `ItemInfo.shn`

The item columns name their own half, which is the strongest independent confirmation that the Plus/Rate
split is real:

| goes to Item.Plus | goes to Item.Rate |
|---|---|
| `MinWC` `MaxWC` `AC` `MinMA` `MaxMA` `MR` `TH` `TB` `ShieldAC` `CriRate` `CrlTB` `HitRatePlus` `EvaRatePlus` `MACriPlus` `CriDamPlus` `MagCriDamPlus` | `WCRate` `MARate` `ACRate` `MRRate` |

## Open, and known to be open

1. **The eight per-class virtual stat methods** — base WC/AC/TH/TB/MA/MR/MH/MB. `c_Storepure` fills slots
   5..14 through them, so *each class computes its own* and there is no shared stat-to-attack formula to
   write down. `CharClass`'s own bodies are all folded together at `0x00449600` (`MaxHP` 0x00449610, `MaxSP`
   0x00449660) by Identical Code Folding; the per-class overrides have not been located. Until they are,
   those slots stay **zero** and an unequipped character deals no damage. Tracked by the deliberately red
   `BaseWeaponAndArmourStatsAreUnreadPerClassVirtuals`.

2. **`ItemInfo` has `WCRate`/`MARate`/`ACRate`/`MRRate`, but `c_MakeTotal` never folds Item.Rate into the
   total.** Either the damage formula reads that cluster directly (consistent with how the other unfolded
   layers behave) or the StatModifier-to-cluster-index *naming* inherited from the damage-engine port is off
   by a pair. The plus/rate alternation is proven; which *name* belongs to which pair is not independently
   verified here. Resolving it means finding the equip path that writes the cluster.

3. **`AtkPerAP` / `DmgPerAP`** and the stone columns (`PwrStoneWC`, `GrdStoneAC`, ...) are unused so far.
