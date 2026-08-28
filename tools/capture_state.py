#!/usr/bin/env python
"""Snapshot the server state a packet capture depends on, so the capture stays interpretable later.

    python tools/capture_state.py --out state.json           # every zone
    python tools/capture_state.py --zone zone02 --out state.json

RUN THIS IMMEDIATELY BEFORE OR AFTER TAKING A CAPTURE. A pcap on its own is not interpretable: the damage
a swing does is a function of server DATA as much as of server code, and that data is neither in the
packets nor guaranteed to match any reference tree.

WHY THIS EXISTS -- the mistake it is built to prevent, which has now happened twice in different forms:

 1. `Z:/ServerSource` IS NOT THE DEPLOYED DATA. `DamageByAngle.txt` on the live server is flat 1000; the
    reference tree's copy expands to 1000-1200. A prediction built on the reference tree once scored
    216/219 and the number was worthless.

 2. AND THE DEPLOYED FILE IS NOT NECESSARILY WHAT THE RUNNING PROCESS HOLDS. These tables are read once,
    at zone startup. `DamageByAngle.txt` was edited at 2026-07-30 10:51 and `Damage.pcapng` was taken at
    11:58 the same day -- 67 minutes later, with no way now to tell whether the zone had restarted in
    between. Reading the file today answers a question about today. That ambiguity is unresolved and it
    is the only thing standing between this port and 219/219 on that capture.

So every file here is reported with BOTH its mtime and `inForce`: whether the zone process running now
started AFTER the file was last written. `inForce: false` means the process is serving something other
than what is on disk, and any conclusion drawn from that file is void.
"""
import argparse
import json
import os
import subprocess
import sys
import time

# The data the damage engine reads at startup. Anything whose value can change a predicted damage.
FILES = [
    "9Data/Shine/World/DamageByAngle.txt",
    "9Data/Shine/DamageLvGapPVE.shn",
    "9Data/Shine/DamageLvGapEVP.shn",
    "9Data/Shine/MobInfoServer.shn",
    "9Data/Shine/MobInfo.shn",
    "9Data/Shine/MobWeapon.shn",
    "9Data/Shine/ItemInfo.shn",
]
PARAM_GLOB = "9Data/Shine/World/Param*Server.txt"
ROOT = "/fiesta"

# Equipped gear. `tItem.nStorageType` 9 is the backpack -- everything else is worn or otherwise on the
# character, and any of it can carry stats, so nothing but 9 is dropped.
INVENTORY_STORAGE_TYPE = 9

# A random option's `nOptionType` low digit is the PARAMETER::STAT SLOT INDEX the bonus lands in -- not a
# random-option enum of its own. Same ordering the port carries from the PDB and `oracle_accessors.py`.
#
# Confirmed on two items against the client's own tooltips: Kaineneceflight reads STR +10 (+8), Dex +7
# (+14), END +3, INT +7 against pairs (0,8) (3,7) (2,14) and `GradeItemOption` STR 10 / CON 3 / DEX 7;
# Kaineneceshield reads DEX (+14), END +10 (+6), SPR +5 (+7) against pairs (1,6) (4,7) (2,14) and fixed
# CON 10 / MEN 5. Type 2 = Dex appears on both, which is what makes it a cross-check rather than a fit.
#
# The client labels Con as END and Men as SPR.
STAT_SLOT = {0: "Str", 1: "Con", 2: "Dex", 3: "Int", 4: "Men"}

# Within an item's option block: offset 0 is the upgrade level, offset 3 a bitmask of which random-option
# slots are filled, and offsets (4,5) (6,7) (8,9) are (stat slot, rolled value) PAIRS. The FIXED half of
# each bonus is not here at all -- it is `GradeItemOption`, keyed by the item's InxName.
UPGRADE_OFFSET, RANDOM_PAIR_OFFSETS = 0, ((4, 5), (6, 7), (8, 9))

# Read-only, one batch. `-h -1` drops headers, `-s |` makes it parseable, and every statement is a SELECT.
CHARACTER_SQL = """SET NOCOUNT ON;
SELECT '@@CHAR', c.nCharNo, c.sID, c.nLevel, s.nClass, s.nRace, s.nGender,
       c.nStrength, c.nConstitute, c.nDexterity, c.nIntelligence, c.nMentalPower,
       c.nRedistributePoint, c.nAP, c.nHP, c.nSP, c.sLoginZone, c.nFame
  FROM tCharacter c JOIN tCharacterShape s ON s.nCharNo = c.nCharNo
 WHERE c.sID = '%(name)s';
SELECT '@@ITEM', i.nStorageType, i.nStorage, i.nItemID, i.nFlags, i.nItemKey
  FROM tItem i JOIN tCharacter c ON c.nCharNo = i.nOwner
 WHERE c.sID = '%(name)s' AND i.nStorageType <> %(inv)d
 ORDER BY i.nStorageType, i.nStorage;
SELECT '@@OPT', o.nItemKey, o.nOptionType, o.nOptionData
  FROM tItemOptions o JOIN tItem i ON i.nItemKey = o.nItemKey
  JOIN tCharacter c ON c.nCharNo = i.nOwner
 WHERE c.sID = '%(name)s' AND i.nStorageType <> %(inv)d
 ORDER BY o.nItemKey, o.nOptionType;
GO
"""

# One shell round-trip per pod. Sections are delimited so a partial read is detectable rather than
# silently mis-parsed -- a truncated exec that looks like an empty table is exactly the sort of
# "negative result" this repo has been burned by.
SCRIPT = r"""
echo '@@START'
ps -eo pid,lstart,comm --no-headers 2>/dev/null | grep -i -m1 'Zone.exe' \
  || ps -eo pid,lstart,comm --no-headers 2>/dev/null | head -1
echo '@@STAT'
for f in %(files)s; do
  if [ -f "$f" ]; then
    stat -c '%%Y %%s %%n' "$f"
    sha256sum "$f" | cut -c1-16
  else
    echo "MISSING 0 $f"
    echo '-'
  fi
done
echo '@@PARAMS'
ls -1 %(params)s 2>/dev/null | while read f; do
  echo "$(stat -c '%%Y' "$f") $(sha256sum "$f" | cut -c1-16) $(basename "$f")"
done
echo '@@ANGLE'
cat %(angle)s 2>/dev/null
"""


def kubectl(ns, pod, script):
    """MSYS_NO_PATHCONV keeps Git-Bash from rewriting /fiesta/... into a Windows path on the way in."""
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    out = subprocess.run(["kubectl", "exec", "-n", ns, pod, "--", "bash", "-lc", script],
                         capture_output=True, env=env)
    return out.stdout.decode("utf-8", "replace").replace("\r", "")


def sqlcmd(ns, database, sql):
    """Run a read-only batch against the game database, through the mssql pod.

    The quoting here is fiddly and this is the form that works: the SQL goes in on STDIN through
    `exec -i` so nothing has to survive a shell, and MSYS_NO_PATHCONV stops Git-Bash rewriting the
    /opt/... tools path into a Windows one."""
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    inner = ("/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa "
             "-P \"${MSSQL_SA_PASSWORD:-$SA_PASSWORD}\" -C -W -h -1 -s '|' -d " + database)
    out = subprocess.run(["kubectl", "exec", "-i", "-n", ns, "mssql-0", "--", "bash", "-lc", inner],
                         input=sql.encode(), capture_output=True, env=env)
    return out.stdout.decode("utf-8", "replace").replace(chr(13), "")


def character(ns, database, name):
    """The character's SERVER-SIDE parameter state, as the server persists it.

    ⚠️ What this is: the INPUTS the server builds a `Parameter::Container` from -- class, level, allocated
    free-stat points, and every worn item with its options. That is what `CharacterParameters.Build` needs,
    and it means a capture can be checked against a container CONSTRUCTED the way the server constructs
    one, instead of one inverted out of displayed accessor outputs. Inverting was wrong for a month.

    ⚠️ What this is NOT: the live container. Buffs, abnormal states, charged effects and anything else
    applied in-session are not persisted and are not here. It is also the state NOW, not at capture time --
    which is exactly why this is meant to be run beside the capture, not months later."""
    raw = sqlcmd(ns, database, CHARACTER_SQL % {"name": name.replace("'", "''"),
                                                "inv": INVENTORY_STORAGE_TYPE})
    rows = [[c.strip() for c in ln.split("|")] for ln in raw.splitlines() if "|" in ln]

    def num(v):
        try:
            return int(v)
        except ValueError:
            return v

    out = {"name": name, "database": database, "found": False, "items": [], "options": {}}
    for r in rows:
        if r[0] == "@@CHAR" and len(r) >= 18:
            out.update({
                "found": True, "charNo": num(r[1]), "sID": r[2], "level": num(r[3]),
                # The class id -- resolve it to a name through `ClassName.shn`, which is what decides the
                # Param<Class>Server.txt file and therefore JobChangeDmgUp. Not resolved here on purpose:
                # this tool is a faithful dumper and has no SHN reader.
                "classId": num(r[4]), "race": num(r[5]), "gender": num(r[6]),
                # The CHARSTATDISTSTR allocation, i.e. the same points `so_ply_FreeStatStr` indexes.
                "freeStat": {"Strength": num(r[7]), "Constitute": num(r[8]), "Dexterity": num(r[9]),
                             "Intelligence": num(r[10]), "MentalPower": num(r[11]),
                             "RedistributePoint": num(r[12])},
                "statPointsUnspent": num(r[13]), "hp": num(r[14]), "sp": num(r[15]),
                "loginZone": r[16], "fame": num(r[17]),
            })
        elif r[0] == "@@ITEM" and len(r) >= 6:
            out["items"].append({"storageType": num(r[1]), "slot": num(r[2]), "itemId": num(r[3]),
                                 "flags": num(r[4]), "itemKey": r[5]})
        elif r[0] == "@@OPT" and len(r) >= 4:
            out["options"].setdefault(r[1], []).append({"type": num(r[2]), "data": num(r[3])})

    for it in out["items"]:
        it["options"] = out["options"].get(it["itemKey"], [])
    out.pop("options")
    return out


def zone_pods(ns, prefix):
    out = subprocess.run(["kubectl", "get", "pods", "-n", ns, "-o", "name"], capture_output=True)
    pods = [p.split("/", 1)[1] for p in out.stdout.decode().split() if "/zone" in p]
    return [p for p in pods if prefix is None or p.startswith(prefix)]


def parse_damage_by_angle(text):
    """The one table that has actually burned this project, parsed rather than hashed.

    Two `#Table` blocks -- DamageByAngle_Chr and _Mob -- each a list of (angle, rate) breakpoints that the
    server expands into 91 entries. Recording the breakpoints means a later reader can see the curve
    itself, not a digest they have no way to interpret."""
    tables, current = {}, None
    for line in text.splitlines():
        parts = [p.strip() for p in line.split("\t") if p.strip()]
        if not parts:
            continue
        if parts[0].lower() == "#table" and len(parts) > 1:
            current = parts[1]
            tables[current] = []
        elif parts[0].lower() == "#record" and current and len(parts) >= 3:
            try:
                tables[current].append([int(parts[1]), int(parts[2])])
            except ValueError:
                pass
    return tables


def sections_of(raw):
    out, key = {}, None
    for line in raw.splitlines():
        if line.startswith("@@"):
            key = line[2:]
            out[key] = []
        elif key is not None:
            out[key].append(line)
    return out


def snapshot(ns, pod):
    script = SCRIPT % {
        "files": " ".join(ROOT + "/" + f for f in FILES),
        "params": ROOT + "/" + PARAM_GLOB,
        "angle": ROOT + "/9Data/Shine/World/DamageByAngle.txt",
    }
    sections = sections_of(kubectl(ns, pod, script))

    # The zone's own PROCESS, not the pod: a container can restart a crashed Zone.exe without the pod
    # restarting, and it is the process that holds the expanded tables.
    started, proc_epoch = None, None
    if sections.get("START"):
        fields = sections["START"][0].split()
        if len(fields) > 2:
            started = " ".join(fields[1:-1])
            try:
                proc_epoch = int(time.mktime(time.strptime(started)))
            except ValueError:
                pass

    def in_force(mtime):
        # None, not False, when it cannot be decided -- "unknown" and "stale" are different answers and
        # collapsing them is how a missing check turns into a confident wrong one.
        if mtime is None or proc_epoch is None:
            return None
        return proc_epoch > mtime

    files, stat_lines = {}, sections.get("STAT", [])
    for i in range(0, len(stat_lines) - 1, 2):
        head, digest = stat_lines[i].split(), stat_lines[i + 1].strip()
        if len(head) < 3:
            continue
        mtime = None if head[0] == "MISSING" else int(head[0])
        files[head[2].replace(ROOT + "/", "")] = {
            "mtime": mtime,
            "mtimeIso": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(mtime)) if mtime else None,
            "bytes": int(head[1]),
            "sha256_16": digest,
            "inForce": in_force(mtime),
        }

    params = {}
    for line in sections.get("PARAMS", []):
        bits = line.split()
        if len(bits) == 3:
            params[bits[2]] = {"mtime": int(bits[0]), "sha256_16": bits[1],
                               "inForce": in_force(int(bits[0]))}

    return {
        "zoneProcessStarted": started,
        "zoneProcessStartedEpoch": proc_epoch,
        "damageByAngle": parse_damage_by_angle("\n".join(sections.get("ANGLE", []))),
        "files": files,
        "classParamFiles": params,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--namespace", default="fiesta")
    ap.add_argument("--zone", help="pod name prefix, e.g. zone02; default is every zone pod")
    ap.add_argument("--character", help="also dump this character's server-side parameter state")
    ap.add_argument("--database", default="World00_Character")
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    pods = zone_pods(a.namespace, a.zone)
    if not pods:
        print("no zone pods matched", file=sys.stderr)
        return 1

    state = {"takenAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
             "namespace": a.namespace, "zones": {}}
    if a.character:
        print("  character %s ..." % a.character, file=sys.stderr)
        state["character"] = character(a.namespace, a.database, a.character)
    for pod in pods:
        print("  %s ..." % pod, file=sys.stderr)
        state["zones"][pod] = snapshot(a.namespace, pod)

    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=1)

    print("\nwrote %s\n" % a.out)

    ch = state.get("character")
    if ch and ch["found"]:
        worn = [i for i in ch["items"] if i["storageType"] == 8]
        print("%-30s charNo %s  level %s  classId %s  (resolve via ClassName.shn)"
              % (ch["name"], ch["charNo"], ch["level"], ch["classId"]))
        print("%-30s free stat %s" % ("", ch["freeStat"]))
        for it in worn:
            # `nOptionType` is BLOCK BASE + OFFSET, and the base is per EQUIP SLOT -- 400 accessory,
            # 500 weapon, 600 armour, 700 shield, 800 helmet. Offset 0 is the upgrade level.
            #
            # ⚠️ This used to look for the literal types 600/700 and nothing else. On a weapon that is the
            # WRONG BLOCK, so a +5 sword printed as unenhanced, and every random option on every item was
            # dropped from the summary entirely. A snapshot that quietly omits stats is worse than no
            # snapshot: it reads as authoritative.
            opts = sorted(it["options"], key=lambda o: o["type"])
            by_offset = {o["type"] % 100: o["data"] for o in opts}
            up = by_offset.get(UPGRADE_OFFSET)

            rolled = []
            for slot_off, value_off in RANDOM_PAIR_OFFSETS:
                stat, value = by_offset.get(slot_off), by_offset.get(value_off)
                # 0 is a REAL stat slot (Str), so presence is tested on the value, never on the slot.
                if value:
                    rolled.append("%s+%d" % (STAT_SLOT.get(stat, "slot%s" % stat), value))
            it["randomOptions"] = rolled

            print("%-30s slot %-3s item %-7s %-6s %s"
                  % ("", it["slot"], it["itemId"],
                     "" if up in (None, 0) else "+%s" % up, "  ".join(rolled)))
        print()
    elif ch:
        print("!! character %r not found in %s -- nothing dumped" % (ch["name"], ch["database"]))

    problems = 0
    for pod, z in state["zones"].items():
        chr_rates = sorted({r for _, r in z["damageByAngle"].get("DamageByAngle_Chr", [])})
        mob_rates = sorted({r for _, r in z["damageByAngle"].get("DamageByAngle_Mob", [])})
        stale = [n for n, f in list(z["files"].items()) + list(z["classParamFiles"].items())
                 if f.get("inForce") is False]
        print("%-30s zone process started %s" % (pod, z["zoneProcessStarted"] or "UNREAD"))
        print("%-30s DamageByAngle  Chr %s  Mob %s" % ("", chr_rates or "UNREAD", mob_rates or "UNREAD"))
        if stale:
            problems += 1
            print("%-30s !! NOT IN FORCE (written after this process started): %s"
                  % ("", ", ".join(sorted(stale))))
    if problems:
        print("\n!! A file marked NOT IN FORCE is not what the running zone is serving. Restart that zone")
        print("   before capturing, or the capture cannot be read against these files.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
