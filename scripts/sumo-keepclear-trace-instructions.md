# Instructions: capture the SUMO 1.20.0 `checkRewindLinkLanes` keepClear trace (C5)

You are running in your **existing `pjanec/sumo` debug clone** (the one that built the DEBUG binary
for the C3 / C4-iv / arrival-time traces). This reuses that build with **one debug define enabled**
and captures **one short trace**.

## Why

The Traffic project is porting **keepClear / don't-block-the-box** (C5): a vehicle must not enter a
junction it cannot clear, so when its exit lane is jammed it stops at the junction ENTRY rather than
creeping onto the internal lane and blocking cross traffic. The mechanism is
`MSVehicle::checkRewindLinkLanes` (`src/microsim/MSVehicle.cpp:5025`, ~235 lines): it walks the
vehicle's planned link chain (`myLFLinkLanes`), accumulating downstream available space
(`getSpaceTillLastStanding` / `getBruttoVehLenSum`) until a stopped vehicle is found, then if
`availableSpace - lengthWithGap < 0` at a keepClear link it truncates the plan (`removalBegin`) and
sets `myVLinkPass = myVLinkWait` so the vehicle brakes to the stop line.

The engine has NONE of this downstream-space accounting. The exact per-link `seenSpace` /
`availableSpace` / `leftSpace` / `removalBegin` values (which depend on `lengthsInFront`, the
internal-lane brutto sums, and `getSpaceTillLastStanding`'s stopped-vehicle math) are runtime
details that defeat static reading — hence this trace. The stop POSITION is already understood
(`WJ.len 92.80 - DIST_TO_STOPLINE_EXPECT_PRIORITY 1.0 = 91.8`, confirmed in the golden); what the
trace pins is the AVAILABLE-SPACE ACCOUNTING that decides *when* keepClear fires and at *which* link.

The scenario is the committed `scenarios/34-keepclear` (a 4-way priority cross): `mBlock` sits
stopped on exit edge JE, `mThrough` (W->E) must keepClear-stop at the J entry, `nCross` (N->S minor)
then crosses. The trace vehicle is `mThrough`.

**Do not push to `main`. Do not open a PR.** Commit the log to a branch and report back.

---

## Step 0 — Start from the existing debug branch

```bash
cd <your pjanec/sumo checkout>
git checkout debug/arrivaltime-row-trace     # or any branch with the DEBUG build
git switch -c debug/keepclear-trace
```

---

## Step 1 — Enable the checkRewindLinkLanes prints, gate to `mThrough`

Edit **`src/microsim/MSVehicle.cpp`**. Two changes:

### 1a. Turn ON `DEBUG_CHECKREWINDLINKLANES` (near line 92)

Find `//#define DEBUG_CHECKREWINDLINKLANES` and remove the `//`:

```cpp
#define DEBUG_CHECKREWINDLINKLANES
```

### 1b. Gate `DEBUG_COND` to `mThrough` (near line 108)

```cpp
#define DEBUG_COND (getID() == "mThrough")
```

(Leave `DEBUG_PLAN_MOVE` as-is; the checkRewindLinkLanes block has its own `#ifdef`. If you also want
the per-link plan-move context, uncomment `#define DEBUG_PLAN_MOVE` near line 90 too — helpful but
optional.)

---

## Step 2 — Rebuild

```bash
cmake --build build -j"$(nproc)" --target sumo
```

(Use whatever Debug binary path your prior builds produced — `build/bin/sumo` or `bin/sumoD`.)

---

## Step 3 — Scenario data (self-contained; also in the attached zip)

Unzip the attached `keepclear-trace.zip` into a fresh dir (it is the exact committed
`scenarios/34-keepclear` inputs), or write the files by hand. `RUN.sh` builds the net and runs:

```
keepclear/
  nodes.nod.xml  edges.edg.xml  connections.con.xml
  config.sumocfg  rou.rou.xml  RUN.sh
```

`RUN.sh` (edit `SUMO=`/`NETCONVERT=` to your Debug binaries if needed):

```bash
#!/usr/bin/env bash
set -euo pipefail
SUMO=${SUMO:-../build/bin/sumo}
NETCONVERT=${NETCONVERT:-../build/bin/netconvert}
"$NETCONVERT" --node-files nodes.nod.xml --edge-files edges.edg.xml \
    --connection-files connections.con.xml --output-file net.net.xml --no-turnarounds
"$SUMO" -c config.sumocfg --fcd-output fcd.xml --precision 6 2>&1 | tee keepclear.log
echo "=== grep the accounting lines ==="
grep -nE "CHECK_REWIND|approached=|avail=|seenSpace=|leftSpace=|removalBegin=|foundStopped|allowsContinuation" keepclear.log | head -80
```

Run it:

```bash
cd keepclear && bash RUN.sh
```

---

## Step 4 — Verify the trace has what I need, then commit

The decisive window is roughly **t=9..13** (as `mThrough` closes on the J entry at WJ pos ~81->91.8
and mBlock is stopped on JE). The log must contain, per step, the `CHECK_REWIND_LINKLANES` block for
`mThrough`:

- `veh=mThrough lengthsInFront=<...>` (the seed for `seenSpace`);
- per planned link: `link=<id> ... approached=<lane> approachedBrutto=<getBruttoVehLenSum>
  avail=<availableSpace> seenSpace=<...> foundStopped=<0/1>` and, on the exit lane,
  `last=mBlock ... stls=<getSpaceTillLastStanding> avail=<...> foundStopped2=<...>`;
- the `link=... canLeave=... opened=... allowsContinuation=...` back-propagation lines;
- the `veh=mThrough link=<id> avail=<...> leftSpace=<...>` lines and the final
  `removalBegin=<i> brakeGap=<...> dist=<...>` line.

Grep to confirm before committing:

```bash
grep -nE "CHECK_REWIND|lengthsInFront=|approachedBrutto=|stls=|avail=|leftSpace=|removalBegin=" \
  keepclear/keepclear.log | head -80
```

If the `CHECK_REWIND_LINKLANES` block is **absent**, the gate didn't take — recheck Step 1
(`#define DEBUG_CHECKREWINDLINKLANES` uncommented AND `DEBUG_COND` = `getID() == "mThrough"`),
rebuild, rerun. Do not commit an empty log.

Then commit:

```bash
git add src/microsim/MSVehicle.cpp keepclear/
git commit -m "debug: enable DEBUG_CHECKREWINDLINKLANES, capture keepClear trace (mThrough)"
git push -u origin debug/keepclear-trace
```

---

## Deliverable — report back

1. the branch + commit SHA;
2. the committed log path (`keepclear/keepclear.log`);
3. an inline paste of the `CHECK_REWIND_LINKLANES` block for `mThrough` across **t=9..13** — the
   `lengthsInFront`, per-link `approachedBrutto` / `stls` / `avail` / `seenSpace` / `foundStopped`,
   and the final `leftSpace` / `removalBegin` lines.

Those give me the exact available-space accounting and the `removalBegin` decision, which let me port
`checkRewindLinkLanes` (plus the `getSpaceTillLastStanding` / `getBruttoVehLenSum` lane-occupancy
queries) to the 1e-3 parity bar and land `scenarios/34-keepclear`.
