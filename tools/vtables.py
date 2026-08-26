#!/usr/bin/env python
"""Dump a class's vtable, resolving every slot to the function(s) that live at that address.

    python tools/vtables.py --class CharClass
    python tools/vtables.py --family CharClass --slot 12      # one slot across a whole family
    python tools/vtables.py --family CharClass --overrides    # which slots any subclass changes

WHY THIS EXISTS. Asking "does any class override MaxHP?" by grepping the PDB for `?MaxHP@X@@` answers a
DIFFERENT question: it finds classes that have their own *symbol*. A class that inherits the base slot has no
symbol, so absence of a symbol reads as "no override" -- which is usually right, but it is an argument from
absence and it cannot distinguish "inherits" from "the symbol was not emitted".

The vtable settles it positively: slot N of class X literally contains an address, and that address either is
or is not the base implementation. That is evidence rather than the lack of it.

IDENTICAL CODE FOLDING. Several distinct methods routinely share one address because the linker merged
byte-identical bodies. A slot is therefore resolved to a LIST of names, not one -- printing only the first
would invent a specificity the binary does not have.
"""
import argparse, os, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from disasm import Code


class Vtables:
    def __init__(self, code=None):
        self.c = code or Code()
        # every name at an address, not just the first -- ICF makes this a genuine many-to-one
        self.names_at = {}
        for n, v in self.c.syms.items():
            self.names_at.setdefault(v, []).append(n)
        self.text_lo, self.text_hi = 0x401000, 0x6A2000

    def vtable_of(self, cls):
        return self.c.syms.get("??_7%s@@6B@" % cls)

    def families(self, prefix):
        out = []
        for n, v in self.c.syms.items():
            if n.startswith("??_7") and n.endswith("@@6B@"):
                name = n[4:-5]
                if name.startswith(prefix):
                    out.append((name, v))
        return sorted(out, key=lambda t: t[1])

    def slots(self, va, limit=64):
        """Read consecutive function pointers. Stops at the first value that is not code."""
        out = []
        off = self.c.off(va)
        if off is None:
            return out
        for i in range(limit):
            (p,) = struct.unpack_from("<I", self.c.data, off + i * 4)
            if not (self.text_lo <= p < self.text_hi):
                break
            out.append(p)
        return out

    def label(self, p, short=True):
        names = self.names_at.get(p)
        if not names:
            return "sub_%X" % p
        if short:
            names = [n.split("@@")[0].lstrip("?") for n in names]
        names = sorted(set(names))
        if len(names) == 1:
            return names[0]
        return "%s  [ICF x%d: %s]" % (names[0], len(names), ", ".join(names[1:6]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--class", dest="cls")
    ap.add_argument("--family")
    ap.add_argument("--slot", type=int)
    ap.add_argument("--overrides", action="store_true")
    a = ap.parse_args()

    v = Vtables()

    if a.cls:
        va = v.vtable_of(a.cls)
        if not va:
            sys.exit("no vtable symbol for %s" % a.cls)
        print("%s  vtable @ 0x%08X" % (a.cls, va))
        for i, p in enumerate(v.slots(va)):
            print("  [%2d] 0x%08X  %s" % (i, p, v.label(p)))
        return

    fam = v.families(a.family)
    base = fam[0]
    base_slots = v.slots(base[1])
    print("family %s*: %d vtables, base %s has %d slots\n" % (a.family, len(fam), base[0], len(base_slots)))

    if a.slot is not None:
        print("slot %d across the family:" % a.slot)
        for name, va in fam:
            s = v.slots(va)
            p = s[a.slot] if a.slot < len(s) else None
            mark = "" if p == base_slots[a.slot] else "   <-- OVERRIDES"
            print("  %-28s 0x%08X  %s%s" % (name, p or 0, v.label(p) if p else "-", mark))
        return

    if a.overrides:
        print("slots where at least one subclass differs from %s:\n" % base[0])
        any_diff = False
        for i, bp in enumerate(base_slots):
            diff = []
            for name, va in fam[1:]:
                s = v.slots(va)
                if i < len(s) and s[i] != bp:
                    diff.append((name, s[i]))
            if diff:
                any_diff = True
                print("  [%2d] base %s" % (i, v.label(bp)))
                for name, p in diff:
                    print("         %-26s 0x%08X  %s" % (name, p, v.label(p)))
        if not any_diff:
            print("  (none)")


if __name__ == "__main__":
    main()
