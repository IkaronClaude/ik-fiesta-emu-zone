#!/usr/bin/env python
"""Where does the SKILL EMPOWER term land in roe_AttackPower? Ask the real function.

    python tools/oracle_empower.py

WHY AN ORACLE AND NOT A READING. The empower LOOKUP is unambiguous from the disassembly:

    level = (u16)arg->empower & 0xF;                  // SKILL_EMPOWER.damage
    term  = *(u32*)(arg->sklinfo->sdi_Activ + level*4 + 0x1BB);   // = nT0[level-1]

Its PLACEMENT is not. `roe_AttackPower` adds it into an x87 accumulator that already has the Str chain,
the weapon bounds and several trailing permille rates flowing through it, and "which side of which rate"
is exactly the kind of question where reading confidently produces a plausible wrong answer. Placing it
wrong would not throw -- it would quietly move the 556/556 band, which is the worst possible failure here.

So: drive the REAL function under emulation, sweep one input at a time, and let the arithmetic answer.

The discriminator is the mastery rate. `PassiveSkill.Rate[PhisycalWeaponMastery]` is a trailing rate
applied at the END of the weapon-damage chain. If the empower term is added BEFORE it, doubling the rate
doubles the empower contribution too; if AFTER, the contribution is unchanged. One sweep, one answer.
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zone_oracle import ZoneOracle
from oracle_accessors import build_container, OFF, STAT, CLUSTER

# The three rules whose roe_AttackPower reads empower, and their singletons.
RULES = {
    "NormalPY":     (0x858ED4, 0x6D009C, "?roe_AttackPower@RulesOfEngagementNormalPY@@MAENPAUEngageArgument@@@Z"),
    "PhisycalSkill": (0x858EDC, None,    "?roe_AttackPower@RulesOfEngagementPhisycalSkill@@MAENPAUEngageArgument@@@Z"),
}

# ActiveSkillInfo: nT0..nT3 are four contiguous ulong[5] from +0x1BF, indexed as ONE flat run of twenty.
NT_BASE = 0x1BF
ACTIVE_SKILL_INFO_SIZE = 0x400

MASTERY_SLOT = 24        # PhisycalWeaponMastery


def vtable_for(o, cont, level, kind, default, null):
    vt = o.alloc(0x1000)
    o.write(vt, struct.pack("<I", default) * (0x1000 // 4))
    ret = lambda p: o._emit(bytes([0xB8]) + struct.pack("<I", p) + bytes([0xC3]))
    o.write(vt + 0x430, struct.pack("<I", ret(cont)))
    o.write(vt + 0x4D0, struct.pack("<I", ret(kind)))
    o.write(vt + 0x4D8, struct.pack("<I", ret(level)))
    # so_GetItemActionObserves MUST return NULL. The harness's default stub returns a pointer to zeroed
    # memory, which is non-null, and roe_AttackPower would then walk into EventRun_IncDmgRate -- a whole
    # second mechanism layered on top of the one under test.
    o.write(vt + 0x5D0, struct.pack("<I", null))
    obj = o.alloc(0x3000)
    o.write(obj, struct.pack("<I", vt))
    return obj


def main():
    o = ZoneOracle()
    for _, (singleton, vt, _) in RULES.items():
        if vt:
            o.write(singleton, struct.pack("<I", vt))
    # PhisycalSkill's vtable symbol, written the same way its ctor would.
    from disasm import Code
    syms = Code().syms
    o.write(0x858EDC, struct.pack("<I", syms["??_7RulesOfEngagementPhisycalSkill@@6B@"]))

    zero = o.alloc(16)
    default = o._emit(bytes([0xB8]) + struct.pack("<I", zero) + bytes([0xC3]))
    null = o._emit(bytes([0x33, 0xC0, 0xC3]))          # xor eax,eax; ret

    def attack_power(rule, mastery_rate, empower_level, nt_values):
        singleton, _, sym = RULES[rule]

        cont = bytearray(build_container({"Str": 371, "WCmin": 1709, "WCmax": 1840}))
        # PassiveSkill.Rate[PhisycalWeaponMastery] -- the trailing rate used as the discriminator.
        struct.pack_into("<i", cont, OFF["PassiveSkill"] + CLUSTER + MASTERY_SLOT * 4, mastery_rate)
        att = vtable_for(o, o_write(o, bytes(cont)), 82, 2, default, null)
        dfn = vtable_for(o, o_write(o, bytes(build_container({"Con": 140, "AC": 102}))), 61, 5,
                         default, null)

        activ = o.alloc(ACTIVE_SKILL_INFO_SIZE)
        o.write(activ, b"\0" * ACTIVE_SKILL_INFO_SIZE)
        for i, v in enumerate(nt_values):
            o.write(activ + NT_BASE + i * 4, struct.pack("<I", v))

        sklinfo = o.alloc(0x300)
        o.write(sklinfo, b"\0" * 0x300)
        o.write(sklinfo + 0x04, struct.pack("<I", activ))          # sdi_Activ

        arg = o.alloc(0x40)
        o.write(arg, b"\0" * 0x40)
        o.write(arg, struct.pack("<II", att, dfn))
        o.write(arg + 0x08, struct.pack("<I", sklinfo))
        o.write(arg + 0x0C, struct.pack("<H", empower_level & 0xF))
        o.write(arg + 0x1C, struct.pack("<i", 1000))
        o.write(arg + 0x28, struct.pack("<i", 1000))
        return o.call(sym, [arg], this=singleton, ret="double")

    def o_write(oo, blob):
        p = oo.alloc(len(blob)); oo.write(p, blob); return p

    nt = [1000] + [0] * 19        # level 1 -> 1000, everything else 0

    print("Does the empower term ride the trailing mastery rate?\n")
    print("  rule           mastery  empower0     empower1     delta")
    for rule in RULES:
        for mastery in (1000, 2000):
            try:
                a = attack_power(rule, mastery, 0, nt)
                b = attack_power(rule, mastery, 1, nt)
                print("  %-14s %-8d %-12.4f %-12.4f %+.4f" % (rule, mastery, a, b, b - a))
            except Exception as e:
                print("  %-14s %-8d FAILED: %s" % (rule, mastery, e))

    print("\n  delta at 1000 == delta at 2000  -> added AFTER the mastery rate")
    print("  delta doubles with the rate     -> added BEFORE it")


if __name__ == "__main__":
    main()
