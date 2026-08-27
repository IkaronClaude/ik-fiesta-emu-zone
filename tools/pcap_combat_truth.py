#!/usr/bin/env python
"""Extract per-swing combat GROUND TRUTH from a pcapng, as JSON the C# tests can check against.

    python tools/pcap_combat_truth.py --pcap Z:/Damage.pcapng --port 9022 --out truth.json
    python tools/pcap_combat_truth.py --decoded dump.txt --pcap Z:/Damage.pcapng --out truth.json

The simulation is only "1:1" if its numbers match a real server's. This produces the numbers to match:
every `NC_BAT_SWING_DAMAGE_CMD` in the capture, with its attacker, defender, damage and decoded flags,
plus the handle -> mob-id roster and the player's level, so a test can pick "every clean hit Orc landed
on the player" and compare that against what `DamageCalculator` predicts for the same pair.

WHY IT SHELLS OUT rather than reimplementing. `fiesta-proxy/tools/pcap_decode.py` already does framing,
the per-conversation handshake seed and the asymmetric C->S XOR; `analyse_damage.py roster` already
unrolls the variable-length `NC_BRIEFINFO_MOB_CMD` array into handle -> mob id. Both are proven against
these captures. Re-deriving either would be a second implementation to keep honest.

⚠️ THREE THINGS THAT MATTER, all learned the hard way in this ecosystem:

 1. FILE ORDER IS THE CHRONOLOGY. `pcap_decode.py` interleaves both directions by timestamp; the `@`
    offsets printed on each line are PER-DIRECTION byte offsets, not a clock. Ordering or windowing on
    `@` silently drops one side of the conversation.

 2. PARSE THE HEX, NOT THE FIELD LINES. The struct printer renders `flag` as a type name rather than a
    value, and a PDB field order is not always the serialised order. The 16-byte hex row is the wire.

 3. `damage == 0` IS NOT A DECODE FAILURE — it is a miss, a block or an immunity, and the flag says
    which. Filtering zeros out silently would throw away the miss rate, which is its own ground truth.
"""
import argparse, json, os, re, subprocess, sys

PCAP_DECODE = r"C:/Projects/fiesta-proxy/tools/pcap_decode.py"
ANALYSE = r"C:/Projects/fiesta-proxy/tools/analyse_damage.py"
XOR_TABLE = os.environ.get("XOR_TABLE_PATH", r"C:/Projects/ik-fiesta-bots/xor-table.hex")

# PROTO_NC_BAT_SWING_DAMAGE_CMD::flag, from Fiesta.pdb. Byte 0 then byte 1, LSB first.
#
# ⚠️ INCOMPLETE, and measured to be so. In Damage.pcapng the flag word takes values 0x0004 and 0x000C
# (damage always 0 -> miss / block), 0x0001 (damage ~2x the plain range -> a CRITICAL, though this table
# calls bit 0 "isdamage"), and a family with only HIGH bits set -- 0x2800, 0x2A00, 0x3200, 0x3E00, 0x5A00,
# 0x7400, 0xF200 -- whose damage sits above what a maximum roll can produce. Those high bits have no names
# here at all. Consumers must test `flagWord == 0`, never `flags == []`.
FLAGS_B0 = ["isdamage", "iscritical", "ismissed", "isshieldblock",
            "isheal", "isenchant", "isresist", "IsCostumWeapon"]
FLAGS_B1 = ["isDead", "isImmune", "IsCostumShield"]

FRAME = re.compile(r"^\s+(S<-|C->)\s+@\s*(\d+)\s+\[0x([0-9A-Fa-f]{4})\]\s+(\S+)")
CONV = re.compile(r"^==== server (\S+) <-> client (\S+) ====")
HEXROW = re.compile(r"^\s+([0-9a-f]{4})\s+((?:[0-9a-f]{2} )+)")


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


def roster(pcap, stream):
    """handle -> mob id, via `analyse_damage.py roster` (it unrolls the briefinfo array)."""
    env = dict(os.environ, XOR_TABLE_PATH=XOR_TABLE, PYTHONIOENCODING="utf-8")
    cmd = [sys.executable, ANALYSE, pcap, "roster"]
    if stream is not None:
        cmd += ["--stream", str(stream)]
    out = subprocess.run(cmd, capture_output=True, env=env,
                         cwd=os.path.dirname(ANALYSE)).stdout.decode("utf-8", "replace")
    mobs = {}
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 4 and parts[0].isdigit() and parts[1].isdigit():
            mobs[int(parts[0])] = int(parts[1])
    return mobs


STATS = re.compile(r"^# PLAYER STATS @ window start pid (\d+).*?:\s*(.+)$")


def player_stats(pcap, stream, start, end):
    """The server-reported accessor OUTPUTS at a moment: DEF, DmgMin/DmgMax, Aim, Evasion, MDef, primaries.

    These are what the client displays, and they are the right thing to check a damage prediction against
    because they are the SERVER'S numbers, not ours -- comparing our `roe_AC` to our own container would
    only prove the code agrees with itself.

    `analyse_damage.py damage` prints them as a header for whatever window it is given, and WARNS when a
    stat changes inside that window. Passing a narrow window is therefore the point: a wide one silently
    averages two configurations.
    """
    env = dict(os.environ, XOR_TABLE_PATH=XOR_TABLE, PYTHONIOENCODING="utf-8")
    cmd = [sys.executable, ANALYSE, pcap, "damage"]
    if stream is not None:
        cmd += ["--stream", str(stream)]
    if start is not None:
        cmd += ["--start-packet", str(start)]
    if end is not None:
        cmd += ["--end-packet", str(end)]
    out = subprocess.run(cmd, capture_output=True, env=env,
                         cwd=os.path.dirname(ANALYSE)).stdout.decode("utf-8", "replace")
    stats, changed = {}, "stats CHANGED inside this window" in out
    for line in out.splitlines():
        m = STATS.match(line)
        if not m:
            continue
        for pair in m.group(2).split():
            if "=" in pair:
                k, v = pair.split("=", 1)
                if v.lstrip("-").isdigit():
                    stats[k] = int(v)
        break
    return stats, changed


def flag_names(lo, hi):
    """Named bits only — see `flagWord` for the authoritative "was this a plain hit" test.

    ⚠️ NEVER decide "clean hit" from this list being empty. The two byte tables below cover 8 + 3 bits of a
    16-bit field, so a flag word like 0x2800 (bits 11 and 13, both unnamed) yields NO names and reads as a
    plain hit — and in `Damage.pcapng` those are precisely the swings that exceed what a maximum roll can
    produce. An unknown bit is not the absence of a flag.
    """
    names = [n for i, n in enumerate(FLAGS_B0) if lo & (1 << i)]
    names += [n for i, n in enumerate(FLAGS_B1) if hi & (1 << i)]
    return names


def parse(lines):
    """Frames in FILE order, with their hex payload reassembled across rows.

    ⚠️ A payload longer than 16 bytes prints as SEVERAL hex rows (`0000`, `0010`, …). Matching only the
    `0000` row truncates every larger struct to its first 16 bytes — which read `NC_BAT_TARGETINFO_CMD`'s
    level (at @27) as absent and reported every player as level 0.
    """
    swings, levels, chat, statdist, allocs, order = [], {}, [], {}, [], 0
    shape = {"chrclass": None}
    pending, buf = None, bytearray()
    conv = -1

    def flush():
        nonlocal pending, buf
        if pending is not None and buf:
            emit(pending, bytes(buf))
        pending, buf = None, bytearray()

    def emit(name, raw):
        if name == "NC_BAT_SWING_DAMAGE_CMD" and len(raw) >= 16:
            lo, hi = raw[4], raw[5]
            swings.append({
                "order": order,
                "conv": conv,
                "attacker": int.from_bytes(raw[0:2], "little"),
                "defender": int.from_bytes(raw[2:4], "little"),
                "damage": int.from_bytes(raw[6:8], "little"),
                "restHp": int.from_bytes(raw[8:12], "little"),
                "flagWord": lo | (hi << 8),
                "flags": flag_names(lo, hi),
            })
        elif name == "NC_ACT_CHAT_REQ" and len(raw) > 2:
            # The operator narrates the experiment in chat while capturing, so these lines ARE the legend
            # for the packet log. Payload is [itemLinkDataCount u8][len u8][text].
            text = raw[2:2 + raw[1]].decode("latin-1", "replace").strip()
            if text and not text.startswith("&"):
                chat.append({"order": order, "text": text})
        elif name == "NC_CHAR_STAT_INCPOINTSUC_ACK" and raw:
            # One accepted free-stat point. NC_CHAR_CLIENT_BASE_CMD reports the allocation as it stood at
            # LOGIN, so a session that spends points drifts from it -- these acks are how the wire says so,
            # and without them the distribution is stale for any window after an allocation.
            allocs.append((order, raw[0]))
        elif name == "NC_CHAR_CLIENT_BASE_CMD" and len(raw) >= 0x5D:
            # CHARSTATDISTSTR - the character's FREE-STAT ALLOCATION, one byte per stat, at +0x57.
            #
            # This is the input to `roe_Damage`'s per-rule override: so_ply_FreeStatStr reads byte[0] and
            # so_ply_FreeStatCon byte[1] of this record, then indexes a per-points table.
            #
            # Offset verified against the operator's own chat narration rather than by counting: byte[1]
            # goes 3 -> 50 across the capture's three sessions, and the chat reads "Okay END was +3, now
            # going to +20" then "END to 50". Placing the record at +0x56 instead (the obvious-looking
            # six small bytes) puts 90 in Strength and moves the wrong field.
            # FIRST one only. Every session sends its own, and the later ones report a distribution the
            # window has not reached yet -- taking the last gives session 3's 50 points where the window
            # has 3, and the allocation roll-forward then compounds the error.
            if statdist:
                pass
            else:
                statdist.update(zip(
                    ["Strength", "Constitute", "Dexterity", "Intelligence", "MentalPower",
                     "RedistributePoint"],
                    raw[0x57:0x5D]))
        elif name == "NC_CHAR_CLIENT_SHAPE_CMD" and raw:
            # PROTO_AVATAR_SHAPE_INFO packs race:2, chrclass:5, gender:1 into byte 0. The CLASS is the
            # only thing here the damage engine needs, and it needs it: `so_ply_JobChangeDamageUp` reads
            # `JobChangeDmgUp` out of THIS class's `Param<Class>Server.txt` row, which is 1000 for a base
            # class and up to 2000 just after a job change. Without the class that multiplier cannot be
            # looked up, and every outgoing prediction is short by it.
            #
            # FIRST one only, for the same reason the stat distribution takes the first: a relog re-sends
            # it, and mixing sessions is what the conversation split exists to prevent.
            if shape["chrclass"] is None:
                shape["chrclass"] = (raw[0] >> 2) & 0x1F
        elif name == "NC_BAT_TARGETINFO_CMD" and len(raw) > 27:
            # order u8, targethandle u16, five u32, then targetlevel u8 at @27.
            levels[int.from_bytes(raw[1:3], "little")] = raw[27]

    for line in lines:
        c = CONV.match(line)
        if c:
            # A relog opens a NEW conversation, and the player's handle, gear and stats can all differ
            # across one. Merging them makes a damage histogram mix configurations -- the same mistake
            # `analyse_damage.py` guards with its own --stream flag.
            flush()
            conv += 1
            continue
        m = FRAME.match(line)
        if m:
            flush()
            pending = m.group(4)
            order += 1
            continue
        h = HEXROW.match(line)
        if h and pending is not None:
            at = int(h.group(1), 16)
            data = bytes.fromhex(h.group(2).replace(" ", ""))
            if at == len(buf):
                buf.extend(data)
            continue
    flush()
    return swings, levels, chat, statdist, allocs, shape


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pcap", required=True)
    ap.add_argument("--port", type=int, help="zone port; VARIES per map (Uruga 9022, RouN 9016)")
    ap.add_argument("--stream", type=int, help="stream index for the roster lookup")
    ap.add_argument("--decoded", help="reuse/keep the decoded dump here instead of re-running")
    ap.add_argument("--from-chat", help="keep only swings AFTER the chat line containing this text")
    ap.add_argument("--to-chat", help="...and BEFORE the one containing this text")
    ap.add_argument("--start-packet", type=int, help="narrow the player-stat snapshot to a clean window")
    ap.add_argument("--end-packet", type=int)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    lines = decode(a.pcap, a.port, a.decoded)
    swings, levels, chat, statdist, allocs, shape = parse(lines)
    mobs = roster(a.pcap, a.stream)

    # The player is the handle that appears in combat and is NOT in the mob roster. TARGETINFO gives its
    # level; a mob's level comes from game data, so only the player's is needed from the wire.
    seen = {s["attacker"] for s in swings} | {s["defender"] for s in swings}
    players = sorted(h for h in seen if h not in mobs)

    # WINDOW THE SWINGS THE SAME WAY THE STATS ARE WINDOWED. Pinning the stat snapshot to a clean packet
    # range while selecting swings across the WHOLE session is the same mistake as merging conversations,
    # one level down: the histogram then mixes configurations the header claims are constant. The window
    # is expressed in the operator's own chat annotations because that is what marks the experiment.
    def order_of(fragment):
        for c in chat:
            if fragment.lower() in c["text"].lower():
                return c["order"]
        raise SystemExit("no chat line matching %r; try --list-chat" % fragment)

    lo = order_of(a.from_chat) if a.from_chat else None
    hi = order_of(a.to_chat) if a.to_chat else None
    if lo is not None or hi is not None:
        before = len(swings)
        swings = [s for s in swings
                  if (lo is None or s["order"] > lo) and (hi is None or s["order"] < hi)]
        print("chat window: kept %d of %d swings" % (len(swings), before))

    # Roll the login-time distribution forward by every point accepted BEFORE the window opens.
    STAT_INDEX = {0: "Strength", 1: "Constitute", 2: "Dexterity", 3: "Intelligence", 4: "MentalPower"}
    spent = 0
    for o, idx in allocs:
        if lo is not None and o > lo:
            break
        key = STAT_INDEX.get(idx)
        if key:
            statdist[key] = statdist.get(key, 0) + 1
            spent += 1
    if spent:
        print("rolled %d free-stat point(s) allocated before the window into the distribution" % spent)

    stats, mixed = player_stats(a.pcap, a.stream, a.start_packet, a.end_packet)

    truth = {
        "source": os.path.basename(a.pcap),
        "chat": chat,
        "freeStatDistribution": statdist,
        "playerStats": stats,
        "playerStatsMixedInWindow": mixed,
        "chrclass": shape["chrclass"],
        "players": [{"handle": h, "level": levels.get(h, 0)} for h in players],
        "mobHandles": {str(k): v for k, v in sorted(mobs.items())},
        "swings": swings,
    }
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(truth, f, indent=1)

    clean = [s for s in swings if s["flagWord"] == 0 and s["damage"] > 0]
    if mixed:
        print("!! player stats CHANGE inside the chosen window -- narrow it with --start-packet/--end-packet")
    print("free-stat distribution: %s" % statdist)
    print("player stats: %s" % ", ".join("%s=%d" % kv for kv in sorted(stats.items())))
    print("%s: %d swings, %d clean (no flags, non-zero), %d mob handles, players %s"
          % (truth["source"], len(swings), len(clean), len(mobs),
             ", ".join("%d(lv%d)" % (p["handle"], p["level"]) for p in truth["players"])))
    print("wrote %s" % a.out)


if __name__ == "__main__":
    main()
