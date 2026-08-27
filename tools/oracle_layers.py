#!/usr/bin/env python
"""Which container LAYERS does roe_MinWC / roe_MaxWC actually read? Answered by running them.

    python tools/oracle_layers.py

`c_MakeTotal` folds nine layers into the total the client is shown. The weapon accessors read a DIFFERENT,
smaller set — so a bonus can be visible in the client's damage display and contribute nothing to the damage
actually dealt. Guessing which is which from the disassembly is how this port has gone wrong before, so
this puts a marker bonus in one layer at a time and asks the real function.

RESULT (2026-08-27), +1197 placed in each layer in turn, weapon 512-643 and Str 371:

    (none)          883 .. 1014   ratio 1.148
    Upgrade        2080 .. 2211   ratio 1.063   <- reaches the accessors
    AbnormalState  2080 .. 2211   ratio 1.063   <- reaches the accessors
    LastTune        883 .. 1014   ratio 1.148   <- INVISIBLE to them
    WeaponTitle     883 .. 1014   ratio 1.148   <- INVISIBLE
    PassiveSkill    883 .. 1014   ratio 1.148   <- INVISIBLE
    ItemPowerRate   883 .. 1014   ratio 1.148   <- INVISIBLE

⚠️ This matters for any harness that reconstructs a character from the wire. The client is sent CLUSTER
TOTALS, so a reconstruction that drops the whole displayed figure into `Base` gives the accessors a bonus
the server may never have handed them -- inflating the attack and, because the bonus is flat, compressing
the min:max ratio. That is a silent error the magnitude alone will not reveal; the RATIO is what exposes it.
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zone_oracle import ZoneOracle

CLUSTER = 204
OFF = {"Base": 0x000, "Item": 0x0CC, "ItemPowerRate": 0x264, "Upgrade": 0x3FC,
       "WeaponTitle": 0x594, "PassiveSkill": 0x72C, "AbnormalState": 0x8C4, "LastTune": 0xA5C}
ROE, VT = 0x858ED4, 0x6D009C


def main():
    o = ZoneOracle()
    o.write(ROE, struct.pack("<I", VT))          # the ctor never runs under emulation
    zero = o.alloc(16)
    ret = lambda p: o._emit(bytes([0xB8]) + struct.pack("<I", p) + bytes([0xC3]))
    default = ret(zero)                          # MUST be a pointer to zeros, never NULL

    print("+1197 placed in each layer in turn (weapon 512-643, Str 371):\n")
    for layer in [None, "Upgrade", "AbnormalState", "LastTune", "WeaponTitle",
                  "PassiveSkill", "ItemPowerRate"]:
        b = bytearray(3504)
        struct.pack_into("<i", b, OFF["Base"], 371)
        struct.pack_into("<i", b, OFF["Item"] + 5 * 4, 512)
        struct.pack_into("<i", b, OFF["Item"] + 6 * 4, 643)
        if layer:
            struct.pack_into("<i", b, OFF[layer] + 5 * 4, 1197)
            struct.pack_into("<i", b, OFF[layer] + 6 * 4, 1197)
        for src, off in OFF.items():
            if src == "Base":
                continue
            for s in range(51):
                struct.pack_into("<i", b, off + CLUSTER + s * 4, 0 if 42 <= s <= 48 else 1000)

        cont = o.alloc(3504); o.write(cont, bytes(b))
        vt = o.alloc(0x1000); o.write(vt, struct.pack("<I", default) * (0x1000 // 4))
        o.write(vt + 0x430, struct.pack("<I", ret(cont)))
        o.write(vt + 0x4D8, struct.pack("<I", ret(82)))
        obj = o.alloc(0x3000); o.write(obj, struct.pack("<I", vt))
        arg = o.alloc(0x40); o.write(arg, struct.pack("<II", obj, obj))
        o.write(arg + 0x28, struct.pack("<i", 1000))

        lo = o.call("?roe_MinWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", [arg], this=ROE, ret="double")
        hi = o.call("?roe_MaxWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z", [arg], this=ROE, ret="double")
        seen = "reaches the accessors" if lo > 1500 else "INVISIBLE to them"
        print("  %-16s %7.0f .. %-7.0f ratio %.3f   %s" % (layer or "(none)", lo, hi, hi / lo, seen))


if __name__ == "__main__":
    main()
