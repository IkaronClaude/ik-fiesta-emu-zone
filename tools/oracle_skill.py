#!/usr/bin/env python
"""What does a SKILL actually change about a swing? Ask the real functions.

    python tools/oracle_skill.py

Two claims are under test, both read from the disassembly and neither trustworthy until measured.

1. `roe_AttackPower@PhisycalSkill` (0x00506B40) reads FOUR columns out of the CLIENT skill row
   (`sdi_Activ` -> ActiveSkillInfo), not out of ActiveSkillInfoServer as the plan assumed:

       low  = low  + low  * MinWCRate/1000 + MinWC        // +0xDF, +0xDB
       high = high + high * MaxWCRate/1000 + MaxWC        // +0xE7, +0xE3

   The bounds are scaled INDEPENDENTLY, so a skill can widen its range and not merely shift it. That is
   the part worth measuring: a single-multiplier reading would agree on the midpoint and disagree at the
   ends, which is exactly the error a static read hides.

2. `roe_HitRate@PhisycalSkill` (0x00502D30) reads TWO columns out of the SERVER row (`sdi_ServInf`):
   `SkilPyHitRate` (+0x23) replaces the plain swing's hard-coded 850, and `SkillHitType` (+0x3A) switches
   to a formula that ignores aim and evasion completely and divides the two LEVELS instead.

The level-ratio branch is the one that must be measured rather than read. "Ignores aim and evasion" is a
strong claim, and the cheap way to be sure is to move the attacker's Dex by a factor of ten and watch the
answer not move.
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zone_oracle import ZoneOracle
from oracle_accessors import build_container, OFF, STAT, CLUSTER
from disasm import Code

AP = "?roe_AttackPower@RulesOfEngagementPhisycalSkill@@MAENPAUEngageArgument@@@Z"
HR = "?roe_HitRate@RulesOfEngagementPhisycalSkill@@UAENPAUEngageArgument@@@Z"
PHYSICAL = 0x858EDC

ACTIVE = {"MinWC": 0xDB, "MinWCRate": 0xDF, "MaxWC": 0xE3, "MaxWCRate": 0xE7}
SERVER = {"SkilPyHitRate": 0x23, "SkillHitType": 0x3A}
MASTERY = 24


class Harness:
    def __init__(self):
        self.o = ZoneOracle()
        self.o.write(PHYSICAL, struct.pack("<I", Code().syms["??_7RulesOfEngagementPhisycalSkill@@6B@"]))
        zero = self.o.alloc(16)
        self.default = self.o._emit(bytes([0xB8]) + struct.pack("<I", zero) + bytes([0xC3]))
        self.null = self.o._emit(bytes([0x33, 0xC0, 0xC3]))

    def obj(self, stats, level, kind):
        o = self.o
        cont = bytearray(build_container(stats))
        # Weapon mastery neutral, so it scales the result by exactly one and stays out of the way.
        struct.pack_into("<i", cont, OFF["PassiveSkill"] + CLUSTER + MASTERY * 4, 1000)
        p = o.alloc(len(cont)); o.write(p, bytes(cont))
        vt = o.alloc(0x1000); o.write(vt, struct.pack("<I", self.default) * (0x1000 // 4))
        ret = lambda q: o._emit(bytes([0xB8]) + struct.pack("<I", q) + bytes([0xC3]))
        o.write(vt + 0x430, struct.pack("<I", ret(p)))       # so_parameter
        o.write(vt + 0x4D0, struct.pack("<I", ret(kind)))
        o.write(vt + 0x4D8, struct.pack("<I", ret(level)))   # so_GetLevel
        o.write(vt + 0x5D0, struct.pack("<I", self.null))    # no item-action observer
        ob = o.alloc(0x3000); o.write(ob, struct.pack("<I", vt))
        return ob

    def arg(self, att, dfn, active=None, server=None, empower=0):
        o = self.o
        activ = o.alloc(0x400); o.write(activ, b"\0" * 0x400)
        for k, v in (active or {}).items():
            o.write(activ + ACTIVE[k], struct.pack("<I", v))
        serv = o.alloc(0x80); o.write(serv, b"\0" * 0x80)
        for k, v in (server or {}).items():
            o.write(serv + SERVER[k], struct.pack("<I", v))
        ski = o.alloc(0x300); o.write(ski, b"\0" * 0x300)
        o.write(ski + 0x00, struct.pack("<I", serv))         # sdi_ServInf
        o.write(ski + 0x04, struct.pack("<I", activ))        # sdi_Activ
        a = o.alloc(0x40); o.write(a, b"\0" * 0x40)
        o.write(a, struct.pack("<II", att, dfn))
        o.write(a + 0x08, struct.pack("<I", ski))
        o.write(a + 0x0C, struct.pack("<H", empower & 0xFFFF))
        o.write(a + 0x1C, struct.pack("<i", 1000))
        o.write(a + 0x28, struct.pack("<i", 1000))
        return a


def ftol(x):
    return float(int(x))


def main():
    h = Harness()
    att = h.obj({"Str": 371, "WCmin": 1709, "WCmax": 1840}, 82, 2)
    dfn = h.obj({"Con": 140, "AC": 102}, 61, 5)

    # ---- 1. the four ActiveSkillInfo columns -------------------------------------------------------
    # roe_AttackPower draws a random point in the range, so it cannot show a bound directly -- and
    # sampling it does not work either, because the emulated RNG is deterministic and 400 calls return
    # one value. PIN the draw instead: `rb_largerandom(span)` returns a value in [0, span], so a stub
    # returning 0 yields the LOW bound exactly and one returning its argument yields the HIGH bound.
    RB = "?rb_largerandom@RandomBox@@QAEHH@Z"
    # ⚠️ The stub must be ONE code shape whose behaviour varies through DATA. Rewriting the code between
    # calls does nothing: Unicorn caches the translated block, so the second stub is never executed and
    # both bounds come back identical -- which reads exactly like "the span is zero" and is not.
    mask = h.o.slot(0)
    h.o.stub_function(RB, bytes([0x8B, 0x44, 0x24, 0x04])          # mov eax,[esp+4]   (the span)
                          + bytes([0x23, 0x05]) + struct.pack("<I", mask)  # and eax,[mask]
                          + bytes([0xC2, 0x04, 0x00]))             # ret 4

    def bounds(active):
        a = h.arg(att, dfn, active=active)
        h.o.write(mask, struct.pack("<I", 0))
        lo = h.o.call(AP, [a], this=PHYSICAL, ret="double")
        h.o.write(mask, struct.pack("<I", 0xFFFFFFFF))
        hi = h.o.call(AP, [a], this=PHYSICAL, ret="double")
        return lo, hi

    base_lo, base_hi = bounds({})
    print("Skill row -> the weapon range it actually produces\n")
    print("  %-46s %10s %10s" % ("skill row", "low", "high"))
    print("  %-46s %10.1f %10.1f" % ("(none: plain bounds)", base_lo, base_hi))

    cases = [
        ("MinWCRate=1000  (double the LOW bound only)",  {"MinWCRate": 1000}),
        ("MaxWCRate=1000  (double the HIGH bound only)", {"MaxWCRate": 1000}),
        ("MinWC=500       (flat, LOW bound only)",       {"MinWC": 500}),
        ("MaxWC=500       (flat, HIGH bound only)",      {"MaxWC": 500}),
        ("all four together",
         {"MinWC": 500, "MinWCRate": 1000, "MaxWC": 500, "MaxWCRate": 1000}),
    ]
    for label, active in cases:
        lo, hi = bounds(active)
        p_lo = base_lo + base_lo * active.get("MinWCRate", 0) / 1000.0 + active.get("MinWC", 0)
        p_hi = base_hi + base_hi * active.get("MaxWCRate", 0) / 1000.0 + active.get("MaxWC", 0)
        ok = "OK" if (abs(lo - p_lo) < 1.5 and abs(hi - p_hi) < 1.5) else "MISMATCH"
        print("  %-46s %10.1f %10.1f   predicted %.1f / %.1f  %s"
              % (label, lo, hi, p_lo, p_hi, ok))

    # ---- 2. the two ActiveSkillInfoServer columns ---------------------------------------------------
    print("\n\nSkill hit rate -> which inputs it answers to\n")
    print("  %-52s %10s" % ("case", "hit rate"))

    def hit(server, attacker=None):
        a = h.arg(attacker or att, dfn, server=server)
        return h.o.call(HR, [a], this=PHYSICAL, ret="double")

    for rate in (850, 1700, 425):
        print("  %-52s %10.1f" % ("AsNormal, SkilPyHitRate=%d" % rate, hit({"SkilPyHitRate": rate})))

    print()
    for rate in (850, 1700):
        v = hit({"SkilPyHitRate": rate, "SkillHitType": 1})
        pred = ftol(rate * 82 / 61)
        print("  %-52s %10.1f   predicted %.1f"
              % ("ByLevelRatio, SkilPyHitRate=%d (lv 82 vs 61)" % rate, v, pred))

    # The claim that makes ByLevelRatio worth a separate member: aim must not matter.
    print("\n  Does aim move each branch? (attacker Dex 20 -> 2000)")
    keen = h.obj({"Str": 371, "WCmin": 1709, "WCmax": 1840, "Dex": 2000}, 82, 2)
    for label, server in (("AsNormal", {"SkilPyHitRate": 850}),
                          ("ByLevelRatio", {"SkilPyHitRate": 850, "SkillHitType": 1})):
        a, b = hit(server), hit(server, attacker=keen)
        verdict = "unchanged -- aim is ignored" if a == b else "MOVED %.1f -> %.1f" % (a, b)
        print("    %-14s %s" % (label, verdict))

    # And SkillHitType really is a != 0 test, not a == 1 test.
    print("\n  SkillHitType is tested against ZERO, not against 1:")
    for t in (0, 1, 2, 7):
        print("    type=%-3d -> %10.1f" % (t, hit({"SkilPyHitRate": 850, "SkillHitType": t})))


if __name__ == "__main__":
    main()
