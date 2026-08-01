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
- **Default L2 FCD hash `e94b88b7534c21b5fd3bf8657dbb1666`** (city-organic-L2, 1000 steps,
  Sim.Run, no env vars). Entry 38 made the merge-arm entry-order tie-break + foes-based merge
  reachability DEFAULT (they fix a latent mutual PHASE-1 merge deadlock SUMO never had);
  `c768d7f6…` is the obsolete pre-38 baseline. **Gate-ON L2 hash `0c9bad719a22ba1a56615ab246316a3c`.**
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

## 1. THE NEXT TASK — F1.1: conflict geometry for non-foes internal-lane pairs

**The class (measured, both gate states, this is NOT gate regression):** a STOPPED vehicle on a
plain (non-bay) internal lane is driven through by movers whose links netconvert never put in each
other's foes rows. ~15 of L2's 18 gate-ON `crossLane|stopXmove` pair-steps; recurring sites:

- j=1150 `:1150_2_0` × `:1150_0_1` (4 episodes), j=123 `:123_11_0` × `:123_9_1` (4),
  j=1021 `:1021_15_0` × `:1021_12_0`, j=428 `:428_7_0` × `:428_5_1` and `:428_13_1` × `:428_15_0`,
  j=301 `:301_6_1` × `:301_8_0` and `:301_6_0` × `:301_13_0`, j=271, j=717, j=12, j=23, j=993.
- Honest SUMO on the same net: 0. SUMO 1.20 also drives through these (foes-blind) — fixing it is
  the SAME sanctioned beyond-SUMO honesty deviation as the bay work
  (`docs/CONSTRAINT-high-realism-artefact-ladder.md` binding: target SUMO's flow, not its method).

**The design shape (recommended): generalize the Entry-36 bay machinery, don't invent new.**
Every piece F1.1 needs already exists and is measured:

1. **Ingest**: extend the `BayConflict` pass (`NetworkParser.cs`, search `F2.1b`/`Entry 36`) to
   emit rows for EVERY ordered internal-lane pair of a junction where `!foes(i,j)` — not just cont
   bays. Same `TryCorridorOverlap` (proximity-sampled, 2.0 m centerline threshold), same
   **`minEgoOverlapLen=1.0` brush filter** (NON-NEGOTIABLE — a 0.27 m brush row wedged
   city-organic at junction 359; proximity ≠ contact, 2.0 m > the 1.8 m body-touch distance).
   Watch ingest cost: pairs are O(links²) per junction — fine at these sizes, but measure parse
   time on city-15000 once.
2. **Engine**: the bay arm (`Engine.cs`, `F2.1b` in `JunctionYieldConstraint`, jyArm 7) already
   consumes rows via `_physOnLaneFirst/Second` (PHYSICAL occupancy — never `FindFoeVehicle` for
   physical questions, it first-masks), with the entry-order backstop (earlier entrant inside the
   junction skips), the back-bumper exiting test, the `Speed<=2.0` hold predicate (three dial
   points measured — do NOT tune), and the `IgnoreJunctionBlockerSeconds` patience escape. If the
   generalized rows keep the same record shape, the arm may need ZERO changes — decide whether to
   rename `BayConflict` → something like `PhantomConflict` (doc-comment honesty) or keep the name.
3. **Gate-scoped as before** under `JunctionPhysicalOccupancyGate` — F1.1 completes the F3.1
   ladder; the default flip (F3.2) is a separate later decision with the owner.
4. **Journal Entry 39 BEFORE with predictions FIRST** (the rules): expected L2 gate-ON stopXmove
   18 → ≤5 (the ~15 non-foes class should collapse; the 2 lane-sequence-mismatch clips and the
   1 unstoppable transient remain — §5 named residuals), landings unchanged, bothSlow not
   exploding, DRAINED everywhere.

**Acceptance gates for F1.1 (run ALL — each one has caught a real wedge this workstream):**
- Classifier + analyzer, L2 AND mixed-1k, BOTH gate states (`--examples 40`).
- Battery gate ON vs `net-regression-entry38-mergefix.txt`: stuckDwell 0, arrivals in noise.
- Live-city smoke at `LIVECITY_CARS=400`, gate ON (the ONLY surface that saw the Entry-37
  density collapse): drained, arrivals ≈ 830+, INTERNALSTUCK transient only.
- Long-horizon: full `dotnet test -c Release` (LiveCity's hour-horizon test is in the sln).
- Gate-off L2 hash still `e94b88b7…` byte-identical; determinism ≥3 runs + `--max-parallelism 1`
  (via Sim.Sumo) both states.
- Ingest pin test extended (like `JunctionBayConflictIngestTests`): at least one named non-foes
  site row present with sane arcs + one brush-dropped pair asserted absent.

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

- **Lane-sequence link mismatch** (L2 t=543/544 site): a mover with a pending strategic lane
  change resolves its upcoming link through the other lane's connection — all yield arms watch the
  wrong rows on approach. Pre-existing, out of F1.1 scope; survives F1.1 (~2 pair-steps).
- **Late-stop transient** (L2 t=468): occupant stops AFTER the mover commits at speed — no hold
  timing can stop physics; survives F1.1 (~1 pair-step).
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
