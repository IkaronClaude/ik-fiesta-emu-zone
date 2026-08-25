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

## On the cardioid

The operator's observation from play is that aggro range depends on orientation — a cardioid rather than a
circle. **I have not found that, and what I have read says circle.** The range is one scalar, the
per-candidate callback has no angle term, and `MobTargetAggresive2` merely delegates.

That does not settle it. An orientation term could still live in:

1. **The scan itself** — whoever computes the `distance` argument. If that value is not a plain Euclidean
   distance, the shape lives there. This is the first thing to check.
2. **`so_SubLayer_CanSee`** — the name suggests visibility, which is where a facing cone would naturally go.
3. **The unresolved vtable predicates** at candidate `+0x8CC`, `+0x904`, `+0x484` and scanner `+0x980`.
4. **`mdb_SpeciesDistance(a, b)`** — a per-species distance that may modulate range.
5. **A different selector** — `MobTargetAggresiveALL` and `MobTargetPlayerCaptivate` have their own
   `mts_SelectTarget`, and player-facing behaviour may not run through `MobTargetAggresive` at all.

There is also a plausible explanation with no orientation in it: aggro is **nearest-valid-wins inside a
circle**, and a mob that is already walking toward you re-scans from a closer position each tick, which can
feel directional from the player's side. Worth ruling in or out before assuming a shape.

## Next

Drive `ali_Work` under the oracle with synthetic objects and sweep the distance argument to confirm the
nearest-wins rule directly, then walk up to the caller to find what computes that distance. Reading has
taken this as far as it usefully goes.
