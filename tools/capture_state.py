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
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    pods = zone_pods(a.namespace, a.zone)
    if not pods:
        print("no zone pods matched", file=sys.stderr)
        return 1

    state = {"takenAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
             "namespace": a.namespace, "zones": {}}
    for pod in pods:
        print("  %s ..." % pod, file=sys.stderr)
        state["zones"][pod] = snapshot(a.namespace, pod)

    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=1)

    print("\nwrote %s\n" % a.out)
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
