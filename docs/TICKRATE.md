# Tick rate — the server's, and ours

## Zone.exe has no fixed tick rate

The main loop is `ZoneServer::zs_mainthreadfunction` (0x005ACA30). It is **free-running**: each iteration
does its work and then calls `SleepManager::sm_Routine` (0x005AA610), which is the only pacing in the loop.
There is no `Sleep(33)`, no frame budget, no target Hz anywhere in it.

`sm_Routine` is a self-tuning yield, not a clock:

```
count = this[0]                                  // iterations since the last reset
if (this[8] < count) this[8] = count             // high-water mark
if (count * 2500 / this[8] < this[4] * this[4])
    sm_Sleep()                                   // -> Sleep(1), and this[4] = 0
else { this[4]++; this[0] = 0; }
```

`sm_Sleep` (0x005AA5B0) calls **`Sleep(1)`** and nothing else. So the loop runs as fast as the machine
allows, giving up a millisecond only when its iteration count is low relative to the busiest iteration it
has seen. On an idle zone that puts it somewhere around a kilohertz; under load it is simply "as fast as it
goes".

**Object `so_Routine` runs every iteration.** There is no per-object throttle in the dispatch.

## What *is* periodic: the 100 ms ClockWatch

Timed logic does not read `timeGetTime` directly; it reads a counter the main loop maintains at the top of
each iteration (`+0xCB` onward):

```
delta   = timeGetTime() - clockwatch.last        // ms since the previous iteration
acc    += delta                                  // 64-bit total uptime in ms
[a70]   = acc * 10 / 1000                        // = acc / 100
```

so **`[0x14D41A70]` counts tenths of a second**, and that is the resolution every timeout in the object
routines is expressed in. `ShineMobileObject::so_Routine` compares against it directly; `ShineMob::so_Routine`
computes deadlines like `value * 10 + 100` tenths, i.e. `value + 10` seconds.

**So the honest answer is two numbers, not one:** the loop is unbounded and spins at roughly 1 kHz or
faster, while the effective cadence of anything time-dependent is **100 ms**.

That is why this simulation defaults to `TickMs = 100` — it matches the granularity the server's own timed
logic actually resolves, rather than a frame rate the server never had.

## Two random sources, seeded in that same loop

Worth recording because it is visible in the first 0xB0 bytes of the main thread and it catches people out:

```
srand(time(NULL));
for (i = 0; i < 16; i++) seed[i] = rand();
InitWELLRNG512a(seed);
```

The CRT LCG seeds **WELL512**, and then both remain in use — WELL512 for combat, plain `rand()` for spawn
placement. Assuming a single generator makes runs irreproducible in a way that is very hard to see.

## What this simulation sustains

Single thread, Release, one core, measured with `sim.Step()` in a loop after a 200-tick warm-up:

| scenario | per tick | ticks/s | vs. real time at 100 ms/tick |
|---|---:|---:|---:|
| 1 mob, no Lua | 0.7 µs | 1,445,000 | 144,000x |
| 1 mob + Lua | 7.9 µs | 126,000 | 12,600x |
| Uruga (233 mobs), no Lua | 18.6 µs | 53,700 | 5,370x |
| Uruga (233 mobs) + Lua | 199.8 µs | 5,000 | **500x** |
| 1000 mobs + Lua | 1,168 µs | 856 | 86x |

The headline: **a fully populated Uruga with a scripted character runs about 500x faster than real time**,
so five simulated minutes cost about six tenths of a second.

**The Lua bridge dominates, not the mob AI.** 233 mobs cost 18.6 µs of simulation and 199.8 µs once the
script runs — better than a 10x difference, because the script rebuilds a table of every nearby mob on every
tick. If the tick budget ever matters, that marshalling is the thing to fix, not the AI.

Scaling is roughly linear in mob count, as expected from an O(n) think loop with no spatial index. A
quadtree would only start paying for itself well past a thousand mobs.
