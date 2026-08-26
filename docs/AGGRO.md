# Mob aggro and target selection

Read from `Zone.exe`. Everything below is a **reading of the disassembly**; nothing here has been executed
under the oracle yet, so treat it as a well-supported hypothesis rather than a measurement. The distinction
matters — on the damage engine, readings that felt certain were wrong more than once.

## What target selection actually is

`MobTargetSelector` derives from `AxialListIterator`. Target selection is therefore not a lookup, it is a
**spatial scan with a per-candidate callback**: the scan machinery walks nearby objects and calls
`ali_Work(scanner, candidate, distance)` on each one.

```
AxialListIterator
  └── MobTargetSelector          mts_SelectTarget, mts_GetTopAggroTarget, mts_AppendAggroPoint,
        │                        mts_DecreaseAggroPoint, mts_AggroAdjust, mts_AggroClear, mts_TargetChange
        └── MobTargetBout
              ├── MobTargetNoBrain
              └── MobTargetAggresive          ali_Work, IsIgnoreLevelGap, IsNoAttackAbstate, IsNoTargetAbstate
                    ├── MobTargetAggresive2   ali_Work -> delegates to MobTargetAggresive::ali_Work
                    ├── MobTargetAggresiveALL
                    ├── MobTargetAggresiveNoLevel
                    └── MobTargetPlayerCaptivate
```

## `MobTargetAggresive::ali_Work` — the per-candidate test

`unsigned char ali_Work(ShineObject* scanner, ShineObject* candidate, unsigned long distance)`

**It contains no distance computation and no angle computation.** The distance arrives as the third
argument, already computed by the scan. The body is a chain of rejection gates followed by a nearest-wins
comparison:

| order | gate |
|---|---|
| 1 | `MobDataBox::mdb_CanIKill(mobId, MobKillerIdentity)` — may this mob kill that kind of thing at all |
| 2 | candidate vtable `+0x8CC` — must be non-zero |
| 3 | scanner vtable `+0x980`, passed the candidate |
| 4 | candidate vtable `+0x904` |
| 5 | candidate vtable `+0x3E4` with `0x123`, then with `0x1F3` — abnormal-state style checks |
| 6 | candidate vtable `+0x484`, rejecting when the result is `3` |
| 7 | `SubLayer::slil_Interact(layerA, layerB)` — channel / instance visibility |
| 8 | `ShineObject::so_SubLayer_CanSee(candidate)` |
| 9 | `MobTargetAggresive::IsNoTargetAbstate(scanner, candidate)` |
| 10 | level-gap check — both levels via vtable `+0x4D8`, then a `this` vtable `+0x40` predicate |

Then:

```
+0x19C  mov eax, [ebp+0x10]      ; the distance argument
+0x19F  cmp eax, [edi+8]         ; against the best distance so far
+0x1A2  jge  reject
        this->best_distance = distance
        this->best_handle   = candidate->vtable+0x344()
        this->best_object   = candidate
        return 1
```

So the rule is **nearest valid candidate wins**, and validity is the gate chain.

## Detection range

`ShineMob::so_getDetectRange` is five instructions:

```
mov eax, [ecx]              ; vtable
mov edx, [eax+0x70C]        ; virtual so_mob_DataBox()
call edx
mov eax, [eax+4]            ; -> the mob's data record
mov ax,  word ptr [eax+0x3B]
ret
```

**A single `uint16` read from the mob's data record at +0x3B.** It is a scalar, set per mob from the mob
table, with a Lua binding (`cMobDetectRange`) and a scene setter (`so_scene_DetectRange`).

## The scan: a circle, and r-squared IS the nearest-wins seed

`MobTargetAggresive::mts_SelectTarget` runs the scan through

```
ShineObject::so_AllOfRange(unsigned long range, const SHINE_XY_TYPE* center, int,
                           FanFormSectorArgument*, AxialListIterator*, unsigned char, unsigned short)
```

`FanFormSectorArgument` is how this engine carries an orientation-dependent shape: its constructor takes
two `ShineObject*` and builds `UnitVector`s from their coordinates (`SHINE_XY_TYPE` at object `+0x66`),
storing a second vector derived from the object's own target when both arguments are the same object.

**The aggro call passes NULL for it** (`mts_SelectTarget+0x3C9`), decoded by reading the pushes
right-to-left against the signature:

| push | parameter | value |
|---|---|---|
| `+0x3F2` | `range` | `AllOfRangeArgument::operator unsigned long()` |
| `+0x3E7` | `center` | result of the vtable `+0x8F4` call |
| `+0x3CE` | `int` | `[ebx+0x10]` |
| `+0x3C9` | `FanFormSectorArgument*` | **0** |
| `+0x3C8` | `AxialListIterator*` | `ebx` |
| `+0x3C6` | `unsigned char` | 0 |
| `+0x3C1` | `unsigned short` | 0xFFFF |

Mob aggro is a **plain circular range query**. The fan/sector path belongs to skills —
`AxialListScanSkillTarget` / `alsst_SkillBlast` are the cone-AoE users.

### Why the callback needs no radius test

Immediately before the call:

```
+0x3B9  mov  eax, [ebx+0x10]     ; r
+0x3BE  imul ecx, eax            ; r * r
+0x3CB  mov  [ebx+8], ecx        ; best-distance-so-far := r squared
```

`[ebx+8]` is exactly the slot `ali_Work` compares against (`cmp eax, [edi+8]` / `jge reject`), so the
distance it receives is **squared**, and the radius and the nearest-wins rule are the *same* comparison:
outside the circle is farther than the seed and rejected; inside it, nearest survives. The absent range
check looked like a missing piece and is actually the design.

## On the cardioid

The operator's observation from play is that aggro range depends on orientation — a cardioid rather than a
circle. **It is a circle.** Three independent pieces agree: a scalar range from `so_getDetectRange`, no angle term
in the callback, and a NULL sector argument at the call site — the very parameter that would make it one.

The engine clearly *can* do orientation-dependent shapes, which is likely where the intuition comes from,
but that path is skills, not aggro. The observed directional behaviour needs another explanation, and the
mundane one is worth testing first: nearest-valid-wins re-evaluated every think tick, from wherever the mob
now stands, so a mob already closing on you keeps re-acquiring at shorter range and reads as directional.

Places an orientation term could still hide, in rough order of likelihood:

1. **Inside `so_AllOfRange`** — it still computes the squared distance per candidate, and if that is not
   plain Euclidean the shape lives there. Not yet read.
2. **`so_SubLayer_CanSee`** — the name suggests visibility, which is where a facing cone would naturally go.
3. **The unresolved vtable predicates** at candidate `+0x8CC`, `+0x904`, `+0x484` and scanner `+0x980`.
4. **`mdb_SpeciesDistance(a, b)`** — a per-species distance that may modulate range.
5. **A different selector** — `MobTargetAggresiveALL` and `MobTargetPlayerCaptivate` have their own
   `mts_SelectTarget`, and player-facing behaviour may not run through `MobTargetAggresive` at all.

## Next

Acquisition is answered. The other half of aggro is the **hate list** — `mts_AppendAggroPoint`,
`mts_DecreaseAggroPoint`, `mts_GetTopAggroTarget`, `mts_AggroAdjust`, `MobAggroManager` and `HitMeList` —
which decides who a mob *stays* on once combat starts. That is what a simulator needs; acquisition only
picks the first victim.
