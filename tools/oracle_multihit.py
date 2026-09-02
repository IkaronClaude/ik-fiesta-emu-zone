#!/usr/bin/env python
"""How does a multi-hit STRIKE scale the damage the rules returned? Run the real instructions.

    python tools/oracle_multihit.py

`roe_CalcDamage` does not scale damage by the multi-hit rate at all -- it reads `pMultiHitArg` exactly
once, to gate the critical stun. The scaling lives in the CALLER, `smo_SkillBlast+0x93B`, applied to the
finished integer:

    dmg = (uint)(dmg * serverRate) / 1000;      // UNSIGNED reciprocal divide
    dmg = (dmg * mha_DamageRate) / 1000;        // SIGNED, truncating toward zero
    if (scaled > 0 && dmg == 0 && mha_DamageRate > 0) dmg = 1;

Two claims there are worth more than a careful read, because both are invisible in the common case and
wrong only at the edges:

  1. the two divides are DIFFERENT (`mul; shr` vs `imul; sar` plus a sign fixup), so they disagree on a
     negative product, and
  2. the floor to 1 is conditional on THREE things, not one.

`smo_SkillBlast` is 6.6KB and needs a whole live object graph, so calling it is not the cheap route.
The block itself needs nothing but a stack, a MultiHitArgument and one global -- so this drives exactly
that instruction range and reads the result out of EAX. Same principle as the other oracles: the
arithmetic answers for itself.
"""
import struct, sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zone_oracle import ZoneOracle

BLOCK_BEGIN = 0x581A9B       # smo_SkillBlast+0x93B: mov ecx, [0x1325EDB8]
BLOCK_END = 0x581B09         # +0x9A9, where both paths rejoin
SERVER_RATE_GLOBAL = 0x1325EDB8
MHA_SLOT = -0xE8             # [ebp-0xE8] holds the MultiHitArgument*


def run(o, stack, mha, damage, mha_damage_rate, server_rate):
    from unicorn.x86_const import (UC_X86_REG_EAX, UC_X86_REG_EBP, UC_X86_REG_ESP)

    o.write(SERVER_RATE_GLOBAL, struct.pack("<i", server_rate))
    o.write(mha + 4, struct.pack("<i", mha_damage_rate))     # mha_DamageRate

    ebp = stack + 0x800
    o.uc.reg_write(UC_X86_REG_EBP, ebp)
    o.uc.reg_write(UC_X86_REG_ESP, ebp - 0x400)
    o.write(ebp + MHA_SLOT, struct.pack("<I", mha))
    o.uc.reg_write(UC_X86_REG_EAX, damage & 0xFFFFFFFF)

    o.uc.emu_start(BLOCK_BEGIN, BLOCK_END)
    v = o.uc.reg_read(UC_X86_REG_EAX)
    return v - (1 << 32) if v >= (1 << 31) else v


def predicted(damage, rate, server_rate):
    """The port's MultiHit.HitDamage, in Python, so a disagreement is visible here and not only in C#."""
    scaled = ((damage * server_rate) & 0xFFFFFFFF) // 1000
    if scaled >= (1 << 31):
        scaled -= 1 << 32
    prod = scaled * rate
    prod = prod - (1 << 32) if (prod & 0xFFFFFFFF) >= (1 << 31) and abs(prod) < (1 << 32) else prod
    prod = ((prod + (1 << 31)) & 0xFFFFFFFF) - (1 << 31)     # the imul keeps 32 bits
    out = int(prod / 1000)                                    # truncate toward zero
    if scaled > 0 and out == 0 and rate > 0:
        return 1
    return out


def main():
    o = ZoneOracle()
    stack = o.alloc(0x1000)
    mha = o.alloc(0x40)
    o.write(mha, b"\0" * 0x40)

    cases = [
        # damage,  mha_DamageRate, serverRate,  what it probes
        (1000, 1000, 1000, "a full-rate strike is the damage unchanged"),
        (1000, 500, 1000, "half-rate strike"),
        (1000, 0, 1000, "a ZERO-rate strike deals nothing -- the floor must NOT rescue it"),
        (1000, 1, 1000, "rate 1 rounds to 0 and is floored to 1"),
        (0, 1000, 1000, "no damage to begin with -- the floor must not invent any"),
        (1, 1, 1000, "both tiny"),
        (1000, 2500, 1000, "a strike can be worth MORE than the whole hit"),
        (1000, 1000, 2000, "the server rate doubles it"),
        (1000, 1000, 0, "server rate 0 zeroes everything, floor included"),
        (7, 333, 1000, "truncation, not rounding: 7*333/1000 = 2.33"),
        (999, 999, 999, "three-way truncation"),
        (100000, 1000, 1000, "large"),
    ]

    print("smo_SkillBlast+0x93B -- one multi-hit strike\n")
    print("  %8s %7s %7s   %8s %8s  %s" % ("damage", "rate", "srvRate", "real", "port", ""))
    bad = 0
    for damage, rate, srv, note in cases:
        real = run(o, stack, mha, damage, rate, srv)
        mine = predicted(damage, rate, srv)
        flag = "" if real == mine else "  <-- MISMATCH"
        if real != mine:
            bad += 1
        print("  %8d %7d %7d   %8d %8d  %s%s" % (damage, rate, srv, real, mine, note, flag))

    print("\n  %d/%d agree" % (len(cases) - bad, len(cases)))


if __name__ == "__main__":
    main()
