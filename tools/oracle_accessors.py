#!/usr/bin/env python
"""Differential check: the real roe_MinWC / roe_MaxWC / roe_AC against this port, same container.

    python tools/oracle_accessors.py

The pcap harness reconstructs a captured character and predicts a damage band. If the band is wrong, the
fault is either the ACCESSORS (what the container turns into attack and defence) or the PIPELINE after
them. This separates the two: it builds the same container the harness builds, hands it to the REAL
functions under emulation, and prints both answers side by side.

⚠️ The rule singletons' vtable pointers are set by a constructor the oracle never runs, so `this` at
0x858ED4 is uninitialised until we write the vtable in by hand. Without that the first virtual call lands
in garbage and Unicorn reports UC_ERR_INSN_INVALID -- which reads as "the function is unrunnable".
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zone_oracle import ZoneOracle

CLUSTER = 204
SLOTS = 51
# Parameter::Container, from the PDB
OFF = {"Base": 0x000, "Item": 0x0CC, "ItemPowerRate": 0x264, "Upgrade": 0x3FC,
       "WeaponTitle": 0x594, "PassiveSkill": 0x72C, "AbnormalState": 0x8C4, "LastTune": 0xA5C}
STAT = {"Str": 0, "Con": 1, "Dex": 2, "Int": 3, "Men": 4, "WCmin": 5, "WCmax": 6,
        "AC": 7, "TH": 8, "TB": 9, "MAmin": 10, "MAmax": 11, "MR": 12}
# ParameterCluster.Rate(): 1000 everywhere except slots 42..48, which the live rate eraser holds at 0.
RATE_ERASED = range(42, 49)

ROE_NORMALPY = 0x858ED4
VT_NORMALPY = 0x6D009C


def build_container(base_stats):
    blob = bytearray(3504)
    for name, val in base_stats.items():
        struct.pack_into("<i", blob, OFF["Base"] + STAT[name] * 4, val)
    for src, off in OFF.items():
        if src == "Base":
            continue
        for slot in range(SLOTS):                      # Rate half sits at +0xCC of each pair
            v = 0 if slot in RATE_ERASED else 1000
            struct.pack_into("<i", blob, off + CLUSTER + slot * 4, v)
    return bytes(blob)


def main():
    o = ZoneOracle()
    o.write(ROE_NORMALPY, struct.pack("<I", VT_NORMALPY))   # the ctor never ran; do its one job

    zero = o.alloc(16)
    def ret_ptr(p): return o._emit(bytes([0xB8]) + struct.pack("<I", p) + bytes([0xC3]))
    default = ret_ptr(zero)

    def make_object(container_bytes, level, kind):
        cont = o.alloc(len(container_bytes)); o.write(cont, container_bytes)
        vt = o.alloc(0x1000)
        # ONE shared default stub, repeated as a pointer -- emitting 1024 separate stubs exhausts the
        # oracle's code arena and surfaces as UC_ERR_WRITE_UNMAPPED, which looks like a memory bug.
        o.write(vt, struct.pack("<I", default) * (0x1000 // 4))
        o.write(vt + 0x430, struct.pack("<I", ret_ptr(cont)))        # Parameter::Container getter
        o.write(vt + 0x4D0, struct.pack("<I", ret_ptr(kind)))        # object type (2 player / 5 monster)
        o.write(vt + 0x4D8, struct.pack("<I", ret_ptr(level)))       # level
        obj = o.alloc(0x40); o.write(obj, struct.pack("<I", vt))
        return obj

    # The captured character, exactly as PcapGroundTruthTests.RebuildPlayer builds it.
    player = build_container({"Str": 371, "Con": 302, "Dex": 246, "Int": 201, "Men": 255,
                              "WCmin": 1709, "WCmax": 1840, "AC": 1215 - 302,
                              "MR": 550, "TH": 550, "TB": 432})
    # The Orc, as MobCombatant.Build does: primaries from MobInfoServer, weapon staged into Item.Plus.
    orc = bytearray(build_container({"Str": 822, "Con": 140, "Dex": 147, "Int": 135, "Men": 112,
                                     "AC": 102, "TB": 179, "MR": 127}))
    for slot, val in (("WCmin", 747), ("WCmax", 1137), ("TH", 267)):
        struct.pack_into("<i", orc, OFF["Item"] + STAT[slot] * 4, val)
    orc = bytes(orc)

    objects = {}
    for label, blob, level in (("player lv82", player, 82), ("Orc lv61", orc, 61)):
        a = make_object(blob, level, 2 if "player" in label else 5)
        arg = o.alloc(0x40); o.write(arg, struct.pack("<II", a, a))
        o.write(arg + 0x28, struct.pack("<i", 1000))
        vals = {}
        for fn, sym in (("roe_MinWC", "?roe_MinWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z"),
                        ("roe_MaxWC", "?roe_MaxWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z"),
                        ("roe_AC",    "?roe_AC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z")):
            vals[fn] = o.call(sym, [arg], this=ROE_NORMALPY, ret="double")
        print("%-12s  roe_MinWC=%-12.4f roe_MaxWC=%-12.4f roe_AC=%.4f" %
              (label, vals["roe_MinWC"], vals["roe_MaxWC"], vals["roe_AC"]))
        objects[label] = a

    # head-on angle table, so the angle term is neutral and the comparison is about the pipeline
    o.write(0x0AF0D0F0, struct.pack("<I", o.alloc(4)))
    tbl = o.alloc(256); o.write(tbl, struct.pack("<H", 1000) * 128)
    o.write(0x0AF0D0F0, struct.pack("<I", tbl))

    print()
    for label, (att, dfn) in (("player -> Orc", ("player lv82", "Orc lv61")),
                              ("Orc -> player", ("Orc lv61", "player lv82"))):
        try:
            dmg, _ = calc_damage(o, objects[att], objects[dfn], ROE_NORMALPY)
            print("%-14s real roe_CalcDamage = %d" % (label, dmg))
        except Exception as e:
            print("%-14s roe_CalcDamage FAILED: %s" % (label, e))


def calc_damage(o, att, dfn, ROE, damagerate=1000):
    """The WHOLE pipeline: roe_CalcDamage on a fully built EngageArgument."""
    arg = o.alloc(0x40)
    o.write(arg, struct.pack("<II", att, dfn))
    o.write(arg + 0x1C, struct.pack("<i", damagerate))    # damagerate
    o.write(arg + 0x28, struct.pack("<i", 1000))          # nBMPDamageRate
    return o.call("?roe_CalcDamage@RulesOfEngagement@@UAEHPAUEngageArgument@@@Z",
                  [arg], this=ROE, ret="int"), arg


if __name__ == "__main__":
    main()
