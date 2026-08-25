#!/usr/bin/env python
"""Size the port: how many real game functions are in Zone.exe, as opposed to library and compiler noise.

    python tools/survey.py
    python tools/survey.py --domain "Mob|Aggro|Target|Tactic|Skill|Abstate"

Counts PUBLIC symbols that live in an executable section, then classifies them. Two caveats that matter for
reading the number:

  * PUBLICS ONLY. File-static functions have no public record, so the true function count is HIGHER than
    anything here. This is a floor, not a total.
  * IDENTICAL CODE FOLDING. The linker merges functions with identical bodies, so several symbols can share
    one address -- MobTargetSelector::mts_AggroClear and MobActionElement::mae_AfterRegen are both
    0x00417480. Distinct addresses is what you must WRITE; distinct symbols is what you must NAME.
"""
import argparse, collections, os, re, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from zone_oracle import publics, DEFAULT_EXE, DEFAULT_PDB, IMAGE

# A symbol is library/compiler noise if it matches any of these.
CRT = re.compile(r"^_{1,2}[A-Za-z]|^\?terminate@|^\?_|@std@@|@stdext@|^\?\?[0-9A-Z]?\?\$_|_CRT|^_?Cnd_|^_?Mtx_")
STL_TEMPLATE = re.compile(r"\?\$(_?[A-Z]\w*)@|@\?\$")
RTTI_OR_DATA = re.compile(r"^\?\?_[RC7]|^\?\?_[89]|^\?\?_G|^\?\?_E")
BOOST_ETC = re.compile(r"@boost@|@lua|^lua_|^luaL_|@zlib|^_?deflate|^_?inflate|^mysql_|@Poco@")


def classify(name):
    if RTTI_OR_DATA.match(name):
        return "rtti/vtable/string"
    if BOOST_ETC.search(name):
        return "third-party (lua/zlib/etc)"
    if CRT.match(name):
        return "crt/compiler"
    if STL_TEMPLATE.search(name) and "@std@@" not in name:
        return "template instantiation"
    return "game"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--pdb", default=DEFAULT_PDB)
    ap.add_argument("--domain", default=None, help="regex; report this subset separately")
    ap.add_argument("--top", type=int, default=25, help="show the largest game classes")
    a = ap.parse_args()

    import pefile
    pe = pefile.PE(a.exe, fast_load=True)
    segrva = {i + 1: s.VirtualAddress for i, s in enumerate(pe.sections)}
    syms = publics(open(a.pdb, "rb").read(), segrva)

    IMAGE_SCN_MEM_EXECUTE = 0x20000000
    code = [(s.VirtualAddress, s.Misc_VirtualSize) for s in pe.sections
            if s.Characteristics & IMAGE_SCN_MEM_EXECUTE]

    def in_code(va):
        rva = va - IMAGE
        return any(v <= rva < v + max(sz, 1) for v, sz in code)

    buckets = collections.defaultdict(list)
    for n, v in syms.items():
        buckets["_all"].append((n, v))
        if in_code(v):
            buckets[classify(n)].append((n, v))

    print("Zone.exe -- %d public symbols, %d of them in executable sections\n"
          % (len(syms), sum(len(v) for k, v in buckets.items() if k != "_all")))

    print("%-28s %8s %10s" % ("category", "symbols", "distinct fn"))
    print("%-28s %8s %10s" % ("-" * 28, "-" * 8, "-" * 10))
    for k in sorted(buckets, key=lambda k: -len(buckets[k])):
        if k == "_all":
            continue
        addrs = {v for _, v in buckets[k]}
        print("%-28s %8d %10d" % (k, len(buckets[k]), len(addrs)))

    game = buckets["game"]
    game_addrs = {v for _, v in game}
    print("\nGAME CODE: %d symbols across %d distinct addresses" % (len(game), len(game_addrs)))
    print("  (publics only, so file-static functions are NOT counted -- this is a floor)")

    by_cls = collections.defaultdict(set)
    for n, v in game:
        m = re.match(r"\?(\w+)@(\w+)@(\w*)@?", n)
        cls = "(free functions)"
        if m:
            cls = m.group(2) if not m.group(3) else "%s::%s" % (m.group(3), m.group(2))
        by_cls[cls].add(n)
    print("\n  largest game classes by method count:")
    for cls, ms in sorted(by_cls.items(), key=lambda kv: -len(kv[1]))[:a.top]:
        print("     %-52s %4d" % (cls[:52], len(ms)))

    if a.domain:
        rx = re.compile(a.domain, re.I)
        sub = [(n, v) for n, v in game if rx.search(n)]
        print("\nDOMAIN /%s/: %d symbols, %d distinct addresses"
              % (a.domain, len(sub), len({v for _, v in sub})))
        dom_cls = collections.defaultdict(set)
        for n, v in sub:
            m = re.match(r"\?(\w+)@(\w+)@(\w*)@?", n)
            cls = m.group(2) if m and not m.group(3) else (("%s::%s" % (m.group(3), m.group(2))) if m else "(free)")
            dom_cls[cls].add(n)
        for cls, ms in sorted(dom_cls.items(), key=lambda kv: -len(kv[1]))[:a.top]:
            print("     %-52s %4d" % (cls[:52], len(ms)))


if __name__ == "__main__":
    main()
