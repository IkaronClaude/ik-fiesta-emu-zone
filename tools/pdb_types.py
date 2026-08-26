#!/usr/bin/env python
"""Read struct/class/enum layouts out of the PDB's TPI stream.

    python tools/pdb_types.py --list Parameter          # every type whose name contains "Parameter"
    python tools/pdb_types.py --struct "Parameter::Cluster"
    python tools/pdb_types.py --enum Stat

WHY. Everything this project has learned about memory layout so far was inferred from CODE -- which cluster
an eraser seeds, which row offset `c_Storepure` reads, what `charClass + 0x10858` must be. That works, and it
has been wrong at the edges more than once. The PDB carries a 5.3 MB TPI stream with the actual field names,
offsets and types, so those inferences can be replaced with a reading.

Only public symbols were ever parsed from this PDB before (see zone_oracle.publics). This is the other half.
"""
import argparse, os, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from zone_oracle import DEFAULT_PDB

LF_MODIFIER, LF_POINTER, LF_PROCEDURE, LF_MFUNCTION = 0x1001, 0x1002, 0x1008, 0x1009
LF_ARGLIST, LF_FIELDLIST, LF_BITFIELD = 0x1201, 0x1203, 0x1205
LF_BCLASS, LF_VBCLASS, LF_IVBCLASS, LF_VFUNCTAB = 0x1400, 0x1401, 0x1402, 0x1409
LF_ENUMERATE, LF_ARRAY, LF_CLASS, LF_STRUCTURE = 0x1502, 0x1503, 0x1504, 0x1505
LF_UNION, LF_ENUM, LF_MEMBER, LF_STMEMBER = 0x1506, 0x1507, 0x150D, 0x150E
LF_METHOD, LF_NESTTYPE, LF_ONEMETHOD = 0x150F, 0x1510, 0x1511

BUILTIN = {
    0x0000: "<none>", 0x0003: "void", 0x0008: "HRESULT",
    0x0010: "char", 0x0011: "short", 0x0012: "long", 0x0013: "__int64",
    0x0020: "unsigned char", 0x0021: "unsigned short", 0x0022: "unsigned long", 0x0023: "unsigned __int64",
    0x0030: "bool", 0x0040: "float", 0x0041: "double",
    0x0068: "__int8", 0x0069: "unsigned __int8",
    0x0070: "char", 0x0071: "wchar_t", 0x0072: "__int16", 0x0073: "unsigned __int16",
    0x0074: "int", 0x0075: "unsigned int", 0x0076: "__int64", 0x0077: "unsigned __int64",
}


def read_msf(path):
    d = open(path, "rb").read()
    bs, _fpm, _n, dirbytes = struct.unpack_from("<IIII", d, 32)
    blockmap = struct.unpack_from("<I", d, 52)[0]
    dirpages = (dirbytes + bs - 1) // bs
    idx = struct.unpack_from("<%dI" % dirpages, d, blockmap * bs)
    dirdata = b"".join(d[p * bs:(p + 1) * bs] for p in idx)[:dirbytes]

    ns = struct.unpack_from("<I", dirdata, 0)[0]
    sizes = list(struct.unpack_from("<%dI" % ns, dirdata, 4))
    pos = 4 + 4 * ns
    streams = []
    for s in sizes:
        if s == 0xFFFFFFFF:
            s = 0
        nb = (s + bs - 1) // bs
        blocks = struct.unpack_from("<%dI" % nb, dirdata, pos)
        pos += 4 * nb
        streams.append(b"".join(d[b * bs:(b + 1) * bs] for b in blocks)[:s])
    return streams


def numeric(buf, off):
    """A CodeView numeric leaf: a bare uint16 when small, otherwise a tagged wider value."""
    (v,) = struct.unpack_from("<H", buf, off)
    if v < 0x8000:
        return v, off + 2
    if v == 0x8000: return struct.unpack_from("<b", buf, off + 2)[0], off + 3
    if v == 0x8001: return struct.unpack_from("<h", buf, off + 2)[0], off + 4
    if v == 0x8002: return struct.unpack_from("<H", buf, off + 2)[0], off + 4
    if v == 0x8003: return struct.unpack_from("<i", buf, off + 2)[0], off + 6
    if v == 0x8004: return struct.unpack_from("<I", buf, off + 2)[0], off + 6
    if v == 0x8009: return struct.unpack_from("<q", buf, off + 2)[0], off + 10
    if v == 0x800A: return struct.unpack_from("<Q", buf, off + 2)[0], off + 10
    raise ValueError("numeric leaf %#x" % v)


def cstr(buf, off):
    e = buf.index(b"\0", off)
    return buf[off:e].decode("latin-1"), e + 1


class Types:
    def __init__(self, pdb=DEFAULT_PDB):
        tpi = read_msf(pdb)[2]
        (_ver, hdrsize, self.lo, self.hi, _bytes) = struct.unpack_from("<IIIII", tpi, 0)
        self.recs = {}
        off = hdrsize
        ti = self.lo
        while ti < self.hi and off + 4 <= len(tpi):
            (ln, kind) = struct.unpack_from("<HH", tpi, off)
            self.recs[ti] = (kind, tpi[off + 4:off + 2 + ln])
            off += 2 + ln
            ti += 1
        self.by_name = {}
        for ti, (kind, buf) in self.recs.items():
            if kind in (LF_CLASS, LF_STRUCTURE, LF_UNION, LF_ENUM):
                n, prop = self._tag(kind, buf)
                if n and not (prop & 0x80):        # skip forward references
                    self.by_name.setdefault(n, ti)

    def _tag(self, kind, buf):
        """Name + property word of a class/struct/union/enum record."""
        try:
            if kind == LF_ENUM:
                _cnt, prop = struct.unpack_from("<HH", buf, 0)
                name, _ = cstr(buf, 12)
                return name, prop
            _cnt, prop = struct.unpack_from("<HH", buf, 0)
            off = 12 if kind == LF_UNION else 16
            _size, off = numeric(buf, off - 4 + 4) if kind == LF_UNION else numeric(buf, off)
            name, _ = cstr(buf, off)
            return name, prop
        except Exception:
            return None, 0

    def name(self, ti, depth=0):
        if ti < self.lo:
            base, mode = ti & 0xFF, (ti >> 8) & 0xFF
            b = BUILTIN.get(ti) or BUILTIN.get(base, "t%#x" % ti)
            return b + " *" if mode else b
        rec = self.recs.get(ti)
        if not rec or depth > 6:
            return "t%#x" % ti
        kind, buf = rec
        if kind in (LF_CLASS, LF_STRUCTURE, LF_UNION, LF_ENUM):
            n, _ = self._tag(kind, buf)
            return n or "t%#x" % ti
        if kind == LF_POINTER:
            (utype,) = struct.unpack_from("<I", buf, 0)
            return self.name(utype, depth + 1) + " *"
        if kind == LF_MODIFIER:
            (utype,) = struct.unpack_from("<I", buf, 0)
            return "const " + self.name(utype, depth + 1)
        if kind == LF_ARRAY:
            (elem, _idx) = struct.unpack_from("<II", buf, 0)
            n, _ = numeric(buf, 8)
            en = self.name(elem, depth + 1)
            esz = self.sizeof(elem) or 1
            return "%s[%d]" % (en, n // esz if esz else n)
        if kind == LF_BITFIELD:
            (utype,) = struct.unpack_from("<I", buf, 0)
            length, pos = struct.unpack_from("<BB", buf, 4)
            return "%s : %d" % (self.name(utype, depth + 1), length)
        if kind in (LF_PROCEDURE, LF_MFUNCTION):
            return "<function>"
        return "t%#x" % ti

    def sizeof(self, ti):
        if ti < self.lo:
            base = ti & 0xFF
            if (ti >> 8) & 0xFF:
                return 4
            return {0x10: 1, 0x20: 1, 0x30: 1, 0x68: 1, 0x69: 1, 0x70: 1,
                    0x11: 2, 0x21: 2, 0x71: 2, 0x72: 2, 0x73: 2,
                    0x12: 4, 0x22: 4, 0x74: 4, 0x75: 4, 0x40: 4,
                    0x13: 8, 0x23: 8, 0x76: 8, 0x77: 8, 0x41: 8}.get(base, 4)
        rec = self.recs.get(ti)
        if not rec:
            return 0
        kind, buf = rec
        if kind in (LF_CLASS, LF_STRUCTURE, LF_UNION):
            try:
                off = 12 if kind == LF_UNION else 16
                sz, _ = numeric(buf, off)
                return sz
            except Exception:
                return 0
        if kind == LF_ARRAY:
            n, _ = numeric(buf, 8)
            return n
        if kind == LF_POINTER:
            return 4
        if kind == LF_ENUM:
            return 4
        if kind == LF_MODIFIER:
            return self.sizeof(struct.unpack_from("<I", buf, 0)[0])
        return 0

    def fields(self, ti):
        """Members of a class/struct, as (offset, name, typename, size)."""
        kind, buf = self.recs[ti]
        _cnt, _prop, fieldlist = struct.unpack_from("<HHI", buf, 0)
        return self._walk(fieldlist)

    def _walk(self, fl_ti):
        out = []
        rec = self.recs.get(fl_ti)
        if not rec or rec[0] != LF_FIELDLIST:
            return out
        buf = rec[1]
        off = 0
        while off < len(buf):
            if buf[off] >= 0xF0:                 # LF_PAD
                off += buf[off] & 0x0F
                continue
            (leaf,) = struct.unpack_from("<H", buf, off)
            off += 2
            if leaf == LF_MEMBER:
                _attr, ty = struct.unpack_from("<HI", buf, off)
                o, p = numeric(buf, off + 6)
                nm, p = cstr(buf, p)
                out.append((o, nm, self.name(ty), self.sizeof(ty)))
                off = p
            elif leaf == LF_BCLASS:
                _attr, ty = struct.unpack_from("<HI", buf, off)
                o, p = numeric(buf, off + 6)
                out.append((o, "<base %s>" % self.name(ty), "", self.sizeof(ty)))
                off = p
            elif leaf == LF_VFUNCTAB:
                out.append((0, "<vftable>", "void **", 4))
                off += 6
            elif leaf == LF_ENUMERATE:
                _attr = struct.unpack_from("<H", buf, off)[0]
                v, p = numeric(buf, off + 2)
                nm, p = cstr(buf, p)
                out.append((v, nm, "", 0))
                off = p
            elif leaf in (LF_STMEMBER, LF_NESTTYPE):
                _a, ty = struct.unpack_from("<HI", buf, off)
                nm, p = cstr(buf, off + 6)
                off = p
            elif leaf == LF_ONEMETHOD:
                attr, _ty = struct.unpack_from("<HI", buf, off)
                p = off + 6
                if ((attr >> 2) & 7) in (4, 6):        # intro virtual carries a vtable slot
                    p += 4
                _nm, p = cstr(buf, p)
                off = p
            elif leaf == LF_METHOD:
                _cnt, _lst = struct.unpack_from("<HI", buf, off)
                _nm, p = cstr(buf, off + 6)
                off = p
            elif leaf in (LF_VBCLASS, LF_IVBCLASS):
                off += 10
                _v, off = numeric(buf, off)
                _v, off = numeric(buf, off)
            else:
                break
            off = (off + 3) & ~3 if False else off
        return out

    def enum_values(self, ti):
        kind, buf = self.recs[ti]
        _cnt, _prop, _ut, fieldlist = struct.unpack_from("<HHII", buf, 0)
        return self._walk(fieldlist)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pdb", default=DEFAULT_PDB)
    ap.add_argument("--list")
    ap.add_argument("--struct")
    ap.add_argument("--enum")
    ap.add_argument("--limit", type=int, default=400)
    a = ap.parse_args()

    t = Types(a.pdb)

    if a.list:
        hits = sorted(n for n in t.by_name if a.list.lower() in n.lower())
        print("%d types matching %r" % (len(hits), a.list))
        for n in hits[:a.limit]:
            print("  %-60s size %d" % (n, t.sizeof(t.by_name[n])))
        return

    if a.enum:
        ti = t.by_name.get(a.enum) or next((v for k, v in t.by_name.items() if a.enum in k), None)
        if not ti:
            sys.exit("no enum %r" % a.enum)
        for v, nm, _, _ in t.enum_values(ti):
            print("  %4d  %s" % (v, nm))
        return

    if a.struct:
        ti = t.by_name.get(a.struct) or next((v for k, v in sorted(t.by_name.items()) if a.struct in k), None)
        if not ti:
            sys.exit("no type %r" % a.struct)
        nm = next(k for k, v in t.by_name.items() if v == ti)
        print("%s   size %d   (type index %#x)\n" % (nm, t.sizeof(ti), ti))
        for o, fname, tname, sz in t.fields(ti):
            print("  +0x%04X  %-40s %-34s %s" % (o, fname, tname, sz or ""))


if __name__ == "__main__":
    main()
