# HIREALISM-PASSTHROUGH-GATE — camera-zone gating of the ignore-junction-blocker pass-through

**Status: IMPLEMENTED (owner-approved; journal Entry 66).** Small feature: design + tasks folded per CLAUDE.md.

## 1. WHAT (owner request, 2026-08-02)

Inside a high-realism area (driven by camera FOV), cars must never visibly pass through each
other — even at the cost of the junction staying blocked. The blocked car's waiting time must
keep counting while suppressed, so the recovery fires **immediately** when the camera moves away.

## 2. Existing seams (all shipped, all tested)

- **X1 RealismMask** (`docs/HIGH-DENSITY-X1-DESIGN.md`, `src/Sim.Core/RealismMask.cs`,
  `Engine.SetVisibleEdges`): host publishes an immutable visible-edge-id set from the camera
  (volatile swap, captured once per step into `_activeMask`); `null`/absent = fully permissive =
  byte-identical goldens. Already forbids **teleport** (`MayTeleport`) and **despawn-pop**
  (`MayPop`). Tests: `RungHDx1RealismMaskTests`.
- **The pass-through** = `IgnoreJunctionBlockerSeconds` (SUMO MSLink.cpp:1601 port; live-city
  default 60 s when the F3 occupancy gate is on, `LiveCitySim.cs:456`). Seven skip sites in
  `Engine.cs` (crossing occupancy arm, pick rows, PHASE1occ, merge PHASE 1/2 — the
  Entry 57/58/63 family), every one a stateless per-step comparison `foe.WaitingTime >= T`.
- **WaitingTime** accrues in Execute independently of any skip logic → the "keeps counting,
  fires instantly on zone exit" requirement is structural, not new code.
- Teleport is OFF in live-city (`TimeToTeleportSeconds=0`, owner decision); if ever enabled,
  X1's `MayTeleport` already zone-gates it. No work needed there.

## 3. HOW

### 3.1 Engine: third mask flag (parity-inert)

`RealismMask` gains `forbidPassThrough` (default **true** — on camera, no cheating, matching
the existing flags' spirit); `SetVisibleEdges` gains the parameter. New predicate
`MayPassThrough(edgeId)` (visible && forbidPassThrough ⇒ false). At each of the seven
ignore-blocker sites the condition becomes:

```
IgnoreJunctionBlockerSeconds >= 0.0 && foe.WaitingTime >= IgnoreJunctionBlockerSeconds
    && (_activeMask is null || _activeMask.MayPassThrough(foeEdgeId))
```

**The FOE's edge** is tested (the stationary car being driven through — the overlap happens at
its body). `foeEdgeId` is derived from the foe's current lane via `LanesByHandle[..].EdgeId`
(internal lanes carry their own edge ids; §3.2 makes the host include them). Cost: one
HashSet lookup per skip-eligible foe evaluation, only when a mask is set; `_activeMask is
null` short-circuit keeps every golden/bench run at literal-zero overhead. Determinism:
mask captured once per step (existing X1 discipline), reads are pure → par==single holds.

### 3.2 LiveCitySim: world-space region → edge-set helper

The 3D host thinks in camera world-space, not edge ids. New API:

```
LiveCitySim.SetHighRealismRegions(IReadOnlyList<(double X, double Y, double Radius)> circles)
LiveCitySim.ClearHighRealismRegions()
```

Maps circles → edge-id set via a lazily-built spatial index of per-edge AABBs (all lanes'
geometry bounds, internal ':' edges included — so a junction inside the circle contributes its
internal edges, which is where the foe stands), then calls `Engine.SetVisibleEdges(set,
forbidTeleport: true, forbidPop: true, forbidPassThrough: true)`. Circles chosen over frustum
polygons: the host can conservatively bound any FOV with 1–2 circles, and the index test is
trivial. Multiple cameras = multiple circles. Cadence: host calls at its own rate (e.g. every
render frame or on camera move); the engine snapshots per step.

### 3.3 Host plumbing + env knob

- Packed-library surface: expose the two methods through the LiveCity host API City3D consumes
  (same route as existing runtime knobs).
- `LIVECITY_HIREALISM_RADIUS=<meters>` (docs/ENV-GATES.md + `AllLiveCityGateVars`): headless
  testing knob — a fixed circle at the crop/net center. The 3D viewer wires the real camera.

## 4. Determinism / parity argument

No mask set (every golden, bench, parity test, default live-city): `_activeMask is null` at
every new check → byte-identical by construction; bench hash must stay `A134ED3716DDE7BC`.
With a mask: the edge set is a pure per-step snapshot; all reads are lock-free immutable →
par==single unchanged. The gate DELAYS a recovery, never invents motion.

## 5. Success conditions

1. Unit (ParityTests, alongside `RungHDx1RealismMaskTests`): `MayPassThrough` semantics —
   visible+default ⇒ false; not-visible ⇒ true; flag-off ⇒ true; null mask ⇒ gate inert.
2. Engine A/B (headless Geneva, ped-heavy arm, deterministic env): with a region covering the
   busiest junction cluster, ignore-blocker skips inside the region = 0 while waits keep
   climbing (witness shows wait > 60 with no pass-through); OUTSIDE the region skips fire as
   today; after clearing the region mid-run, suppressed pass-throughs fire within one step.
3. Full sln green; goldens byte-identical; bench hash unchanged.
4. 3D: owner sees no on-camera drive-throughs in a high-realism region; junction visibly
   clears when the camera moves away.

## 6. Tasks

- T1 engine: `forbidPassThrough` flag + `MayPassThrough` + the seven-site gate (+unit tests).
- T2 LiveCitySim: region→edge-set helper + `SetHighRealismRegions` (+edge-AABB index).
- T3 plumbing: host API surface + `LIVECITY_HIREALISM_RADIUS` + ENV-GATES.md entry.
- T4 measurement: success-condition 2 A/B, journal entry, gates.

Tracker: [x] T1  [x] T2  [x] T3  [x] T4 — all success conditions measured in journal Entry 66 (3D verdict = condition 4, pending next owner session).
