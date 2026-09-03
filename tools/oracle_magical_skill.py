#!/usr/bin/env python
"""Run the REAL magical skill damage pipeline for one captured bucket, and diff it against this port.

    python tools/oracle_magical_skill.py

`oracle_magical.py` showed the magical ACCESSORS are exact (roe_MinMA 622, roe_MaxMA 702, roe_MR 239 --
all agreeing with the port), so the 1.28x-1.76x magical under-prediction is downstream of them. This runs
`roe_AttackPower@RulesOfEngagementMagicalSkill` and then `roe_CalcDamage`, with the RNG pinned to each end
of its range, for a bucket taken straight out of `MageDamageLvl60.pcapng`.
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from oracle_skill import Harness
from oracle_accessors import build_container, OFF, STAT, CLUSTER
from disasm import Code

# roe_AttackPower@MagicalSkill reads the MAGIC quartet, +0xEB..+0xF7, where the physical one reads
# +0xDB..+0xE7 -- see ActiveSkillInfo.
ACTIVE_MA = {"MinMA": 0xEB, "MinMARate": 0xEF, "MaxMA": 0xF3, "MaxMARate": 0xF7}
# The empower table, nT0..nT3 as one contiguous run of twenty -- `roe_AttackPower` indexes it with
# (damage nibble - 1) and does not respect the boundaries between the four declared arrays. The offset is
# `oracle_empower.py`'s NT_BASE, established there rather than guessed here.
EMPOWER_TABLE = 0x1BF
AP_MA = "?roe_AttackPower@RulesOfEngagementMagicalSkill@@MAENPAUEngageArgument@@@Z"
DP_MA = "?roe_DefendPower@RulesOfEngagementMagicalSkill@@MAENPAUEngageArgument@@@Z"
CALC = "?roe_CalcDamage@RulesOfEngagement@@UAEHPAUEngageArgument@@@Z"
RB = "?rb_largerandom@RandomBox@@QAEHH@Z"


def main():
    h = Harness()
    o = h.o
    magical = o.alloc(0x40)
    o.write(magical, struct.pack("<I", Code().syms["??_7RulesOfEngagementMagicalSkill@@6B@"]))

    # The captured mage: the container ExactlyMagical builds, whose accessors return the wire's numbers.
    att = h.obj({"Int": 1, "MAmin": 622 - 1, "MAmax": 702 - 1, "Men": 1, "MR": 468 - 1}, 60, 2)
    # The Orc, as MobParameters.StoreMob builds it. roe_MR = Men-chain + MR = 239.
    dfn = h.obj({"Str": 822, "Con": 140, "Dex": 147, "Int": 135, "Men": 112,
                 "AC": 102, "TB": 179, "MR": 127}, 61, 5)

    # The DamageByAngle tables ARE the arrays; filled with 1000 (neutral), which is what the deployed
    # server holds. Without them roe_CalcDamage reads unmapped memory and Unicorn raises UC_ERR_EXCEPTION,
    # which reads as "the function is unrunnable" and is really a missing table.
    o.write(0x0AF0D0F0, struct.pack("<H", 1000) * 91)
    o.write(0x0AF0D1A8, struct.pack("<H", 1000) * 91)

    mask = o.slot(0)
    o.stub_function(RB, bytes([0x8B, 0x44, 0x24, 0x04])
                        + bytes([0x23, 0x05]) + struct.pack("<I", mask)
                        + bytes([0xC2, 0x04, 0x00]))

    # ⚠️ ONE EngageArgument, reused. `Harness.arg` allocates a fresh skill block and emits stubs each
    # call, and a handful of those exhausts the oracle's code arena -- which surfaces as a host access
    # violation, i.e. it looks like the FUNCTION crashed rather than the harness. Vary the case through
    # DATA on a single allocation, the same discipline the RNG stub needs.
    a = h.arg(att, dfn)
    ski = struct.unpack("<I", o.read(a + 0x08, 4))[0]
    activ = struct.unpack("<I", o.read(ski + 0x04, 4))[0]

    def run(active, empower=0, nT0=None):
        o.write(activ, bytes(0x400))
        for k, v in active.items():
            o.write(activ + ACTIVE_MA[k], struct.pack("<I", v))
        o.write(a + 0x0C, struct.pack("<H", empower & 0xFFFF))
        # ⚠️ The empower NIBBLE alone proves nothing: the term it selects is looked up in the skill row's
        # own table, which this harness zeroes. `MagicBall01`'s nT0 is 100/128/155/182/228, so write that
        # -- otherwise "empower changed nothing" is a statement about the fixture, not about the server.
        for i, v in enumerate(nT0 or ()):
            o.write(activ + EMPOWER_TABLE + i * 4, struct.pack("<I", v))
        out = []
        for bits in (0, 0xFFFFFFFF):
            o.write(mask, struct.pack("<I", bits))
            out.append(o.call(AP_MA, [a], this=magical, ret="double"))
        return out[0], out[1]

    print("%-44s %11s %11s   %s" % ("case", "attack lo", "attack hi", "port expects"))
    for label, active, emp, expect in (
            ("plain (no skill row)",                  {},                            0, "622 / 702"),
            ("LightningBolt08 MinMA=327 MaxMA=381",   {"MinMA": 327, "MaxMA": 381},  0, "949 / 1083"),
            ("MagicMissile08  MinMA=515 MaxMA=598",   {"MinMA": 515, "MaxMA": 598},  0, "1137 / 1300"),
            ("FireBall01      MinMA=2840 MaxMA=3552", {"MinMA": 2840,"MaxMA": 3552}, 0, "3462 / 4254"),
            ("MagicBall01     MinMA=447 MaxMA=558",   {"MinMA": 447, "MaxMA": 558},  0, "1069 / 1260"),
            ("MagicBall01     + empower damage=5",    {"MinMA": 447, "MaxMA": 558},  5, "1297 / 1488"),
            ("MinMARate=1000  (double LOW only)",     {"MinMARate": 1000},           0, "1244 / 702"),
            ("MaxMARate=1000  (double HIGH only)",    {"MaxMARate": 1000},           0, "622 / 1404"),
    ):
        lo, hi = run(active, empower=emp,
                     nT0=[100, 128, 155, 182, 228] if emp else None)
        print("%-44s %11.1f %11.1f   %s" % (label, lo, hi, expect))


if __name__ == "__main__":
    main()
