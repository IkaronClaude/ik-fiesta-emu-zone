#!/usr/bin/env python
"""Run any function from the real Zone.exe under emulation, so a C# translation can be diffed against it.

    from zone_oracle import ZoneOracle
    o = ZoneOracle()
    o.call("??ADamageTable@DamageByAngle@@QAEGH@Z", [90], this=table_addr, ret="u16")

This is the compliance instrument for the whole port. Everything it does is in service of one thing: get a
single function to execute in isolation and return what it would have returned inside the running server.

WHY EMULATION RATHER THAN READING THE DISASSEMBLY: reading gives you a hypothesis, running gives you an
answer. On the damage engine, reading produced several confident and wrong conclusions that a single
differential run would have caught -- and, just as often, the run disagreed because the HARNESS was wrong,
which reading could never have revealed. Treat both sides as suspects.

Setup this performs, each item because its absence produced a specific failure:
  * IAT stubbing -- an unresolved import thunk holds its on-disk value and control lands in garbage.
  * profiler entry/exit stubs -- FunctionProfiler::pr_Entrance is on the front of most methods.
  * a zeroed _ptiddata for the CRT -- __getptd_noexit decodes an encoded pointer from a null global and
    calls through it.
  * x87 control word set to 0x027F -- UNICORN BOOTS IT AT 0x0000, which is 24-bit SINGLE precision. Every
    floating-point result is then silently rounded to float32, an error of ~1e-7 that reads as harmless
    last-digit noise and is not.
  * an unmapped-memory hook that maps on demand -- the alternative is guessing every buffer a function
    touches.

⚠️ UNICORN CACHES TRANSLATED BLOCKS. Once a function has executed, rewriting its BYTES does nothing. Any
value you want to vary per call must live in a DATA slot the code reads, never as an immediate patched into
an instruction. This trap has produced fictitious findings three separate times.
"""
import os
import re
import struct

IMAGE = 0x400000
STACK = 0x00200000
HEAP = 0x30000000
STUB = 0x70000000
SLOTS = 0x78000000          # data slots for patched-in values; see the cache warning above

DEFAULT_EXE = os.environ.get("ZONE_EXE", r"Z:/ServerSource/Zone00/Zone.exe")
DEFAULT_PDB = os.environ.get("ZONE_PDB", r"Z:/ServerSource/Zone00/Zone.pdb")

# Bytes each stdcall import pops, for the APIs reached so far. Anything absent is assumed to take none;
# a wrong guess corrupts the stack rather than failing loudly, so add to this table when a new one appears.
IMPORT_ARGBYTES = {
    "GetSystemTimeAsFileTime": 4, "QueryPerformanceCounter": 4, "GetTickCount": 0,
    "EnterCriticalSection": 4, "LeaveCriticalSection": 4, "InitializeCriticalSection": 4,
    "GetCurrentThreadId": 0, "TlsGetValue": 4, "TlsSetValue": 8,
}


def publics(pdb, segrva):
    """Public symbols by name -> VA, scanned straight out of the PDB's S_PUB32 records.

    A full MSF parse is not needed: each record is `[len][type=0x110E][flags][offset][seg][name\\0]`, so
    finding a plausible name and validating the record type 12 bytes earlier recovers the table."""
    out = {}
    for m in re.finditer(rb"[?_][ -~]{4,220}", pdb):
        i = m.start()
        if i < 14 or struct.unpack_from("<H", pdb, i - 12)[0] != 0x110E:
            continue
        off, seg = struct.unpack_from("<IH", pdb, i - 6)
        if seg in segrva:
            out.setdefault(pdb[i:pdb.find(b"\x00", i)].decode("latin-1"), IMAGE + segrva[seg] + off)
    return out


class ZoneOracle:
    def __init__(self, exe=DEFAULT_EXE, pdb=DEFAULT_PDB):
        from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_MEM_UNMAPPED
        import pefile

        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        pe = pefile.PE(exe, fast_load=True)
        # fast_load skips the data directories, so DIRECTORY_ENTRY_IMPORT would be absent and the IAT
        # stubbing below would silently do nothing.
        pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]])
        data = open(exe, "rb").read()

        self.uc.mem_map(IMAGE, (pe.OPTIONAL_HEADER.SizeOfImage + 0xFFF) & ~0xFFF)
        self.uc.mem_write(IMAGE, data[:pe.OPTIONAL_HEADER.SizeOfHeaders])
        for s in pe.sections:
            raw = data[s.PointerToRawData:s.PointerToRawData + s.SizeOfRawData]
            if raw:
                self.uc.mem_write(IMAGE + s.VirtualAddress, raw)
        self.uc.mem_map(STACK, 0x200000)
        self.uc.mem_map(HEAP, 0x200000)
        self.uc.mem_map(STUB, 0x10000)
        self.uc.mem_map(SLOTS, 0x10000)

        segrva = {i + 1: s.VirtualAddress for i, s in enumerate(pe.sections)}
        self.syms = publics(open(pdb, "rb").read(), segrva)
        self.by_va = {}
        for n, v in self.syms.items():
            self.by_va.setdefault(v, n)

        self.uc.hook_add(UC_HOOK_MEM_UNMAPPED,
                         lambda uc, a, addr, sz, v, u: (self._map(addr), True)[1])

        self._stub_n = 0
        self._slot_n = 0
        self._stub_imports(pe)
        self._stub_runtime()
        self._set_fpu()

    # ---- memory helpers ------------------------------------------------------------------------------

    def _map(self, addr):
        try:
            self.uc.mem_map(addr & ~0xFFF, 0x1000)
        except Exception:
            pass

    def alloc(self, size, fill=b"\x00"):
        """A zeroed scratch block on the emulated heap. Returned addresses never overlap."""
        addr = HEAP + 0x10000 + self._slot_n * 0x1000
        self._slot_n += max(1, (size + 0xFFF) // 0x1000)
        self.uc.mem_write(addr, fill * size if len(fill) == 1 else fill)
        return addr

    def slot(self, value=0, fmt="<I"):
        """A DATA slot. Use this for anything that varies per call -- never patch an immediate."""
        addr = SLOTS + self._stub_n * 8
        self._stub_n += 1
        self.uc.mem_write(addr, struct.pack(fmt, value))
        return addr

    def write(self, addr, blob):
        self.uc.mem_write(addr, blob)

    def read(self, addr, size):
        return bytes(self.uc.mem_read(addr, size))

    # ---- environment ---------------------------------------------------------------------------------

    def _emit(self, code):
        addr = STUB + 0x1000 + self._stub_n * 0x20
        self._stub_n += 1
        self.uc.mem_write(addr, code)
        return addr

    def _stub_imports(self, pe):
        for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
            for imp in entry.imports:
                if not imp.address:
                    continue
                name = (imp.name or b"").decode("latin-1")
                argb = IMPORT_ARGBYTES.get(name, 0)
                stub = self._emit(bytes([0x31, 0xC0]) +                       # xor eax, eax
                                  (bytes([0xC2]) + struct.pack("<H", argb) if argb else bytes([0xC3])))
                self.uc.mem_write(imp.address, struct.pack("<I", stub))

    def _stub_runtime(self):
        # The profiler wraps most methods; it takes one arg and is __thiscall, so `ret 4`.
        for n in ("?pr_Entrance@PerformanceRecorder@FunctionProfiler@@QAEXPAD@Z",
                  "?pr_Exit@PerformanceRecorder@FunctionProfiler@@QAEXPAD@Z"):
            if n in self.syms:
                self.uc.mem_write(self.syms[n], bytes([0xC2, 0x04, 0x00]))
        # __getptd_noexit walks TlsGetValue -> DecodePointer -> `call eax` on an ENCODED pointer held in a
        # global that is zero here, so the call lands on null. There is no per-thread state worth
        # emulating for a pure function: hand back a zeroed _ptiddata.
        ptd = self.alloc(0x800)
        for n in ("__getptd_noexit", "__getptd"):
            if n in self.syms:
                self.uc.mem_write(self.syms[n], bytes([0xB8]) + struct.pack("<I", ptd) + bytes([0xC3]))

    # AssertClass entries, with the stack bytes each pops. An assert that actually runs reaches string
    # formatting, the clock and ShineExit, none of which are emulated -- so a function whose validation
    # branch fires dies with a CPU exception instead of returning. That looks like the function being
    # unreachable when it is really the harness being incomplete.
    #
    # The cleanup byte counts are READ FROM THE CALL SITES, not guessed: getting one wrong corrupts the
    # stack and the caller returns into garbage, which is far harder to diagnose than the original fault.
    ASSERT_STUBS = {
        "?ac_AssertFail@AssertClass@@AAEXPBD0@Z": 8,        # __thiscall, 2 args
        "?ac_AssertFail@AssertClass@@AAEXPBDHH@Z": 12,      # __thiscall, 3 args
        "?ac_DateTime@AssertClass@@AAEXXZ": 0,
        "?ShineExit@@YAXPAD@Z": None,                       # __cdecl -- caller cleans, so a plain ret
    }

    def stub_asserts(self, extra=()):
        """Neutralise assertion reporting so a validation branch RETURNS instead of faulting.

        `extra` takes (va, pop_bytes) pairs for entries whose symbol name is unreliable -- the PDB's public
        record for the assert helper reached from DamageTable::operator[] reads as a truncated `?ac_As`,
        so that one is stubbed by address with the cleanup read off its call site."""
        for sym, pop in self.ASSERT_STUBS.items():
            if sym not in self.syms:
                continue
            code = bytes([0xC3]) if pop is None else bytes([0xC2]) + struct.pack("<H", pop)
            self.uc.mem_write(self.syms[sym], code)
        for va, pop in extra:
            code = bytes([0xC3]) if pop is None else bytes([0xC2]) + struct.pack("<H", pop)
            self.uc.mem_write(va, code)


    def _set_fpu(self):
        """x87 precision control to 53-bit, which is what a real MSVC process runs.

        Unicorn boots FPCW at 0x0000, whose PC field is 00 = 24-bit SINGLE. Left alone, every fmul and
        fdiv rounds to float32. The error is ~1e-7 relative -- small enough to look like noise and large
        enough to make a correct translation appear wrong."""
        try:
            from unicorn.x86_const import UC_X86_REG_FPCW
            self.uc.reg_write(UC_X86_REG_FPCW, 0x027F)
        except Exception:
            pass

    # ---- calling -------------------------------------------------------------------------------------

    def stub_function(self, symbol, code):
        """Replace a function wholesale. `code` must be position-independent and return correctly.

        For a value that varies between calls, have the code read a `slot()` -- see the cache warning."""
        if symbol in self.syms:
            self.uc.mem_write(self.syms[symbol], code)
            return True
        return False

    def const_stub(self, value_slot, ret_bytes=0):
        """`mov eax,[slot]; ret [n]` -- the correct way to force an integer return that can change."""
        code = bytes([0xA1]) + struct.pack("<I", value_slot)
        code += bytes([0xC2]) + struct.pack("<H", ret_bytes) if ret_bytes else bytes([0xC3])
        return code

    def call(self, symbol, args=(), this=None, ret="int", cleanup="callee"):
        """Execute one function and return its result.

        `args` are pushed right-to-left as 32-bit words; pass an int, or `("double", x)` for a value that
        occupies two words. `this` goes in ECX (__thiscall). `ret` is int/u16/u8/double/float/void.
        `cleanup` is who pops the arguments -- MSVC methods are callee-cleaned, __cdecl helpers are not.
        """
        from unicorn.x86_const import UC_X86_REG_ESP, UC_X86_REG_ECX, UC_X86_REG_EAX

        va = self.syms[symbol] if symbol in self.syms else symbol
        if not isinstance(va, int):
            raise KeyError("unknown symbol %r" % symbol)

        words = []
        for a in args:
            if isinstance(a, tuple) and a[0] == "double":
                words += list(struct.unpack("<II", struct.pack("<d", float(a[1]))))
            elif isinstance(a, float):
                words += list(struct.unpack("<II", struct.pack("<d", a)))
            else:
                words.append(int(a) & 0xFFFFFFFF)

        # A landing pad to stop at. For a double return the value is in st(0), which cannot be read
        # portably, so the pad first stores it to memory.
        result = self.alloc(16)
        if ret in ("double", "float"):
            pad = self._emit(bytes([0xDD, 0x1D]) + struct.pack("<I", result) + bytes([0xC3]))
            stop = pad + 6
        else:
            pad = self._emit(bytes([0xC3]))
            stop = pad

        esp = STACK + 0x180000
        frame = struct.pack("<I", pad) + b"".join(struct.pack("<I", w) for w in words)
        self.uc.mem_write(esp, frame)
        self.uc.reg_write(UC_X86_REG_ESP, esp)
        self.uc.reg_write(UC_X86_REG_ECX, this if this else 0)
        self.uc.emu_start(va, stop, count=20_000_000)

        if ret == "void":
            return None
        if ret in ("double", "float"):
            return struct.unpack("<d", self.read(result, 8))[0]
        eax = self.uc.reg_read(UC_X86_REG_EAX)
        if ret == "u8":
            return eax & 0xFF
        if ret == "u16":
            return eax & 0xFFFF
        if ret == "uint":
            return eax
        return eax - (1 << 32) if eax >= (1 << 31) else eax


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser(description="smoke-test the oracle")
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--pdb", default=DEFAULT_PDB)
    a = ap.parse_args()
    o = ZoneOracle(a.exe, a.pdb)
    print("symbols: %d" % len(o.syms))
    from unicorn.x86_const import UC_X86_REG_FPCW
    print("FPCW   : 0x%04X (0x027F = 53-bit double, what a real process runs)"
          % o.uc.reg_read(UC_X86_REG_FPCW))
