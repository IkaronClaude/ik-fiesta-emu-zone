#!/usr/bin/env python
"""What every `SubAbstateAction` does, read off `aeo_ParameterEnchant`'s jump table.

    python tools/abstate_actions.py                 # all 120, with their container writes
    python tools/abstate_actions.py --csharp        # emit the port's table
    python tools/abstate_actions.py --action 94     # one action, with its disassembly
    python tools/abstate_actions.py --markdown > docs/SUBABSTATE_ACTIONS.md

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

MARKDOWN_HEADER = """# `SubAbstateAction` — what every abnormal-state action does

Every buff, debuff, poison and stun in Fiesta reaches a character's stats through one function:
`AbnormalStateContainer::AbstateElementInObject::aeo_ParameterEnchant` at **0x004079F0** in `Zone.exe`.
`SubAbState.shn` gives a sub-state up to four `(ActionIndex, ActionArg)` pairs, and each `ActionIndex` is a
`SubAbstateAction` — one case of the switch below. This table is that switch, read out of the binary.

**{ACTIONS} actions, {HANDLERS} distinct handler bodies, {WITH} with a container effect, {NONE} with none.**

## Where this comes from

The dispatch is an ordinary compiler switch, so it can be walked rather than guessed at:

```
action = element[0x10 + i * 0x24]          ; i = 0..3, the four action slots
arg    = element[0x14 + i * 0x24]
ebx    = action - 1
if (ebx > 0x77) goto done                  ; so the valid range is 1..120
ebx    = byte [ebx + 0x408320]             ; 0x78 one-byte case indices
jmp    [ebx * 4 + 0x408200]                ; the 72 distinct handler bodies
```

Each handler is straight-line and does one of three things: add or subtract into a stat slot of the
container's **AbnormalState** cluster, write one of the container's named second-tier fields, or set a bit
in `Parameter::Container::flag`. The names in the table are the PDB's own `SubAbstateAction` enum — nothing
here is a nickname.

Regenerate with `python tools/abstate_actions.py --markdown`.

## How to read the Effect column

| Notation | Meaning |
| --- | --- |
| `+ WCmin plus` | adds the action's argument to `AbnormalState.Plus[WCmin]` — a flat bonus |
| `- AC rate` | subtracts it from `AbnormalState.Rate[AC]` — a permille scale, identity 1000 |
| `MissPercentFix +` | adds to a named field past the clusters, not to a stat |
| `flag cannotattack` | sets a bit in `Parameter::Container::flag` (+0xCCE) — behaviour, not a number |
| `_none_` | the handler IS the shared epilogue: no container effect at all |

Two things worth knowing before using the numbers:

- **`plus` and `rate` are different halves and combine differently.** `Parameter::Container` keeps a flat
  half and a permille half per source; `c_MakeTotal` adds the plus halves and multiplies the rate ones, and
  the rate halves' identity is 1000, not 0. A `rate` action of 200 is +20%, not +200 points.
- **Weapon and magic actions touch BOTH bounds.** `SAA_WCPLUS` writes `WCmin` and `WCmax` together, which is
  why a weapon debuff shifts the whole damage range instead of squashing it.

"""

MARKDOWN_FOOTER = """
## Caveats

- **`_none_` means no *container* effect, not no effect.** 49 actions resolve to the shared epilogue and
  write nothing into `Parameter::Container` — but several of them clearly do something elsewhere.
  `SAA_DOTDAMAGE`, `SAA_FEAR`, `SAA_SILIENCE`, `SAA_SETABSTATE*` and the targeting family are implemented in
  `SubAbnormalStateActor` subclasses, in the tactic state machine, or in packet handlers. This table says
  where they do *not* act, which is still worth knowing precisely.
- **Handlers are shared.** 120 actions map onto 72 bodies, so two differently-named actions can be literally
  the same code. That is the server's doing, not a collapsing of the table here.
- **`SAA_NOMOVE` has two outcomes.** It sets `cannotmove_entangle`, unless the sub-state's type at +0x26 is
  `0x15` or `0x60`, in which case it branches into `SAA_AWAY`'s body and sets `cannotmove_stun` instead. The
  PDB names those two bits separately, so the distinction is deliberate: entangle and stun are not the same
  immobilisation.
- **`SAA_CRITICALRATE` writes `CriDamRate`.** That slot's name says "critical damage rate", but it is the
  slot `roe_CriticalRate` reads to decide whether a swing crits at all — an action the developers named
  CRITICALRATE writing it is the clearest evidence of what the slot really carries.
- **`SAA_HEALRATE` assigns where its neighbours accumulate** (`mov`, not `add`), so it overwrites rather
  than stacking. It is the only one in the table that does.
- The stat slot names are `Parameter::Cluster`'s own field names from the PDB, including the misspellings
  (`ResistDeaseas`, `PhisycalWeaponMastery`, `ACAbsoulte`). They are kept verbatim so they match the symbols.

## Cross-references

- `AbState.shn` maps the `abstateID` on the wire to a `SubAbState` name.
- `SubAbState.shn` maps `(InxName, Strength)` to up to four `(ActionIndexA..D, ActionArgA..D)` pairs — the
  ids in this table.
- The wire carries `ABSTATE_INFORMATION = { abstateID u32, restKeeptime u32, strength u32 }` in
  `NC_BRIEFINFO_ABSTATE_CHANGE_CMD` (0x1C18) and `..._LIST` (0x1C19); `strength` selects the `SubAbState`
  row, and is not a boolean.
"""

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
    for off, name, type_name, _size in t.fields(t.by_name["Parameter::Container"]):
        if off is None or off < TOTAL_END:
            continue
        out[off] = name
        # A nested unnamed struct -- `DotDamagePlus` is one, ten bytes of named shorts. Expanding it is
        # what turns "DotDamagePlus+2" into "DotDamagePlus.Poison", and the member names are the reason
        # SAA_ADDPOISONDMG can be confirmed rather than assumed.
        nested = t.by_name.get((type_name or "").strip())
        if nested:
            for sub_off, sub_name, _t, _s in t.fields(nested):
                if sub_off is not None:
                    out[off + sub_off] = "%s.%s" % (name, sub_name)
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


def camel(flag):
    """`cannotmove_stun` -> `CannotMoveStun`, so the C# enum reads like C# while the PDB name stays the
    source. The mapping is mechanical, so the two cannot drift apart."""
    for run_on, split in (("cannotmove", "cannot_move"), ("cannotattack", "cannot_attack")):
        flag = flag.replace(run_on, split)
    return "".join(w.capitalize() for w in flag.split("_"))


def main():
    # Windows consoles default to a codepage that cannot encode the em dashes in the generated doc, and a
    # redirect inherits it -- so the file would come out with replacement characters.
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass

    ap = argparse.ArgumentParser()
    ap.add_argument("--action", type=int, help="show one action with its disassembly")
    ap.add_argument("--csharp", action="store_true", help="emit the port's table")
    ap.add_argument("--markdown", action="store_true", help="emit docs/SUBABSTATE_ACTIONS.md")
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

    if a.markdown:
        effect = [r for r in rows if r[4] or r[5]]
        print(MARKDOWN_HEADER
              .replace("{ACTIONS}", str(len(rows)))
              .replace("{HANDLERS}", str(len({r[2] for r in rows})))
              .replace("{WITH}", str(len(effect)))
              .replace("{NONE}", str(len(rows) - len(effect))))
        print("| # | `SubAbstateAction` | Effect | Handler |")
        print("| ---: | --- | --- | --- |")
        for action, name, case, va, writes, flags, truncated, _, _ in rows:
            bits = []
            for m, cluster, slot in writes:
                sign = "+" if m == "add" else "-" if m == "sub" else "="
                if cluster == "Container":
                    bits.append("%s `%s`" % (sign, slot.replace(" (alt)", "")))
                else:
                    half = "rate" if cluster.endswith("Rate") else "plus"
                    bits.append("%s `%s` %s" % (sign, slot.replace(" (alt)", ""), half))
            for f in flags:
                bits.append("flag `%s`" % f)
            print("| %d | `%s` | %s | `0x%08X` |"
                  % (action, name, ", ".join(bits) or "_none_", va))
        print(MARKDOWN_FOOTER)
        return

    if a.csharp:
        # Emitted, not transcribed. 120 rows of (half, sign, slot) is exactly the shape of thing that
        # acquires a typo when a human copies it, and the typo would be a silently wrong stat.
        print("    // GENERATED by tools/abstate_actions.py --csharp. Do not hand-edit.")
        print("    // aeo_ParameterEnchant (0x004079F0); case table 0x408320, jump table 0x408200.")
        for action, name, case, va, writes, flags, truncated, _, _ in rows:
            if not writes and not flags:
                continue
            stats, fields = [], []
            for m, cluster, slot in writes:
                slot = slot.replace(" (alt)", "")
                if cluster == "Container":
                    sign = "0" if m == "mov" else "+1" if m == "add" else "-1"
                    fields.append("new(ContainerField.%s, %s)" % (slot.replace(".", ""), sign))
                else:
                    half = "Rate" if cluster.endswith("Rate") else "Plus"
                    stats.append("new(StatHalf.%s, %s, Stat.%s)"
                                 % (half, "+1" if m == "add" else "-1", slot))
            keep = [f for f in flags if "(alt)" not in f]
            alt = [f.replace(" (alt)", "") for f in flags if "(alt)" in f]
            flagset = " | ".join("ContainerFlag." + camel(f) for f in keep) or "ContainerFlag.None"
            altset = " | ".join("ContainerFlag." + camel(f) for f in alt) or "ContainerFlag.None"
            print("        [SubAbstateAction.%s] = new([%s], [%s], %s, %s),"
                  % (name, ", ".join(stats), ", ".join(fields), flagset, altset))
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
