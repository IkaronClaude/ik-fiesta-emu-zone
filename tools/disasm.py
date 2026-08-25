#!/usr/bin/env python
"""Disassemble a Zone.exe function with calls, globals and constants resolved to names.

    python tools/disasm.py --sym "?ali_Work@MobTargetAggresive@@UAEHPAVShineObject@ShineObjectClass@@@Z"
    python tools/disasm.py --va 0x4A9E20 --count 200

Reading a function is how you form a hypothesis about it; running it under tools/zone_oracle.py is how you
find out. This exists to make the first step fast, so the annotations are the ones that actually save time:
call targets by symbol, float/double constants by value, and the extent of the function taken from the next
public symbol rather than from the first `ret` (early-exit paths are common and stopping there truncates).
"""
import argparse, os, re, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from zone_oracle import publics, DEFAULT_EXE, DEFAULT_PDB, IMAGE


class Code:
    def __init__(self, exe=DEFAULT_EXE, pdb=DEFAULT_PDB):
        import pefile
        self.pe = pefile.PE(exe, fast_load=True)
        self.data = open(exe, "rb").read()
        self.secs = [(s.VirtualAddress, s.Misc_VirtualSize, s.PointerToRawData) for s in self.pe.sections]
        segrva = {i + 1: s.VirtualAddress for i, s in enumerate(self.pe.sections)}
        self.syms = publics(open(pdb, "rb").read(), segrva)
        self.by_va = {}
        for n, v in sorted(self.syms.items()):
            self.by_va.setdefault(v, n)
        self.sorted_vas = sorted(self.by_va)

    def off(self, va):
        rva = va - IMAGE
        for v, vs, pr in self.secs:
            if v <= rva < v + max(vs, 1):
                return pr + (rva - v)
        return None

    def extent(self, va):
        """Function length, bounded by the next public symbol. Not by the first `ret`."""
        import bisect
        i = bisect.bisect_right(self.sorted_vas, va)
        return (self.sorted_vas[i] - va) if i < len(self.sorted_vas) else 0x800

    def const(self, va, size):
        o = self.off(va)
        if o is None:
            return None
        if size == 8:
            return struct.unpack_from("<d", self.data, o)[0]
        if size == 4:
            return struct.unpack_from("<f", self.data, o)[0]
        return None

    def dump(self, va, count=400, only=None):
        from capstone import Cs, CS_ARCH_X86, CS_MODE_32
        md = Cs(CS_ARCH_X86, CS_MODE_32)
        size = min(self.extent(va), 0x2000)
        out = []
        for n, ins in enumerate(md.disasm(self.data[self.off(va):self.off(va) + size], va)):
            if n > count:
                break
            note = ""
            if ins.mnemonic == "call" and ins.op_str.startswith("0x"):
                note = "   ; " + (self.by_va.get(int(ins.op_str, 16)) or "sub_%X" % int(ins.op_str, 16))
            m = re.search(r"\[(0x[0-9a-f]+)\]", ins.op_str)
            if m and not note:
                addr = int(m.group(1), 16)
                if ins.mnemonic in ("fld", "fmul", "fdiv", "fadd", "fsub", "fcomp", "fdivr", "fsubr"):
                    v = self.const(addr, 8 if "qword" in ins.op_str else 4)
                    if v is not None:
                        note = "   ; = %r" % v
                elif addr in self.by_va:
                    note = "   ; " + self.by_va[addr]
            line = "  +0x%-5X %-30s %s%s" % (ins.address - va, ins.bytes.hex(), ins.mnemonic + " " + ins.op_str, note)
            if only and not re.search(only, line):
                continue
            out.append(line)
        return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--pdb", default=DEFAULT_PDB)
    ap.add_argument("--sym")
    ap.add_argument("--va")
    ap.add_argument("--count", type=int, default=400)
    ap.add_argument("--only", help="regex filter on the rendered line")
    a = ap.parse_args()
    c = Code(a.exe, a.pdb)
    va = int(a.va, 0) if a.va else c.syms[a.sym]
    print("%s\n  VA 0x%08X  extent 0x%X\n" % (a.sym or a.va, va, c.extent(va)))
    print("\n".join(c.dump(va, a.count, a.only)))


if __name__ == "__main__":
    main()
