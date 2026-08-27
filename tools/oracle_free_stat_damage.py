#!/usr/bin/env python
"""Differential check: `roe_Damage`'s PER-RULE override, run for real under emulation.

    python tools/oracle_free_stat_damage.py

`roe_Damage` is overridden at vtable slot 4 by five of the eight rules, and this port had the BASE
function and treated it as the whole story. The overrides add a flat pair on top:

    damage = base(arg, attack, defend) + attacker->FreeStatStr() - defender->FreeStatCon()   (physical)
                                       + attacker->FreeStatInt() - defender->FreeStatMen()   (magical)

READING GAVE THE SHAPE; THIS GIVES THE ANSWER. The two accessors return level-keyed records out of
runtime-allocated globals (0x0DA50BC4), so a static read cannot see the VALUES — but the arithmetic can be
verified exactly by stubbing the accessors and running the real function, which is what this does.

The scale (1 free point = 1 damage) comes from the operator measuring it in-client on 2026-07-29: 30 points
into END produced a clean -30 on damage taken.

⚠️ THE VTABLE MUST BE FULLY POPULATED. The base `roe_Damage` calls the attacker's level accessor, so a
vtable with only the two interesting slots filled lands in unmapped memory and Unicorn reports
UC_ERR_INSN_INVALID -- which reads as "the function is unrunnable" rather than "the harness is short a
stub". Every slot here points at a default that returns a zeroed record.
"""
import struct, sys
sys.path.insert(0, r'C:/Projects/ik-fiesta-emu-zone/tools')
from zone_oracle import ZoneOracle
o = ZoneOracle()

zero_rec = o.alloc(16)                                   # safe target for every unstubbed virtual
def ret_ptr(p): return o._emit(bytes([0xB8]) + struct.pack("<I", p) + bytes([0xC3]))
def ret_int(v): return o._emit(bytes([0xB8]) + struct.pack("<I", v) + bytes([0xC3]))

LEVEL_SLOT = 0x4D8       # so_ply_Level, per roe_LevelGapDamageRevision's use of +0x4D8

def make_object(slot, value, level=61):
    rec = o.alloc(8); o.write(rec, b"\x00" + struct.pack("<H", value) + b"\x00"*5)
    default = ret_ptr(zero_rec)
    vt = o.alloc(0x800)
    o.write(vt, b"".join(struct.pack("<I", default) for _ in range(0x800 // 4)))
    o.write(vt + slot, struct.pack("<I", ret_ptr(rec)))
    o.write(vt + LEVEL_SLOT, struct.pack("<I", ret_int(level)))
    obj = o.alloc(0x40); o.write(obj, struct.pack("<I", vt))
    return obj

o.write(0x74d3ed, b"\x00")     # dl_ActivAll off
BASE = "?roe_Damage@RulesOfEngagement@@MAENPAUEngageArgument@@NN@Z"
OVER = "?roe_Damage@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@NN@Z"

print("%-8s %-8s %-8s %-8s | %-13s %-13s %s" % ("freeStr","freeCon","attack","defend","base","NormalPY","delta"))
ok = True
for freeStr, freeCon, atk, dfn in [(0,0,2080.,242.), (20,0,2080.,242.), (0,20,2080.,242.),
                                   (30,7,1569.,1517.), (5,60,900.,300.), (255,255,500.,100.)]:
    a = make_object(0x468, freeStr); d = make_object(0x474, freeCon)
    arg = o.alloc(0x40); o.write(arg, struct.pack("<II", a, d))
    o.write(arg + 0x28, struct.pack("<i", 1000))          # nBMPDamageRate
    try:
        base = o.call(BASE, [arg, ("double", atk), ("double", dfn)], this=0x858ED4, ret="double")
        over = o.call(OVER, [arg, ("double", atk), ("double", dfn)], this=0x858ED4, ret="double")
    except Exception as e:
        print("  FAILED:", e); ok = False; break
    delta, expect = over - base, freeStr - freeCon
    flag = "" if abs(delta - expect) < 1e-9 else "   <-- MISMATCH"
    print("%-8d %-8d %-8.0f %-8.0f | %-13.6f %-13.6f %+.6f (expect %+d)%s"
          % (freeStr, freeCon, atk, dfn, base, over, delta, expect, flag))
