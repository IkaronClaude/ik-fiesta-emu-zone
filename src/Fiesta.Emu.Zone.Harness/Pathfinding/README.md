# Pathfinding — copied verbatim from `ik-fiesta-bots`

⭐ **These files are the BOT'S OWN pathfinding, copied unchanged, on the operator's instruction:
"Just use the shbd code and navmesh from the bots - it works and is correct".**

They keep their original `Fiesta.Bot.Pathfinding` namespace deliberately, so a `diff` against
`ik-fiesta-bots/src/Fiesta.Bot/Pathfinding/` stays meaningful. Do not reformat or "tidy" them; the source
of truth is that repo, and drift here would be silent.

## Why a reimplementation was thrown away

The harness first grew its own `.shbd` reader, tile A* and navmesh. Each piece looked right and each was
wrong in a way that only showed up as the BOT behaving badly:

- a bounded tile A* whose 20,000-expansion cap silently turned the pathfinder off — it failed to route
  **100 of 101** `walkTo` calls, so every walk became a straight line into geometry;
- a hand-rolled navmesh whose goal-slide searched only 8 tiles, while a refused destination in Marlone
  Clan's Hideout sat **32 tiles (200 world units)** inside a wall;
- and a per-candidate reachability check that made a 150-second run take **179 seconds**.

Every one of those was diagnosed as a driver defect first. Copying the working implementation removes a
whole class of that.

## What this repo must not copy from it

`BlockGrid`'s runtime-blocked learning (from live `MOVEFAIL`) and the `.sbi` door overlay describe a LIVE
session. The simulation has neither, so nothing here should start feeding them.
