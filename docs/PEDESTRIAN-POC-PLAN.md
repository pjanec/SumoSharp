# PEDESTRIAN-POC-PLAN.md — the de-risking experiment ladder

**Status: plan for review. No code yet.** Companion to `PEDESTRIAN-OVERVIEW.md` (WHAT) and
`PEDESTRIAN-DESIGN.md` (HOW). Each POC is a small, committed, green experiment that **resolves one open
question or proves one mechanism** before we commit to the final design. POCs land as tests/harnesses in
the existing `dotnet test` loop (offline, no SUMO), plus — where useful — a viewer demo. They are
*throwaway-tolerant*: a POC that disproves a design assumption has done its job.

The ladder is ordered by **risk-retired-per-unit-effort**, not by feature order. The riskiest, most
load-bearing assumptions (navigation seam, promotion/PathArc, scale) come early.

Convention for each POC below: **Goal · Validates (requirement / design ref) · Builds · Success
conditions (specific, measurable)**. Success conditions are the *acceptance gate* — the POC is not "done"
until every one passes. Implementation of each POC is delegated to a Sonnet implementor with these exact
conditions; Opus reviews the diff and re-runs the gate before ticking it (the repo's orchestration model).

---

## POC-0 — Pedestrian test network

**Goal.** Produce a small `net.xml` with real pedestrian infrastructure to run everything else against.

**Validates.** Prerequisite for all POCs; the SUMO-geometry navmesh provider (`PEDESTRIAN-DESIGN.md` §4).

**Builds.** A netconvert-generated scenario under `scenarios/` (or `samples/`) containing: sidewalk lanes
(`allow="pedestrian"`), at least one **signalized pedestrian crossing**, a **walkingArea** junction, an
open **plaza** polygon, and a small **parking-lot** surface with entry/exit connection points. Committed
inputs only (SUMO is authoring-time, per `CLAUDE.md`).

**Success conditions.**
1. `Sim.Ingest` loads the net and exposes the pedestrian lanes, crossing(s), and walkingAreas as distinct,
   queryable geometry (a test asserts counts and that crossing lanes carry TLS linkage).
2. The plaza and parking-lot surfaces are represented as walkable polygons the navmesh providers can
   consume.
3. `dotnet test` stays green with SUMO absent (hermetic).

---

## POC-1 — Navigation seam + strategic/tactical routing

**Goal.** A pedestrian walks origin→destination along the real walkable space, via ORCA, driven entirely
through the navigation interfaces.

**Validates.** The primary gap — strategic + tactical layers (`PEDESTRIAN-DESIGN.md` §1, §4); decision D4;
the "two providers prove the seam" claim.

**Builds.** `IWalkableSpace` / `IPedNavigation` / `ILocalSteering`; **two** providers behind them — the
**DotRecast** navmesh and the **SUMO-geometry bake** — and the funnel→`pref`→`OrcaCrowd` tactical path.

**Success conditions.**
1. A ped routed across POC-0's net reaches its destination via a portal path that stays on walkable space
   (property test: never leaves the walkable polygons; arrives within `dist/maxSpeed × slack` seconds).
2. **Both** providers produce a valid path for the same O→D (the interface is honest, not a one-impl
   shim); the higher layers are identical code for both.
3. Two peds given crossing O→Ds negotiate a shared walkingArea with **no overlap** (ORCA operational layer
   intact under navigation input).
4. Deterministic: identical trajectory across runs and across single-thread vs parallel `Step`.

---

## POC-2 — Crosswalk gate + car-stop (interactivity, disjoint crowds)

**Goal.** Pedestrians accumulate at a signalized crossing, cross as a group on the walk phase; an
approaching car stops for them.

**Validates.** Req 3 (car stops for crossers), Req 5 (accumulate-then-surge); decision D5 (rule-based
gate); `PEDESTRIAN-DESIGN.md` §6; open question §11.4 (is rule-gate + avoidance enough, or is a hard
stop-line interlock needed?).

**Builds.** The portal-gate reading `Engine.TlStates`; the release-as-group behavior; the crossing peds
fed to the lane engine (`Engine.CrowdSource` / `ExternalObstacle`).

**Success conditions.**
1. During the "don't walk" phase, peds queue at the portal (a growing count within a bounded region, none
   entering the roadway).
2. On the walk phase the queue releases and clears the crossing; a measurable **surge** in crossing flux
   vs the queued phase.
3. A car approaching during the walk phase **halts before the stop line** (min longitudinal gap to any
   crossing ped ≥ 0 across the whole run — no ped is ever inside a vehicle footprint).
4. Decision recorded: whether the existing avoidance constraint sufficed or a hard interlock was required
   (resolves §11.4).

---

## POC-3 — Sim-LOD promotion/demotion + PathArc↔FreeKinematic DR

**Goal.** Prove the single most novel and load-bearing mechanism: an ambient low-power ped promotes to a
full ORCA agent when it matters and demotes when it doesn't — and an IG reproduces a low-power ped from
its path alone, switching to position streaming exactly on promotion.

**Validates.** Req 1 (the scale enabler) + Req 7 (networking); `PEDESTRIAN-DESIGN.md` §5, §7; Principles 3
& 4; open question §11.1 (triggers/hysteresis).

**Builds.** The low-power deterministic path-follower; promotion/demotion with hysteresis (command-buffer
transitions); the `PathArc` DR model; the DR-model switch on the lifecycle topic; a headless "IG"
reconstructor consuming the stream.

**Success conditions.**
1. A low-power ped following its path is reconstructed by the headless IG **from the path sent once**, to
   within render tolerance (server pose == IG pose over the whole run) — validates Principle 4 / §8.
2. Introducing an external entity near the ped **promotes** it (low→high, `PathArc`→`FreeKinematic`); the
   IG switches from path-DR to position streaming on the broadcast event; the promoted ped then avoids the
   external entity (no overlap).
3. Removing the stimulus **demotes** it after the dwell; **no flapping** under a stimulus hovering at the
   threshold (promotion count is bounded and monotone-ish, asserted).
4. While low-power, **zero** per-frame position packets are emitted for that ped (only the one-time path +
   heartbeat) — the DR-error publisher stays silent (measured send count ≈ heartbeat rate).
5. Deterministic run-to-run and single vs parallel.

---

## POC-4 — Dense believability (crowd, not lanes)

**Goal.** Determine whether pure ORCA gives believable dense-crowd flow, or whether a density term is
needed.

**Validates.** Req 2, Req 5 (mall bottleneck); `PEDESTRIAN-DESIGN.md` §3 "Believability"; open question
§11.2.

**Builds.** A bidirectional corridor scenario and a mall-entrance bottleneck; flux/density measurement;
(only if needed) a speed–density term on `maxSpeed`.

**Success conditions.**
1. Bidirectional corridor: measure throughput and observe whether **counter-flow lanes emerge**; record
   the fundamental diagram (flow vs density). No deadlock (drains via `RemoveOnArrival`/`MaxNeighbours`).
2. Mall bottleneck: a queue forms outside and a stream forms inside; measured **outflow is capacity-
   limited and stable** (no permanent jam).
3. A recorded **decision**: pure ORCA accepted, or a density term added — with the measured evidence that
   drove it (per §3's "measure first" rule). Either outcome is a passing POC.

---

## POC-5 — Obstacle dodging + reroute (external entities)

**Goal.** Pedestrians go around a blocker, and reroute when a way is fully blocked.

**Validates.** Req 3 (dodge external car parked across a sidewalk; walking external blocker); Req 6
prerequisite (box obstacles); `PEDESTRIAN-DESIGN.md` §3a, §6.

**Builds.** Oriented-box obstacle support on the pedestrian path (§3a); the strategic reroute on full
portal occlusion.

**Success conditions.**
1. A ped stream flows around an oriented **box** (a car footprint) partly blocking a sidewalk — no overlap,
   throughput drops but does not stop.
2. A **moving** external entity (a walking blocker via `SetExternalObstacles`) is avoided reciprocally/one-
   sidedly as configured.
3. When a portal is **fully** occluded, affected peds **reroute** via `IPedNavigation` to an alternate
   path (asserted route change), rather than jamming at the blockage.

---

## POC-6 — Parking lot (the full assembly)

**Goal.** Cars maneuvering in a lot, peds weaving between parked cars, mode-switching at entry/exit, and
boarding/alighting — all cooperating.

**Validates.** Req 6 in full; `PEDESTRIAN-DESIGN.md` §2 (regime machine), §6; open question §11.3.

**Builds.** Car `LaneTravel↔ParkingManeuver↔Parked` transitions at entry/exit (`Despawn` /
`MixedTrafficCrowd.Add` / `SpawnVehicle` re-insertion); ped `Walking↔Riding` board/alight; the static-
obstacle index (§3b) for parked-car boxes; the shared obstacle world for maneuvering cars + peds.

**Success conditions.**
1. A car **enters** the lot (leaves the lane world), maneuvers to a slot, **parks**; later **departs** and
   **re-inserts** onto the exit lane at a valid pos/speed and resumes lane travel — verified end-to-end.
2. A ped walks to a parked car and **boards** (despawns at the footprint → lifecycle despawn event); a
   parked car **alights** a ped that appears beside it (lifecycle spawn event).
3. Peds crossing the inner drive lane are **yielded to** by a maneuvering car (bridge mutual avoidance; no
   overlap between any ped and any car footprint over the run).
4. Determinism holds across the churn (slot recycle, transitions) — run-to-run identical, single ==
   parallel.

---

## POC-7 — Performance & network scale (the acceptance measurement)

**Goal.** Produce the missing scale numbers: does the design hit ~10k high / ~100k total peds + ~10k cars
on the target hardware, within a bounded network budget?

**Validates.** Req 1 + Req 7; `PEDESTRIAN-DESIGN.md` §7 bandwidth math, §9 performance; open question
§11.8 (the single biggest risk to *acceptance*).

**Builds.** Parallelized `OrcaCrowd.Step` (contiguous-chunk, §3c); the static-obstacle index (§3b); end-to-
end crowd transport wiring + int16-cm quantization; a large mixed scenario; instrumentation (steps/s,
MB/s, per-record bytes, send counts by DR model).

**Success conditions.**
1. A run with ~10k high-power + ~90k low-power peds + ~10k cars sustains an interactive step rate on a
   16+‑core box (record steps/s; compare parallel vs serial `Step` speedup and where it plateaus).
2. **Measured** egress bandwidth to one IG channel is reported and broken down by class (car / high-power
   ped / low-power ped path+heartbeat), demonstrating the ambient bulk is near-free (PathArc silent) and
   the total fits the stated budget.
3. `OrcaCrowd.Step` parallelization is **bit-identical** to serial (determinism preserved) or, if a fast
   path diverges, it is gated and behaviorally validated.
4. The numbers are written back into `PEDESTRIAN-DESIGN.md` §7 (replacing the estimates) as the design's
   acceptance evidence.

---

## Sequencing

- **Opening batch (recommended): POC-0 → POC-1 → POC-3.** Network + navigation seam + the promotion/PathArc
  mechanism. Rationale: promotion+PathArc is the riskiest, most novel piece and *both* the compute budget
  and the bandwidth budget hinge on it; proving it early validates the entire scale thesis. POC-2
  (visible crosswalk behavior) is the alternative opener if a demonstrable interactive result is wanted
  sooner.
- **Then:** POC-2 (crosswalk), POC-4 (believability), POC-5 (dodging) — largely independent, parallelizable
  across implementors.
- **Then:** POC-6 (parking) — depends on §3a/§3b and the regime machine.
- **Finally:** POC-7 (scale) — depends on the perf work and gives the acceptance numbers.

Evac generalization (Req 4) is not a separate POC: once POC-1 (routing) and POC-3 (promotion) exist, evac
becomes "destination = safe zone + forced promotion + flee override," validated by re-pointing the existing
`Sim.Evac` tests at the generalized layer.

## Exit criteria for the POC phase

The POC phase closes and `PEDESTRIAN-TASKS.md` / `PEDESTRIAN-TRACKER.md` (the production task breakdown)
open when: the navigation seam is proven with two providers (POC-1); promotion + PathArc + server↔IG
reproduction are proven (POC-3); the interactivity mechanisms are demonstrated (POC-2, POC-5); the
believability decision is recorded (POC-4); the parking assembly works (POC-6); and POC-7 has produced
real scale/bandwidth numbers that either meet the target or bound what is achievable. Any POC that
disproves a design assumption feeds a revision of `PEDESTRIAN-DESIGN.md` before the production phase begins.
