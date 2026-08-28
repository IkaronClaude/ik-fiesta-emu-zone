#!/usr/bin/env python
"""Sort every swing in a capture into a bucket of IDENTICAL COMBAT STATE, and report each bucket.

    python tools/damage_buckets.py --decoded dump.txt --out buckets.json
    python tools/damage_buckets.py --pcap Z:/FighterDamageLvl60.pcapng --port 9022 --out buckets.json

WHY NOT CHAT WINDOWS. The obvious way to read a damage capture is to slice it by the operator's chat
annotations, which is how `pcap_combat_truth.py` does it. That is wrong for anything but the simplest
session: a window bounded by two chat lines is bounded by TYPING, not by state. In
`FighterDamageLvl60.pcapng` the operator typed "Okay now levelling up by 1", ran the command, then typed
"And vit 5" -- 278 frames later. Slicing on those two lines gives a window holding ONE outgoing swing,
with the three minutes of post-levelup fighting sitting outside it. The chat says WHAT changed and WHY; it
cannot say WHEN, because the operator types at human speed while the state changes at packet speed.

So state is tracked from the packets and every swing is tagged with the state prevailing AT IT:

  * the player's whole parameter vector, from `NC_CHAR_CHANGEPARAMCHANGE_CMD` (0x1035);
  * the abnormal states active on the player;
  * the abnormal states active on THAT SPECIFIC mob;
  * how many level-ups have happened;
  * which mob id was involved, and in which direction.

⚠️ THE PARAMETER IDS ARE DELIBERATELY NOT NAMED. A bucket only needs to know whether two swings happened
under the same stats, and the whole vector answers that exactly. Naming the ids would mean choosing
between two mappings this project holds that disagree (`analyse_damage.py`'s 6=DmgMin/7=DmgMax/8=DEF
against a memory note's 0x08=AC/0x0D=MR/0x01=END), and a wrong name would silently mislabel every bucket
while the bucketing itself stayed correct. The vector is the fact; the names are a separate question.

⚠️ LEVEL IS A COUNT OF LEVEL-UPS, NOT A NUMBER. `NC_BAT_LEVELUP_CMD` is 235 bytes and the level's offset
would be a guess. A count is exact and needs none: this capture contains exactly one, the 59 -> 60
transition the session was built around, so `levelups` 0 and 1 ARE the two levels.

⚠️ HANDLES ARE PER-CONVERSATION AND RECYCLED. A relog opens a new conversation and re-issues handles, and
within one conversation a dead mob's handle is reused by the next spawn. Everything is keyed by
(conv, handle); `BRIEFINFODELETE` and `REGENMOB` both clear the handle's state, or a fresh mob inherits
the debuffs left on the corpse that had the number before it.
"""
import argparse
import collections
import json
import os
import re
import subprocess
import sys

PCAP_DECODE = r"C:/Projects/fiesta-proxy/tools/pcap_decode.py"
XOR_TABLE = os.environ.get("XOR_TABLE_PATH", r"C:/Projects/ik-fiesta-bots/xor-table.hex")

FRAME = re.compile(r"^\s+(S<-|C->)\s+@\s*(\d+)\s+\[0x([0-9A-Fa-f]{4})\]\s+(\S+)")
CONV = re.compile(r"^==== server (\S+) <-> client (\S+) ====")
HEXROW = re.compile(r"^\s+([0-9a-f]{4})\s+((?:[0-9a-f]{2} )+)")

# Named bits of PROTO_NC_BAT_SWING_DAMAGE_CMD::flag. INCOMPLETE by measurement -- the high bits have no
# names here at all -- so "was this a plain hit" is tested on the raw word being zero, never on this list
# being empty. See pcap_combat_truth.py, which learned it the hard way.
FLAGS_B0 = ["isdamage", "iscritical", "ismissed", "isshieldblock",
            "isheal", "isenchant", "isresist", "IsCostumWeapon"]
FLAGS_B1 = ["isDead", "isImmune", "IsCostumShield"]


def decode(pcap, port, cache):
    if cache and os.path.exists(cache):
        return open(cache, encoding="utf-8", errors="replace").read().splitlines()
    env = dict(os.environ, XOR_TABLE_PATH=XOR_TABLE, PYTHONIOENCODING="utf-8")
    cmd = [sys.executable, PCAP_DECODE, pcap, "--hide-movement"]
    if port:
        cmd += ["--port", str(port)]
    out = subprocess.run(cmd, capture_output=True, env=env,
                         cwd=os.path.dirname(PCAP_DECODE)).stdout.decode("utf-8", "replace")
    if cache:
        open(cache, "w", encoding="utf-8").write(out)
    return out.splitlines()


def frames(lines):
    """(conv, order, name, payload) for every frame, in file order.

    The hex rows are the wire. The struct printer renders `flag` as a type name rather than a value and a
    PDB field order is not always the serialised order, so nothing here reads a decoded field."""
    conv, order, pending, buf = -1, 0, None, bytearray()

    def done():
        if pending is not None:
            yield conv, order, pending, bytes(buf)

    for line in lines:
        if CONV.match(line):
            for f in done():
                yield f
            pending, buf = None, bytearray()
            conv += 1
            continue
        m = FRAME.match(line)
        if m:
            for f in done():
                yield f
            pending, buf = m.group(4), bytearray()
            order += 1
            continue
        h = HEXROW.match(line)
        if h and pending is not None and int(h.group(1), 16) == len(buf):
            buf.extend(bytes.fromhex(h.group(2).replace(" ", "")))
    for f in done():
        yield f


def u16(b, o):
    return int.from_bytes(b[o:o + 2], "little")


def u32(b, o):
    return int.from_bytes(b[o:o + 4], "little")


def collect(lines):
    """Walk the stream once, maintaining state, and tag each swing with the state prevailing at it."""
    params = {}                                   # paramType -> value, the player's whole vector
    abstates = collections.defaultdict(set)       # (conv, handle) -> active abstate ids
    mob_of = {}                                   # (conv, handle) -> mob id, from REGENMOB
    levelups = 0
    swings, chat, hit_counts = [], [], collections.Counter()

    def apply_abstate(conv, handle, ident, active):
        key = (conv, handle)
        if active:
            abstates[key].add(ident)
        else:
            abstates[key].discard(ident)

    for conv, order, name, raw in frames(lines):
        if name == "NC_BRIEFINFO_REGENMOB_CMD" and len(raw) >= 5:
            # handle u16@0, mode u8@2, mobid u16@3. This is the roster, and it is PER CONVERSATION, which
            # is what makes it trustworthy: a handle->mob map merged across relogs is the documented way
            # to attribute a swing to the wrong monster.
            handle = u16(raw, 0)
            mob_of[(conv, handle)] = u16(raw, 3)
            # A fresh spawn on a recycled handle starts clean.
            abstates.pop((conv, handle), None)
        elif name == "NC_BRIEFINFO_BRIEFINFODELETE_CMD" and len(raw) >= 2:
            handle = u16(raw, 0)
            abstates.pop((conv, handle), None)
            mob_of.pop((conv, handle), None)
        elif name == "NC_CHAR_CHANGEPARAMCHANGE_CMD" and raw:
            # count u8, then count * (paramType u8, value u32). These are the SERVER'S numbers after gear,
            # buffs and free-stat allocation -- no formula, nothing inferred.
            n, off = raw[0], 1
            for _ in range(n):
                if off + 5 > len(raw):
                    break
                params[raw[off]] = u32(raw, off + 1)
                off += 5
        elif name == "NC_BAT_ABSTATESET_CMD" and len(raw) >= 6:
            apply_abstate(conv, u16(raw, 0), u32(raw, 2), True)
        elif name == "NC_BAT_ABSTATERESET_CMD" and len(raw) >= 6:
            apply_abstate(conv, u16(raw, 0), u32(raw, 2), False)
        elif name == "NC_BRIEFINFO_ABSTATE_CHANGE_CMD" and len(raw) >= 14:
            # handle u16@0, then ABSTATE_INFORMATION: id u32@2, tick u32@6, active u32@10.
            apply_abstate(conv, u16(raw, 0), u32(raw, 2), u32(raw, 10))
        elif name == "NC_BRIEFINFO_ABSTATE_CHANGE_LIST_CMD" and len(raw) >= 3:
            # handle u16@0, count u8@2, then count * ABSTATE_INFORMATION (12 bytes each). Ignoring this
            # leaves per-mob state wrong wherever the server sends it in bulk rather than one at a time.
            handle, count = u16(raw, 0), raw[2]
            for i in range(count):
                off = 3 + i * 12
                if off + 12 > len(raw):
                    break
                apply_abstate(conv, handle, u32(raw, off), u32(raw, off + 8))
        elif name == "NC_BAT_LEVELUP_CMD":
            levelups += 1
        elif name == "NC_ACT_CHAT_REQ" and len(raw) > 2:
            text = raw[2:2 + raw[1]].decode("latin-1", "replace").strip()
            if text and not text.startswith("&"):
                chat.append({"order": order, "conv": conv, "text": text})
        elif name == "NC_BAT_SWING_DAMAGE_CMD" and len(raw) >= 16:
            att, dfn = u16(raw, 0), u16(raw, 2)
            hit_counts[(conv, dfn)] += 1
            swings.append({
                "order": order, "conv": conv, "attacker": att, "defender": dfn,
                "damage": u16(raw, 6), "restHp": u32(raw, 8),
                "flagWord": raw[4] | (raw[5] << 8),
                "flags": [n for i, n in enumerate(FLAGS_B0) if raw[4] & (1 << i)]
                         + [n for i, n in enumerate(FLAGS_B1) if raw[5] & (1 << i)],
                "levelups": levelups,
                "attackerMob": mob_of.get((conv, att)),
                "defenderMob": mob_of.get((conv, dfn)),
                # Snapshots, not references: the state moves on and a bucket must remember what was true
                # AT THE SWING.
                "params": tuple(sorted(params.items())),
                "attackerAbstates": tuple(sorted(abstates[(conv, att)])),
                "defenderAbstates": tuple(sorted(abstates[(conv, dfn)])),
            })
    return swings, chat, hit_counts


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pcap")
    ap.add_argument("--port", type=int)
    ap.add_argument("--decoded", help="reuse/keep the decoded dump here")
    ap.add_argument("--min-hits", type=int, default=1, help="only print buckets with at least this many")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    lines = decode(a.pcap, a.port, a.decoded)
    swings, chat, hit_counts = collect(lines)

    # The player, per conversation: the handle that gets hit most and was never announced as a mob.
    # Robust to relogs and to a handle number meaning different things in different conversations.
    mob_handles = {(s["conv"], s["attacker"]) for s in swings if s["attackerMob"] is not None}
    mob_handles |= {(s["conv"], s["defender"]) for s in swings if s["defenderMob"] is not None}
    player = {}
    for (conv, handle), n in hit_counts.items():
        if (conv, handle) in mob_handles:
            continue
        if n > hit_counts.get((conv, player.get(conv, -1)), 0):
            player[conv] = handle

    buckets, unattributed = collections.defaultdict(list), 0
    for s in swings:
        if s["flagWord"] != 0 or s["damage"] <= 0:
            continue
        me = player.get(s["conv"])
        if s["attacker"] == me:
            side, mob = "OUT", s["defenderMob"]
            own_ab, foe_ab = s["attackerAbstates"], s["defenderAbstates"]
        elif s["defender"] == me:
            side, mob = "IN", s["attackerMob"]
            own_ab, foe_ab = s["defenderAbstates"], s["attackerAbstates"]
        else:
            unattributed += 1
            continue
        if mob is None:
            # The mob was never announced in this conversation -- it was already in view when the capture
            # or the relog started. Counted, never guessed at.
            unattributed += 1
            continue
        buckets[(side, mob, s["levelups"], s["params"], own_ab, foe_ab)].append(s["damage"])

    rows = []
    for (side, mob, levelups, params, own_ab, foe_ab), dmg in buckets.items():
        rows.append({"side": side, "mob": mob, "levelups": levelups,
                     "selfAbstates": list(own_ab), "enemyAbstates": list(foe_ab),
                     "params": dict(params), "n": len(dmg),
                     "min": min(dmg), "max": max(dmg),
                     "mean": round(sum(dmg) / len(dmg), 1), "damage": sorted(dmg)})
    rows.sort(key=lambda r: (r["side"], r["mob"], r["levelups"], -r["n"]))

    json.dump({"chat": chat, "buckets": rows, "playerHandlePerConv": player},
              open(a.out, "w"), indent=1)

    kept = [r for r in rows if r["n"] >= a.min_hits]
    print("%d clean hits in %d state buckets (%d with n>=%d); %d hits unattributed\n"
          % (sum(r["n"] for r in rows), len(rows), len(kept), a.min_hits, unattributed))
    print("  side mob   lvup n    min   max   mean   self-abstates              enemy-abstates")
    for r in kept:
        print("  %-4s %-5s %-4s %-4d %-5d %-5d %-6.1f %-26s %s"
              % (r["side"], r["mob"], r["levelups"], r["n"], r["min"], r["max"], r["mean"],
                 ",".join(map(str, r["selfAbstates"])) or "-",
                 ",".join(map(str, r["enemyAbstates"])) or "-"))
    print("\nwrote %s" % a.out)


if __name__ == "__main__":
    main()
