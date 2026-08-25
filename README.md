# ik-fiesta-emu-zone

A C# re-implementation of Fiesta Online **Zone** server logic, translated function-by-function from the
original `Zone.exe`.

**Current scope is deliberately small: a mob / combat simulator that behaves like the zone.** Movement,
aggro, targeting, skills and cooldowns - enough to run a bot driver against it and iterate far faster than
against a live server. There is no S2S or S2C networking here and none is planned for now.

Longer term this is intended to grow into a full emulator that can run in place of `Zone.exe` and
interoperate with the other server executables, which is why the translation is 1:1 from the start rather
than a convenient approximation.

## The approach

This is a **1:1 decompile-to-C# translation**, not a reimplementation from behaviour. Class names, method
names and structure mirror the original — `MobTargetSelector::mts_SelectTarget` becomes
`MobTargetSelector.mts_SelectTarget` — so that any function can be read against its disassembly without a
translation table.

**Fuzzing is the discovery tool; tests are the compliance artifact.** A harness runs the C# and the *real*
function from `Zone.exe` (under Unicorn) on randomised inputs, which is how you find out which variables are
load-bearing, where the branches are, and which reading of the disassembly is correct. What ships is then an
ordinary per-function test suite aiming for **100% branch coverage**, with every expected value read from
the binary rather than computed by hand. Those tests are what prevent drift, and they run without Windows,
`Zone.exe` or Unicorn.

The method was developed on the damage engine in `ik-fiesta-bots`, where it found defects that reading the
disassembly had missed, and where **five of the first six "port bugs" turned out to be defects in the
measuring instrument rather than the port**. Read `docs/METHOD.md` before trusting a green run.

## What you need to supply

Nothing in this repo is game data. To run the oracle you need your own copy of:

| file | used for |
|---|---|
| `Zone.exe` | the code the oracle executes |
| `Zone.pdb` | symbol names and offsets |

Point the tools at them with `--exe` / `--pdb`, or set `ZONE_EXE` / `ZONE_PDB`.

## Layout

| path | |
|---|---|
| `src/Fiesta.Emu.Zone/` | the translated server |
| `tools/` | the oracle and the discovery/fuzz harness |
| `docs/` | the porting method, and per-subsystem findings |
| `tests/` | the compliance suite - per function, branch-complete |

## Status

Early. See `PROJECT_PLAN.md` for what is ported and what is verified — those are two different lists, and
the plan keeps them separate on purpose.
