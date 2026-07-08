# Instructions: capture the SUMO 1.20.0 `getLeaderInfo` merge trace (C4-iv sameTarget-merge)

You are running in your **existing `pjanec/sumo` debug clone** (the one that already built the
DEBUG binary for the C3 minor-link trace, branch `debug/c3-minor-link-trace`). This reuses that
build with **one extra debug define enabled** and captures two short traces.

The Traffic project is porting the **sameTarget-merge yield** (a vehicle entering a merge must
follow-yield to a vehicle already traversing the *other* merging lane). The mechanism is
`MSLink::getLeaderInfo`'s `sameTarget` branch feeding `MSVehicle::adaptToJunctionLeader`. Two exact
runtime values defeat static reading and need the instrumented print:

1. the **`lengthBehindCrossing` / `foeLengthBehindCrossing`** for the merge conflict, and
2. the **`leaderBack` / gap** at the step the foe *first* enters the merge lane (its back still on
   the previous edge).

Those are printed by SUMO's `DEBUG_PLAN_MOVE_LEADERINFO` block, which is **not** currently enabled
(the C3 build only turned on `DEBUG_PLAN_MOVE`).

**Do not push to `main`. Do not open a PR.** Commit the logs to a branch and report back.

---

## Step 0 — Start from the existing debug branch

```bash
cd <your pjanec/sumo checkout>
git checkout debug/c3-minor-link-trace     # the branch that already has the DEBUG build edits
git switch -c debug/c4iv-merge-trace        # new branch for this capture
git describe --tags   # should still resolve to v1_20_0 provenance (same base as C3)
```

(If you prefer, stay on `debug/c3-minor-link-trace` and just commit there — either is fine, as long
as the artifacts land on a branch you can share.)

---

## Step 1 — Enable the leader-info debug prints, gate to the merging vehicles

Edit **`src/microsim/MSVehicle.cpp`**. Three small changes:

### 1a. Turn ON `DEBUG_PLAN_MOVE_LEADERINFO` (near line 91)

It is currently commented. Find:

```cpp
//#define DEBUG_PLAN_MOVE_LEADERINFO
```

and remove the `//`:

```cpp
#define DEBUG_PLAN_MOVE_LEADERINFO
```

### 1b. Keep `DEBUG_PLAN_MOVE` ON (near line 90)

From the C3 build this should already read (leave it as-is):

```cpp
#define DEBUG_PLAN_MOVE
```

### 1c. Gate `DEBUG_COND` to BOTH merging vehicles (near line 108)

From the C3 build this currently reads `#define DEBUG_COND (getID() == "rA")`. Change it to cover
both scenarios' merging vehicles in one build:

```cpp
#define DEBUG_COND (getID() == "rA" || getID() == "vB")
```

> Why these two: `rA` is the ramp vehicle in the ASYMMETRIC merge (scenario A below) and `vB` is the
> minor vehicle in the SYMMETRIC merge (scenario B). Tracing both in one build lets us resolve the
> conflict-geometry residual (A) and the leaderBack residual (B) together.

---

## Step 2 — Rebuild the `sumo` binary

Same build as before (Debug). From the repo root:

```bash
cmake --build build -j"$(nproc)" --target sumo
```

(If your Debug binary lands at the source-tree `bin/sumoD` as the C3 README noted, use that path
below instead of `build/bin/sumo`.)

---

## Step 3 — Scenario A (ASYMMETRIC merge): reuse the C3 net, new route file

This reuses the **existing `scenario19/net.net.xml`** you already have (the on-ramp merge net). Only
the route file changes (a SLOW mainline vehicle that crawls across the merge as the ramp vehicle
arrives).

```bash
mkdir -p mergeA
cp scenario19/net.net.xml mergeA/net.net.xml     # reuse the committed C3 net
cd mergeA
```

### `mergeA/rou.rou.xml`

```xml
<?xml version="1.0" encoding="UTF-8"?>
<routes xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/routes_file.xsd">
    <vType id="car"  vClass="passenger" sigma="0"/>
    <vType id="slow" vClass="passenger" sigma="0" maxSpeed="6"/>
    <route id="main" edges="M D"/>
    <route id="ramp" edges="R D"/>
    <vehicle id="mA" type="slow" route="main" depart="0" departPos="430" departSpeed="6"     departLane="0"/>
    <vehicle id="rA" type="car"  route="ramp" depart="3" departPos="0"   departSpeed="13.89" departLane="0"/>
</routes>
```

### `mergeA/config.sumocfg`

```xml
<?xml version="1.0" encoding="UTF-8"?>
<configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/sumoConfiguration.xsd">
    <input>
        <net-file value="net.net.xml"/>
        <route-files value="rou.rou.xml"/>
    </input>
    <time><begin value="0"/><end value="90"/><step-length value="1"/></time>
    <processing>
        <step-method.ballistic value="false"/>
        <time-to-teleport value="-1"/>
        <default.action-step-length value="1"/>
        <default.speeddev value="0"/>
        <collision.action value="none"/>
        <lanechange.duration value="0"/>
    </processing>
    <random_number><seed value="42"/></random_number>
</configuration>
```

### Run and capture (merge stdout+stderr):

```bash
../build/bin/sumo -c config.sumocfg --fcd-output fcd.xml --precision 6 2>&1 | tee mergeA-leaderinfo.log
cd ..
```

---

## Step 4 — Scenario B (SYMMETRIC merge): new net + route + config

Create a fresh dir and write these **three files exactly** (a symmetric Y-merge whose two internal
lanes are mirror images — this isolates the `leaderBack` residual from the conflict-geometry one).

```bash
mkdir -p mergeB && cd mergeB
```

### `mergeB/net.net.xml`

```xml
<?xml version="1.0" encoding="UTF-8"?>

<!-- symmetric Y-merge (netconvert v1.20.0) -->

<net version="1.20" limitTurnSpeed="5.50" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/net_file.xsd">

    <location netOffset="0.00,30.00" convBoundary="0.00,0.00,600.00,60.00" origBoundary="0.00,-30.00,600.00,30.00" projParameter="!"/>

    <edge id=":J_0" function="internal">
        <lane id=":J_0_0" index="0" speed="13.89" length="19.16" shape="282.59,26.65 286.96,27.13 292.11,27.71 297.27,28.20 301.66,28.40"/>
    </edge>
    <edge id=":J_1" function="internal">
        <lane id=":J_1_0" index="0" speed="13.89" length="19.16" shape="282.59,30.13 286.96,29.65 292.11,29.08 297.27,28.60 301.66,28.40"/>
    </edge>

    <edge id="AJ" from="A" to="J" priority="10">
        <lane id="AJ_0" index="0" speed="13.89" length="284.16" shape="-0.16,58.41 282.59,30.13"/>
    </edge>
    <edge id="BJ" from="B" to="J" priority="1">
        <lane id="BJ_0" index="0" speed="13.89" length="283.84" shape="0.16,-1.59 282.59,26.65"/>
    </edge>
    <edge id="JC" from="J" to="C" priority="10">
        <lane id="JC_0" index="0" speed="13.89" length="298.34" shape="301.66,28.40 600.00,28.40"/>
    </edge>

    <junction id="A" type="dead_end" x="0.00" y="60.00" incLanes="" intLanes="" shape="0.00,60.00 -0.32,56.82"/>
    <junction id="B" type="dead_end" x="0.00" y="0.00" incLanes="" intLanes="" shape="0.00,0.00 0.32,-3.18"/>
    <junction id="C" type="dead_end" x="600.00" y="30.00" incLanes="JC_0" intLanes="" shape="600.00,26.80 600.00,30.00"/>
    <junction id="J" type="priority" x="300.00" y="30.00" incLanes="BJ_0 AJ_0" intLanes=":J_0_0 :J_1_0" shape="301.66,30.00 301.66,26.80 282.75,25.06 282.43,28.24 282.43,28.54 282.75,31.73">
        <request index="0" response="10" foes="10" cont="0"/>
        <request index="1" response="00" foes="01" cont="0"/>
    </junction>

    <connection from="AJ" to="JC" fromLane="0" toLane="0" via=":J_1_0" dir="s" state="M"/>
    <connection from="BJ" to="JC" fromLane="0" toLane="0" via=":J_0_0" dir="s" state="m"/>

    <connection from=":J_0" to="JC" fromLane="0" toLane="0" dir="s" state="M"/>
    <connection from=":J_1" to="JC" fromLane="0" toLane="0" dir="s" state="M"/>

</net>
```

### `mergeB/rou.rou.xml`

```xml
<?xml version="1.0" encoding="UTF-8"?>
<routes xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/routes_file.xsd">
    <vType id="car"  vClass="passenger" sigma="0"/>
    <vType id="slow" vClass="passenger" sigma="0" maxSpeed="6"/>
    <route id="major" edges="AJ JC"/>
    <route id="minor" edges="BJ JC"/>
    <vehicle id="mA" type="slow" route="major" depart="0" departPos="255" departSpeed="6"     departLane="0"/>
    <vehicle id="vB" type="car"  route="minor" depart="2" departPos="220" departSpeed="13.89" departLane="0"/>
</routes>
```

### `mergeB/config.sumocfg`

```xml
<?xml version="1.0" encoding="UTF-8"?>
<configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/sumoConfiguration.xsd">
    <input><net-file value="net.net.xml"/><route-files value="rou.rou.xml"/></input>
    <time><begin value="0"/><end value="60"/><step-length value="1"/></time>
    <processing>
        <step-method.ballistic value="false"/><time-to-teleport value="-1"/>
        <default.action-step-length value="1"/><default.speeddev value="0"/>
        <collision.action value="none"/><lanechange.duration value="0"/>
    </processing>
    <random_number><seed value="42"/></random_number>
</configuration>
```

### Run and capture:

```bash
../build/bin/sumo -c config.sumocfg --fcd-output fcd.xml --precision 6 2>&1 | tee mergeB-leaderinfo.log
cd ..
```

---

## Step 5 — Verify the trace has the lines I need, then commit

The logs must contain `getLeaderInfo` candidate-leader lines for the merging vehicle. Grep to
confirm before committing:

```bash
grep -nE "getLeaderInfo|distToCrossing|candidate leader|sT=1|lbc=|flbc=|leaderBack|backDist|gap=" mergeA/mergeA-leaderinfo.log | head -60
grep -nE "getLeaderInfo|distToCrossing|candidate leader|sT=1|lbc=|flbc=|leaderBack|backDist|gap=" mergeB/mergeB-leaderinfo.log | head -60
```

The exact fields I need (they appear on the `getLeaderInfo` lines, per step, for `rA`/`vB` as they
approach and cross the merge — around **t=8–14** in mergeA and **t=5–12** in mergeB):

- `sT=` (sameTarget flag — should be 1 for the merge foe), `lbc=` (ego lengthBehindCrossing),
  `flbc=` (foe lengthBehindCrossing), `cw=`/`fcw=` (crossing widths).
- `candidate leader=<mA> fdtc=<foeDistToCrossing> lb=<leaderBack> lbd=<leaderBackDist>`.
- `distToCrossing=... leaderBack=... backDist=... backDist2=...`.
- the final `leader=<mA> ... gap=<GAP> ... stopAsap=... gap=...`.
- and the `adaptToJunctionLeader` line: `veh=<rA/vB> lead=<mA> leadSpeed=... gap=... seen=...`.

If those `sT=1` / `candidate leader` lines are **absent**, the gate didn't take — recheck Step 1
(both `#define DEBUG_PLAN_MOVE_LEADERINFO` uncommented AND `DEBUG_COND` covering `rA`/`vB`),
rebuild, rerun. Do not commit an empty log.

Then commit both logs (and the two scenario dirs) to the branch:

```bash
git add src/microsim/MSVehicle.cpp mergeA/ mergeB/
git commit -m "debug: enable DEBUG_PLAN_MOVE_LEADERINFO, capture sameTarget-merge getLeaderInfo traces (rA, vB)"
git push -u origin debug/c4iv-merge-trace
```

---

## Deliverable — report back

1. the branch + commit SHA;
2. the two committed log paths (`mergeA/mergeA-leaderinfo.log`, `mergeB/mergeB-leaderinfo.log`);
3. a quick inline paste of the first few `candidate leader ... lb= ... lbd= ...` + final
   `leader= ... gap=` lines for `rA` (mergeA) and for `vB` (mergeB), so I can confirm the numbers
   before pulling.

Those traces give me the exact per-step `lengthBehindCrossing`, `leaderBack`, and merge `gap`, which
resolve the two residuals and let me finish the C4-iv port to the 1e-3 parity bar (which then also
unblocks the roundabout, C4-iii).
