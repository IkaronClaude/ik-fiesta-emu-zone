#!/usr/bin/env python
"""Read live objects out of a running Zone.exe by SHINE HANDLE.

    python tools/zone_live.py --pod zone01 --handle 17402
    python tools/zone_live.py --pod zone01 --kind 8 --list          # every live object of a kind
    python tools/zone_live.py --pod zone01 --handle 17402 --read 0x2AB20:u32

Why this exists: the previous way to reach a live object was to scan the heap for a character's five
Total primaries and subtract 0xBF4. That works, is per-character, and goes stale the moment gear changes.
The server already has an index from handle to object and it is three reads deep, so use it.

THE LOOKUP, read out of som_GetObject@ShineObjectManager (0x0054FD10):

    kind, index = sohu_HandleSplit(handle)        ; a range table, see HANDLE_RANGES
    if kind >= 13: fail
    list    = [manager + 0x1EC + kind*4]
    if index >= u16[list + 4]: fail               ; per-kind capacity
    entries = [list + 8]
    entry   = entries + index*12                  ; stride 12
    obj     = [entry]                             ; and entry[+8] is a live flag
    return obj if entry[+8] else 0

`manager` is a fixed address baked into the call sites (`mov ecx, 0x132826B8`), which holds because this
build allocates deterministically -- the same reason the free-stat tables sit at fixed addresses.

⚠️ READ /proc/pid/maps IN CHUNKS AND DO NOT SIZE-LIMIT THE MAPPINGS. A `skip if size > 128MB` guard
silently skips the main heap, and every lookup then reports "not found" for objects that are plainly
there. This bit the container work twice in one session.

Only perl, dd and od exist in the zone pods -- no python -- so the in-pod half is perl.
"""
import argparse
import json
import re
import struct
import subprocess

MANAGER = 0x132826B8          # mov ecx, 0x132826B8 at every som_GetObject call site
LISTS_OFF = 0x1EC             # manager + 0x1EC + kind*4 -> per-kind list
COUNT_OFF = 4                 # u16 capacity on the list
ENTRIES_OFF = 8               # pointer to the entry array
ENTRY_STRIDE = 12             # {obj u32, ? u32, live u8}
ENTRY_LIVE = 8

# sohu_HandleSplit (0x00633650): (upper_exclusive, kind, subtract). Ordered; first match wins.
# <= 0x1F40 is the first arm and uses the handle unchanged.
#
# Each arm's span equals the kind's capacity in the live manager, which is what confirms the table:
#   kind 5 span 8000 cap 8000 | kind 2 span 1500 cap 1500 | kind 3 span 1000 cap 1000
#   kind 1 span 3000 cap 3000 | kind 0 span 3584 cap 3584 | kind 4 span  256 cap  256
#   kind 8 span 2048 cap 2048
# Observed live on zone01: kind 2 = PLAYERS (the three bots on that zone, and a cast-fail log's own
# self handle 8817 falls in it), kind 5 = mobs, kinds 0 and 4 = the rest of the world.
HANDLE_RANGES = [
    (0x1F41, 5, 0x0000),
    (0x251C, 2, 0x1F40),
    (0x2904, 3, 0x251C),
    (0x34BC, 1, 0x2904),
    (0x42BC, 0, 0x34BC),
    (0x43BC, 4, 0x42BC),
    (0x4BBC, 8, 0x43BC),
]

# Object fields worth naming. Offsets are object-relative; see docs and the zone-emu port.
FIELDS = {
    "xy_ptr":      (0x66, "u32"),      # -> SHINE_XY_TYPE*, x at +0, y at +4
    "databox":     (0x1F90, "u32"),    # so_mob_DataBox
    "regen_xy":    (0x249C, "xy"),     # so_mob_RegenLocation
    "lasthit_xy":  (0x24A4, "xy"),     # so_mob_LastHittedLocation
    "cur_target":  (0x24AE, "u16"),    # sm_CurrentTarget
    "sen_lp":      (0x2AB20, "u32"),   # so_ply_Sen_GetLP -- the Sentinel soul resource
}


def split_handle(handle):
    """-> (kind, index) or (None, None). The arms are exactly sohu_HandleSplit's."""
    for upper, kind, sub in HANDLE_RANGES:
        if handle < upper:
            return kind, handle - sub
    return None, None


class Zone:
    def __init__(self, pod, namespace="fiesta", pid=None):
        self.pod, self.ns = pod, namespace
        self.pid = pid or self._pid()

    def _sh(self, script):
        out = subprocess.run(
            ["kubectl", "exec", "-n", self.ns, "deploy/" + self.pod, "--", "bash", "-lc", script],
            capture_output=True, text=True, timeout=180)
        return out.stdout

    def _pid(self):
        # Three things match "Zone.exe": the `cmd /c ... Zone.exe > stdout.txt` wrapper, Zone.exe itself,
        # and -- critically -- THE SHELL RUNNING THIS SEARCH, whose own command line contains the string.
        # `tail -1` therefore returns a pid that has already exited, every read comes back empty, and it
        # reads as "the addresses are wrong" rather than "the pid is wrong". Drop the wrappers explicitly.
        out = self._sh('for p in /proc/[0-9]*; do c=$(tr "\\0" " " < $p/cmdline 2>/dev/null); '
                       'case "$c" in *bash*|*"cmd /c"*) ;; *Zone.exe*) echo "$(basename $p)";; esac; done')
        pids = [l.strip() for l in out.splitlines() if l.strip().isdigit()]
        if not pids:
            raise SystemExit("could not find the Zone.exe pid in %s" % self.pod)
        return pids[0]

    def read(self, addr, length):
        """Raw bytes at a virtual address, via /proc/pid/mem. b'' when unmapped."""
        script = (
            "perl -e '"
            "open(my $m, \"<\", \"/proc/%s/mem\") or exit 1;"
            "binmode $m; seek($m, %d, 0) or exit 1;"
            "read($m, my $b, %d) or exit 1;"
            "print unpack(\"H*\", $b);'" % (self.pid, addr, length))
        out = self._sh(script).strip()
        hexs = re.sub(r"[^0-9a-fA-F]", "", out)
        # an odd tail means the shell added something; keep only whole bytes
        if len(hexs) % 2:
            hexs = hexs[:-1]
        try:
            return bytes.fromhex(hexs)[:length]
        except ValueError:
            return b""

    def u32(self, addr):
        b = self.read(addr, 4)
        return struct.unpack("<I", b)[0] if len(b) == 4 else None

    def u16(self, addr):
        b = self.read(addr, 2)
        return struct.unpack("<H", b)[0] if len(b) == 2 else None

    # ---- the lookup ------------------------------------------------------------------------------
    def kind_list(self, kind):
        """-> (count, entriesPtr) for one object kind."""
        lst = self.u32(MANAGER + LISTS_OFF + kind * 4)
        if not lst:
            return None, None
        return self.u16(lst + COUNT_OFF), self.u32(lst + ENTRIES_OFF)

    def object_of(self, handle):
        """som_GetObject: handle -> object address, or None."""
        kind, index = split_handle(handle)
        if kind is None or kind >= 13:
            return None
        count, entries = self.kind_list(kind)
        if not count or not entries or index >= count:
            return None
        entry = entries + index * ENTRY_STRIDE
        blob = self.read(entry, ENTRY_STRIDE)
        if len(blob) < ENTRY_STRIDE:
            return None
        obj, _, live = struct.unpack("<IIBxxx", blob)
        return obj if live else None

    def live_handles(self, kind, limit=4096):
        """Every handle of a kind whose slot is live, with its object address."""
        count, entries = self.kind_list(kind)
        if not count or not entries:
            return []
        n = min(count, limit)
        blob = self.read(entries, n * ENTRY_STRIDE)
        base = next(sub for upper, k, sub in HANDLE_RANGES if k == kind)
        out = []
        for i in range(len(blob) // ENTRY_STRIDE):
            obj, _, live = struct.unpack_from("<IIBxxx", blob, i * ENTRY_STRIDE)
            if live and obj:
                out.append((base + i, obj))
        return out

    def position(self, obj):
        """(x, y) via the object's SHINE_XY_TYPE pointer at +0x66."""
        p = self.u32(obj + FIELDS["xy_ptr"][0])
        if not p:
            return None
        b = self.read(p, 8)
        return struct.unpack("<II", b) if len(b) == 8 else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pod", default="zone01")
    ap.add_argument("--namespace", default="fiesta")
    ap.add_argument("--pid")
    ap.add_argument("--handle", type=lambda s: int(s, 0))
    ap.add_argument("--kind", type=int)
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--read", help="offset:type, e.g. 0x2AB20:u32 (repeatable, comma separated)")
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args()

    z = Zone(a.pod, a.namespace, a.pid)
    print("# %s pid %s manager %#x" % (a.pod, z.pid, MANAGER))

    if a.list:
        kinds = [a.kind] if a.kind is not None else sorted({k for _, k, _ in HANDLE_RANGES})
        for k in kinds:
            count, entries = z.kind_list(k)
            live = z.live_handles(k)
            print("kind %-2d  capacity %-6s entries %-12s live %d"
                  % (k, count, hex(entries) if entries else None, len(live)))
            for h, obj in live[:40]:
                pos = z.position(obj)
                print("   handle %-6d obj %#010x  pos %s" % (h, obj, pos))
        return

    if a.handle is None:
        ap.error("pass --handle or --list")

    kind, index = split_handle(a.handle)
    obj = z.object_of(a.handle)
    row = {"handle": a.handle, "kind": kind, "index": index,
           "object": obj and hex(obj), "pos": obj and z.position(obj)}
    if obj and a.read:
        for spec in a.read.split(","):
            off, _, typ = spec.partition(":")
            off = int(off, 0)
            row[spec] = z.u32(obj + off) if typ != "u16" else z.u16(obj + off)
    print(json.dumps(row, indent=1) if a.json else row)


if __name__ == "__main__":
    main()
