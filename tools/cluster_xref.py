#!/usr/bin/env python
"""Which Parameter::Container slots does a function actually touch?

    python tools/cluster_xref.py --sym "?roe_MinWC@RulesOfEngagement@@QAENPAUEngageArgument@@@Z"
    python tools/cluster_xref.py --slot WCmin          # every function touching that slot, any cluster
    python tools/cluster_xref.py --cluster Item.Rate   # every function touching that cluster

WHY. This project has repeatedly asserted that some cluster is "never read" or "never written" on the
strength of not having found a user. In a release build that is a weak claim and it has already been wrong
once: `Item.Plus`'s weapon slots looked unwritten until `sm_PrepareWeapon` turned up two calls down a path
that had not been walked.

Unreferenced code and data are STRIPPED by /OPT:REF, so anything still present in the image is referenced by
something. "I cannot find the reader" and "there is no reader" are different statements, and only the second
one is worth writing down.

This turns the question into a mechanical scan: displacement offsets that fall inside a container get
decoded to (cluster, slot) so the caller can see what a function really reaches for.
"""
import argparse, bisect, collections, os, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from disasm import Code

CLUSTER = 0xCC          # sizeof(Parameter::Cluster)
SLOTS = 51
TOTAL_OFF = 0xBF4       # Parameter::Container::Total

# Container layout, from the PDB (tools/pdb_types.py --struct "Parameter::Container").
SOURCES = ["Item", "ItemPowerRate", "Upgrade", "WeaponTitle", "PassiveSkill", "AbnormalState", "LastTune"]

STAT = [
    "Str", "Con", "Dex", "Int", "Men",
    "WCmin", "WCmax", "AC", "TH", "TB",
    "MAmin", "MAmax", "MR", "MH", "MB",
    "AbsoluteAttack", "AbsoluteDefend", "AbsoluteHit", "AbsoluteBlock",
    "MoveSpeed", "HPRecover", "SPRecover", "CastingTime", "Critical",
    "PhisycalWeaponMastery", "MagicalWeaponMastery", "ShieldAC",
    "HitRate", "EvaRate", "MACri", "CriDam", "MagCriDam", "CriDamRate", "MagCriDamRate",
    "AttSpeed", "MaxHP", "MaxHP_2", "MaxSP",
    "HPAbsorption_Hitted", "SPAbsorption_Hitted", "HPAbsorption_Hit", "SPAbsorption_Hit",
    "CriticalTB", "RegistNone", "ResistPoison", "ResistDeaseas", "ResistCurse",
    "ResistMoveSpdDown", "ResistGTI", "MaxLP", "LPRecover",
]


def cluster_name(index):
    """Cluster 0 is PureCharParam; then (Plus, Rate) per source; then Total."""
    if index == 0:
        return "PureCharParam"
    if index == 15:
        return "Total"
    src = SOURCES[(index - 1) // 2]
    return f"{src}.{'Plus' if (index - 1) % 2 == 0 else 'Rate'}"


def decode(offset):
    """A container-relative byte offset -> (cluster, slot), or None if it is not on a slot boundary."""
    if not 0 <= offset < TOTAL_OFF + CLUSTER:
        return None
    idx, within = divmod(offset, CLUSTER)
    if within % 4:
        return None
    slot = within // 4
    if slot >= SLOTS:
        return None
    return cluster_name(idx), STAT[slot]


def scan(code, bases=(0,)):
    """Every displacement in .text, bucketed by containing function.

    `bases` lets a caller account for a container EMBEDDED in a larger object: pass the object-relative
    offset of the container (ShineMobileObject::smo_Param is +0x0FC0) and absolute field accesses decode too.
    """
    out = collections.defaultdict(set)
    v, vs, pr = code.secs[0]
    blob = code.data[pr:pr + min(vs, len(code.data) - pr)]
    base = v + 0x400000
    for i in range(len(blob) - 8):
        op = blob[i]
        # mov r32,[reg+d32] / mov [reg+d32],r32 / fild [reg+d32] / mov [reg+d32],imm32
        if op in (0x89, 0x8B, 0xC7, 0xDB):
            modrm = blob[i + 1]
            if (modrm >> 6) == 2 and (modrm & 7) != 4:
                disp = struct.unpack_from("<i", blob, i + 2)[0]
                for b in bases:
                    d = decode(disp - b)
                    if d:
                        j = bisect.bisect_right(code.sorted_vas, base + i) - 1
                        if j >= 0:
                            kind = "w" if op in (0x89, 0xC7) else "r"
                            out[code.by_va[code.sorted_vas[j]]].add((d[0], d[1], kind))
                        break
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sym")
    ap.add_argument("--slot")
    ap.add_argument("--cluster")
    ap.add_argument("--embedded", default="0,0xFC0",
                    help="comma-separated container offsets inside owning objects")
    ap.add_argument("--limit", type=int, default=40)
    a = ap.parse_args()

    c = Code()
    bases = tuple(int(x, 0) for x in a.embedded.split(","))

    if a.sym:
        va = c.syms[a.sym]
        size = min(c.extent(va), 0x2000)
        from capstone import Cs, CS_ARCH_X86, CS_MODE_32
        md = Cs(CS_ARCH_X86, CS_MODE_32)
        seen = []
        for ins in md.disasm(c.data[c.off(va):c.off(va) + size], va):
            for opnd in [ins.op_str]:
                import re
                for m in re.finditer(r"\+ (0x[0-9a-f]+)\]", opnd):
                    for b in bases:
                        d = decode(int(m.group(1), 16) - b)
                        if d:
                            seen.append((ins.address - va, d[0], d[1]))
                            break
        print(f"{a.sym[:70]}\n  container slots touched:\n")
        for off, cl, st in seen:
            print(f"  +0x{off:<5X} {cl:<22} {st}")
        if not seen:
            print("  (none)")
        return

    hits = scan(c, bases)
    for fn, touched in sorted(hits.items()):
        rows = sorted(touched)
        if a.slot:
            rows = [r for r in rows if r[1] == a.slot]
        if a.cluster:
            rows = [r for r in rows if r[0] == a.cluster]
        if rows:
            print(f"{fn[:88]}")
            for cl, st, kind in rows[:a.limit]:
                print(f"    {kind}  {cl:<22} {st}")


if __name__ == "__main__":
    main()
