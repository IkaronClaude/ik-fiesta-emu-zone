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
the flat table or the stock curve. Nobody wrote it down and it is not recoverable now; reading the file
today answers a question about today.

That one is survivable only because the operator happened to narrate `"Forward-facing only now"` in chat,
which settles the angle for that window independently of what was loaded (see below). Without that line
the capture would have been undecidable — a 219/219 and a 136/219 reading of the same pcap, with no way to
choose. **The chat saved it; do not rely on being that lucky twice.**

## The protocol

**Before or immediately after capturing**, from the machine that can reach the cluster:

```bash
python tools/capture_state.py --zone zone02 --character <CharName> --out state-<capture-name>.json
```

Keep it next to the pcap. It records, per zone: the **zone process start time**, the parsed
`DamageByAngle` curve, and the mtime + digest of every file the damage engine reads at startup — each with
an **`inForce`** flag, which is the whole point:

> `inForce: false` means the file was written after the running process started, so the process is serving
> something else and any conclusion drawn from that file is void.

If anything comes back `NOT IN FORCE`, **restart that zone before capturing**. A capture taken over a stale
process cannot be read against the files on disk.

`--character` adds the character's **server-side parameter state**, read out of `World00_Character`: class
id, level, the free-stat allocation, and every worn item with its options (option type 600/700 is the
upgrade level). That is the set of INPUTS `CharacterParameters.Build` needs, so a capture can be checked
against a container **constructed** the way the server constructs one instead of one inverted out of
displayed accessor outputs — inverting was wrong for a month and hid a whole missing multiplier.

It is the persisted state, not the live container: buffs, abnormal states and charged effects are not in
it. And it is the state **now** — today's dump of the `Damage.pcapng` character shows the weapon carrying
no upgrade option at all, where the July analysis assumed a +10 worth +1197. The gear changed in between.
That is the argument for running this beside the capture, in one sentence.

**Also note, in chat, inside the capture** (the operator narrates while capturing anyway, and that chat is
the legend for the packet log):

* the character's class and level — the class alone changes outgoing damage by up to 2x via
  `JobChangeDmgUp`, and the class is otherwise only recoverable from a packed byte in
  `NC_CHAR_CLIENT_SHAPE_CMD`;
* which map, and therefore which zone pod — the port varies (Uruga 9022, RouN 9016);
* what the experiment is meant to isolate, and any deliberate server-side edit made for it. **The flatten
  of `DamageByAngle.txt` an hour before `Damage.pcapng` was almost certainly deliberate — done to remove
  the angle variable from the experiment — and saying so in chat would have settled everything.**

## Read the chat before analysing anything

The operator's running commentary is not colour, it is the **legend**. On `Damage.pcapng` a single line —
`"Forward-facing only now"`, inside the analysed window — eliminated a hypothesis that three sessions had
been circling, because a frontal engagement indexes `DamageByAngle` at 0 and index 0 is 1000 in every
version of the table. Two more lines (`"seems like END has no clean flat effect here"`,
`"Unequipping some more (no end change this time)"`) are an independent observation of the residual that
is still open.

Decode the chat FIRST, in order, and map packets to it:

```bash
cd C:/Projects/fiesta-proxy/tools
XOR_TABLE_PATH=C:/Projects/ik-fiesta-bots/xor-table.hex python pcap_decode.py <cap>.pcapng --opcode 0x2001
```

Read the text out of the hex dump's ASCII column, not a struct field, and join the `|...|` columns of
consecutive rows within one frame.

## What a fresh capture would settle right now

All five zones currently run a **flat 1000** angle table, in force, verified — so a capture taken today has
no ambiguity to begin with.

The angle question is already answered — `"Forward-facing only now"` settled it — so what a fresh capture
buys is a clean measurement of the ~1.16x that is left: one where the class, level, free stats and gear are
recorded rather than inferred, the angle is known rather than argued, and nothing has changed between the
capture and the analysis. `OPEN_QUESTIONS.md` §2 is where that residual lives.

Worth including in the run: some swings **from behind** as well as forward-facing ones, on a server whose
angle table is known flat. If rear hits and frontal hits do the same damage, the table's absence is
confirmed on the wire rather than from a file, and the last thread on that question is closed for good.
