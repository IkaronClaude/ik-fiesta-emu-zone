#!/usr/bin/env python
"""Does roe_AttackPower@MagicalSkill apply the trailing WEAPON MASTERY rate? Ask the real function.

    python tools/oracle_magical_mastery.py

`EngagementRule.AppliesWeaponMastery` singles MagicalSkill out as the ONE rule that skips the trailing
`ApplyRate(value, Rate(PassiveSkill)[MagicalWeaponMastery])`. That claim sits exactly where the magical
residual lives -- the physical ground-truth test passes a mastery rate (1150) and is exact, while the
magical one passes none at all -- so it is worth RUNNING rather than trusting.

⚠️ WITH A CONTROL, and the control is the whole point. Varying only the magical slot proves nothing: if the
harness never reaches the multiply then nothing moves either way, and that reads as confirmation. An
earlier run of exactly this experiment did that and looked like a clean "no". Slot 24
(PhisycalWeaponMastery) is varied alongside slot 25 (MagicalWeaponMastery), and the PHYSICAL answer must
move for the magical non-answer to mean anything.

    Rate[mag=25]  Rate[phys=24]   magical AP   physical AP
    1000          1000                 949.0         949.0
    2000          1000                 949.0         949.0
    1000          2000                 949.0        1898.0     <- control moves
    2000          2000                 949.0        1898.0

So the port is RIGHT: the magical skill rule does not apply weapon mastery, and the asymmetry between the
two ground-truth tests is correct rather than a missing input.
"""
import struct, sys, os
sys.path.insert(0, "C:/Projects/ik-fiesta-emu-zone/tools")
from oracle_skill import Harness
from oracle_accessors import build_container, OFF, STAT, CLUSTER
from disasm import Code

AP_MA = "?roe_AttackPower@RulesOfEngagementMagicalSkill@@MAENPAUEngageArgument@@@Z"
AP_PY = "?roe_AttackPower@RulesOfEngagementPhisycalSkill@@MAENPAUEngageArgument@@@Z"
RB = "?rb_largerandom@RandomBox@@QAEHH@Z"
ACTIVE_MA = {"MinMA": 0xEB, "MaxMA": 0xF3}
PHYS_MASTERY, MAG_MASTERY = 24, 25

h = Harness(); o = h.o
c = Code()
magical = o.alloc(0x40)
o.write(magical, struct.pack("<I", c.syms["??_7RulesOfEngagementMagicalSkill@@6B@"]))
physical = o.alloc(0x40)
o.write(physical, struct.pack("<I", c.syms["??_7RulesOfEngagementPhisycalSkill@@6B@"]))

def obj_with(mag_rate, phys_rate):
    cont = bytearray(build_container({"Int": 1, "MAmin": 621, "MAmax": 701,
                                      "Str": 1, "WCmin": 621, "WCmax": 701}))
    struct.pack_into("<i", cont, OFF["PassiveSkill"] + CLUSTER + PHYS_MASTERY*4, phys_rate)
    struct.pack_into("<i", cont, OFF["PassiveSkill"] + CLUSTER + MAG_MASTERY*4, mag_rate)
    p = o.alloc(len(cont)); o.write(p, bytes(cont))
    vt = o.alloc(0x1000); o.write(vt, struct.pack("<I", h.default) * (0x1000//4))
    ret = lambda q: o._emit(bytes([0xB8]) + struct.pack("<I", q) + bytes([0xC3]))
    o.write(vt + 0x430, struct.pack("<I", ret(p)))
    o.write(vt + 0x4D0, struct.pack("<I", ret(2)))
    o.write(vt + 0x4D8, struct.pack("<I", ret(60)))
    o.write(vt + 0x5D0, struct.pack("<I", h.null))
    ob = o.alloc(0x3000); o.write(ob, struct.pack("<I", vt))
    return ob

dfn = h.obj({"Con": 140, "AC": 102, "Men": 112, "MR": 127}, 61, 5)
mask = o.slot(0)
o.stub_function(RB, bytes([0x8B,0x44,0x24,0x04]) + bytes([0x23,0x05]) + struct.pack("<I", mask) + bytes([0xC2,0x04,0x00]))
o.write(mask, struct.pack("<I", 0))     # pin the roll to the LOW bound

print("%-14s %-14s %12s %12s" % ("Rate[mag=25]", "Rate[phys=24]", "magical AP", "physical AP"))
for mrate, prate in ((1000,1000), (2000,1000), (1000,2000), (2000,2000)):
    a = h.arg(obj_with(mrate, prate), dfn)
    ski = struct.unpack("<I", o.read(a + 0x08, 4))[0]
    activ = struct.unpack("<I", o.read(ski + 0x04, 4))[0]
    o.write(activ, bytes(0x400))
    for k, v in (("MinMA", 327), ("MaxMA", 381)):
        o.write(activ + ACTIVE_MA[k], struct.pack("<I", v))
    o.write(activ + 0xDB, struct.pack("<I", 327))       # MinWC, for the physical comparison
    o.write(activ + 0xE3, struct.pack("<I", 381))
    ma = o.call(AP_MA, [a], this=magical, ret="double")
    py = o.call(AP_PY, [a], this=physical, ret="double")
    print("%-14d %-14d %12.1f %12.1f" % (mrate, prate, ma, py))
print("\nport says AppliesWeaponMastery(MagicalSkill) == false, i.e. magical AP should NOT move.")
