#!/usr/bin/env python
"""Find call sites for a VIRTUAL method, by vtable slot offset.

    python tools/xref_vcall.py --offset 0x1C

MSVC compiles a virtual call here as `mov reg,[vtable+off]` followed by `call reg` -- there are 7,828
`call reg` sites in this binary and ZERO `call [reg+disp]`. So searching for a single opcode by slot
offset finds nothing, which reads as "no callers" rather than "wrong technique". This walks the whole
.text with capstone and matches the PAIR: a load from `[reg+off]` and a `call` of that same register a
few instructions later.

⚠️ The sweep RESYNCHRONISES past undecodable bytes (see `Code.sweep`). An earlier version
looped over `md.disasm(raw, base)` directly, which stops at the first bad byte and so covered only 25%
of .text -- every "no callers" answer it ever gave was worth a quarter of a binary.

Slot offsets are small numbers that also appear as ordinary field accesses, so expect noise. The output
is grouped by containing function precisely so the plausible ones can be picked out by name.
"""
import argparse, bisect, collections, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from disasm import Code
from zone_oracle import IMAGE


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--offset", required=True, help="vtable byte offset, e.g. 0x1C")
    ap.add_argument("--window", type=int, default=4, help="instructions between the load and the call")
    ap.add_argument("--filter", default=None, help="regex on the containing function name")
    a = ap.parse_args()
    off = int(a.offset, 0)

    c = Code()
    pending = {}          # register -> (instruction index, address) where it was loaded from [reg+off]
    hits = collections.defaultdict(list)
    needle = "+ 0x%x]" % off

    for idx, ins in enumerate(c.sweep()):
        m = ins.mnemonic
        if m == "mov" and needle in ins.op_str and ins.op_str.startswith(("eax", "ecx", "edx", "ebx", "esi", "edi")):
            reg = ins.op_str.split(",")[0].strip()
            pending[reg] = (idx, ins.address)
        elif m == "call" and ins.op_str in pending:
            loaded_idx, _ = pending[ins.op_str]
            if idx - loaded_idx <= a.window:
                j = bisect.bisect_right(c.sorted_vas, ins.address) - 1
                owner = c.by_va.get(c.sorted_vas[j], "?") if j >= 0 else "?"
                hits[owner].append(ins.address)
            pending.pop(ins.op_str, None)
        elif m in ("call", "jmp", "ret") or (m == "mov" and ins.op_str.split(",")[0].strip() in pending):
            pending.pop(ins.op_str.split(",")[0].strip(), None)

    import re
    rx = re.compile(a.filter, re.I) if a.filter else None
    shown = 0
    for owner, addrs in sorted(hits.items(), key=lambda kv: -len(kv[1])):
        if rx and not rx.search(owner):
            continue
        print("  %-3d %s" % (len(addrs), owner[:110]))
        shown += 1
        if shown >= 40:
            break
    print("\n%d call sites across %d functions for vtable +0x%X" %
          (sum(len(v) for v in hits.values()), len(hits), off))


if __name__ == "__main__":
    main()
