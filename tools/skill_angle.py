#!/usr/bin/env python
"""Reconstruct the `DamageByAngle` index the server used for every skill hit in a capture.

    XOR_TABLE_PATH=C:/Projects/ik-fiesta-bots/xor-table.hex \
      python tools/skill_angle.py --pcap Z:/MageDamageLvl60.pcapng \
        --decoded <scratch>/mage-dec-full.txt --out <scratch>/angles.json

WHY. `roe_CalcDamage+0x500` multiplies the damage by `DamageByAngle[defenderFacing - directionToAttacker]`,
and whether that table is live on a given deployment has been decided twice, both times wrong, from a file
and from a process dump. This decides it from the WIRE: if the term is acting, the rate it would pay has to
correlate with the multiplier each hit actually needs.

⚠️ **Do NOT decode the capture with `--hide-movement` for this.** The movement packets ARE the measurement.

HOW EACH INPUT IS OBTAINED, and the one assumption.

  attackloc   the CASTER's own position. Not a guess: `smo_SkillBlast` passes `[caster+0x66]` as the 5th
              argument of `??0EngageArgument`, which is `attackloc` (+0x18). Tracked from the player's own
              `NC_ACT_MOVERUN_CMD` (C->S, `{from XY, to XY}`) and `NC_ACT_STOP_REQ`.
  defender    position and facing from that handle's last `NC_ACT_SOMEONEMOVEWALK/RUN_CMD`
              (`{handle u16, from XY, to XY, speed u16, moveattr u16}`), position also from
              `NC_ACT_SOMEONESTOP_CMD` and `NC_BRIEFINFO_REGENMOB_CMD`.
  facing      derived ONLY from a move VECTOR, never from `REGENMOB`'s `dir` byte. That matters: the server's
              direction convention and this file's `atan2` need not agree, and taking both the facing and the
              direction-to-attacker from the same function makes the offset cancel in the subtraction. Using
              the `dir` byte for one and `atan2` for the other would silently rotate every index.

  ⚠️ THE ASSUMPTION. A mob that turns to face its target without moving broadcasts nothing, so its facing
  here is whatever its last move said. That is usually the same answer -- a mob walks TOWARDS what it is
  about to hit -- and the histogram bears it out (mobs cluster at index 0, exactly as they do for a melee
  attacker). It is still an assumption, and it is the one way this measurement could be wrong: a facing that
  is noise rather than stale would also produce zero correlation.

A full turn is 180 direction units (`sr_degree2sr` computes `degrees * 180 / 360`), and the table is indexed
0..90 after folding, so one unit is two degrees.
"""
import argparse, json, math, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import damage_buckets as db

u16, u32 = db.u16, db.u32

DIRECTION_UNITS_PER_TURN = 180
MAX_INDEX = 90


def dir_units(dx, dy):
    """A direction vector as the game's 2-degree units, 0..179."""
    a = math.degrees(math.atan2(dy, dx))
    if a < 0:
        a += 360
    return int(a / 2) % DIRECTION_UNITS_PER_TURN


def fold(delta):
    """`DamageCalculator.AngleDamageIndex` -- fold a facing difference into 0..90."""
    f = abs(delta) % DIRECTION_UNITS_PER_TURN
    return abs(((f + MAX_INDEX) % DIRECTION_UNITS_PER_TURN) - MAX_INDEX)


def collect(lines):
    pos, facing, player_pos, skill_of_index = {}, {}, {}, {}
    out = []
    for conv, order, ts, name, raw in db.frames(lines):
        if name in ("NC_ACT_SOMEONEMOVEWALK_CMD", "NC_ACT_SOMEONEMOVERUN_CMD") and len(raw) >= 18:
            h = u16(raw, 0)
            fx, fy, tx, ty = u32(raw, 2), u32(raw, 6), u32(raw, 10), u32(raw, 14)
            pos[(conv, h)] = (tx, ty)
            if (tx, ty) != (fx, fy):
                facing[(conv, h)] = dir_units(tx - fx, ty - fy)
        elif name == "NC_ACT_SOMEONESTOP_CMD" and len(raw) >= 10:
            pos[(conv, u16(raw, 0))] = (u32(raw, 2), u32(raw, 6))
        elif name == "NC_ACT_MOVERUN_CMD" and len(raw) >= 16:
            player_pos[conv] = (u32(raw, 8), u32(raw, 12))
        elif name == "NC_ACT_STOP_REQ" and len(raw) >= 8:
            player_pos[conv] = (u32(raw, 0), u32(raw, 4))
        elif name == "NC_BRIEFINFO_REGENMOB_CMD" and len(raw) >= 14:
            pos[(conv, u16(raw, 0))] = (u32(raw, 5), u32(raw, 9))
        elif name == "NC_BAT_SKILLBASH_HIT_OBJ_START_CMD" and len(raw) >= 6:
            skill_of_index[(conv, u16(raw, 4))] = u16(raw, 0)
        elif name == "NC_BAT_SKILLBASH_HIT_DAMAGE_CMD" and len(raw) >= 5:
            index, caster, targetnum = u16(raw, 0), u16(raw, 2), raw[4]
            for i in range(targetnum):
                off = 5 + i * db.SKILL_DAMAGE_RECORD
                if off + db.SKILL_DAMAGE_RECORD > len(raw):
                    break
                h = u16(raw, off)
                p, mp, fc = player_pos.get(conv), pos.get((conv, h)), facing.get((conv, h))
                idx = None if not (p and mp and fc is not None) else \
                    fold(fc - dir_units(p[0] - mp[0], p[1] - mp[1]))
                out.append({
                    "order": order, "conv": conv, "skill": skill_of_index.get((conv, index)),
                    "defender": h, "caster": caster,
                    "damage": u32(raw, off + 4), "restHp": u32(raw, off + 8),
                    "flags": [n for j, n in enumerate(db.SKILL_FLAGS_B0) if raw[off + 2] & (1 << j)]
                             + [n for j, n in enumerate(db.SKILL_FLAGS_B1) if raw[off + 3] & (1 << j)],
                    "angleIndex": idx, "playerPos": p, "mobPos": mp, "mobFacing": fc,
                })
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pcap")
    ap.add_argument("--decoded", help="reuse/keep the decoded dump here -- WITHOUT --hide-movement")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    rows = collect(db.decode(a.pcap, None, a.decoded))
    json.dump(rows, open(a.out, "w"))

    got = [r for r in rows if r["angleIndex"] is not None]
    print(f"{len(rows)} skill-damage records, {len(got)} with a computable angle index")
    hist = {}
    for r in got:
        hist[r["angleIndex"]] = hist.get(r["angleIndex"], 0) + 1
    print("index histogram:", sorted(hist.items()))
    print("wrote", a.out)


if __name__ == "__main__":
    main()
