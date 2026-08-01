# JUNCTION-FOE-LANE — resume here (post-Entry-38; next task: F1.1)

**Read this first, cold. It is self-contained.** Branch **`claude/sumosharp-traffic-bugs-g1y9hl`**,
all work committed and pushed. Owner sign-off for this workstream's design: given ("go
autonomously"); latest owner instruction: **"then as you recommend" = do F1.1 first, then the
congestion-rerouting DESIGN DOC** (design only — owner reviews before code; see §6).

## 0. Current verified state (re-verify before believing anything else)

- **Full sln suite** (`dotnet test -c Release`, from the repo root — this IS the iron law; running
  only ParityTests is how a red hour-horizon test once shipped for five entries): ParityTests
  **781/5/0**, LiveCity.Tests **90/90**, Pedestrians **324/324**, Viewer.Motion 19, Host 6,
  DotRecast 2 — all green at head.
- **Default L2 FCD hash `5ac89389889a3e80056fce9f4c4ec158`** (city-organic-L2, 1000 steps,
  Sim.Run, no env vars). Entry 39 (A) made the ACTUAL-LANE link resolution in the junction-yield
  pass DEFAULT (`MSLane::succLinkSec` parity — the pool's sibling-lane link mis-resolution was a
  parity divergence); `e94b88b7…` (Entry 38) and `c768d7f6…` (pre-38) are obsolete baselines.
  **Gate-ON L2 hash `f7d432524bd1e96bda740cac2b0eec6a`** (Entry 40: corridor-follow jyArm 8 +
  the gate-scoped mutual on-junction tie-break; `fd636381…`/`0c9bad71…` obsolete).
- `Sim.Bench` hash **`A134ED3716DDE7BC`** (par==single; moved at Entry 34, re-pinned in
  TASKS-TODO's iron-law block — when it moves again, re-pin in the same commit).
- Everything ELSE in this workstream stays gate-scoped under **`SUMOSHARP_PHYSOCCUPANCY`**
  (Sim.Run/Sim.Sumo) / **`LIVECITY_F3OCCUPANCY`** (LiveCity/City3D viewer path — DIFFERENT VAR):
  the crossing-occupancy arm, the bay arm (jyArm 7), and F2.2's arbitration widening. LiveCity
  auto-arms `IgnoreJunctionBlockerSeconds=60` when its gate is on (`LIVECITY_IGNOREBLOCKER`
  overrides; engine default −1 = SUMO parity).
- Battery references: defaults → `docs/reports/net-regression-entry38-mergefix.txt` (current);
  gate-ON comparisons in Entry 36/37 journal entries.
- Trail: journal Entries 34–38 in `docs/JUNCTION-REALISM-SESSION-JOURNAL.md` (each has
  BEFORE-predictions and AFTER-measurements; 38 also records two attribution corrections).
  Live state: `JUNCTION-FOE-LANE-TRACKER.md`.

**Owner's Aug-1 3D re-check:** "standing without obvious reason heavily reduced; junctions
saturate but drain" (Entry 38 confirmed on terrain). Remaining complaints, mapped: (a) stopped
turners passed through + (b) queue half-stacking → **the F1.1 class, below**; (c) "cars blindly
wait in queues, not seeking alternative trips" → rerouting design item (§6).

## 1. THE NEXT TASK — F1.1 mechanism (B): corridor-follow for the late-stop race

**Where F1.1 stands (journal Entry 39 BEFORE/MID/AFTER-A — read those first).** The original
"~15 non-foes pair-steps need ingest rows" characterization was FALSIFIED by five traces; the
stopXmove class decomposed into three mechanisms:

- **(A) DONE, DEFAULT** — actual-lane link resolution (`MSLane::succLinkSec` parity): the pool's
  strategic chain resolved the SIBLING lane's connection for the whole approach whenever a planned
  lane change had not happened yet, so every yield arm consulted rows for a link the vehicle never
  drove. Fixed in `JunctionYieldConstraint` Step 1 (search `Entry 39 mechanism (A)`), ungated.
  Result: L2 gate-ON stopXmove 19 → 9, defaults 17 → 13, landings 10 → 6; every gate green.
- **(A-residual, unmeasured)**: the SYMMETRIC half — `BuildFoeApproachIndex` still registers an
  approaching mismatched vehicle on its POOL lanes, so OTHER vehicles' crossing arms can see it on
  the wrong approach row. The physical index (`_physOnLane*`) is immune. Measure before fixing.
- **(B) DONE, gate-scoped (Entry 40)** — corridor-follow (jyArm 8) + the mutual on-junction
  arm-5 tie-break its timing perturbation exposed (willpass-saturation latch; see Entry 40 AFTER
  for the full story and the JunctionIsLeaderGate default-fix flag). Below is the original spec
  kept for reference — the late-stop race (~6 of the then-remaining 9 gate-ON stopXmove):
  the correct row is consulted, but the occupant enters the shared NEAR-PARALLEL corridor at
  4–5 m/s (correctly skipped by the `Speed<=2.0` hold predicate — do NOT touch that dial:
  hold-everything measured bothSlow 16→652 GRIDLOCK in Entry 36) and decelerates through 2.0 m/s
  in exactly the step ego commits past the overlap start. Binary hold-or-commit cannot win this
  race. The honest shape: CAR-FOLLOWING along the shared corridor — map the occupant's back
  bumper through the row's arc intervals into ego's frame
  (`egoArcOfFoeBack = EgoArcStart + (foeBack − BayArcStart) · (EgoArcEnd−EgoArcStart)/(BayArcEnd−BayArcStart)`)
  and constrain ego to stop a follow-gap short of it, at ANY occupant speed — i.e.
  adaptToJunctionLeader semantics applied to bay-row occupants, replacing both the speed skip and
  the committed skip for NEAR-PARALLEL rows (a sensible parallelism test: ego and foe interval
  each covering most of both lanes, or direction dot-product — decide from the traced sites
  j=301 (7,8), j=271 (9,10) first). Gate-scoped under `JunctionPhysicalOccupancyGate`. Risks to
  measure: follower chains through junctions (bothSlow), and mutual-follow cycles (the entry-order
  backstop must stay upstream of the follow constraint). Journal Entry 40 BEFORE with predictions
  FIRST: expected L2 gate-ON stopXmove 9 → ≤4, bothSlow not exploding past ~15, DRAINED
  everywhere, smoke 400 arrivals ≥ 800.
- **(C) NAMED RESIDUAL** — straddling-tail corner-cut (~2 pair-steps, j=1021 t=184 witness): the
  stopped foe's tail hangs BEHIND its lane start into the approach mouth ego's turn sweeps;
  centerlines never within 3.2 m, so no lane-pair corridor row can honestly represent it. Needs
  foe-approach-mouth geometry if ever fixed; do not force it into corridor rows.

**Acceptance gates for any (B) change (run ALL — each has caught a real wedge):**
- Classifier + analyzer, L2 AND mixed-1k, BOTH gate states (`--examples 40`).
- Battery (defaults AND gate-ON arms) vs `net-regression-entry38-mergefix.txt` + the Entry-39A
  numbers in the journal: stuckDwell 0, arrivals in noise.
- Live-city smoke at `LIVECITY_CARS=400`, gate ON: drained, arrivals ≈ 820+, INTERNALSTUCK
  transient only.
- Full `dotnet test -c Release` (goldens byte-identical is the stop-ship check).
- Gate-off L2 hash still `5ac89389…` byte-identical (a gate-scoped (B) must not move defaults);
  determinism ≥3 runs + `--max-parallelism 1` (via Sim.Sumo) both states.
- Ingest pin tests (`JunctionBayConflictIngestTests`, 3 tests) stay green.

## 2. The instrument loop (exact commands)

```bash
# Sim.Run/Sim.Sumo/Sim.Viewer are NOT in Traffic.sln -- build csproj explicitly:
dotnet build -c Release src/Sim.Core/Sim.Core.csproj && dotnet build -c Release src/Sim.Run/Sim.Run.csproj
dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /tmp/off.fcd.xml          # defaults
SUMOSHARP_PHYSOCCUPANCY=1 dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /tmp/on.fcd.xml           # gate ON
python3 scripts/classify-junction-overlaps.py /tmp/on.fcd.xml --examples 40
python3 scripts/analyze-junction-realism-fcd.py /tmp/on.fcd.xml
# per-vehicle episode trace (binder log names the arm; [bay]/[merge] name the row/phase):
SUMOSHARP_PHYSOCCUPANCY=1 dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /dev/null --binder-log /tmp/b.csv
SUMOSHARP_PHYSOCCUPANCY=1 SUMOSHARP_TRACEVEH=<id> dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /dev/null 2>/tmp/trace.txt
# battery (defaults or with the gate var):
python3 scripts/run-net-regression.py --exclude city-15000 --out /tmp/bat.txt \
  --compare docs/reports/net-regression-entry38-mergefix.txt
# live-city density smoke + wedge chains (LIVECITY_TRACEVEH for [merge]/[bay] in this host):
dotnet build -c Release src/Sim.Viewer/Sim.Viewer.csproj
LIVECITY_CARS=400 LIVECITY_WITNESS=1 LIVECITY_F3OCCUPANCY=1 dotnet run --project src/Sim.Viewer \
  -c Release --no-build -- --mode live-city --smoke --frames 1200 | grep -E "GRIDLOCK|INTERNALSTUCK|CHAIN"
```

## 3. Where the code is (search anchors)

- **Bay/occupancy arm**: `Engine.cs` search `F2.1b` / `Entry 36` / `Entry 37` — jyArm 7 inside
  `JunctionYieldConstraint`: entry-order backstop, back-bumper exiting, patience skip, `[bay]` trace.
- **Ingest pass**: `NetworkParser.cs` search `F2.1b` / `Entry 36` — stage rows, bay-piece rows
  (negative = stage-2-relative ego arcs), `minEgoOverlapLen`. Geometry: `PolylineGeometry.TryCorridorOverlap`.
- **Merge arm (now partly DEFAULT)**: search `Entry 38` — ungated `IsLeaderByEntryOrder` PHASE-1
  tie-break + ungated `foeWith` reachability (`arbitration: respondsTo` stays). Do not re-gate.
- **Pins**: `JunctionBayConflictIngestTests` (both traced witness sites), floor guards with
  Entry-38 accounting in `DenseFlowDeadLaneDrainTests` (286) and `IgnoreJunctionBlockerTests`.
- **Diag surfaces**: binder log (`--binder-log` on Sim.Run; names arm + blocker), jyArm codes
  1 cycleHold 2 cautiousApproach 3 sameTargetMerge 4 externalAgent 5 adaptToJxnLeader
  6 approachingCross 7 bayOccupancy; LiveCity witness `LIVECITY-INTERNALSTUCK` histogram +
  `LIVECITY-CHAIN` per-head blocker chains (`BlockerEntityIndexes` read surface).

## 4. Named residual classes (do NOT re-trace)

- **Lane-sequence link mismatch — FIXED at defaults (Entry 39 A)**: it was not a ~2-pair-step
  tail but the largest stopXmove mechanism (~7/19). The remaining unmeasured piece is the
  A-residual in §1 (foe-approach index registration).
- **Late-stop race** (j=301 (7,8), j=271 (9,10) witnesses): occupant decelerates through the
  2.0 m/s hold dial in the step ego commits — this is §1's OPEN mechanism (B), not unfixable
  physics; the unstoppable version (foe stops AFTER ego is fully committed at speed) is only the
  tail of it.
- **Dead-lane stranding** (`successiveLane`/`deadLaneMerge` binders): cars on a lane with no
  connection to their next route edge — the class behind BOTH Entry-38 floor re-anchors. Distinct
  problem, partially mitigated in LiveCity by its reroute machinery; untouched in the bare engine.
- **jy5(inTheWay)×jy7 cross-arm pairs cannot be tie-broken** — arm 5 is SUMO-faithful. The answer
  is honest GEOMETRY (brush filter) + bounded PATIENCE, never a new dial.

## 5. Traps (each cost real time)

- Env gates are process-global; set every one explicitly in BOTH A/B arms; `env | grep -E "SUMOSHARP|LIVECITY"` first.
- The gate var differs by host: `SUMOSHARP_PHYSOCCUPANCY` (Sim.Run/Sim.Sumo) vs
  `LIVECITY_F3OCCUPANCY` (LiveCity/City3D). Telling the owner the wrong one cost a full test round.
- "L2 green" ≠ "battery green" ≠ "density green" ≠ "hour-horizon green": the brush wedge was
  battery-only; the Entry-37 collapse was density-only; the merge deadlock was hour-horizon-only.
  Run all four for any arm/ingest change.
- Scratch FCDs die with the VM; the engine is deterministic — regenerate, never trust stale files.
- Reasoned mechanism hypotheses are ~0-for-20+ here; three Entry-36..38 "obvious" attributions were
  each falsified by a trace or a bisect. Trace first, and independently verify ANY "pre-existing"
  claim against `origin/main` before writing it down.
- Owner interaction: plain chat text, no question widgets; design-first for anything new.

## 6. After F1.1 — the queue, in order

1. **Congestion-aware rerouting DESIGN DOC** (owner-requested Aug 1: "cars blindly wait in queues
   as if not looking for alternative trip"). DESIGN ONLY, then owner review. Shape: SUMO's
   rerouting device (`--device.rerouting.*` — periodic per-vehicle shortest-path on time-averaged
   edge travel times); LiveCity already has `SetDestination`/`RegisterRerouted` + the wrong-lane
   reroute path, so the new piece is the periodic congestion-weighted trigger. Constraints:
   deterministic (per-entity seeded period offsets, no `System.Random`), off-by-default +
   goldens-inert, three docs (design / tasks with success conditions / tracker) per CLAUDE.md.
   Backlog entry: `docs/JUNCTION-REALISM-RESUME.md` §5 item 0-NEW.
2. Remaining F3.1 ladder: live-city demo smoke under the gate; owner Geneva re-check; then the
   F3.2 default-flip decision WITH the owner.
3. Parked: F0.1 TraCI probe; junction-realism-L1 −7 arrivals labelling; ped-on-RED (Entry 33);
   Entry-22 resetState accumulator revisit.
