"""Extract combat outcomes from a BOT packet log (not a pcap).

The bot's own log is `[hh:mm:ss.fff] DIR 0xOPCO d=D c=C len=N NAME` followed by indented hex rows, which
is a different shape from `pcap_decode.py`'s output, so `damage_buckets.py` cannot read it. This pulls
the three frames that matter and pairs casts to damage by the skill INDEX, the same way the pcap path
does.
"""
import re
import sys
import collections

FRAME = re.compile(r"^\[(\d\d:\d\d:\d\d\.\d+)\]\s+(\S+)\s+0x([0-9A-Fa-f]{4})\s+.*?len=(\d+)\s+(\S*)")
# ⚠️ The dump puts a DOUBLE space after byte 8 and an ASCII column after a `|`. A naive
# `([0-9A-Fa-f]{2} ?)+` stops at that double space and silently captures only the first 8 bytes --
# enough for a 6-byte cast frame, so casts parsed and every 19-byte damage frame was dropped.
HEX = re.compile(r"^\s+([0-9A-Fa-f]{4})\s+(.*?)(?:\|.*)?$")


def frames(path):
    """Yield (time, direction, opcode, payload_bytes)."""
    cur = None
    buf = bytearray()
    for line in open(path, encoding="utf-8", errors="replace"):
        m = FRAME.match(line)
        if m:
            if cur:
                yield cur + (bytes(buf),)
            cur, buf = (m.group(1), m.group(2), int(m.group(3), 16)), bytearray()
            continue
        h = HEX.match(line)
        if h and cur:
            body = re.sub(r"[^0-9A-Fa-f]", "", h.group(2))
            if len(body) % 2 == 0 and body:
                buf += bytes.fromhex(body)
    if cur:
        yield cur + (bytes(buf),)


u16 = lambda b, o: int.from_bytes(b[o:o + 2], "little")
u32 = lambda b, o: int.from_bytes(b[o:o + 4], "little")

# ⚠️ The SKILL flag layout, NOT the swing one -- a leading `isdamage` bit shifts every later name by one.
SKILL_B0 = ["isdamage", "iscritical", "ismissed", "isshieldblock",
            "isheal", "isenchant", "isresist", "IsCostumWeapon"]


def main(path):
    skill_of_index = {}
    hits = []
    swings = []
    for t, d, op, p in frames(path):
        if op == 0x244E and len(p) >= 6:                      # HIT_OBJ_START {skill, targetobj, index}
            skill_of_index[u16(p, 4)] = u16(p, 0)
        elif op == 0x2452 and len(p) >= 5:                    # HIT_DAMAGE
            index, caster, n = u16(p, 0), u16(p, 2), p[4]
            skill = skill_of_index.get(index)
            for i in range(n):
                o = 5 + i * 14
                if o + 14 > len(p):
                    break
                flag = u16(p, o + 2)
                hits.append(dict(t=t, skill=skill, caster=caster, handle=u16(p, o),
                                 flag=flag, dmg=u32(p, o + 4), resthp=u32(p, o + 8),
                                 flags=[nm for j, nm in enumerate(SKILL_B0) if p[o + 2] & (1 << j)]))
        elif op == 0x2448 and len(p) >= 16:                   # SWING_DAMAGE
            swings.append(dict(t=t, att=u16(p, 0), dfn=u16(p, 2), flag=u16(p, 4), dmg=u16(p, 6)))

    print("skill hits: %d   swings: %d   casts indexed: %d" % (len(hits), len(swings), len(skill_of_index)))
    by = collections.defaultdict(list)
    for h in hits:
        by[h["skill"]].append(h)
    print("\n  skill   n   clean  min   max   mean    flags seen")
    for sk in sorted(by, key=lambda s: (s is None, s)):
        v = by[sk]
        clean = [h["dmg"] for h in v if h["flag"] & 0x01 and not h["flag"] & 0x4E]
        seen = sorted({f for h in v for f in h["flags"]})
        print("  %-6s %-3d %-5d %-5s %-5s %-7s %s" % (
            sk, len(v), len(clean), min(clean) if clean else "-", max(clean) if clean else "-",
            round(sum(clean) / len(clean), 1) if clean else "-", ",".join(seen)))

    cs = [s["dmg"] for s in swings if s["flag"] == 0 and s["dmg"] > 0]
    if cs:
        print("\n  plain swings: %d clean of %d, %d..%d mean %.1f"
              % (len(cs), len(swings), min(cs), max(cs), sum(cs) / len(cs)))


if __name__ == "__main__":
    main(sys.argv[1])
