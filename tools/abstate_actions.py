#!/usr/bin/env python
"""What every `SubAbstateAction` does, read off `aeo_ParameterEnchant`'s jump table.

    python tools/abstate_actions.py                 # all 120, with their container writes
    python tools/abstate_actions.py --csharp        # emit the port's table
    python tools/abstate_actions.py --action 94     # one action, with its disassembly

WHY THIS AND NOT MORE HAND-READING. Nine of these were read one at a time, by eye, because they were the
nine a particular capture happened to exercise. That is fine for nine and hopeless for a hundred and
twenty, and worse, it makes "this action has no parameter effect" indistinguishable from "nobody has looked
at this action yet" -- a distinction the damage tests depend on, because an unread action has to make a
bucket UNPREDICTABLE while a genuinely empty one must not.

The dispatch is a compiler switch and can be walked mechanically:

    ebx = action - 1
    if (ebx > 0x77) goto done                       ; aeo_ParameterEnchant+0x7E
    ebx = byte [ebx + 0x408320]                     ; 0x78 one-byte case indices
    jmp  [ebx*4 + 0x408200]                         ; the distinct handler bodies

so each action's handler is `jump[index[action - 1]]`. Handlers are straight-line and end at the shared
epilogue, and every one of them either writes a container slot

    add/sub dword [eax + disp], reg                 ; eax is Parameter::Container

or sets a behaviour bit

    or byte [eax + 0xCCE], imm                      ; cannotmove_stun / cannotmove_entangle / cannotattack

or does neither, which is the interesting answer: the action exists but has no effect on the container, so
whatever it does lives somewhere else entirely (a `SubAbnormalStateActor`, a packet, the tactic machine).

⚠️ SHARED HANDLERS ARE REAL. The case-index table maps several actions onto one body, so two different
`SubAbstateAction`s can be literally the same code. That is a fact about the server, not a bug here.

⚠️ THIS READS THE WRITES, NOT THE MEANING. `SAA_NOMOVE` sets a bit; what a stunned mob then does is the
tactic machine's business. The name comes from the PDB enum, the write comes from the binary, and nothing
here infers one from the other.
"""
import argparse
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from disasm import Code
from cluster_xref import decode as decode_offset

INDEX_TABLE = 0x408320          # aeo_ParameterEnchant+0x8AB: movzx ebx, byte [ebx + 0x408320]
JUMP_TABLE = 0x408200           # aeo_ParameterEnchant+0x8B2: jmp [ebx*4 + 0x408200]
CASES = 0x78                    # the `cmp ebx, 0x77; ja` bound, so actions 1..120

# `Parameter::Container::flag` (+0xCCE), whose three bits the PDB names.
FLAG_OFFSET = 0xCCE
FLAG_BITS = {1: "cannotmove_stun", 2: "cannotmove_entangle", 4: "cannotattack"}

# The one register the handlers use for the container. Taken from the dispatch: `eax` is loaded from
# `[ebp+8]` at +0x6B and never reloaded, so a write through any other register is NOT a container write and
# must not be decoded as one.
CONTAINER_REG = "eax"

# Past `Total` the container stops being clusters and becomes named scalars.
TOTAL_END = 0xBF4 + 0xCC


def container_fields():
    """`Parameter::Container`'s SECOND-TIER fields by offset, from the PDB.

    Everything past `Total` (+0xBF4) is named scalars rather than clusters -- `MissPercentFix`,
    `RangeEvasion`, `DamageReflection`, `HealRate` and the rest -- and several handlers write those instead
    of a cluster slot. Without this map they decode as "no effect", which is precisely the wrong answer:
    they have an effect, it is just not on a stat.
    """
    from pdb_types import Types
    t = Types()
    out = {}
    for off, name, _type, _size in t.fields(t.by_name["Parameter::Container"]):
        if off is not None and off >= TOTAL_END:
            out[off] = name
    return out


def enum_names():
    """`SubAbstateAction` from the PDB, so the names are read rather than transcribed."""
    from pdb_types import Types
    t = Types()
    ti = t.by_name["SubAbstateAction"]
    return {v: nm for v, nm, _, _ in t.enum_values(ti)}


def handler_addresses(code):
    """action index (1..120) -> handler VA, via the case-index table."""
    out = {}
    for action in range(1, CASES + 1):
        case = code.data[code.off(INDEX_TABLE) + action - 1]
        (target,) = struct.unpack_from("<I", code.data, code.off(JUMP_TABLE) + case * 4)
        out[action] = (case, target)
    return out


def read_handler(code, va, fields, limit=64):
    """Straight-line scan of one handler: the container writes and flag bits it performs.

    Stops at the first unconditional `jmp` (the shared epilogue) or `ret`. Handlers here do not branch;
    if one ever does, this returns what it saw before the branch and says so, rather than guessing at the
    other side.
    """
    writes, flags, truncated, lines, branches = [], [], False, [], []
    for n, ins in enumerate(code.func(va, size=0x200)):
        if n >= limit:
            truncated = True
            break
        lines.append("    %-8s %s" % (ins.mnemonic, ins.op_str))
        op = ins.op_str

        # `dword ptr` for a cluster slot, `word ptr` for most of the second tier (they are u16/s16), and
        # `mov` as well as `add`/`sub` -- SAA_HEALRATE ASSIGNS HealRate where its neighbours accumulate.
        if (ins.mnemonic in ("add", "sub", "mov")
                and (op.startswith("dword ptr [%s + " % CONTAINER_REG)
                     or op.startswith("word ptr [%s + " % CONTAINER_REG))):
            disp = int(op[op.index("+ ") + 2:op.index("]")], 16)
            slot = decode_offset(disp)
            if slot:
                writes.append((ins.mnemonic, slot[0], slot[1]))
            elif disp >= TOTAL_END:
                # Nearest named field at or below the offset. `DotDamagePlus` is a nested unnamed struct
                # ten bytes wide, so its members land INSIDE it and an exact-offset lookup misses them --
                # which would report "no effect" for the whole bleed/poison family.
                base = max((o for o in fields if o <= disp), default=None)
                if base is not None:
                    delta = disp - base
                    writes.append((ins.mnemonic, "Container",
                                   fields[base] + ("+%d" % delta if delta else "")))
        elif ins.mnemonic == "or" and op.startswith("byte ptr [%s + " % CONTAINER_REG):
            disp = int(op[op.index("+ ") + 2:op.index("]")], 16)
            imm = int(op[op.index("], ") + 3:], 0)
            if disp == FLAG_OFFSET:
                flags.append(FLAG_BITS.get(imm, "flag&0x%X" % imm))
        elif ins.mnemonic in ("jmp", "ret"):
            break
        elif ins.mnemonic.startswith("j") and op.startswith("0x"):
            # A conditional branch OUT of the handler. `SAA_NOMOVE` is the case that matters: when the
            # sub-type at +0x26 is 0x15 or 0x60 it jumps into SAA_AWAY's body and sets `cannotmove_stun`
            # instead of `cannotmove_entangle`. Reporting only the fall-through would lose one of the two
            # kinds of immobilisation the PDB bothers to name separately, so the target is recorded and
            # resolved by the caller.
            branches.append(int(op, 16))
    return writes, flags, truncated, lines, branches


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--action", type=int, help="show one action with its disassembly")
    ap.add_argument("--csharp", action="store_true", help="emit the port's table")
    a = ap.parse_args()

    code = Code()
    names = enum_names()
    fields = container_fields()
    handlers = handler_addresses(code)

    rows = []
    for action, (case, va) in sorted(handlers.items()):
        writes, flags, truncated, lines, branches = read_handler(code, va, fields)
        rows.append([action, names.get(action, "?"), case, va, writes, flags, truncated, lines, branches])

    # Resolve branches that land in ANOTHER handler: that handler's effect is this action's alternative.
    bodies = {va: (w, f) for _, _, _, va, w, f, _, _, _ in rows}
    for r in rows:
        for target in r[8]:
            if target in bodies and target != r[3]:
                w, f = bodies[target]
                # Both `cmp 0x15` and `cmp 0x60` jump to the same body, so dedupe -- two branches to one
                # alternative is one alternative, not two.
                for m, c, sl in w:
                    if (m, c, sl + " (alt)") not in r[4]:
                        r[4].append((m, c, sl + " (alt)"))
                for x in f:
                    if x + " (alt)" not in r[5]:
                        r[5].append(x + " (alt)")

    if a.action:
        for action, name, case, va, writes, flags, truncated, lines, _ in rows:
            if action != a.action:
                continue
            print("%d %s   case %d, handler 0x%08X%s"
                  % (action, name, case, va, "  (TRUNCATED)" if truncated else ""))
            for w in writes:
                print("    %s %s %s" % w)
            for f in flags:
                print("    flag %s" % f)
            print()
            print("\n".join(lines))
        return

    if a.csharp:
        print("// generated by tools/abstate_actions.py -- do not hand-edit")
        for action, name, case, va, writes, flags, truncated, _, _ in rows:
            if not writes and not flags:
                continue
            terms = ", ".join('new(%s, %s, Stat.%s)' % ("true" if "Rate" in c else "false",
                                                        "+1" if m == "add" else "-1", s)
                              for m, c, s in writes)
            print("        [%d] = new(SubAbstateAction.%s, [%s]),   // %s"
                  % (action, name, terms, ", ".join(flags) or "-"))
        return

    effect = [r for r in rows if r[4] or r[5]]
    print("%d actions, %d distinct handlers, %d with a container effect\n"
          % (len(rows), len({r[2] for r in rows}), len(effect)))
    print("  #    name                     handler     effect")
    for action, name, case, va, writes, flags, truncated, _, _ in rows:
        what = "; ".join("%s %s %s" % w for w in writes) or ""
        if flags:
            what = (what + "  " if what else "") + "flag: " + ",".join(flags)
        print("  %-4d %-24s 0x%08X  %s%s"
              % (action, name, va, what or "-", "   (TRUNCATED)" if truncated else ""))


if __name__ == "__main__":
    main()
