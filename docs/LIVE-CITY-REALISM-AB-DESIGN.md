# Live-city realism #1 residuals A + B — BOOTSTRAP for a separate session

**Self-contained brief for a SEPARATE session to execute Tasks A and B.** Both are `Sim.Core`
(engine) parity-sensitive changes — a different risk class than the feed-side realism fixes already
shipped on `claude/livecity-realism-fixes` — so they were split off for a dedicated session. Read this
top-to-bottom; it assumes near-zero prior context.

Owner decisions captured (verbatim intent):
- **A: approved** ("ok let's try proposed fix") — but **a repro with pedestrians AT a crosswalk is
  required to verify it** (the wiggle only appears when a ped actually holds a car at a crossing;
  density-dependent).
- **B**: "similar to what the engine already has for external agents (dodging / stopping in front of
  them) — nice to **unify** the mechanism. The external-agent API is maybe using **string names**
  which is performance-unfriendly and needs redesign." Hard requirement: **in the high-realism zone a
  car must NEVER crash into a ped, nor pass one at close distance / high speed.**

---

## 0. Branch, gates, prerequisites (do this first)

- **Branch:** start from the tip of `claude/livecity-realism-fixes` (has all the realism #1/#2 work +
  the main viz-unification merge + the diagnostics below). If it has merged to `main` by the time you
  start, branch off `main`. Use a fresh branch, e.g. `claude/livecity-realism-ab`.
- **Build:** `dotnet build -c Release` (solution is `Traffic.sln`). SUMO NOT needed for the test loop.
- **THE IRON LAW (must stay green after every change):**
  - `dotnet test tests/Sim.ParityTests -c Release` = **657 passed / 4 skipped**, byte-identical.
  - `dotnet run --project src/Sim.Bench -c Release` → deterministic hash **`D96213B7BB4021A7`**,
    `par == single` also `D96213B7BB4021A7`.
  - `dotnet test tests/Sim.LiveCity.Tests -c Release` = **25/25** (includes the crossing-yield +
    DenseFlow-throughput guards — do not regress them).
  - No `System.Random`; per-entity seeded RNG only.
- **Parity strategy for BOTH tasks:** every engine crowd/obstacle path is already gated on
  `CrowdSource != null` (or an external-obstacle store being non-empty), which is the case ONLY for the
  demo (`LiveCitySim`) — **never for a committed golden or the bench**. Keep new behaviour behind that
  same gate (or a new demo-only flag no golden sets) and parity is inert *by construction*. Always
  re-run the three gates above to prove it.

### Diagnostics already built (use these to repro + verify headlessly)
Run from `src/Sim.Viz` (`dotnet run --project src/Sim.Viz -c Release --no-build -- <mode>`):
- `--live-city-cartrace <steps> <carId> [loStep hiStep]` — per-tick authoritative speed / lane /
  binder / world-pos / angle for one car. **The Task-A repro tool.**
- `--live-city-orcatrace <steps> [carId]` — every MOVING car whose bumper is over an ORCA ped ANYWHERE
  (incl. mid-junction), split by internal-lane / fast / crowd-brake-engaged; optional per-tick focus
  dump. **The Task-B repro tool.**
- `--live-city-yieldtrace <steps>` — crossing-ped nose-in metric + throughput (`carArrivedTotal`).
- `--live-city-demo <out.html> [steps]` — faithful DR-smoothed replay (real `LiveCitySim`), for the
  owner to eyeball; click-to-identify shows each car's `__vehN` id (matches the trace names).
  **Delivery cap is 30 MiB**: 10x density (`LIVECITY_PEDS=1600`) × 160 steps ≈ 29.8 MiB (just fits);
  for longer/denser, drop density or lower `RenderHz` (player Catmull-Rom keeps it smooth).
- Binder codes (from `Engine.BindingConstraint`, a diagnostic argmin): **13 = `CrowdLongitudinalConstraint`**
  (waiting on a ped/crowd), 10 = junctionYield, others = car-following / TL / free-flow.

---

## TASK A — stopped car wiggles sideways while held at a crosswalk

### Diagnosis (already done — do not re-derive)
A car stopped for a crossing pedestrian **drifts laterally back and forth by a full lane-width while its
forward speed is exactly 0**, and the DR replay renders it as "floating + rotating weirdly" in front of
the ped. It does NOT happen at red lights (car stops on its correct lane, no lateral pressure).

**Mechanism (pinned):** it is **pure sublane `PosLat` drift within the SAME lane** — the vehicle's lane
id is unchanged throughout, so it is **not** a lane change and **not** the keep-right INLINE "float"
(that path changes `LaneId`; `Engine.cs` ~10938–10964). It is the **MSLCM_SL2015 lateral driver /
preferred-alignment `DriftToward`** (`Engine.cs` ~8654–8712, `computeSpeedLat` truncation to
`maxSpeedLat`) continuing to move `PosLat` toward a target that flip-flops while the car is stopped.
`LaneChangeMinSpeed` does NOT gate it — that gates lane-CHANGE execution (forward-speed keyed), not the
sublane alignment drift.

**Proven repro (Task-A verification substrate):**
```
LIVECITY_PEDS=300 dotnet run --project src/Sim.Viz -c Release --no-build -- \
  --live-city-cartrace 400 __veh218 370 380
```
Around t=186–189 s: `authSpd=0.00`, lane constant `e_d_2_4_d_3_4_1`, but world Y swings
2598.3 → 2600.2 → 2597.2 (±~2–3 m), binder=13 (crowd-bound). **After the fix, Y must stay constant
while authSpd≈0.** (Owner note: this needs peds AT a crosswalk — the `LIVECITY_PEDS=300` run above IS
such a repro; if you want a tighter one, construct a focused scenario where a single ped holds one car
on a crossing and assert its `PosLat` is frozen.)

### Fix design (demo-gated → parity-inert)
Add a **demo-only clamp** on the sublane lateral driver: **when a vehicle's forward speed is below a
threshold (reuse `LaneChangeMinSpeed`, default 1.5 m/s), force its lateral speed / `PosLat` step to 0**
(freeze the sublane alignment drift). Gate it so it is INERT on goldens:
- Preferred: gate on the existing demo seam. Either extend `SetLowRealismLaneChange`/`LowRealismLaneChange`
  semantics, or add a new `Engine` flag e.g. `FreezeLateralWhenStopped` (default false; `LiveCitySim`
  sets it true, like it already sets `LaneChangeMinSpeed`). The clamp only engages when the flag is set
  AND forward speed < threshold → no golden path touched.
- Apply the clamp at the point `DriftToward`/`computeSpeedLat` produces the per-step `latDist`/lateral
  speed (`Engine.cs` ~8666–8712). Zero it (return current `PosLat`) under the gate.
- Owner's rule of thumb: **"lateral motion must always be accompanied by forward motion"** — this is the
  same principle behind the #15 keep-right stopped-float guard (`Engine.cs` ~10920), just applied to the
  sublane alignment driver too.

### Success conditions (A)
1. `--live-city-cartrace` on the repro: a crowd-bound stopped car's world position is **stationary**
   (X and Y constant) while `authSpd≈0`; no lateral swing.
2. No throughput regression: `--live-city-yieldtrace 2000` `carArrivedTotal` within noise of baseline;
   `DenseFlow_...NoGridlock` test green (the stopped-float guard once box-blocked the demo when applied
   without the cooperative sort — watch for that; verify arrivals don't collapse).
3. Parity **657/4** byte-identical + bench **`D96213B7BB4021A7`** (par==single) — proves the gate is inert.
4. A fresh `--live-city-demo` replay (with a ped holding a car at a crossing) delivered to the owner;
   the stopped car sits still in front of the ped.

---

## TASK B — high-realism zone: a car must NEVER hit / close-fast-pass a pedestrian

### Diagnosis (already done)
Cars still pass ORCA pedestrians at close distance / speed on **internal (`:`) junction lanes**
(owner repro `__veh24`; `--live-city-orcatrace` shows every such event is on a `:`-lane, bound by
junctionYield not the crowd-brake). Root cause: `CrowdLongitudinalConstraint` (`Engine.cs` ~8572)
**projects the ped disc onto the car's lane** (`LaneProjection.Project(lane.Shape, …)`) and brakes only
while the lane-projected `latOff` is within the corridor; on a short/curved internal junction lane that
projection **misjudges** a diagonally-crossing ped's lateral offset, so the ped slips the gate and the
car re-accelerates through the junction. The r=0.6 ORCA-footprint inflate (already shipped) fixed the
head-on cases but not this diagonal-on-internal-lane one. (The ped may also be off-route "wandering" —
that overlaps realism defect #4, ped LOD demote/route; note but don't depend on it.)

### Requirement (owner, HARD)
Inside the high-realism zone (the `InterestField` promote pocket; `LiveCitySim.HighRealismPocketX/Y` +
`HighRealismPromoteRadius`, or the camera-driven zone via `SetLcRealismZone`), a car must **never** (a)
overlap a pedestrian, nor (b) pass one at close distance while moving fast. This is a guarantee, not a
best-effort tuning.

### Fix design — unify on the world-disc seam, add a zone hard-guard
The engine ALREADY has two ped/agent-reaction mechanisms; the owner wants them **unified**:
- **External-agent obstacles** — an older **string-keyed** `ExternalObstacle` store (dodge / stop in
  front of a named obstacle). The owner flags the **string-name API as performance-unfriendly and due a
  redesign**.
- **Crowd world-discs** — the neutral `WorldDisc` + `ICrowdFootprintSource`/`CrowdSource` seam
  (`Engine.CrowdLongitudinalConstraint` + the B6 swerve at ~9198), which `LiveCitySim` already uses for
  peds. This is the performant, allocation-free path.

**Recommended direction:**
1. **Consolidate external-agent reactions onto the `WorldDisc` seam**, retiring/adapting the
   string-keyed `ExternalObstacle` API (or making it a thin adapter that emits world-discs keyed by an
   integer handle, not a string). This is the API redesign the owner asked for. Keep it parity-inert
   (no golden populates it).
2. **Add a high-realism-zone HARD ped-safety guard** that does NOT rely on the fragile internal-lane
   lane-projection. For a car whose position is inside the high-realism zone, compute ped proximity in
   **world space** (distance from the car's swept front to any ped disc ahead, using the car's actual
   heading, not lane projection) and impose an **absolute speed cap** / emergency brake so it can never
   reach a ped at unsafe closing distance. This covers internal junction lanes where the lane frame is
   unreliable. Only active inside the zone → localized, so throughput elsewhere is untouched.
3. Consider folding the crowd longitudinal brake, the B6 swerve, and the (redesigned) external-obstacle
   reaction into ONE world-disc reaction pass so there is a single, consistent "avoid the agent ahead"
   mechanism (the unification the owner wants).

**Watch-outs:** the query buffer is `Engine.MaxCrowdDiscs = 256` (already raised from 16 for density —
keep it ≥ realistic disc counts). Don't reintroduce the velocity-0 over-brake (a moving ped must be
followed, not treated as a dead stop — see `InflatedFootprintSource` / the `GateOrcaPedsOnCrossing`
velocity-0 lesson: it cost 15% throughput). Preserve ped velocity in any new disc path.

### Success conditions (B)
1. `--live-city-orcatrace 400` at `LIVECITY_PEDS=1600`: **0** ORCA drive-throughs AND **0** close-fast
   passes for cars inside the high-realism zone (extend the diagnostic to flag close-fast-pass:
   moving car within, say, <1 m of a ped at >2 m/s inside the zone). Outside the zone unchanged.
2. Throughput preserved: `DenseFlow_...NoGridlock` green; `carArrivedTotal` within noise.
3. Parity **657/4** + bench **`D96213B7BB4021A7`** (par==single) — the external-agent API redesign and
   the zone guard must be inert on goldens (no golden uses external agents or CrowdSource).
4. Owner-facing `--live-city-demo` replay (10x density) showing cars firmly yielding to ORCA peds
   mid-junction inside the zone.
5. If the string→handle external-agent API is redesigned, keep/port its existing tests (search
   `ExternalObstacle` in `tests/`) and add coverage that the world-disc path reproduces the old
   dodge/stop behaviour.

---

## Key files
- `src/Sim.Core/Engine.cs` — `CrowdLongitudinalConstraint` (~8572), the sublane lateral driver /
  `DriftToward` / `computeSpeedLat` (~8654–8712), the keep-right INLINE swap + stopped-float guard
  (~10920–10964), `MaxCrowdDiscs` (~8563), `SetLowRealismLaneChange`/`LowRealismLaneChange` (~2763),
  `LaneChangeMinSpeed`, `BindingConstraint`/`BindingConstraints`, the string `ExternalObstacle` store
  (grep `ExternalObstacle`).
- `src/Sim.Core/Bridge/` — `WorldDisc`, `ICrowdFootprintSource`, `CompositeFootprintSource`,
  `CrossRegimeCoupling` (the world-disc seam to consolidate onto).
- `src/Sim.LiveCity/LiveCitySim.cs` — sets the demo flags (`LaneChangeMinSpeed`, `CrowdSource`, the
  per-area LOD `SetLowRealismLaneChange` block ~521), `HighRealismPocketX/Y`, `HighRealismPromoteRadius`,
  `SetLcRealismZone`, `WitnessAuthoritative` (binder/lane/speed readback), `IsOnCrossingPolygon`/
  `CrowdDiscCountsNear` (diagnostics). `LiveCityConfig.cs` — the demo knobs + `LIVECITY_*` env overrides.
- `src/Sim.LiveCity/InflatedFootprintSource.cs` — the shipped velocity-preserving ORCA disc inflate
  (r=0.6); the B guard should compose with, not fight, this.
- `src/Sim.Viz/Program.cs` — the diagnostics (`RunLiveCityCarTrace`, `RunLiveCityOrcaTrace`,
  `RunLiveCityYieldTrace`) and `--live-city-demo`.
- Prior context: `docs/LIVE-CITY-REALISM-1-2-DESIGN.md` (the shipped fixes),
  `docs/LIVE-CITY-REALISM-ATTEMPT-LOG.md` (full investigation trail incl. the A/B diagnoses),
  `docs/LIVE-CITY-REALISM-RESUME.md`.

## Handoff notes
- Do NOT regress the shipped realism #1/#2 fixes (crossing gate r=1.5, paused feed, `MaxCrowdDiscs=256`,
  ORCA inflate r=0.6) or the #15 dense-flow gains (the DenseFlow guard is the tripwire).
- When done, fetch back into `claude/livecity-realism-fixes` (or main) so the improvements flow through
  the unified `--live-city-demo` replay automatically.
