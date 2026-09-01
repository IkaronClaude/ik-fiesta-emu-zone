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

# `ItemInfo.Equip` / `tItem.nStorage` slot numbers, and the empty marker. 0 is a real item id, so
# emptiness is 0xFFFF -- see NC_ITEM_EQUIPCHANGE_CMD setting slot 10 to 65535 when a shield comes off.
WEAPON_EQUIP_SLOT, EMPTY_EQUIP_SLOT = 12, 0xFFFF

# sizeof(PROTO_NC_BRIEFINFO_REGENMOB_CMD), the element of the bulk NC_BRIEFINFO_MOB_CMD array.
# Checked against the wire: a 4173-byte frame is 1 + 28 * 149 exactly.
REGENMOB_RECORD = 149

PCAP_DECODE = r"C:/Projects/fiesta-proxy/tools/pcap_decode.py"
XOR_TABLE = os.environ.get("XOR_TABLE_PATH", r"C:/Projects/ik-fiesta-bots/xor-table.hex")

FRAME = re.compile(r"^\s+(S<-|C->)\s+@\s*(\d+)(?:\s+t=([0-9.]+))?\s+\[0x([0-9A-Fa-f]{4})\]\s+(\S+)")
CONV = re.compile(r"^==== server (\S+) <-> client (\S+) ====")
HEXROW = re.compile(r"^\s+([0-9a-f]{4})\s+((?:[0-9a-f]{2} )+)")

# `PROTO_NC_BAT_SWING_DAMAGE_CMD::<unnamed-type-flag>`, READ FROM THE PDB -- nine one-bit fields over two
# bytes. The list this replaced was assembled by measurement and was off by one from the start: it opened
# with "isdamage", which does not exist, so every CRITICAL in every capture was being labelled `isdamage`
# and `iscritical` was reading `isresist`. `ismissed` happened to land on the right bit, which is why
# nothing looked wrong.
#
# "Was this a plain hit" is still tested on the raw word being zero rather than on this list being empty:
# byte 1 has only one named bit out of eight, so a name-based test would call an unknown high bit clean.
FLAGS_B0 = ["iscritical", "isresist", "ismissed", "isshieldblock",
            "isCostumCharged", "isDead", "isDamege2Heal", "isImmune"]
FLAGS_B1 = ["isCostumShieldCharged"]


def decode(pcap, port, cache):
    if cache and os.path.exists(cache):
        return open(cache, encoding="utf-8", errors="replace").read().splitlines()
    env = dict(os.environ, XOR_TABLE_PATH=XOR_TABLE, PYTHONIOENCODING="utf-8")
    # --timestamps is what makes a DURATION checkable. Without it the dump carries per-direction byte
    # offsets and nothing else, so an abstate's restKeeptime cannot be expired and frame ORDER gets
    # mistaken for a clock.
    cmd = [sys.executable, PCAP_DECODE, pcap, "--hide-movement", "--timestamps"]
    if port:
        cmd += ["--port", str(port)]
    out = subprocess.run(cmd, capture_output=True, env=env,
                         cwd=os.path.dirname(PCAP_DECODE)).stdout.decode("utf-8", "replace")
    if cache:
        open(cache, "w", encoding="utf-8").write(out)
    return out.splitlines()


def frames(lines):
    """(conv, order, ts, name, payload) for every frame, in file order.

    `ts` is SECONDS from the conversation's first frame, or None when the dump was made without
    --timestamps. It is None rather than 0.0 on purpose: 0.0 is a real time and callers that need a
    duration have to be able to tell "the capture did not carry one" from "this happened at the start".


    The hex rows are the wire. The struct printer renders `flag` as a type name rather than a value and a
    PDB field order is not always the serialised order, so nothing here reads a decoded field."""
    conv, order, pending, buf, ts = -1, 0, None, bytearray(), None

    def done():
        if pending is not None:
            yield conv, order, ts, pending, bytes(buf)

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
            pending, buf = m.group(5), bytearray()
            ts = float(m.group(3)) if m.group(3) else None
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
    abstates = collections.defaultdict(dict)      # (conv, handle) -> {abstate id: (strength, expiresAt)}
    mob_of = {}                                   # (conv, handle) -> mob id, from REGENMOB
    levelups = 0
    level = None
    free_stat, chrclass, passives = {}, None, ()
    weapon = None                                 # item id in the weapon slot, or None if unknown
    swings, chat, hit_counts = [], [], collections.Counter()

    def set_abstate(conv, handle, ident, strength, keeptime_ms=None, now=None):
        # STRENGTH, not a flag. `ABSTATE_INFORMATION` is {abstateID, restKeeptime, strength} -- the third
        # word was read here as an on/off bit for a while, which silently discarded the one field that
        # decides an abstate's magnitude: `SubAbState` rows are keyed by (InxName, Strength) and
        # StaMoraleDecreaseWC alone spans 1490..2148 across ranks 17-20.
        #
        # The SECOND word is restKeeptime, and it is why this needs a clock. Without one, a state is only
        # ever removed by an explicit ABSTATERESET, so anything that lapses on its own timer is held for
        # the rest of the capture -- harmless for StaImmortal, which carries no actions, and wrong for
        # SubStaMoraleDecreaseWC, which has 15 s and a real weapon-damage effect.
        expires = None
        if keeptime_ms and now is not None:
            expires = now + keeptime_ms / 1000.0
        abstates[(conv, handle)][ident] = (strength, expires)

    def clear_abstate(conv, handle, ident):
        abstates[(conv, handle)].pop(ident, None)

    def live(conv, handle, now):
        """The states still active at `now`, dropping any whose restKeeptime has run out.

        A state with no expiry (keeptime 0, or a dump without timestamps) is kept: "we do not know when
        this ends" must not be silently turned into "it already ended"."""
        out = {}
        for ident, (strength, expires) in abstates[(conv, handle)].items():
            if expires is not None and now is not None and now >= expires:
                continue
            out[ident] = strength
        return out

    for conv, order, ts, name, raw in frames(lines):
        if name == "NC_BRIEFINFO_REGENMOB_CMD" and len(raw) >= 5:
            # handle u16@0, mode u8@2, mobid u16@3. This is the roster, and it is PER CONVERSATION, which
            # is what makes it trustworthy: a handle->mob map merged across relogs is the documented way
            # to attribute a swing to the wrong monster.
            handle = u16(raw, 0)
            mob_of[(conv, handle)] = u16(raw, 3)
            # A fresh spawn on a recycled handle starts clean.
            abstates.pop((conv, handle), None)
        elif name == "NC_BRIEFINFO_MOB_CMD" and raw:
            # mobnum u8@0, then mobnum * PROTO_NC_BRIEFINFO_REGENMOB_CMD (149 bytes each), same layout as
            # the single-spawn packet.
            #
            # ⚠️ THIS IS THE OTHER HALF OF THE ROSTER, and leaving it out is not a small gap. REGENMOB
            # announces a mob SPAWNING; everything already alive when you enter its view arrives here in
            # bulk. A capture that starts next to a live mob and kills it produces swings whose handle was
            # never in a REGENMOB -- and the client obviously rendered that mob, so "not in the roster"
            # was always a statement about this parser, never about the wire.
            count = raw[0]
            for i in range(count):
                off = 1 + i * REGENMOB_RECORD
                if off + 5 > len(raw):
                    break
                handle = u16(raw, off)
                mob_of[(conv, handle)] = u16(raw, off + 3)
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
            # {handle u16, abstate u32} only -- no strength, so a set with no CHANGE alongside it
            # records the abstate at strength 0 = "present, magnitude unknown".
            # ABSTATESET carries no strength or keeptime -- keep whatever the last full announcement
            # said rather than resetting either to zero.
            prev = abstates[(conv, u16(raw, 0))].get(u32(raw, 2), (0, None))
            abstates[(conv, u16(raw, 0))][u32(raw, 2)] = prev
        elif name == "NC_BAT_ABSTATERESET_CMD" and len(raw) >= 6:
            clear_abstate(conv, u16(raw, 0), u32(raw, 2))
        elif name == "NC_BRIEFINFO_ABSTATE_CHANGE_CMD" and len(raw) >= 14:
            # handle u16@0, then ABSTATE_INFORMATION: abstateID u32@2, restKeeptime u32@6,
            # STRENGTH u32@10.
            set_abstate(conv, u16(raw, 0), u32(raw, 2), u32(raw, 10), u32(raw, 6), ts)
        elif name == "NC_BRIEFINFO_ABSTATE_CHANGE_LIST_CMD" and len(raw) >= 3:
            # handle u16@0, count u8@2, then count * ABSTATE_INFORMATION (12 bytes each). Ignoring this
            # leaves per-mob state wrong wherever the server sends it in bulk rather than one at a time.
            handle, count = u16(raw, 0), raw[2]
            for i in range(count):
                off = 3 + i * 12
                if off + 12 > len(raw):
                    break
                set_abstate(conv, handle, u32(raw, off), u32(raw, off + 8),
                            u32(raw, off + 4), ts)
        elif name == "NC_CHAR_CLIENT_BASE_CMD" and len(raw) >= 0x5D:
            # CHARSTATDISTSTR at +0x57 -- the free-stat ALLOCATION, one byte per stat. It is the input to
            # `roe_Damage`'s per-rule override and to the term the server adds on top of every displayed
            # accessor output, so a prediction cannot be checked without it. FIRST one only; a relog
            # re-sends it and later ones report a distribution the window has not reached.
            # PROTO_NC_CHAR_BASE_CMD.Level at +25, re-sent on every login. Tracked as an ABSOLUTE that
            # each relog re-asserts, then incremented per level-up -- not read once. This capture starts at
            # 60, is cheated down to 59, and is levelled back to 60 mid-session, so a single reading at the
            # front is wrong for every swing that matters.
            level = raw[25]
            if not free_stat:
                free_stat = dict(zip(["Strength", "Constitute", "Dexterity", "Intelligence",
                                      "MentalPower", "RedistributePoint"], raw[0x57:0x5D]))
        elif name == "NC_ITEM_EQUIPCHANGE_CMD" and len(raw) >= 5:
            # location u8@2 is the EQUIP SLOT and item u16@3 the item id -- our own character's equipment,
            # sent to us, unlike NC_BRIEFINFO_CHANGEWEAPON_CMD which broadcasts appearance to others and
            # does NOT carry every swap (it misses this capture's Splitter -> Kaineneceflight entirely).
            #
            # Slot numbering matches `ItemInfo.Equip` and `tItem.nStorage`: 12 weapon, 10 shield, 7 body.
            # 0xFFFF is the EMPTY marker -- item id 0 is a real id, so emptiness cannot be signalled by 0.
            if raw[2] == WEAPON_EQUIP_SLOT:
                item = u16(raw, 3)
                weapon = None if item == EMPTY_EQUIP_SLOT else item
        elif name == "NC_CHAR_CLIENT_PASSIVE_CMD" and len(raw) >= 2:
            # count u16, then count * u16 passive-skill ids, IN LIST ORDER. Order matters: cpl_RecalcParam
            # writes each non-zero mastery value with `mov`, not `add`, so ranks do not sum -- the last
            # non-zero one wins. It is also re-sent per login and GROWS as skills are bought (3 ids early
            # in this capture, 17 later), so it is per-swing state, not a fixture constant.
            n = u16(raw, 0)
            if 2 + n * 2 <= len(raw):
                passives = tuple(u16(raw, 2 + i * 2) for i in range(n))
        elif name == "NC_CHAR_CLIENT_SHAPE_CMD" and raw and chrclass is None:
            # PROTO_AVATAR_SHAPE_INFO packs race:2, chrclass:5, gender:1 into byte 0. The class decides
            # which Param<Class>Server.txt supplies JobChangeDmgUp, which is worth up to 2x on every hit
            # a player lands on a monster -- so a prediction cannot be made without it.
            chrclass = (raw[0] >> 2) & 0x1F
        elif name == "NC_BAT_LEVELUP_CMD":
            levelups += 1
            if level is not None:
                level += 1
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
                "level": level,
                "passives": passives,
                "weapon": weapon,
                "attackerMob": mob_of.get((conv, att)),
                "defenderMob": mob_of.get((conv, dfn)),
                # Snapshots, not references: the state moves on and a bucket must remember what was true
                # AT THE SWING.
                "params": tuple(sorted(params.items())),
                # (id, strength) pairs -- strength is part of the state, not decoration.
                "attackerAbstates": tuple(sorted(live(conv, att, ts).items())),
                "defenderAbstates": tuple(sorted(live(conv, dfn, ts).items())),
            })
    return swings, chat, hit_counts, free_stat, chrclass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pcap")
    ap.add_argument("--port", type=int)
    ap.add_argument("--decoded", help="reuse/keep the decoded dump here")
    ap.add_argument("--min-hits", type=int, default=1, help="only print buckets with at least this many")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    lines = decode(a.pcap, a.port, a.decoded)
    swings, chat, hit_counts, free_stat, chrclass = collect(lines)

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

    # Two accumulators per bucket. `damage` is the CLEAN hits and is what the damage formula is checked
    # against -- unchanged, because a miss carries no damage to predict and a critical is a different
    # calculation. `outcomes` counts every swing that reached the bucket whatever its flags, which is what
    # the hit / block / critical RATES have to be measured against: a rate whose denominator leaves out the
    # swings that missed is not a rate.
    buckets = collections.defaultdict(list)
    outcomes = collections.defaultdict(collections.Counter)
    unattributed = 0
    for s in swings:
        me = player.get(s["conv"])
        if s["attacker"] == me:
            side, mob, own_weapon = "OUT", s["defenderMob"], s["weapon"]
            own_ab, foe_ab = s["attackerAbstates"], s["defenderAbstates"]
        elif s["defender"] == me:
            side, mob, own_weapon = "IN", s["attackerMob"], s["weapon"]
            own_ab, foe_ab = s["defenderAbstates"], s["attackerAbstates"]
        else:
            unattributed += 1
            continue
        if mob is None:
            # The mob was never announced in this conversation -- it was already in view when the capture
            # or the relog started. Counted, never guessed at.
            unattributed += 1
            continue
        key = (side, mob, s["level"], s["params"], own_ab, foe_ab, s["passives"], own_weapon)

        flags, tally = s["flagWord"], outcomes[key]
        tally["swings"] += 1
        # Bit names from the PDB: 0 iscritical, 2 ismissed, 3 isshieldblock. A blocked swing carries BOTH
        # missed and blocked in every frame of this capture -- roe_HitRate sets isshieldblock and then
        # returns 0.0, so the caller misses on it -- so these overlap by design and must never be summed
        # as though they partitioned the swings.
        if flags & 0x04:
            tally["missed"] += 1
        if flags & 0x08:
            tally["blocked"] += 1
        if flags & 0x01:
            tally["critical"] += 1
            tally["criticalDamage"] += s["damage"]
        if flags == 0 and s["damage"] > 0:
            buckets[key].append(s["damage"])

    rows = []
    for key, tally in outcomes.items():
        (side, mob, level, params, own_ab, foe_ab, passives, own_weapon) = key
        dmg = buckets.get(key, [])
        row = {"side": side, "mob": mob, "level": level, "passives": list(passives),
               "weapon": own_weapon,
               "selfAbstates": [list(p) for p in own_ab],
               "enemyAbstates": [list(p) for p in foe_ab],
               "params": dict(params),
               # `n` stays the count of CLEAN hits, so the damage-band check keeps reading the field it
               # always read. `swings` is the denominator for the rates.
               "n": len(dmg), "swings": tally["swings"],
               "missed": tally["missed"], "blocked": tally["blocked"],
               "critical": tally["critical"], "criticalDamage": tally["criticalDamage"],
               "damage": sorted(dmg)}
        if dmg:
            row.update({"min": min(dmg), "max": max(dmg), "mean": round(sum(dmg) / len(dmg), 1)})
        rows.append(row)
    rows.sort(key=lambda r: (r["side"], r["mob"], r["level"], -r["n"]))

    json.dump({"chat": chat, "buckets": rows, "playerHandlePerConv": player,
               "freeStat": free_stat, "chrclass": chrclass}, open(a.out, "w"), indent=1)

    kept = [r for r in rows if r["n"] >= a.min_hits]
    total = sum(r["swings"] for r in rows)
    print("%d clean hits of %d swings in %d state buckets (%d with n>=%d); %d unattributed\n"
          % (sum(r["n"] for r in rows), total, len(rows), len(kept), a.min_hits, unattributed))
    print("  side mob   lv   swings hit  miss blk  crit  min   max   mean   self-abstates")
    for r in kept:
        print("  %-4s %-5s %-4s %-6d %-4d %-4d %-4d %-5d %-5s %-5s %-6s %s"
              % (r["side"], r["mob"], r["level"], r["swings"], r["n"], r["missed"], r["blocked"],
                 r["critical"], r.get("min", "-"), r.get("max", "-"), r.get("mean", "-"),
                 ",".join("%d@%d" % (i, st) for i, st in r["selfAbstates"]) or "-"))

    # The aggregate is the headline. It is the only figure here that can be compared against a predicted
    # RATE without reconstructing a container, and the miss/block overlap is the one structural claim the
    # roll port makes that the wire can confirm on its own.
    for side in ("OUT", "IN"):
        rs = [r for r in rows if r["side"] == side]
        n = sum(r["swings"] for r in rs)
        if not n:
            continue
        miss = sum(r["missed"] for r in rs)
        blk = sum(r["blocked"] for r in rs)
        crit = sum(r["critical"] for r in rs)
        landed = n - miss
        print("\n  %s  %d swings: %d missed (%.1f%%), %d blocked (%.1f%%), "
              "%d critical of %d landed (%.1f permille)"
              % (side, n, miss, 100.0 * miss / n, blk, 100.0 * blk / n,
                 crit, landed, 1000.0 * crit / landed if landed else 0.0))
    print("\nwrote %s" % a.out)


if __name__ == "__main__":
    main()
