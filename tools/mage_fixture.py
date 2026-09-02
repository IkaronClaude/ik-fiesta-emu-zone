"""Turn a BOT packet log into the same fixture shape `damage_buckets.py` emits, for a MAGE.

The bot log is not a pcap, so `damage_buckets.py` cannot read it -- but the harness downstream only wants
the JSON. This walks the log keeping the same state (`0x1035` parameter vector, the mob roster, the skill
index -> skill id map, the free-stat allocation, the empower allocation) and emits `skillBuckets` in the
identical format.

⚠️ PARAM IDS. The fighter capture established 6=DmgMin, 7=DmgMax, 8=DEF. Comparing a level-59 Fighter's
vector against a level-21 Mage's identifies the magic pair: the Fighter's ids 11 and 12 sit at 48-59,
exactly its untrained INT, while the Mage's are 112 and 125. So **11=MAmin, 12=MAmax**, and 13 is MR
(Fighter 242-348, Mage 84).

⚠️ NOTHING HERE IS HARDCODED ANY MORE. An earlier version baked `level=21` and `chrclass=16` as literals.
Both were true when it was written and both are inputs to the damage prediction -- the class picks the
`Param<Class>Server.txt` that carries `JobChangeDmgUp`, and a job change moves a Mage from 16 to 17,
whose multiplier at level 21 is 1970 permille rather than 1000. A bot that levels or job-changes would
have silently kept predicting against the old character. Both now come off the wire, from
`NC_CHAR_CLIENT_BASE_CMD` and `NC_CHAR_CLIENT_SHAPE_CMD`, exactly as the pcap path reads them.
"""
import json
import sys
import collections

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
from parse_botlog import frames, u16, u32

# ⚠️ OPCODES, BY NAME NOT BY GUESS. On the ZONE stream 0x1801 is `NC_MAP_LOGIN_REQ`, not the character
# base info -- reading it as one yields a plausible-looking level 55 for a level-21 character, which is
# exactly the kind of wrong answer that does not announce itself. The character block is 0x1038 /
# `NC_CHAR_CLIENT_BASE_CMD` and the shape is 0x1039 / `NC_CHAR_CLIENT_SHAPE_CMD`; both were read off the
# frame NAMES the decoder prints.

MAmin, MAmax, MR = 11, 12, 13
SKILL_B0 = ["isdamage", "iscritical", "ismissed", "isshieldblock",
            "isheal", "isenchant", "isresist", "IsCostumWeapon"]

# ⚠️ isheal (0x10) and isenchant (0x20) are NOT excluded: a skill that damages AND applies an effect is
# still a clean damage sample. 0x100 is isDead -- a KILLING BLOW reports the damage APPLIED (clamped to
# the target's remaining HP), not the damage rolled, so it always sits below an honest floor.
SKILL_NOT_CLEAN = 0x02 | 0x04 | 0x08 | 0x40 | 0x100

FREE_STAT_NAMES = ["Strength", "Constitute", "Dexterity", "Intelligence",
                   "MentalPower", "RedistributePoint"]


def main(path, out):
    params, mob_of, skill_of_index, cast_opened = {}, {}, {}, {}
    skill_empower = {}
    free_stat, chrclass, level = {}, None, None
    hits = []
    order = 0
    for t, d, op, p in frames(path):
        order += 1
        if op == 0x1035 and p:                                  # CHANGEPARAMCHANGE
            n, off = p[0], 1
            for _ in range(n):
                if off + 5 > len(p):
                    break
                params[p[off]] = u32(p, off + 1)
                off += 5
        elif op == 0x1038 and len(p) >= 0x5D:                    # CHAR_CLIENT_BASE
            # Level at +25; CHARSTATDISTSTR (the free-stat allocation) at +0x57, one byte per stat.
            level = p[25]
            if not free_stat:
                free_stat = dict(zip(FREE_STAT_NAMES, p[0x57:0x5D]))
        elif op == 0x1039 and p and chrclass is None:             # CHAR_CLIENT_SHAPE
            # PROTO_AVATAR_SHAPE_INFO packs race:2, chrclass:5, gender:1 into byte 0.
            chrclass = (p[0] >> 2) & 0x1F
        elif op == 0x103D and len(p) >= 10:                       # CHAR_CLIENT_SKILL
            # {restempow u8, PartMark u8, nMaxNum u16} {chrregnum u32, number u16} then
            # number * PROTO_SKILLREADBLOCKCLIENT {skillid u16, cooltime u32, empow u16, mastery u32}.
            # `empow` is SKILL_EMPOWER -- damage:4, sp:4, keeptime:4, cooltime:4 -- reported on the BASE
            # rank of the skill line, not the rank cast.
            for i in range(u16(p, 8)):
                o = 10 + i * 12
                if o + 12 > len(p):
                    break
                if u16(p, o + 6):
                    skill_empower[u16(p, o)] = u16(p, o + 6)
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
            cast_opened[u16(p, 4)] = order
        elif op == 0x2452 and len(p) >= 5:
            index, caster, n = u16(p, 0), u16(p, 2), p[4]
            skill = skill_of_index.get(index)
            for i in range(n):
                o = 5 + i * 14
                if o + 14 > len(p):
                    break
                hits.append(dict(skill=skill, handle=u16(p, o), flag=u16(p, o + 2),
                                 dmg=u32(p, o + 4), mob=mob_of.get(u16(p, o)),
                                 level=level, params=dict(params)))

    # Bucket by identical state, exactly as damage_buckets.py does.
    by = collections.defaultdict(list)
    for h in hits:
        if h["skill"] is None or h["mob"] is None or h["level"] is None:
            continue
        if not (h["flag"] & 0x01) or h["flag"] & SKILL_NOT_CLEAN:
            continue
        by[(h["skill"], h["mob"], h["level"], tuple(sorted(h["params"].items())))].append(h["dmg"])

    rows = []
    for (skill, mob, lv, ps), dmgs in by.items():
        rows.append(dict(side="OUT", skill=skill, mob=mob, level=lv,
                         enemyAbstates=[], selfAbstates=[],
                         params={str(k): v for k, v in ps},
                         passives=[], weapon=None,
                         n=len(dmgs), min=min(dmgs), max=max(dmgs), damage=sorted(dmgs)))
    json.dump(dict(buckets=[], skillBuckets=rows, chat=[], playerHandlePerConv={},
                   freeStat=free_stat, chrclass=chrclass,
                   skillEmpower={str(k): v for k, v in sorted(skill_empower.items())}),
              open(out, "w"), indent=1)
    print("mage skill buckets: %d, hits: %d" % (len(rows), sum(r["n"] for r in rows)))
    print("level %s  chrclass %s  freeStat %s" % (level, chrclass, free_stat))
    print("empower %s" % skill_empower)
    for r in sorted(rows, key=lambda r: (r["skill"], -r["n"]))[:20]:
        print("  skill=%-6d mob=%-6d n=%-3d %d..%d  MAmin=%s MAmax=%s MR=%s"
              % (r["skill"], r["mob"], r["n"], r["min"], r["max"],
                 r["params"].get(str(MAmin)), r["params"].get(str(MAmax)), r["params"].get(str(MR))))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
