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

The engine clearly *can* do orientation-dependent shapes; that path is skills, not this one.

**None of that explains the operator's experience, and it must not be treated as though it does.** Three
functions read as angle-free is evidence about *those three functions*, not proof about the system. The
operator reports directional aggro from play; that is an observation, and my alternative explanations
(turn ticks, re-acquisition at closing range) are **untested hypotheses that rank below it** until one of
them is demonstrated. See "Proving the angle question" below.

Places an orientation term could still hide, in rough order of likelihood:

1. **Inside `so_AllOfRange`** — it still computes the squared distance per candidate, and if that is not
   plain Euclidean the shape lives there. Not yet read.
2. **`so_SubLayer_CanSee`** — the name suggests visibility, which is where a facing cone would naturally go.
3. **The unresolved vtable predicates** at candidate `+0x8CC`, `+0x904`, `+0x484` and scanner `+0x980`.
4. **`mdb_SpeciesDistance(a, b)`** — a per-species distance that may modulate range.
5. **A different selector** — `MobTargetAggresiveALL` and `MobTargetPlayerCaptivate` have their own
   `mts_SelectTarget`, and player-facing behaviour may not run through `MobTargetAggresive` at all.

## Proving the angle question — REQUIRED, not optional

The operator's experience is that aggro range depends on orientation. Nothing here refutes that; it only
shows that three specific functions have no angle term. **A proof is owed, and until it exists the
question is open.** Nothing in this repo may state the shape as settled.

What would actually count as proof, in rough order of strength:

1. **Drive `mts_SelectTarget` / `ali_Work` under the oracle** with synthetic objects placed at equal
   distance and varying bearing, and show acquisition is bearing-independent. This is the direct
   experiment and it is the one that settles it.
2. ~~**Read `so_AllOfRange`'s per-candidate distance computation.**~~ **Done — see below.** The angular
   machinery IS in the range scan, and it is gated on the sector argument that aggro passes as NULL.
3. **Read `so_CanSeeOtherObject`.** `MobActionTargetting` calls it three times and it is absent from
   acquisition. If it tests facing rather than occlusion, engagement is directional even though detection
   is not.
4. **A live measurement**: fixed distance, systematically varied bearing, time-to-aggro recorded. This is
   what the observation is made of, so it is what a contrary claim has to beat.

### What (2) found

`so_AllOfRange` computes nothing itself — it forwards all seven arguments through a member-function
pointer. The work is in `so_AllOfRangeNomal` (`0x0054C900`, 0xA60 bytes), and it **does** contain angular
machinery:

```
+0x31   mov esi, [ebp+0x14]      ; esi := arg4, the FanFormSectorArgument*
...
+0x149  test esi, esi
+0x14B  je   +0x165              ; NULL -> skip the cosine entirely
+0x14D  eax = [esi]              ; the sector's angle field
+0x14F  cdq / sub eax,edx / sar eax,1     ; half-angle, signed
+0x15A  call ShineRadian::sr_cos1024      ; cos(half-angle)
+0x15F  store the cosine
+0x172  imul eax, edi            ; arg3 squared -- the radius
...
+0x373  imul eax, eax            ; plain squared Euclidean distance
+0x3D2  call UnitVector::UnitVector(SHINE_XY_TYPE*, SHINE_XY_TYPE*)
```

So there is a genuine cosine-of-half-angle sector test in the scan, it is reached only when the sector
argument is non-null, `esi` is loaded from that argument at function entry, and the aggro call site passes
zero for it. **That is a traced chain, not an inference from the call site alone**, and it is the
strongest evidence so far that aggro acquisition is angle-free.

**It is still not proof that aggro is directionally uniform in play.** It shows one mechanism is
switched off. It says nothing about `so_CanSeeOtherObject`, which `MobActionTargetting` calls three times
and which remains unread, and nothing about behaviour outside acquisition. Items (1), (3) and (4) stand.

## Next

Acquisition is answered. The other half of aggro is the **hate list** — `mts_AppendAggroPoint`,
`mts_DecreaseAggroPoint`, `mts_GetTopAggroTarget`, `mts_AggroAdjust`, `MobAggroManager` and `HitMeList` —
which decides who a mob *stays* on once combat starts. That is what a simulator needs; acquisition only
picks the first victim.

---

# The hate list

Acquisition (above) only picks a first victim. What a mob *stays* on is the hate list.

## Two lists that are easy to confuse

| structure | purpose |
|---|---|
| `MobAggroManager : List<MobTargetStruct>` | **the hate list** — who the mob wants to attack |
| `HitMeList::EnemyList : List<Enemy>` | **damage contribution** — who gets exp and loot rights |

They are separate systems with separate lifetimes. `el_StoreDamage`, `el_StoreLastDamage`,
`el_ExpDistribute`, `el_FindLooter` and `el_EnemyExpDistCheck` are all reward attribution, not aggro.
Mixing them up would give a simulator the wrong target-switching behaviour.

## The operations

Held on the selector at `this+0x14`. Elements are **12 bytes** (`lea ecx,[eax+eax*2]` then `[edx+ecx*4+4]`),
in an intrusive list indexed by `uint16` — the `l_PushA` / `l_PopZ` / `l_AllocA` / `l_MakeList` family
shared with every other `List<T>` in the engine.

| method | where the real body is |
|---|---|
| `mts_AppendAggroPoint(ShineObject*, int)` | `MobTargetBout` — the base is a **no-op** |
| `mts_DecreaseAggroPoint(ShineObject*, int)` | `MobTargetBout` — base also a no-op |
| `mts_GetTopAggroTarget(ShineObject*)` | `MobTargetBout`, overridden by `MobTargetAggresive` and `…ALL` |
| `mts_AggroAdjust(ShineObject*, int)` | `MobTargetSelector` and `MobTargetBout` |
| `mts_AggroClear()`, `mts_StoreAggroList(vector<ushort>&)`, `mts_TargetChange(MobTargetStruct*)` | |

`MobTargetSelector::mts_AppendAggroPoint` and `mts_DecreaseAggroPoint` **share a single body** — identical
code folding, so the base implementation does nothing and all real behaviour is `MobTargetBout`'s. Worth
knowing before porting: the base class is not a fallback, it is a stub.

⚠️ The first ~0xB0 bytes of `mts_AppendAggroPoint` are **not aggro logic**. They copy a name into a rotating
256-byte debug buffer (`NameString`, index at `0x865FB0` masked by `0x865FB4`). Easy to mistake for
bookkeeping that matters.

## `ShineMob::so_DamagedBy(ShineObject* attacker, int, int, unsigned char)`

The damage entry point, at `0x00431750`. What it does, in order:

1. `MobChatManager::mcm_DamageChat` — mob barks.
2. A Lua hook — `LuaArgumentMobDamaged` then `fm_LuaScriptFuncExec`. **Mob behaviour is scriptable**, which
   a simulator has to account for or it will diverge on any mob with a script.
3. `EnemyList::el_StoreDamage(attacker, amount)` — reward attribution.
4. `MobAutomaticActionList::maal_MobDamaged` — boss-field automatic actions.
5. **First-attacker latch**: `[mob+0x24AE]` is a `uint16` initialised to `0xFFFF`; if it is still `0xFFFF`
   the attacker's handle is stored there. It is never overwritten while set.
6. **Social aggro** — `sm_Scream4Rescue(attacker, …)`, with a parameter read from the mob's own data record:

```
mov eax, [vtable+0x70C]   ; so_mob_DataBox()
push 0 ; push 0x3E7
call eax
mov ecx, [eax+4]          ; the mob data record
mov edx, [ecx+0x5F]       ; <- the rescue parameter lives at record +0x5F
push edx ; push attacker
call sm_Scream4Rescue
```

So a damaged mob calls nearby allies, governed by a per-mob field at record `+0x5F` — the same record that
carries the detect range at `+0x3B`.

## Still open

**I have not yet found the call that turns damage into aggro points.** `so_DamagedBy` does not call
`mts_AppendAggroPoint` directly. The likely route is the tactic layer: `MobActionBase::mab_Damaged` receives
a damaged event, and `MobActionInMove_Cancelable` overrides it. Note `MobActionBase::mab_Damaged` is folded
with `so_scene_DetectRange` and `mts_ViewAggroList`, so the base is another empty stub and only the override
does anything.

Finding that link is the next step, and it is the one a simulator most needs: it decides how much hate a hit
generates and therefore when a mob switches target.

## A note on searching this binary

Virtual calls compile as `mov reg, [vtable+off]` followed by `call reg` — there are **7,828** `call reg`
sites and **zero** `call [reg+disp]`. Searching for callers by vtable slot offset therefore finds nothing,
which looks like "no callers" rather than "wrong technique".

---

# The mob AI state machine

`MobActionBase::mab_Think(MobActionArgument*)` **returns `MobActionBase*` — the next state**. That is the
whole driver: each tick, call `think` on the current action and adopt whatever it returns. The base
implementation returns `&Actor::targetting` unconditionally.

States are static singletons on `MobActionArgument::Actor`:

| address | field | class |
|---|---|---|
| `0x0084CFB8` | `base` | `MobActionBase` |
| `0x0084CFBC` | `targetting` | `MobActionTargetting` |
| `0x0084CFC0` | `toregion` | `MobAction2Region` |
| `0x0084CFC4` | `roaming` | `MobActionRoaming` |
| `0x0084CFC8` | `nobrain` | `MobActionNoBrain` |
| `0x0084CFCC` | `return2regen` | `DuringReturn2Regen` |

Seventeen classes implement `mab_Think`: the six above plus `MobActionInMove`, `InMove_Cancelable`,
`InChase`, `Chase`, `Attack`, `SwingDamage`, `WaitSkillEnd`, `Turning`, `BackStep`, `AvoidOverlap`,
`Wander`. The current action pointer lives at `MobActionArgument+0x320`.

The rest of the interface: `mab_Damaged`, `mab_TargetChange`, `mab_IsWaiting`, `mab_WalkTo`, `mab_RunTo`,
`mab_RandomDirectWalk`, `mab_SkillRegistAtScript`, `mab_GetTargetHandle`, `mab_GetTargetObject`,
`mb_SetTargetPoint`.

## Damage cancels movement and forces re-acquisition

```
MobActionInMove_Cancelable::mab_Damaged(arg):
    actor->so_mobile_StopHere()               ; ShineMobileObject, vtable +0xA48
    arg->currentAction = &Actor::targetting
```

So being hit while in a cancelable move stops the mob dead and sends it back to Targetting.

## `MobActionTargetting::mab_Think`

Calls `mts_TargetObject()` for the selector's choice, `ShinePlayer::sp_IsNormalAttack`,
`ShineMob::sm_SetTarget(handle)`, and — three times — **`ShineObject::so_CanSeeOtherObject(other)`**.

That last one is a line-of-sight test that does **not** appear in the acquisition path at all. It is the
most plausible remaining home for the directional behaviour the operator sees: a target inside the circle
that cannot be *seen* is not engaged. Whether it tests occlusion, facing, or both is unread.

## Reading shortcut: the universal stub

`0x00549070` is *the* empty function body in this binary. `MobActionBase::mab_Damaged`,
`mb_SetTargetPoint`, `MobTargetSelector::mts_AggroClear`, `mts_Routine` and
`ShineObject::so_scene_DetectRange` all fold onto it. **Any symbol at `0x00549070` is a no-op**, so a base
class whose method lands there is a stub, not a fallback — worth checking before porting a method that
appears to do nothing.

---

# The damage → aggro link (found)

`so_DamagedBy` **does** feed aggro — through a virtual call I could not resolve on the first read.
`ShineMob::so_mob_AppendAggro(ShineObject*, int)` is **vtable slot +0x700**, and its five callers are:

| caller | |
|---|---|
| `ShineMob::so_DamagedBy` | taking a hit |
| `ShineMobileObject::so_Bash` | melee swing |
| `ShineMob::so_KilledBy` | the killing blow |
| `MiscDataTable::mdt_SkillBlast_Summon` | summon skills |
| `MobTargetAggresive::mts_SelectTarget` | **acquisition also appends aggro** |

That last one is worth noting: simply being *acquired* puts a target on the hate list, so the list is not
purely damage-driven.

## The amount

From `so_DamagedBy+0x147`:

```
mov  ecx, ebx              ; damage (the same value handed to el_StoreDamage)
imul ecx, [ebp+0x10]       ; x the third parameter -- a permille rate. 32-bit, so it WRAPS
mov  eax, 0x10624DD3
imul ecx / sar edx,6 / shr+add    ; signed divide by 1000, truncating toward zero
push eax                   ; the aggro points
call [vtable+0x700]        ; so_mob_AppendAggro(attacker, points)
```

**aggro = `damage * ratePermille / 1000`**, integer, truncating toward zero, with a 32-bit multiply that
wraps. The `0x10624DD3` / `sar 6` sequence is the same divide-by-1000 idiom used by
`roe_LevelGapDamageRevision` in the damage engine.

Consequences a simulator has to get right:

* **Hate and damage contribution are different numbers**, not just different lists. They coincide only at
  a rate of exactly 1000.
* **A rate of 0 generates no hate at all** while still dealing full damage — 0 is a real value here.
* At large `damage * rate` the product wraps and hate can go **negative**, which the port reproduces
  rather than saturating.

## Finding virtual callers in this binary

`tools/xref_vcall.py --offset 0x700`. Virtual calls compile as `mov reg,[vtable+off]` then `call reg`, so
matching the *pair* is what finds callers; a single-opcode search by slot offset returns nothing and reads
as "no callers".
