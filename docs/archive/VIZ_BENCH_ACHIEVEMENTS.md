# VIZ_BENCH_ACHIEVEMENTS.md — what the viz + benchmark track delivered

> **STATUS: ARCHIVED (2026-07-28)** — Narrative handoff for the original `Sim.Viz` / `Sim.BenchCity` track. Its "current state" is 151 green tests against today's 775, and its remaining items are subsumed by the later perf and parity work (`docs/PERF-HANDOVER.md`, the rung docs). **Kept for the two real engine bugs it surfaced** -- that is the part not recoverable from the code.


A summary of the `Sim.Viz` (replay/visualization) and scaled-city benchmark work built on the
`claude/spec-docs-review-qgwatc` branch, from the two source specs (`VIZ_SPEC.md`,
`BENCHMARK_SPEC.md`). Task-level detail is in `VIZ_BENCH_TASKS.md`; this is the narrative + the
handoff state. All of this is **off the `dotnet test` parity path** — additive tooling that never
changed `Sim.Core` behavior (the parity suite stayed green throughout, 104 → 151 as the parity
track itself grew).

## What was built

### Phase 0 — FCD writer seam (`VB-0`)
- `Sim.Harness.FcdWriterObserver` — writes a SUMO-schema `--fcd-output` file **as the engine runs**,
  hung on the existing D9 `ISimExportObserver` export seam (no change to `Engine.EmitTrajectory`).
- `VehicleExportSnapshot.VehicleType` added so the emitted FCD carries the `type=` attribute.
- `src/Sim.Run` — a tiny CLI: `dotnet run --project src/Sim.Run -- <scenarioDir> [--fcd-out PATH]`
  runs the engine and dumps FCD. Round-trip test proves the writer is lossless and the emitted
  engine FCD lands within tolerance vs the SUMO golden.

### Phase 1 — `Sim.Viz` offline replay tool (`VB-1..VB-4`)
- `src/Sim.Viz` — a C# exporter + committed `template.html`/`template.js` (vanilla Canvas 2D, no
  external libs). `dotnet run --project src/Sim.Viz -- <scenarioDir>` reads net + FCD + rou and
  writes a **self-contained `replay.html`**.
- Renders width-accurate lanes, junction fills, SUMO-native traffic-light signal heads, and
  true-size oriented vehicle boxes colored by vClass; wall-clock playback with play/pause/restart/
  speed/scrub, cursor-anchored zoom, and touch pan.
- One additive `Sim.Ingest` change: `Junction.Shape` (the junction polygon), inert for the engine.
- Committed replays for scenarios `01`, `09-traffic-light`, `11-priority-junction`.

### Phase 2 — scaled-city benchmark (`VB-5..VB-8`)
- `scripts/gen-benchmark.sh <targetConcurrency>` — one parameterized generator (netgenerate →
  randomTrips → duarouter) with Little's-law period tuning and a collapse-detection guard.
- `Sim.Harness.AggregateComparator` (+ `TripInfoParser`/`SummaryOutputParser`/tolerance) — a NEW
  **aggregate/statistical** comparator (arrived count, mean trip duration, mean network speed,
  trip-duration KS distance), unit-tested in `dotnet test`. This is distinct from the
  vehicle-for-vehicle parity comparator.
- `src/Sim.BenchCity` — runs the engine on a `city-<N>` scenario, emits tripinfo/summary analogs +
  FCD + perf/stability metrics (RTF, throughput, peak RSS, a **stuck/gridlock detector** — the
  engine runs teleport-off, so its deadlock signal is stuck-count, not SUMO's teleport count).
- Committed rungs `scenarios/_bench/city-{30,300,3000,15000}` (SUMO reference goldens; engine FCD
  is regenerated, never committed). Scaling curve in `scenarios/_bench/SCALING.md`.
- Demo scenario `scenarios/_bench/city-organic` — an **organic** `netgenerate --rand` town (49
  junctions, traffic lights, a spliced single-lane priority roundabout) with realistic
  through-traffic demand (~180 concurrent).

## Two real engine bugs the benchmark surfaced (both fixed on `main`)

The benchmark did exactly what a scaling harness is for — it exposed correctness gaps that the
small single-junction parity scenarios cannot reach. Both were **reported, not patched here**
(benchmark work doesn't touch `Sim.Core`), and both were subsequently fixed on the parity track:

1. **`FindFoeVehicle` false-positive foe (→ `C4-vi`).** The priority-junction yield matched a foe if
   the foe internal lane appeared *anywhere* in a vehicle's whole route, with no proximity filter —
   so on a 576-junction net almost every approach found a false foe and vehicles stalled. city-300
   went from **46 arrived (broken) to 241** (SUMO 238) once `C4-vi` gated the yield by reservation
   distance. Post-fix the engine tracks SUMO across the ladder: **city-3000 passes at ~4,175 peak
   concurrent, 0 stuck** (see `SCALING.md`).
2. **Single-look-ahead route→lane resolution (→ `C2-iii`, `C2-v`, partially).** A general
   multi-lane (`-L 2`) city couldn't be routed. `C2-iii` (multi-hop best-lanes) and `C2-v`
   (intra-edge lane change) closed most of it; a residual case remains (see handoff below).

Plus one ingest-robustness gap I flagged that landed as **`C2-iv`** (the engine now loads stock
`duarouter`/`randomTrips` output directly — embedded routes + `DEFAULT_VEHTYPE`).

## Mobile viewing: solved, with a real iOS lesson

`VIZ_SPEC` assumed "open `replay.html` from GitHub on a phone." That does **not** work on iOS:
opening a local HTML file from the Files app runs it as a **static preview with no JavaScript** —
the canvas stayed black, controls were dead, and gestures fell through to the browser. The fix is
to serve it over https. The phone-viewing path is therefore a **hosted artifact URL**, not a local
file. Along the way the front-end got hardened for real devices:
- devicePixelRatio-correct draw path (was rendering at 1/dpr in a corner on high-DPR screens);
- self-correcting camera fit (re-fits every frame until first user gesture) for iOS's late/0-height
  layout;
- CSS-pixel-consistent pointer/touch input;
- **centripetal Catmull-Rom** position interpolation + tangent-derived heading, so vehicles follow
  the curved path through a junction turn instead of sliding along the straight chord between the
  1 Hz FCD samples.

## Current state

- `dotnet test` = **151 green**; `Sim.Core` untouched by this track; branch is a clean fast-forward
  of `main`.
- Committed: `Sim.Run`, `Sim.Viz` (+ template), `Sim.BenchCity`, `AggregateComparator` (+ tests),
  `gen-benchmark.sh`, the `city-{30,300,3000,15000,organic}` scenarios, `SCALING.md`, this doc,
  `VIZ_BENCH_TASKS.md`, and the `NEED-*.md` engine-gap briefings.

## What remains (handoff)

1. **Multi-lane (`-L 2+`) — the residual route→lane gap.** A general `netgenerate -L 2` city still
   throws `No <connection> found` at insertion (`NetworkModel.ResolveSequenceCore`). This is the
   task being handed to the parity session — see `NEED-C2iii-followup-intraedge-lanechange.md`.
   Until it lands, the benchmark + demo stay single-lane (`-L 1`), which means no lane-changing/
   overtaking is exercised at scale.
2. **city-15000 (the headline rung)** — needs a clean run (the environment restarted mid-run). The
   committed net/rou/config/SUMO-refs make it a one-command engine run; fill the `SCALING.md` row.
3. **Clean perf pass** — RTF/RSS for city-300/3000 in `SCALING.md` are provisional (measured under
   CPU contention / with FCD writing); re-measure with `--fcd-out ""` and no concurrent load.
4. **Optional realism upgrades** — organically-generated roundabouts need an OSM import or a
   loop-containing base net (`--rand` makes tree-like layouts); a real OSM district is the
   `BENCHMARK_SPEC` stretch goal (needs Overpass access + tolerant of real topology).
