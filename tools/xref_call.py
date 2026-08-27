#!/usr/bin/env python
"""Find DIRECT (`call rel32`) call sites for a function, grouped by the containing function.

    python tools/xref_call.py --va 0x54A110
    python tools/xref_call.py --sym "?sp_SetRulesOfEngagement@..."

Companion to xref_vcall.py, which handles the virtual case. A direct call is `e8 rel32`, so the target is
computable without disassembling -- but the CONTAINING function still has to be resolved, and that is what
makes the output readable.
"""
import argparse, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from disasm import Code


def main():
    ap = argparse.ArgumentParser()
    g = ap.add_mutually_exclusive_group(required=True)
    g.add_argument("--va")
    g.add_argument("--sym")
    a = ap.parse_args()

    c = Code()
    target = int(a.va, 0) if a.va else c.syms[a.sym]

    hits = []
    for ins in c.sweep():
        if ins.mnemonic == "call" and ins.op_str.startswith("0x") and int(ins.op_str, 16) == target:
            owner, delta = c.owner(ins.address)
            hits.append((ins.address, owner, delta))

    print("%d direct call site(s) to 0x%X %s\n" % (len(hits), target, c.by_va.get(target, "")))
    for va, owner, delta in hits:
        print("  0x%08X  %s+0x%X" % (va, owner, delta))


if __name__ == "__main__":
    main()
