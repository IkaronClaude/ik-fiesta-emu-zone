#!/usr/bin/env python
"""Differential check: the real roe_MinMA / roe_MaxMA / roe_MR against this port, same container.

    python tools/oracle_magical.py

`oracle_accessors.py` does this for the PHYSICAL trio (roe_MinWC / roe_MaxWC / roe_AC) and the physical
damage prediction is exact, 545/545. The magical prediction is not: every hit in `MageDamageLvl60.pcapng`
lands 1.28x-1.76x above the predicted ceiling, and the size of the miss varies inversely with the skill's
own MinMA -- the shape of a wrong BASE attack rather than a wrong coefficient.

Nothing had ever run the magical accessors. `DamageCalculator.MagicAttack`'s own note says the magic pair
is "NOT the mirror image of WeaponDamage, which is the natural assumption and is wrong" -- the FIELDS each
one reads were probed; the arithmetic was not. This runs them.

⚠️ The rule singleton's vtable pointer is set by a constructor the oracle never runs, so rather than hunt
the static object we allocate our own `this` and write the vtable in. The accessors only use `this` to
dispatch.
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zone_oracle import ZoneOracle

CLUSTER = 204
SLOTS = 51
OFF = {"Base": 0x000, "Item": 0x0CC, "ItemPowerRate": 0x264, "Upgrade": 0x3FC,
       "WeaponTitle": 0x594, "PassiveSkill": 0x72C, "AbnormalState": 0x8C4, "LastTune": 0xA5C}
STAT = {"Str": 0, "Con": 1, "Dex": 2, "Int": 3, "Men": 4, "WCmin": 5, "WCmax": 6,
        "AC": 7, "TH": 8, "TB": 9, "MAmin": 10, "MAmax": 11, "MR": 12}
RATE_ERASED = range(42, 49)

VT = {"NormalPY": 0x6D009C, "NormalMA": 0x6D00DC,
      "PhisycalSkill": 0x6D011C, "MagicalSkill": 0x6D015C}


def build_container(base_stats, item_stats=None):
    blob = bytearray(3504)
    for name, val in base_stats.items():
        struct.pack_into("<i", blob, OFF["Base"] + STAT[name] * 4, val)
    for name, val in (item_stats or {}).items():
        struct.pack_into("<i", blob, OFF["Item"] + STAT[name] * 4, val)
    for src, off in OFF.items():
        if src == "Base":
            continue
        for slot in range(SLOTS):
            struct.pack_into("<i", blob, off + CLUSTER + slot * 4,
                             0 if slot in RATE_ERASED else 1000)
    return bytes(blob)


def main():
    o = ZoneOracle()
    zero = o.alloc(16)

    def ret_ptr(p):
        return o._emit(bytes([0xB8]) + struct.pack("<I", p) + bytes([0xC3]))

    default = ret_ptr(zero)

    def make_object(container_bytes, level, kind):
        cont = o.alloc(len(container_bytes)); o.write(cont, container_bytes)
        vt = o.alloc(0x1000)
        o.write(vt, struct.pack("<I", default) * (0x1000 // 4))
        o.write(vt + 0x430, struct.pack("<I", ret_ptr(cont)))
        o.write(vt + 0x4D0, struct.pack("<I", ret_ptr(kind)))
        o.write(vt + 0x4D8, struct.pack("<I", ret_ptr(level)))
        obj = o.alloc(0x3000); o.write(obj, struct.pack("<I", vt))
        return obj

    def rule(name):
        this = o.alloc(0x40)
        o.write(this, struct.pack("<I", VT[name]))
        return this

    # ⭐ EXACTLY the container BucketGroundTruthTests.ExactlyMagical builds: Base[Int]=1 with
    # Base[MAmin]=wire-1 so the accessor returns the wire number, because the wire number IS the
    # accessor's output. If the real function disagrees with 622/702/468, the harness's whole magical
    # attack is built on a wrong reconstruction.
    mage = build_container({"Int": 1, "MAmin": 622 - 1, "MAmax": 702 - 1,
                            "Men": 1, "MR": 468 - 1})
    # The Orc, as MobParameters.StoreMob does.
    orc = build_container({"Str": 822, "Con": 140, "Dex": 147, "Int": 135, "Men": 112,
                           "AC": 102, "TB": 179, "MR": 127},
                          {"WCmin": 747, "WCmax": 1137, "TH": 267})

    port = {"mage lv60": {"roe_MinMA": 622.0, "roe_MaxMA": 702.0, "roe_MR": 468.0},
            "Orc lv61":  {"roe_MinMA": 0.0,   "roe_MaxMA": 0.0,   "roe_MR": 239.0}}

    for rname in ("NormalMA", "MagicalSkill"):
        this = rule(rname)
        print(f"=== rule {rname} ===")
        for label, blob, level, kind in (("mage lv60", mage, 60, 2), ("Orc lv61", orc, 61, 5)):
            a = make_object(blob, level, kind)
            arg = o.alloc(0x40); o.write(arg, struct.pack("<II", a, a))
            o.write(arg + 0x28, struct.pack("<i", 1000))
            got = {}
            for fn, sym in (("roe_MinMA", "?roe_MinMA@RulesOfEngagement@@QAENPAUEngageArgument@@@Z"),
                            ("roe_MaxMA", "?roe_MaxMA@RulesOfEngagement@@QAENPAUEngageArgument@@@Z"),
                            ("roe_MR",    "?roe_MR@RulesOfEngagement@@QAENPAUEngageArgument@@@Z")):
                got[fn] = o.call(sym, [arg], this=this, ret="double")
            flag = lambda k: "OK " if abs(got[k] - port[label][k]) < 0.5 else "DIFF"
            print("  %-10s roe_MinMA=%-11.4f (port %-9.1f %s)  roe_MaxMA=%-11.4f (port %-9.1f %s)  "
                  "roe_MR=%-10.4f (port %-8.1f %s)"
                  % (label, got["roe_MinMA"], port[label]["roe_MinMA"], flag("roe_MinMA"),
                     got["roe_MaxMA"], port[label]["roe_MaxMA"], flag("roe_MaxMA"),
                     got["roe_MR"], port[label]["roe_MR"], flag("roe_MR")))


if __name__ == "__main__":
    main()
