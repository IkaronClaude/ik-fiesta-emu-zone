# `SubAbstateAction` — what every abnormal-state action does

Every buff, debuff, poison and stun in Fiesta reaches a character's stats through one function:
`AbnormalStateContainer::AbstateElementInObject::aeo_ParameterEnchant` at **0x004079F0** in `Zone.exe`.
`SubAbState.shn` gives a sub-state up to four `(ActionIndex, ActionArg)` pairs, and each `ActionIndex` is a
`SubAbstateAction` — one case of the switch below. This table is that switch, read out of the binary.

**120 actions, 72 distinct handler bodies, 71 with a container effect, 49 with none.**

## Where this comes from

The dispatch is an ordinary compiler switch, so it can be walked rather than guessed at:

```
action = element[0x10 + i * 0x24]          ; i = 0..3, the four action slots
arg    = element[0x14 + i * 0x24]
ebx    = action - 1
if (ebx > 0x77) goto done                  ; so the valid range is 1..120
ebx    = byte [ebx + 0x408320]             ; 0x78 one-byte case indices
jmp    [ebx * 4 + 0x408200]                ; the 72 distinct handler bodies
```

Each handler is straight-line and does one of three things: add or subtract into a stat slot of the
container's **AbnormalState** cluster, write one of the container's named second-tier fields, or set a bit
in `Parameter::Container::flag`. The names in the table are the PDB's own `SubAbstateAction` enum — nothing
here is a nickname.

Regenerate with `python tools/abstate_actions.py --markdown`.

## How to read the Effect column

| Notation | Meaning |
| --- | --- |
| `+ WCmin plus` | adds the action's argument to `AbnormalState.Plus[WCmin]` — a flat bonus |
| `- AC rate` | subtracts it from `AbnormalState.Rate[AC]` — a permille scale, identity 1000 |
| `MissPercentFix +` | adds to a named field past the clusters, not to a stat |
| `flag cannotattack` | sets a bit in `Parameter::Container::flag` (+0xCCE) — behaviour, not a number |
| `_none_` | the handler IS the shared epilogue: no container effect at all |

Two things worth knowing before using the numbers:

- **`plus` and `rate` are different halves and combine differently.** `Parameter::Container` keeps a flat
  half and a permille half per source; `c_MakeTotal` adds the plus halves and multiplies the rate ones, and
  the rate halves' identity is 1000, not 0. A `rate` action of 200 is +20%, not +200 points.
- **Weapon and magic actions touch BOTH bounds.** `SAA_WCPLUS` writes `WCmin` and `WCmax` together, which is
  why a weapon debuff shifts the whole damage range instead of squashing it.


| # | `SubAbstateAction` | Effect | Handler |
| ---: | --- | --- | --- |
| 1 | `SAA_STRRATE` | + `Str` rate | `0x00407A8D` |
| 2 | `SAA_STRPLUS` | + `Str` plus | `0x00407A9E` |
| 3 | `SAA_WCPLUS` | + `WCmax` plus, + `WCmin` plus | `0x00407AC0` |
| 4 | `SAA_WCRATE` | + `WCmax` rate, + `WCmin` rate | `0x00407AED` |
| 5 | `SAA_ACPLUS` | + `AC` plus | `0x00407B74` |
| 6 | `SAA_ACRATE` | + `AC` rate | `0x00407B85` |
| 7 | `SAA_DEXPLUS` | + `Dex` plus | `0x00407BB8` |
| 8 | `SAA_TBPLUS` | + `TB` plus | `0x00407BDA` |
| 9 | `SAA_TBRATE` | + `TB` rate | `0x00407BEB` |
| 10 | `SAA_THPLUS` | + `TH` plus | `0x00407C1E` |
| 11 | `SAA_THRATE` | + `TH` rate | `0x00407C2F` |
| 12 | `SAA_INTPLUS` | + `Int` plus | `0x00407C62` |
| 13 | `SAA_MAPLUS` | + `MAmax` plus, + `MAmin` plus | `0x00407C73` |
| 14 | `SAA_MENTALPLUS` | + `Men` plus | `0x00407D27` |
| 15 | `SAA_MRPLUS` | + `MR` plus | `0x00407D49` |
| 16 | `SAA_MRRATE` | + `MR` rate | `0x00407D5A` |
| 17 | `SAA_SHIELDAMOUNT` | _none_ | `0x004081BB` |
| 18 | `SAA_SHIELDACRATE` | + `ShieldAC` rate | `0x00407D8D` |
| 19 | `SAA_NOMOVE` | flag `cannotmove_entangle`, flag `cannotmove_stun (alt)` | `0x00407D9E` |
| 20 | `SAA_SPEEDRATE` | + `MoveSpeed` rate | `0x00407DCB` |
| 21 | `SAA_ATTACKSPEEDRATE` | + `AttSpeed` rate | `0x00407DED` |
| 22 | `SAA_MAXHPRATE` | + `MaxHP` rate | `0x00407E0F` |
| 23 | `SAA_MAXSPRATE` | + `MaxSP` rate | `0x00407E31` |
| 24 | `SAA_DEADHPSPRECOVRATE` | _none_ | `0x004081BB` |
| 25 | `SAA_NOATTACK` | flag `cannotattack` | `0x00407E42` |
| 26 | `SAA_TICK` | _none_ | `0x004081BB` |
| 27 | `SAA_DOTDAMAGE` | _none_ | `0x004081BB` |
| 28 | `SAA_CONHEAL` | _none_ | `0x004081BB` |
| 29 | `SAA_CASTINGTIMEPLUS` | _none_ | `0x004081BB` |
| 30 | `SAA_HEALAMOUNT` | _none_ | `0x004081BB` |
| 31 | `SAA_POISONRESISTRATE` | + `ResistPoison` rate | `0x00407E4E` |
| 32 | `SAA_DISEASERESISTRATE` | + `ResistDeaseas` rate | `0x00407E5F` |
| 33 | `SAA_CURSERESISTRATE` | + `ResistCurse` rate | `0x00407E70` |
| 34 | `SAA_CRITICALRATE` | + `CriDamRate` rate | `0x00407E81` |
| 35 | `SAA_MAXHPPLUS` | + `MaxHP` plus | `0x00407EA3` |
| 36 | `SAA_MAXSPPLUS` | + `MaxSP` plus | `0x00407EB4` |
| 37 | `SAA_INTRATE` | + `Int` rate | `0x00407EC5` |
| 38 | `SAA_FEAR` | _none_ | `0x004081BB` |
| 39 | `SAA_ALLSTATEPLUS` | + `Str` rate, + `Con` rate, + `Dex` rate, + `Int` rate, + `Men` rate | `0x00407ED6` |
| 40 | `SAA_REVIVEHEALRATE` | _none_ | `0x004081BB` |
| 41 | `SAA_COUNT` | _none_ | `0x004081BB` |
| 42 | `SAA_SILIENCE` | _none_ | `0x004081BB` |
| 43 | `SAA_DEADLYBLESSING` | _none_ | `0x004081BB` |
| 44 | `SAA_DAMAGERATE` | _none_ | `0x004081BB` |
| 45 | `SAA_TARGETENEMY` | _none_ | `0x004081BB` |
| 46 | `SAA_MARATE` | + `MAmax` rate, + `MAmin` rate | `0x00407CA0` |
| 47 | `SAA_HEALRATE` | = `HealRate` | `0x00407FA8` |
| 48 | `SAA_DOTRATE` | _none_ | `0x004081BB` |
| 49 | `SAA_AWAY` | flag `cannotmove_stun` | `0x00408129` |
| 50 | `SAA_TOTALDAMAGERATE` | _none_ | `0x004081BB` |
| 51 | `SAA_DISPELSPEEDRATE` | _none_ | `0x004081BB` |
| 52 | `SAA_SETABSTATEME` | _none_ | `0x004081BB` |
| 53 | `SAA_SETABSTATEFRIEND` | _none_ | `0x004081BB` |
| 54 | `SAA_SETABSTATE` | _none_ | `0x004081BB` |
| 55 | `SAA_AREA` | _none_ | `0x004081BB` |
| 56 | `SAA_GTIRESISTRATE` | + `ResistGTI` rate | `0x00408135` |
| 57 | `SAA_MAXHPRATEDAMAGE` | _none_ | `0x004081BB` |
| 58 | `SAA_METAABILITY` | = `ChangeAbilityInfo` | `0x00408163` |
| 59 | `SAA_METASKIN` | _none_ | `0x004081BB` |
| 60 | `SAA_MISSRATE` | + `MissPercentFix` | `0x00408153` |
| 61 | `SAA_REFLECTDAMAGE` | + `DamageReflection` | `0x00408143` |
| 62 | `SAA_RELESEACTION` | _none_ | `0x004081BB` |
| 63 | `SAA_SCANENEMYUSER` | _none_ | `0x004081BB` |
| 64 | `SAA_TARGETALL` | _none_ | `0x004081BB` |
| 65 | `SAA_HIDEENEMY` | _none_ | `0x004081BB` |
| 66 | `SAA_TARGETNOTME` | _none_ | `0x004081BB` |
| 67 | `SAA_DOTDIEDAMAGE` | _none_ | `0x004081BB` |
| 68 | `SAA_ADDALLDOTDMG` | + `DotDamagePlus.Poison`, + `DotDamagePlus.Desease`, + `DotDamagePlus.Blooding`, + `DotDamagePlus.PitBlooding`, + `DotDamagePlus.Burn` | `0x00408007` |
| 69 | `SAA_ADDBLOODINGDMG` | + `DotDamagePlus.Blooding` | `0x00407FE1` |
| 70 | `SAA_ADDPOISONDMG` | + `DotDamagePlus.Poison` | `0x00407FBB` |
| 71 | `SAA_EVASIONAMOUNT` | + `RangeEvasion` | `0x00408100` |
| 72 | `SAA_USESPRATE` | + `SPRate` | `0x004080ED` |
| 73 | `SAA_ACMINUS` | - `AC` plus | `0x00407B96` |
| 74 | `SAA_ACDOWNRATE` | - `AC` rate | `0x00407BA7` |
| 75 | `SAA_SUBTRACTALLDOTDMG` | - `DotDamagePlus.Poison`, - `DotDamagePlus.Desease`, - `DotDamagePlus.Blooding`, - `DotDamagePlus.PitBlooding`, - `DotDamagePlus.Burn` | `0x0040807A` |
| 76 | `SAA_SUBTRACTBLOODINGDMG` | - `DotDamagePlus.Blooding` | `0x00407FF4` |
| 77 | `SAA_SUBTRACTPOISONDMG` | - `DotDamagePlus.Poison` | `0x00407FCE` |
| 78 | `SAA_ATKSPEEDDOWNRATE` | - `AttSpeed` rate | `0x00407DFE` |
| 79 | `SAA_AWAYBACK` | _none_ | `0x004081BB` |
| 80 | `SAA_CRITICALDOWNRATE` | - `CriDamRate` rate | `0x00407E92` |
| 81 | `SAA_DEXMINUS` | - `Dex` plus | `0x00407BC9` |
| 82 | `SAA_HEALAMOUNTMINUS` | _none_ | `0x004081BB` |
| 83 | `SAA_MAMINUS` | - `MAmax` plus, - `MAmin` plus | `0x00407CCD` |
| 84 | `SAA_MADOWNRATE` | - `MAmax` rate, - `MAmin` rate | `0x00407CFA` |
| 85 | `SAA_MAXHPDOWNRATE` | - `MaxHP` rate | `0x00407E20` |
| 86 | `SAA_MRMINUS` | - `MR` plus | `0x00407D6B` |
| 87 | `SAA_MRDOWNRATE` | - `MR` rate | `0x00407D7C` |
| 88 | `SAA_SPEEDDOWNRATE` | - `MoveSpeed` rate | `0x00407DDC` |
| 89 | `SAA_STRMINUS` | - `Str` plus | `0x00407AAF` |
| 90 | `SAA_TBMINUS` | - `TB` plus | `0x00407BFC` |
| 91 | `SAA_TBDOWNRATE` | - `TB` rate | `0x00407C0D` |
| 92 | `SAA_THMINUS` | - `TH` plus | `0x00407C40` |
| 93 | `SAA_THDOWNRATE` | - `TH` rate | `0x00407C51` |
| 94 | `SAA_WCMINUS` | - `WCmax` plus, - `WCmin` plus | `0x00407B1A` |
| 95 | `SAA_WCDOWNRATE` | - `WCmax` rate, - `WCmin` rate | `0x00407B47` |
| 96 | `SAA_DOTWCRATE` | _none_ | `0x004081BB` |
| 97 | `SAA_TARGETNUMVER` | _none_ | `0x004081BB` |
| 98 | `SAA_DOTMARATE` | _none_ | `0x004081BB` |
| 99 | `SAA_MENDOWNRATE` | - `Men` rate | `0x00407D38` |
| 100 | `SAA_USESPDOWN` | _none_ | `0x004081BB` |
| 101 | `SAA_CRIUPRATE` | _none_ | `0x004081BB` |
| 102 | `SAA_MRSHIELDRATE` | = `MagicalImmuneRate` | `0x00408173` |
| 103 | `SAA_ACSHIELDRATE` | = `PhysicalImmuneRate` | `0x00408183` |
| 104 | `SAA_MONSTERSTICK` | _none_ | `0x004081BB` |
| 105 | `SAA_SETACTIVESKILL` | _none_ | `0x004081BB` |
| 106 | `SAA_HPRATEDAMAGE` | _none_ | `0x004081BB` |
| 107 | `SAA_EXPRATE` | _none_ | `0x004081BB` |
| 108 | `SAA_DROPRATE` | _none_ | `0x004081BB` |
| 109 | `SAA_AWAYBACKSPOT` | _none_ | `0x004081BB` |
| 110 | `SAA_STOPANI` | _none_ | `0x004081BB` |
| 111 | `SAA_DOTDMGDOWNRATE` | _none_ | `0x004081BB` |
| 112 | `SAA_SHIELDRATE` | _none_ | `0x004081BB` |
| 113 | `SAA_LPAMOUNT` | + `LPRecover` plus | `0x004081A1` |
| 114 | `SAA_MINHP` | _none_ | `0x004081BB` |
| 115 | `SAA_DMGDOWNRATE` | _none_ | `0x004081BB` |
| 116 | `SAA_SPEEDRESISTRATE` | + `ResistMoveSpdDown` rate | `0x00408193` |
| 117 | `SAA_MELEE` | _none_ | `0x004081BB` |
| 118 | `SAA_RANGE` | _none_ | `0x004081BB` |
| 119 | `SAA_ALLSTATPLUS` | + `Str` plus, + `Con` plus, + `Dex` plus, + `Int` plus, + `Men` plus | `0x00407F3F` |
| 120 | `SAA_RANGEOVER` | + `RangeOver` | `0x004081AF` |

## Caveats

- **`_none_` means no *container* effect, not no effect.** 49 actions resolve to the shared epilogue and
  write nothing into `Parameter::Container` — but several of them clearly do something elsewhere.
  `SAA_DOTDAMAGE`, `SAA_FEAR`, `SAA_SILIENCE`, `SAA_SETABSTATE*` and the targeting family are implemented in
  `SubAbnormalStateActor` subclasses, in the tactic state machine, or in packet handlers. This table says
  where they do *not* act, which is still worth knowing precisely.
- **Handlers are shared.** 120 actions map onto 72 bodies, so two differently-named actions can be literally
  the same code. That is the server's doing, not a collapsing of the table here.
- **`SAA_NOMOVE` has two outcomes.** It sets `cannotmove_entangle`, unless the sub-state's type at +0x26 is
  `0x15` or `0x60`, in which case it branches into `SAA_AWAY`'s body and sets `cannotmove_stun` instead. The
  PDB names those two bits separately, so the distinction is deliberate: entangle and stun are not the same
  immobilisation.
- **`SAA_CRITICALRATE` writes `CriDamRate`.** That slot's name says "critical damage rate", but it is the
  slot `roe_CriticalRate` reads to decide whether a swing crits at all — an action the developers named
  CRITICALRATE writing it is the clearest evidence of what the slot really carries.
- **`SAA_HEALRATE` assigns where its neighbours accumulate** (`mov`, not `add`), so it overwrites rather
  than stacking. It is the only one in the table that does.
- The stat slot names are `Parameter::Cluster`'s own field names from the PDB, including the misspellings
  (`ResistDeaseas`, `PhisycalWeaponMastery`, `ACAbsoulte`). They are kept verbatim so they match the symbols.

## Cross-references

- `AbState.shn` maps the `abstateID` on the wire to a `SubAbState` name.
- `SubAbState.shn` maps `(InxName, Strength)` to up to four `(ActionIndexA..D, ActionArgA..D)` pairs — the
  ids in this table.
- The wire carries `ABSTATE_INFORMATION = { abstateID u32, restKeeptime u32, strength u32 }` in
  `NC_BRIEFINFO_ABSTATE_CHANGE_CMD` (0x1C18) and `..._LIST` (0x1C19); `strength` selects the `SubAbState`
  row, and is not a boolean.

