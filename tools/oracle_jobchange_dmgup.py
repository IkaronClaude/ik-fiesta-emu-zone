#!/usr/bin/env python
"""Differential check: the real so_ply_JobChangeDamageUp against the arithmetic this port is about to use.

    python tools/oracle_jobchange_dmgup.py

WHAT THIS SETTLES. `roe_CalcDamage+0x5B2` calls the ATTACKER's vtable slot 0xD2C with the defender and the
integer damage, and for a player that slot is `ShinePlayer::so_ply_JobChangeDamageUp` -- a multiplier the
port applied nowhere. Reading it gives

    if (def == NULL || def->so_ObjectType() != 5 /*monster*/)  return dmg;
    row = charClass->cc_array[level <= 150 ? level : 0];
    if (row == NULL) return dmg;
    return (unsigned __int64)dmg * (row->JobChangeDmgUp + rndbox[2].next()) / 1000;

and running it says whether that reading is right. The pieces that only a run can settle: the 64-bit
multiply and UNSIGNED divide (a plain int32 `dmg * rate / 1000` overflows above 2.1M), the `ja` on a
16-bit compare that sends level > 150 to row 0 rather than clamping to 150, and the size of the random
term -- `rndbox` slot 2 is a shuffled pool of 0s and 1s, so it is worth +-0.1%, not the 24% the capture
residual needs.

⚠️ The random pool is a DATA slot the function reads (`rndbox[2].box[++index & mask]`), so it has to be
written into memory; patching the instruction would be defeated by Unicorn's block cache.
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from zone_oracle import ZoneOracle

SYM = "?so_ply_JobChangeDamageUp@ShinePlayer@ShineObjectClass@@UAEHPAVShineObject@2@H@Z"

CC_ARRAY = 0x10858          # CharClass::cc_array, PrimaryParameter *[151]
CHARCLASS_AT = 0xFBC        # ShinePlayer's class pointer, read as `mov esi, [this + 0xFBC]`
JOBCHANGE_DMGUP_AT = 0x80   # PrimaryParameter::JobChangeDmgUp, unsigned short

# rndbox slot 2 -- 16384 u16 entries, then its own index and mask. `?rndbox@@3VRandomBox@@A` is at
# 0x14D720D0 and a RandomSlot is 0x8008 bytes, so slot 2 starts at +0x10010.
RND_BOX = 0x14D820E0
RND_INDEX = 0x14D8A0E0
RND_MASK = 0x14D8A0E4

MONSTER, PLAYER = 5, 2


def expected(dmg, rate, random_term=0):
    """The port's arithmetic, written the way the binary does it: 64-bit multiply, unsigned divide."""
    return (dmg * (rate + random_term)) // 1000


class Harness:
    def __init__(self):
        self.o = ZoneOracle()
        zero = self.o.alloc(16)
        self.default = self._ret(zero)

    def _ret(self, value):
        return self.o._emit(bytes([0xB8]) + struct.pack("<I", value) + bytes([0xC3]))

    def _vtable(self, slots):
        vt = self.o.alloc(0x1000)
        self.o.write(vt, struct.pack("<I", self.default) * (0x1000 // 4))
        for slot, stub in slots.items():
            self.o.write(vt + slot, struct.pack("<I", stub))
        return vt

    def target(self, object_type):
        """A defender whose only interesting property is what it IS."""
        vt = self._vtable({0x4D0: self._ret(object_type)})
        obj = self.o.alloc(0x2000)
        self.o.write(obj, struct.pack("<I", vt))
        return obj

    def player(self, level, rate, row_present=True):
        """An attacker at `level` whose class table gives `rate` at that level."""
        charclass = self.o.alloc(CC_ARRAY + 151 * 4 + 16)
        if row_present:
            row = self.o.alloc(130)
            self.o.write(row + JOBCHANGE_DMGUP_AT, struct.pack("<H", rate))
            self.o.write(charclass + CC_ARRAY + (level if level <= 150 else 0) * 4,
                         struct.pack("<I", row))

        vt = self._vtable({0x4D8: self._ret(level)})
        obj = self.o.alloc(0x2000)
        self.o.write(obj, struct.pack("<I", vt))
        self.o.write(obj + CHARCLASS_AT, struct.pack("<I", charclass))
        return obj

    def randoms(self, values):
        """Seed rndbox slot 2's pool. An empty mask makes every draw read entry 0."""
        self.o.write(RND_MASK, struct.pack("<I", len(values) - 1 if values else 0))
        self.o.write(RND_INDEX, struct.pack("<I", 0xFFFFFFFF if values else 0))
        self.o.write(RND_BOX, b"".join(struct.pack("<H", v) for v in values or [0]))

    def run(self, attacker, defender, dmg):
        return self.o.call(SYM, [defender, dmg], this=attacker, ret="int")


def main():
    h = Harness()
    h.randoms([])                      # no random term until the last block asks for one
    monster, player_target = h.target(MONSTER), h.target(PLAYER)

    print("  level  rate       dmg   real   port   ")
    bad = 0
    for level, rate, dmg in [(82, 1280, 100), (82, 1280, 1), (82, 1280, 7), (20, 2000, 250),
                             (59, 1190, 4321), (100, 1000, 999), (115, 1025, 40),
                             (82, 1280, 3_000_000), (82, 0, 500), (12, 1000, 12345)]:
        got = h.run(h.player(level, rate), monster, dmg)
        want = expected(dmg, rate)
        bad += got != want
        print("  %5d  %5d  %8d  %5d  %5d  %s" % (level, rate, dmg, got, want,
                                                 "" if got == want else "  <-- MISMATCH"))

    print()
    checks = [
        ("defender is a PLAYER -> untouched", h.run(h.player(82, 1280), player_target, 100), 100),
        ("defender is NULL -> untouched", h.run(h.player(82, 1280), 0, 100), 100),
        ("no row for the level -> untouched",
         h.run(h.player(82, 1280, row_present=False), monster, 100), 100),
        # `cmp ax, 0x96; ja` -- above 150 it takes cc_array[0], it does NOT clamp to 150.
        ("level 200 -> row 0, not row 150", h.run(h.player(200, 1280), monster, 100), 128),
    ]
    for label, got, want in checks:
        bad += got != want
        print("  %-34s real=%-6d expected=%-6d%s" % (label, got, want,
                                                     "" if got == want else "   <-- MISMATCH"))

    # The random term. Slot 2's pool holds 0s and 1s, so a rate of 1280 draws 1280 or 1281.
    print()
    h.randoms([1, 1, 1, 1])
    one = h.run(h.player(82, 1280), monster, 100000)
    h.randoms([0, 0, 0, 0])
    zero = h.run(h.player(82, 1280), monster, 100000)
    bad += (one, zero) != (expected(100000, 1280, 1), expected(100000, 1280, 0))
    print("  random term 1 -> %d, random term 0 -> %d  (worth %.2f%%)" %
          (one, zero, 100.0 * (one - zero) / zero))

    print("\n%s" % ("all agree" if not bad else "%d MISMATCHES" % bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
