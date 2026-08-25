# How a function gets ported and proved

Two tools with two different jobs, and it matters which is which:

- **Fuzzing is a DISCOVERY tool.** It answers *which inputs are load-bearing*, *where the branches are*, and
  *which of my candidate readings of the disassembly is right*. It is exploratory and it is throwaway.
- **Tests are the COMPLIANCE artifact.** Ordinary deterministic tests, per function, aiming for **100%
  branch coverage** — every branch of the translated function exercised by a case whose expected value came
  from the original binary. These are what prevent drift, and they run anywhere: no Windows, no `Zone.exe`,
  no Unicorn.

A green fuzz run is not the deliverable. The tests it lets you write are.

## The loop

1. **Read** the function's disassembly and translate it, preserving operation order.
2. **Probe** with the oracle: which fields does it actually read? Vary each input and watch the output.
   This is where the load-bearing variables come from — not from the parameter list, which lies.
3. **Fuzz** the translation against the original to find disagreements, and **minimise** each to its
   smallest reproducing input.
4. **Decide between candidate explanations by scoring them over a population**, never by fitting the one
   minimised case.
5. **Write the tests.** One per branch, plus the boundary cases the fuzz found. Every expected value is
   read from the oracle, never computed by hand.
6. **Measure branch coverage** and close the gaps. An unexercised branch is an unported branch.

## Why the expected values come from the oracle

A hand-computed expectation encodes what I *believe* the function does, so the test then passes for the
wrong reason. This is not hypothetical: a hand-derived `1.2` and a hand-derived `161.0` both turned out to
be wrong against the real values `1.202` and `160.0`, and in both cases the translation was right and the
test was wrong. Read the number from the binary.

## Why probing beats reading, and reading beats guessing

Reading the disassembly gives a hypothesis. Running the function gives an answer. Both beat inferring from
symmetry — the magic attack path looked like the mirror of the physical one and was not, in three separate
ways.

But note the failure mode of probing too: **a multiplicative factor is invisible when the value it
multiplies is zero**. A dependency probe over an empty object finds only the additive terms. Probe over a
populated baseline.

## Traps in the instrument

Five of the first six "port bugs" on the damage engine were defects in the **measuring apparatus**, not the
translation. When a differential run disagrees, the harness is a suspect too.

- **Unicorn boots the x87 at FPCW 0x0000 — 24-bit single precision.** Every float result is silently
  rounded to float32; the error is ~1e-7 and reads as harmless noise. `ZoneOracle` sets 0x027F.
- **Unicorn caches translated blocks.** A value baked into an instruction can never be changed afterwards.
  This pinned a character level at 1 for a whole process and showed up as a clean, entirely fictitious
  scaling error. Anything that varies per call must live in a data slot.
- **Build caches that key on filename** serve stale binaries, so a corrected file appears to change nothing.
  Three correct fixes were recorded as ineffective before this was found.
- **A negative control must actually change the semantics.** One "sabotage", meant to prove the fuzz could
  detect a broken integer multiply, left `int * int` arithmetic intact and reproduced the original
  behaviour exactly — and the green run read as the fuzz being blind.
- **An untouched input is not a passing input.** Widening a generator from 10 fields to all 51, sparse to
  dense, and ~3000 to full int32 turned 12,600/12,600 into 121/180 — every failure real.
- **Comparison must be exact.** A 1e-9 relative tolerance hides eight ULPs at 1e17, and lets a disagreement
  surface only in whatever function later amplifies it — pointing at the wrong culprit.

## Saying what was measured

"The binary has an SSE branch" is a reading. "Therefore the live server takes it" is an inference — and that
one was wrong, reported as a correction to verified work. Keep readings, inferences and measurements
distinct in commits, docs and test comments.

## What "done" looks like

Every branch covered by a test whose expected value came from the binary, and the residual stated rather
than rounded away. "Exact in every realistic magnitude regime, with 1-ULP disagreements above ~1e14" is more
useful than "done".
