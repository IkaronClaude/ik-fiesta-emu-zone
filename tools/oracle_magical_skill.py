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

    # ⭐ THE DEFENCE SIDE. The attack side is exact (above), so this is where the residual must be, and
    # it now splits by MOB rather than by skill -- Pinky's buckets demand a bigger correction than the
    # Orc's. `roe_DefendPower@MagicalSkill` is its own function; the port assumes it is just roe_MR.
    # ⭐⭐ THE CORE STEP, AND THE TWO PATHS DO NOT SHARE IT. MagicalSkill's vtable slot +0x10 is
    # `RulesOfEngagementNormalMA::roe_Damage`, while PhisycalSkill's is `NormalPY::roe_Damage`. The
    # physical one is validated at 545/545; the magical one never has been. The port applies ONE formula,
    # `((attackerLevel + 1) * attack) / defend`, to both.
    DMG_MA = "?roe_Damage@RulesOfEngagementNormalMA@@MAENPAUEngageArgument@@NN@Z"
    DMG_PY = "?roe_Damage@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@NN@Z"
    # ⭐ THE LAST UNRUN LINK. roe_CalcDamage calls roe_LevelGapDamageRevision(attacker, defender, damage);
    # the port models it as a permille rate from DamageLvGapPVE. Run the real one.
    LGR = "?roe_LevelGapDamageRevision@RulesOfEngagement@@QAEHPAVShineObject@ShineObjectClass@@0H@Z"
    print("roe_LevelGapDamageRevision(player lv60 -> mob, damage=1000):")
    for label, lvl in (("Orc lv61", 61), ("Pinky lv61", 61), ("KingMushRoom lv62", 62),
                       ("OrcHunter lv63", 63), ("same level 60", 60)):
        d = h.obj({"Men": 112, "MR": 127, "Con": 140, "AC": 102}, lvl, 5)
        try:
            got = o.call(LGR, [att, d, 1000], this=magical, ret="int")
            print("   %-20s -> %d  (x%.4f)" % (label, got, got / 1000.0))
        except Exception as e:
            print("   %-20s FAILED %s" % (label, type(e).__name__))
    print()

    print("roe_Damage core step -- attacker level 60:")
    print("   %-10s %-9s %-12s %-12s %s" % ("attack", "defend", "NormalMA", "NormalPY", "port (L+1)*a/d"))
    a0 = h.arg(att, dfn)
    for atk, dfd in ((949.0, 239.0), (1137.0, 239.0), (3462.0, 239.0), (949.0, 319.0), (1000.0, 1000.0)):
        ma = o.call(DMG_MA, [a0, ("double", atk), ("double", dfd)], this=magical, ret="double")
        py = o.call(DMG_PY, [a0, ("double", atk), ("double", dfd)], this=magical, ret="double")
        print("   %-10.1f %-9.1f %-12.4f %-12.4f %.4f" % (atk, dfd, ma, py, 61 * atk / dfd))
    print()

    print("roe_DefendPower@MagicalSkill vs the port's MagicResistance:")
    for label, stats, lvl, want in (("Orc lv61",   {"Men": 112, "MR": 127}, 61, 239.0),
                                    ("Pinky lv61", {"Men": 142, "MR": 177}, 61, 319.0)):
        d = h.obj(dict(stats, Str=822, Con=140, Dex=147, Int=135, AC=102, TB=179), lvl, 5)
        ad = h.arg(att, d)
        try:
            got = o.call(DP_MA, [ad], this=magical, ret="double")
            mark = "OK " if abs(got - want) < 0.5 else "DIFF"
            print("   %-11s real=%-10.4f port=%-8.1f %s" % (label, got, want, mark))
        except Exception as e:
            print("   %-11s FAILED: %s" % (label, type(e).__name__))
    print()

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

    # ⭐ END TO END. Attack and defence are both exact, so run the WHOLE function and see whether the real
    # server reproduces the capture (this port has a bug downstream) or reproduces this port (the capture
    # carries something we are not modelling).
    print()
    print("roe_CalcDamage, whole pipeline -- Orc lv61, player lv60:")
    for label, active, emp, nt, port_pred, observed in (
            ("LightningBolt08", {"MinMA": 327, "MaxMA": 381},  0, None, "411..469", "615..676"),
            ("MagicMissile08",  {"MinMA": 515, "MaxMA": 598},  0, None, "493..562", "695..778"),
            ("FireBall01",      {"MinMA": 2840,"MaxMA": 3552}, 0, None, "1501..1844", "1964"),
            ("MagicBall01 +e5", {"MinMA": 447, "MaxMA": 558},  5,
                                [100, 128, 155, 182, 228], "562..644", "753..853"),
    ):
        o.write(activ, bytes(0x400))
        for k, v in active.items():
            o.write(activ + ACTIVE_MA[k], struct.pack("<I", v))
        for i, v in enumerate(nt or ()):
            o.write(activ + EMPOWER_TABLE + i * 4, struct.pack("<I", v))
        o.write(a + 0x0C, struct.pack("<H", emp))
        out = []
        for bits in (0, 0xFFFFFFFF):
            o.write(mask, struct.pack("<I", bits))
            try:
                out.append(str(o.call(CALC, [a], this=magical, ret="int")))
            except Exception as e:
                out.append(type(e).__name__)
        print("   %-17s real %8s..%-8s   port %-12s capture %s"
              % (label, out[0], out[1], port_pred, observed))


if __name__ == "__main__":
    main()
