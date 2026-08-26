#!/usr/bin/env python
"""Differential check: the C# cWell512Random against the real one in Zone.exe.

    python tools/verify_well512.py --draws 400

The RNG is the one piece of the simulator where "close enough" is worthless -- if the sequence differs at
all, every chance-driven branch diverges and a reproducible simulation is not reproducible against the
server. So this compares the raw doubles bit-for-bit, not to a tolerance.
"""
import argparse, hashlib, json, os, struct, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from zone_oracle import ZoneOracle

CSX = r'''
#r "{dll}"
using System;
using Fiesta.Emu.Zone.Random;

var state = new uint[16];
var parts = Console.ReadLine().Split(',');
for (var i = 0; i < 16; i++) state[i] = uint.Parse(parts[i]);
var index = uint.Parse(parts[16]);
var draws = int.Parse(parts[17]);

var rng = new cWell512Random(state, index);
for (var i = 0; i < draws; i++)
    Console.WriteLine(BitConverter.DoubleToInt64Bits(rng.well512_GetRandom()));
Console.Out.Flush();
'''


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--draws", type=int, default=400)
    ap.add_argument("--seed", type=int, default=12345)
    ap.add_argument("--dll", default=os.path.join(HERE, "..", "src", "Fiesta.Emu.Zone",
                                                  "bin", "Release", "net10.0", "Fiesta.Emu.Zone.dll"))
    a = ap.parse_args()

    import random as pyrandom
    rng = pyrandom.Random(a.seed)
    state = [rng.getrandbits(32) for _ in range(16)]
    index = rng.randrange(16)

    # ---- the real function, under emulation ----
    o = ZoneOracle()
    obj = o.alloc(0x80)
    o.write(obj, b"".join(struct.pack("<I", w) for w in state) + struct.pack("<I", index))
    sym = "?well512_GetRandom@cWell512Random@@QAENXZ"
    native = [o.call(sym, [], this=obj, ret="double") for _ in range(a.draws)]

    # ---- the port ----
    tmp = tempfile.mkdtemp(prefix="well512_")
    tag = hashlib.md5(("%s%s" % (time.time(), os.path.getmtime(a.dll))).encode()).hexdigest()[:10]
    csx = os.path.join(tmp, "w_%s.csx" % tag)     # unique name: dotnet-script caches BY FILENAME
    open(csx, "w", encoding="utf-8").write(CSX.format(dll=os.path.abspath(a.dll).replace("\\", "/")))
    proc = subprocess.run(["dotnet-script", csx], input=",".join(
        [str(w) for w in state] + [str(index), str(a.draws)]) + "\n",
        capture_output=True, text=True)
    if proc.returncode != 0:
        print(proc.stdout[-2000:]); print(proc.stderr[-2000:]); return 2
    ported = [struct.unpack("<d", struct.pack("<q", int(l)))[0]
              for l in proc.stdout.strip().splitlines() if l.strip()]

    bad = 0
    for i, (n, p) in enumerate(zip(native, ported)):
        if n != p:                      # EXACT -- a tolerance here would hide a diverging sequence
            bad += 1
            if bad <= 5:
                print("  draw %-4d native=%.17g  port=%.17g" % (i, n, p))
    print("\nstate seed=%d index=%d" % (a.seed, index))
    print("draws compared: %d   exact matches: %d   mismatches: %d"
          % (min(len(native), len(ported)), min(len(native), len(ported)) - bad, bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
