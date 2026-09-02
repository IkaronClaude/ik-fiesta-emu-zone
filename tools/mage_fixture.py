"""Turn a BOT packet log into the same fixture shape `damage_buckets.py` emits, for the MAGE.

The bot log is not a pcap, so `damage_buckets.py` cannot read it -- but the harness downstream only wants
the JSON. This walks the log keeping the same state (`0x1035` parameter vector, the mob roster, the skill
index -> skill id map) and emits `skillBuckets` in the identical format.

⚠️ PARAM IDS. The fighter capture established 6=DmgMin, 7=DmgMax, 8=DEF. Comparing a level-59 Fighter's
vector against a level-21 Mage's identifies the magic pair: the Fighter's ids 11 and 12 sit at 48-59,
exactly its untrained INT, while the Mage's are 112 and 125. So **11=MAmin, 12=MAmax**, and 13 is MR
(Fighter 242-348, Mage 84).
"""
import json
import sys
import collections

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
from parse_botlog import frames, u16, u32

MAmin, MAmax, MR = 11, 12, 13
SKILL_B0 = ["isdamage", "iscritical", "ismissed", "isshieldblock",
            "isheal", "isenchant", "isresist", "IsCostumWeapon"]


def main(path, out):
    params, mob_of, skill_of_index = {}, {}, {}
    level = None
    hits = []
    for t, d, op, p in frames(path):
        if op == 0x1035 and p:                                  # CHANGEPARAMCHANGE
            n, off = p[0], 1
            for _ in range(n):
                if off + 5 > len(p):
                    break
                params[p[off]] = u32(p, off + 1)
                off += 5
        elif op == 0x1C08 and len(p) >= 5:                      # BRIEFINFO_REGENMOB {handle, mode, mobid}
            mob_of[u16(p, 0)] = u16(p, 3)
        elif op == 0x1C09 and p:                                # BRIEFINFO_MOB (bulk roster)
            for i in range(p[0]):
                o = 1 + i * 149
                if o + 5 > len(p):
                    break
                mob_of[u16(p, o)] = u16(p, o + 3)
        elif op == 0x244E and len(p) >= 6:
            skill_of_index[u16(p, 4)] = u16(p, 0)
        elif op == 0x2452 and len(p) >= 5:
            index, caster, n = u16(p, 0), u16(p, 2), p[4]
            skill = skill_of_index.get(index)
            for i in range(n):
                o = 5 + i * 14
                if o + 14 > len(p):
                    break
                flag = u16(p, o + 2)
                hits.append(dict(skill=skill, handle=u16(p, o), flag=flag, dmg=u32(p, o + 4),
                                 mob=mob_of.get(u16(p, o)), params=dict(params)))

    # Bucket by identical state, exactly as damage_buckets.py does.
    by = collections.defaultdict(list)
    for h in hits:
        if h["skill"] is None or h["mob"] is None:
            continue
        # ⭐ 0x100 is isDead -- a KILLING BLOW reports the damage APPLIED (clamped to the target's
        # remaining HP), not the damage rolled, so it always sits below an honest floor.
        if not (h["flag"] & 0x01) or h["flag"] & 0x14E:         # clean damaging hits only
            continue
        key = (h["skill"], h["mob"], tuple(sorted(h["params"].items())))
        by[key].append(h["dmg"])

    rows = []
    for (skill, mob, ps), dmgs in by.items():
        rows.append(dict(side="OUT", skill=skill, mob=mob, level=21,
                         enemyAbstates=[], selfAbstates=[],
                         params={str(k): v for k, v in ps},
                         passives=[], weapon=None,
                         n=len(dmgs), min=min(dmgs), max=max(dmgs), damage=sorted(dmgs)))
    json.dump(dict(buckets=[], skillBuckets=rows, chat=[], playerHandlePerConv={},
                   freeStat={}, chrclass=16), open(out, "w"), indent=1)
    print("mage skill buckets: %d, hits: %d" % (len(rows), sum(r["n"] for r in rows)))
    for r in sorted(rows, key=lambda r: r["skill"])[:14]:
        print("  skill=%-6d mob=%-6d n=%-3d %d..%d  MAmin=%s MAmax=%s MR=%s"
              % (r["skill"], r["mob"], r["n"], r["min"], r["max"],
                 r["params"].get(str(MAmin)), r["params"].get(str(MAmax)), r["params"].get(str(MR))))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
