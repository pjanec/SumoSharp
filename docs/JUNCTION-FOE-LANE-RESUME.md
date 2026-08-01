# JUNCTION-FOE-LANE — resume here (the Geneva overlap/pass-through fix)

**Read this first, cold. It is self-contained.** Branch **`claude/sumosharp-traffic-bugs-g1y9hl`**.
Suite: `dotnet test tests/Sim.ParityTests -c Release` = **781 / 5 / 0**. Everything in this
workstream is gate-scoped under **`SUMOSHARP_PHYSOCCUPANCY`** (`Engine.JunctionPhysicalOccupancyGate`,
default **OFF**); gate-off is **byte-identical** to the pre-workstream engine (city-organic-L2 FCD
hash `c768d7f6dd8535f46f170956737a2921`, re-verify after any edit; gate-ON L2 hash for the current
code: `0c9bad719a22ba1a56615ab246316a3c`). Trail: journal **Entries 35, 35b, 36** (36 has the two
episode traces and the full scorecard), design `JUNCTION-FOE-LANE-DESIGN.md`, live state
`JUNCTION-FOE-LANE-TRACKER.md`. Owner sign-off: given ("go autonomously").

## 1. Where this stands (post-Entry-36)

Both Geneva reports have working gate-scoped fixes, measured on both surfaces:

- **(a) queue-tail stacking** (same-junction double-landings): F2.2 — foes-based merge reachability
  + `IsLeaderByEntryOrder` PHASE-1 tie-break. L2 landings 12 → 6, mixed-1k 10 → 5.
- **(b) pass-through of a blocked turner** — the BAY half is DONE (Entry 36): bay-piece ingest rows
  (ego's first-stage bay vs foe bays, ego arcs stage-2-relative/negative), entry-order backstop in
  the jyArm-7 bay arm, back-bumper exiting test, and the `minEgoOverlapLen=1.0` brush filter.
  The Entry-35b "hold-timing trade-off" **dissolved** — it was never a timing dial, it was missing
  geometry (the wedge) plus over-eager geometry (the brush): L2 gate ON went GRIDLOCK/41-stuck/
  dwell-634 → DRAINED/dwell-19 with bothSlow BELOW the OFF baseline (15 vs 23).

**Scoreboard (city-organic-L2, 1000 steps; classifier pair-steps / landing onsets):**

| arm state | bothMove | bothSlow | stopXmove | landings | flow |
|---|---|---|---|---|---|
| gate OFF (= shipped default) | 145 | 23 | 17 | 12 | drained, dwell 16 |
| gate ON (current code) | 124 | 15 | 18 | 6 | drained, dwell 19 |
| honest SUMO (same net) | 4 | 0 | 0 | 0 | drained |

Battery gate ON vs `docs/reports/net-regression-entry34-stays.txt`: stuckDwell 0 everywhere
(city-3000 13 = its baseline 13), city-organic arrived 494 > 491, junction-realism-L2
INCONCLUSIVE → DRAINED; two mild flags — junction-realism-L1 arrived 362→355 (entry-hold
throughput cost, no wedge), willpass-saturation overlaps 3→4.

## 2. The instrument loop (run these, exactly)

```bash
# builds -- Sim.Run/Sim.Sumo are NOT in Traffic.sln (measure stale code otherwise):
dotnet build -c Release src/Sim.Core/Sim.Core.csproj && dotnet build -c Release src/Sim.Run/Sim.Run.csproj
# A/B on the workhorse net (deterministic engine -- identical cmd => identical FCD):
dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /tmp/off.fcd.xml
SUMOSHARP_PHYSOCCUPANCY=1 dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /tmp/on.fcd.xml
python3 scripts/classify-junction-overlaps.py /tmp/on.fcd.xml --examples 40
python3 scripts/analyze-junction-realism-fcd.py /tmp/on.fcd.xml   # DRAINED-vs-GRIDLOCK + dwell + wedge row
# cross-net wedge guard (the thing that caught the Entry-36 brush wedge):
SUMOSHARP_PHYSOCCUPANCY=1 python3 scripts/run-net-regression.py --exclude city-15000 \
  --out /tmp/battery-on.txt --compare docs/reports/net-regression-entry34-stays.txt
# per-vehicle episode trace (binder log names the arm; [bay] names the row):
SUMOSHARP_PHYSOCCUPANCY=1 dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /dev/null --binder-log /tmp/b.csv
SUMOSHARP_PHYSOCCUPANCY=1 SUMOSHARP_TRACEVEH=<id> dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /dev/null 2>/tmp/trace.txt
```

## 2b. Entry 37 (READ THIS before judging the gate on any DENSE surface)

The classifier nets and the battery CANNOT see the density-dependent collapse — only the live-city
smoke at `LIVECITY_CARS=400` reached it. A 5-vehicle ring spanning jy7 -> admission -> leaderFollow
wedged one junction and cascaded citywide; the cut is `Engine.IgnoreJunctionBlockerSeconds` (SUMO's
`--ignore-junction-blocker`, now also applied to the bay arm), defaulted to 60 s by LiveCitySim when
its F3 gate is on. The owner-viewer gate var is **`LIVECITY_F3OCCUPANCY`** (LiveCity path), NOT
`SUMOSHARP_PHYSOCCUPANCY` (Sim.Run/Sim.Sumo path). Smoke A/B loop:

```bash
dotnet build -c Release src/Sim.Viewer/Sim.Viewer.csproj
LIVECITY_CARS=400 LIVECITY_WITNESS=1 LIVECITY_F3OCCUPANCY=1 dotnet run --project src/Sim.Viewer \
  -c Release --no-build -- --mode live-city --smoke --frames 1200 | grep -E "GRIDLOCK|INTERNALSTUCK|CHAIN"
```

Pre-existing, separate: LongHorizonGridlockDiagTests' all-sibling-gates-ON config fails (129 long
stalls) at bcd6813 already — Sim.LiveCity.Tests is not in Traffic.sln, nobody had run it.

## 3. What remains before a default flip (F3.1 → F3.2)

1. **F1.1 — the now-dominant residual.** ~15 of L2's 18 gate-ON stopXmove pair-steps are stopped
   turners on PLAIN internal lanes vs movers netconvert never made foes (recurring sites: j=1150
   `:1150_2_0`×`:1150_0_1`, j=123 `:123_11_0`×`:123_9_1`, j=1021, j=428, j=301 straights). Same
   sites clip gate-OFF — this class predates the bay work. Design question: extend the corridor-
   geometry pass to ALL internal-lane pairs absent from the foes matrix (with the brush filter,
   which is what makes that safe now), or port SUMO's `myConflicts` geometry wholesale (F1.1's
   original framing). The classifier target "stopXmove → ~0" is unreachable without this.
2. **Live-city demo smoke** (overlaps 0, no gridlock, dead-stop ≈ 9–12%) — untested under the gate.
3. **Owner Geneva re-check** — the report that started this is the final gate.
4. Optional: F0.1 TraCI probe half; junction-realism-L1's −7 arrivals deserves one look (no wedge,
   dwell unchanged — likely honest entry-wait cost, but label it before the flip).

## 4. Where the code is (search anchors)

- **Bay arm**: `Engine.cs`, search `F2.1b` / `Entry 36` — inside `JunctionYieldConstraint` after the
  IntLanes foe loop, `jyArm = 7`. Contains the entry-order backstop (`egoInsideJunction &&
  !IsLeaderByEntryOrder(...) -> skip`), the back-bumper exiting test, and the `[bay]` trace
  (SUMOSHARP_TRACEVEH-gated).
- **Ingest**: `NetworkParser.cs`, search `F2.1b` / `Entry 36` — stage-lane rows, bay-piece rows
  (negative ego arcs), `minEgoOverlapLen`. `PolylineGeometry.TryCorridorOverlap` is proximity-
  sampled (threshold 2.0 m CENTERLINE distance — REMEMBER: that exceeds the 1.8 m body-touch
  distance, which is exactly why the brush filter exists).
- **Physical occupancy index**: `_physOnLaneFirst/Second`, filled in `BuildFoeApproachIndex` (the
  route-pool indexes first-mask bay occupants — measured; never use `FindFoeVehicle` for physical
  questions).
- **Pins**: `JunctionBayConflictIngestTests` (both traced witness sites: 301 sibling-bay pieces
  kept, 359 brush dropped, long rows kept). Instruments: `--examples` on the classifier.
- **F2.2 (done)**: merge reachability `!respondsTo && !physicalFoe` + `arbitration:` param +
  PHASE-1 `IsLeaderByEntryOrder` skip. Do not touch.
- **Env plumbing**: `SUMOSHARP_PHYSOCCUPANCY` in `Sim.Run/Program.cs` + `SumoShim.cs`,
  `docs/ENV-GATES.md` row.

## 5. Named residual classes (so nobody re-traces them)

- **Lane-sequence link mismatch** (L2 t=543/544, gate OFF t=372, same site): a mover with a PENDING
  strategic lane change resolves its upcoming junction link through the OTHER lane's connection, so
  EVERY yield arm watches the wrong link's rows on approach; by the time the link corrects, ego is
  mid-overlap (COMMITTED-skip, correctly non-wedging). Pre-existing, shared by all arms, out of
  bay-work scope.
- **Late-stop transient** (L2 t=468): the bay occupant stopped AFTER the mover committed at
  12.8 m/s — physically unstoppable at any hold timing. Structural; do not chase with dials.
- **jyArm-5 (inTheWay) vs jyArm-7 cross-arm pairs cannot be tie-broken** — arm 5 is SUMO-faithful
  and follows a physically-in-the-way foe regardless of entry order. The Entry-36 answer is to make
  the GEOMETRY honest (a brush is not a conflict) so the pair never forms; if a future net wedges
  cross-arm on a GENUINE shared corridor, that is a new problem — trace it, don't tune it.

## 6. Traps (each already cost time THIS workstream)

- `Sim.Run`/`Sim.Sumo`/`Sim.Viewer` are NOT in `Traffic.sln` — build csproj explicitly.
- Env gates are process-global; `env | grep SUMOSHARP` before any A/B; set the gate explicitly in
  BOTH arms.
- **"L2 green" is not "battery green"**: Entry 36's brush wedge was INVISIBLE on both classifier
  nets and appeared only in the cross-net battery. Run the battery for ANY ingest/arm change.
- Corridor PROXIMITY is not body CONTACT (2.0 m threshold vs 1.8 m touch distance) — a row is a
  claim that bodies can meet; the brush filter enforces it, don't bypass it.
- The pool-based foe indexes answer "who is ROUTED through", not "who is STANDING ON".
- Scratch FCDs die with the VM; the engine is deterministic — regenerate with §2's exact commands.
- Reasoned mechanism hypotheses are ~0-for-20 here. Entry 36 both ways: the resume-doc's own
  designed fix (admission-side relocation) was falsified by the first trace, and the first fix's
  geometry was corrected by the second. Trace first.
