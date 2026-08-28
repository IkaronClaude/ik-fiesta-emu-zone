# Taking a capture that stays interpretable

A pcap on its own is not enough to check a damage prediction against. Damage is a function of server
**data** as much as of server **code**, and that data is neither in the packets nor guaranteed to match any
reference tree. A capture without its state snapshot is a measurement with no units.

This is not a hypothetical. `Damage.pcapng` — four weeks of analysis, several retracted headline results —
is stuck on one unanswerable question, and the answer would have been one command at capture time.

## The two ways a capture goes uninterpretable

**1. The reference tree is not the deployed data.** `Z:/ServerSource/9Data/Shine/World/DamageByAngle.txt`
expands to 1000–1200. The deployed one is **flat 1000**. A prediction built on the reference tree scored
216/219 against `Damage.pcapng` and the number meant nothing.

**2. The deployed file is not necessarily what the running process holds.** These tables are read once, at
zone startup. `DamageByAngle.txt` was edited at **2026-07-30 10:51**; `Damage.pcapng` was taken at
**11:58 the same day**. Whether the zone restarted in those 67 minutes decides whether the capture ran on
the flat table or the stock curve — and it decides whether this port is at 136/219 or 219/219 on it.
Nobody wrote it down, and it is not recoverable now. Reading the file today answers a question about today.

## The protocol

**Before or immediately after capturing**, from the machine that can reach the cluster:

```bash
python tools/capture_state.py --zone zone02 --out state-<capture-name>.json
```

Keep it next to the pcap. It records, per zone: the **zone process start time**, the parsed
`DamageByAngle` curve, and the mtime + digest of every file the damage engine reads at startup — each with
an **`inForce`** flag, which is the whole point:

> `inForce: false` means the file was written after the running process started, so the process is serving
> something else and any conclusion drawn from that file is void.

If anything comes back `NOT IN FORCE`, **restart that zone before capturing**. A capture taken over a stale
process cannot be read against the files on disk.

**Also note, in chat, inside the capture** (the operator narrates while capturing anyway, and that chat is
the legend for the packet log):

* the character's class and level — the class alone changes outgoing damage by up to 2x via
  `JobChangeDmgUp`, and the class is otherwise only recoverable from a packed byte in
  `NC_CHAR_CLIENT_SHAPE_CMD`;
* which map, and therefore which zone pod — the port varies (Uruga 9022, RouN 9016);
* what the experiment is meant to isolate, and any deliberate server-side edit made for it. **The flatten
  of `DamageByAngle.txt` an hour before `Damage.pcapng` was almost certainly deliberate — done to remove
  the angle variable from the experiment — and saying so in chat would have settled everything.**

## What a fresh capture would settle right now

All five zones currently run a **flat 1000** angle table, in force, verified — so a capture taken today has
no ambiguity. That makes it decisive either way:

* incoming damage lands **inside** the flat-table ceiling → the stock curve was live during
  `Damage.pcapng`, that capture is explained, and this port is 1:1 on both;
* incoming damage **exceeds** it by ~1.16x again → the angle table was never the explanation, there is a
  real unmodelled mechanism scaling both attackers, and `OPEN_QUESTIONS.md` §2 is where it lives.

Either answer is worth more than more analysis of the old capture.
