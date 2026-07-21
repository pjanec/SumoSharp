# Phase 2 — deterministic crossing-yield (Option B). Design + task plan

**Goal.** Cars yield to **any** pedestrian on a crosswalk — including **low-power** (un-promoted) peds —
**cheaply**, without paying to promote every crossing ped to full ORCA. This is the owner's hard
requirement (Option B) and the perf/robustness upgrade over Phase 1's per-crossing promotion.

Contract: `SUMOSHARP-LIVE-CITY-DECISIONS.md` (Option B accepted; the `class → yield rule` table).
Builds on Phase 1 (`LIVE-CITY-2D-BUILDER-DESIGN.md`). HOW only; the WHAT/why is in those docs.

## 1. The seam (already exists, proven, parity-inert)
`Engine.CrowdLongitudinalConstraint` (`src/Sim.Core/Engine.cs:7790`) already brakes a vehicle for any
`WorldDisc` returned by `Engine.CrowdSource.QueryNear(...)` directly ahead in its lane — **gated on
`CrowdSource != null`, so byte-identical for every golden**. Phase 1 wires `CrowdSource` to the
manager's high-power `OrcaCrowd`, so cars see *promoted* peds. Phase 2 makes cars see *low-power crossing
peds too*, through the same seam — no new engine math, just a richer footprint source.

## 2. Mechanism — per-crossing occupancy → virtual blocking disc
The insight the owner flagged: a low-power ped's pose is a **pure, deterministic function of time**, so
we never need per-car/per-ped neighbour tests. Instead:

- **`CrossingOccupancySource : ICrowdFootprintSource`** (NEW, `Sim.Pedestrians` or a coupling class).
  Holds the baked crossing polygons (centroid, half-width, the crossed vehicle lane(s), and the crossing
  **class**). Each tick it computes, cheaply, which crossings are **occupied** by a low-power ped, and on
  `QueryNear(x,y,r,into)` returns **one virtual `WorldDisc` per occupied, yield-required crossing** near
  (x,y) — a "closed gate" spanning the crosswalk — carrying the occupying ped's velocity so the car brakes
  smoothly and releases when the crossing clears. **O(occupied crossings), not O(peds).**
- **Occupancy is cheap and deterministic:** only peds whose route includes a given crossing can occupy it;
  a low-power ped on a crossing is detected by point-in-crossing-polygon on the (already-computed) render
  poses, or (cheaper) by precomputing each ped's [enter,exit] time interval per crossing from its PathArc
  leg. No `System.Random`; same bytes run-to-run.
- **Compose with the high-power crowd:** `Engine.CrowdSource = Composite(highCrowd, crossingOccupancy)`
  so cars yield to BOTH promoted peds AND low-power crossing peds. A tiny `CompositeFootprintSource`
  (`QueryNear` = sum of children into the span) is added — it does **not** exist yet (the earlier spec
  assumed it); it's ~15 lines and inert.

## 3. Class-based yield rules (Decisions Q1)
Per-crossing behaviour is gated by the crossing `class` (from `edge_fields.json`, cross-checked against
the net's `<crossing priority>` + `<tlLogic>`):

| class | occupancy → disc emitted when… |
|---|---|
| **unsignalized** (zebra, ped priority) | **always** while a ped is on/entering the crossing → car must stop. |
| **signalized** | only while the peds legitimately have the **walk phase** (the TL governs both; a ped shouldn't be on the crossing against its signal). Read the crossing's `<tlLogic>` state via `CrossingTlReader`/the live-signal seam. |
| **discouraged** (minor node, car has RoW) | emit a disc **only if a ped is physically in the lane** (safety brake, don't drive through), but do not create a RoW stop — a shorter/weaker constraint (e.g. only when the ped is within the carriageway, not merely approaching). |

Crossing → crossed vehicle lane(s) + stop position come from the net `<crossing crossingEdges="…">` +
geometry (the internal crossing edge sits across those vehicle lanes); this is how a disc is placed on the
right lane at the right longitudinal position for `CrowdLongitudinalConstraint` to pick it up.

## 4. Determinism & parity (invariants)
- **Parity-inert:** `CrossingOccupancySource` is only ever composed into `CrowdSource` by an opt-in
  coupling (the live-city demo, later a production flag). `CrowdSource` stays null for every committed
  golden → determinism hash + `dotnet test` unmoved.
- **Deterministic:** occupancy is a pure function of the low-power poses (themselves pure functions of
  time); no RNG; no per-thread order dependence.

## 5. Wiring into the live-city demo (what changes in `BuildLiveCity`)
- **Drop the per-crossing `InterestSource`s** (W-A(i)) — no longer needed to make cars yield; low-power
  peds now stop cars directly. **Keep** the central high-realism `InterestSource` pocket (W-A(ii)) purely
  as the LOD showcase. Net effect: far fewer promotions (cheaper), and cars yield at *every* crossing, not
  only where promotion happened to fire in time.
- `Engine.CrowdSource = Composite(manager.HighPowerFootprints, crossingOccupancy)`.
- Colouring (W-C) unchanged; expect **peak high-power to drop sharply** (only the pocket promotes now) —
  a clearer grey-ambient / orange-pocket contrast, and a cheaper run.

## 6. Success conditions
1. **Low-power ped stops a car:** with the central pocket moved away from a chosen crossing (so the ped
   there is definitely low-power/grey), a car still measurably decelerates to ~0 for that grey ped on the
   crossing and resumes after it clears. *Verify:* the `RunLiveCity` yield metric stays non-zero / min
   speed ~0 **with per-crossing promotion removed**, i.e. yielding survives without promotion.
2. **Class rules hold:** a unit test drives one ped of each class onto a crossing with a car approaching:
   unsignalized → car stops; signalized-on-red-for-peds → no phantom stop; discouraged → car brakes only
   when the ped is in-lane. (Hermetic, on the POC-0 net + a small synthetic crossing set.)
3. **Cheap:** peak high-power in the live-city run drops (promotion no longer used for yielding); the run
   stays well within its current time. Report emitted-disc counts.
4. **Parity untouched:** full `dotnet test` + determinism hash green; two runs byte-identical.

## 7. Task list & tracker
- [x] **P2-T1 — `CompositeFootprintSource`** (`src/Sim.Core/Bridge/`, `ICrowdFootprintSource` over N
      children, span-safe concat). `CompositeFootprintSourceTests` (concat, no-overflow, empty).
- [x] **P2-T2 — crossing model:** folded into P2-T3 — the source builds directly from the baked
      `BakedPolygonKind.Crossing` polygons (vertices + bbox). No net `crossingEdges` / stop-pos mapping was
      needed: a gate disc at the occupying ped's own position sits in the approaching car's forward
      corridor, so `CrowdLongitudinalConstraint` picks it up without an explicit crossing→lane map.
- [x] **P2-T3 — `CrossingOccupancySource`** (`src/Sim.Pedestrians/Crossing/`): per-tick `Update` over the
      low-power poses (bbox pre-filter + ray-cast point-in-polygon) → one stopped "closed-gate" `WorldDisc`
      per occupied crossing; empty fast-path `QueryNear`. `CrossingOccupancySourceTests` (in/out, radius
      filter, empty). **v1 is class-agnostic** ("brake for any ped physically on a crosswalk") — the safe
      superset that satisfies "never drive through a ped"; the finer class rules are P2-T4.
- [ ] **P2-T4 — signalized/discouraged class gating** — DEFERRED (non-blocking): v1 gates every crossing
      the same (safe). Refine with the `edge_fields` class + walk-phase state only if the busy signalized
      grid shows too many phantom stops (traffic flows fine at the demo dial today).
- [x] **P2-T5 — rewire `BuildLiveCity` + measure:** `CrowdSource = Composite(HighPowerFootprints,
      crossingOccupancy)`; **per-crossing promotion dropped** (the pocket now anchors on the central
      intersection so it still promotes ~13 peds = a visible orange high-realism zone). Yielding survives
      with promotion gone (`carYieldObservations=317`, min car speed `0.00`, `peakOccupiedCrossings=50`).
      **Vehicle cost unchanged: `engine.Step()` ≈ 5 ms/step, same as Phase 1; ped-side occupancy Update
      ≈ 0.3 ms/step.** Two runs byte-identical; full gate green (ParityTests 654/+3, Pedestrians 219).
      One bug found + fixed en route: lively/weaving peds are `PedDrModel.ActivityTimeline`, not `PathArc`,
      so the low-power gather filters on `!= FreeKinematic`.

**Phase 2 status: COMPLETE** (P2-T1/T2/T3/T5; T4 deferred). Vehicle sim provably not slowed; cars yield to
un-promoted crossing peds. Phase 3 (City3D combined + semantic) is next.

## 8. Open confirmations (resolve at implementation, non-blocking)
- Exact `edge_fields.json` field name carrying the crossing class (Decisions Q1 says the taxonomy;
  confirm the JSON key). Fallback: derive class from the net's `<crossing priority>` + node type.
- Whether to place ONE disc per occupied crossing (span the crosswalk) or one per occupying ped — start
  with per-crossing (cheapest, matches the "closed gate" framing); revisit only if a car clips a ped mid-
  crossing at speed (then per-ped, or the `CrossRegimeCoupling` sub-stepped exchange).

## 9. Out of scope for Phase 2
- City3D (Phase 3). The full production density-coupling / dynamic crossing-rate (R8/P8-4b) stays parked.
