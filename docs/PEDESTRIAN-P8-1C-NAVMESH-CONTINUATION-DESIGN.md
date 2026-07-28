# PEDESTRIAN-P8-1C-NAVMESH-CONTINUATION-DESIGN.md — sidewalk↔sidewalk continuation bridging

**Status: landed and validated** — see §4.1 ("both parts landed and validated on the real Geneva crop;
threshold tuning parked per owner"). Successor to P8-1b
(`PEDESTRIAN-P8-1B-NAVMESH-CONNECTIVITY-DESIGN.md`). Closes the **residual** real-net navmesh fragmentation
that P8-1b's *area-anchored* bridge structurally cannot: sidewalk↔sidewalk seams with no walkingArea between
them. Driven by the sub-area (SumoData) session's geometry-free witness `subarea-pedfrag2` (target **83 → 1**).

## 1. The residual (from the sub-area session's diagnosis)

P8-1b's area-overlap / near-abutment pass is **area-anchored**: it adds a portal only when at least one
polygon is an AREA kind (`WalkingArea`/`WalkablePolygon`), so a junction always bridges *through* a
walkingArea — never sidewalk↔sidewalk directly. That is what preserves the POC-0 no-shortcut invariant.

But real netconvert geometry is full of seams where two **buffered sidewalk strips abut within ≤5 cm with no
surviving walkingArea between them**. Two sub-shapes produce identical geometry:
- SUMO emits a **degenerate zero-area walkingArea** at a simple continuation, which the baker's `MinArea`
  filter drops — leaving the two sidewalks with nothing to anchor to;
- an irregular seam where **no walkingArea exists at all** (the witness embeds this, deliberately).

Both leave the same signature: **two `SidewalkSegment` strips, ≤5 cm apart, collinear.** On the real ~2 km
Geneva crop, ~95% of the 366 residual components are this mode (189/192 near-pairs are sidewalk↔sidewalk,
152/192 within 5 cm).

## 2. Why approach A (spine continuation), not B (rehabilitate the degenerate walkingArea)

Two fixes were on the table. **Approach A** (chosen): bridge sidewalk↔sidewalk near-abutment directly, gated
by a continuation test. **Approach B**: stop dropping the degenerate zero-area walkingArea and let the
existing area-anchored pass connect through it.

A is strictly broader and is the sub-area session's explicit steer:
- A covers **both** sub-shapes above (no-walkingArea *and* dropped-walkingArea) — they have the same ≤5 cm
  collinear geometry. B only fixes the dropped-walkingArea subset.
- B **would not satisfy the witness at all** — the witness has no walkingArea (degenerate or otherwise) to
  rehabilitate.

## 3. The fix — a 4th adjacency pass in `PolygonGraph`

After the shared-edge, vertex-proximity, and area-overlap passes, add
`AddSidewalkContinuationAdjacency`:

For each pair of `SidewalkSegment` polygons `(i, j)` **not already connected** whose boundaries approach
within the existing `AbutProximityEps` (5 cm, via `ClosestBoundaryApproach`), add a seam portal **iff** the
pair passes the **continuation test**. Additive + dedup, exactly like the other passes — it only ever ADDS a
portal to an unconnected near-pair; every existing bake keeps its portals byte-identical.

### 3.1 The continuation discriminator (spine end-tangents)

`BakedPolygon.Spine` is the sidewalk's **original centreline polyline** (set only for sidewalks) — the true
travel axis, robust even when the buffered strip is a non-convex bent polygon. The test:

1. Each strip's spine has two ends. Pick the **nearest spine-end pair** `(ei, ej) = argmin |Spine_i[end] −
   Spine_j[end]|` — the ends that actually abut.
2. **Outward end-tangent** at each nearest end: `d_i = normalize(ei − prev_i)` where `prev_i` is the spine
   vertex adjacent to `ei` (so `d_i` points *out* of strip `i`, toward the seam). Likewise `d_j`.
3. **Continuation angle** `θ = angle(d_i, d_j)`. A straight continuation joins end-to-end and collinear, so
   the two outward tangents point at each other: `θ ≈ 180°`. A junction **corner** turns: `θ ≈ 90°`.
4. Bridge **iff `θ ≥ ContinuationMinAngleDeg`** (provisional **135°** — see §4). Reject corners.

Worked cases: two collinear strips end-to-end → `d_i=+x`, `d_j=−x` → θ=180° → bridge. A right-angle corner
(one strip along +x, the other along +y) → θ=90° → reject. A continuation bending 30° → θ≈150° → bridge.

### 3.2 Why this preserves the no-shortcut invariant

- The **angle gate** structurally excludes the junction corners area-anchoring used to block: a corner
  (θ≈90°) is rejected, so A* is never handed a portal that lets it cut across the non-walkable junction
  interior (the exact POC-0 failure).
- **Additive + dedup**: pairs already connected (all synthetic-grid / POC-0 pairs, which share *exact*
  edges and have no ≤5 cm gaps) are untouched. The uniform grid gains nothing; only genuine collinear
  sidewalk seams gain a portal, and a continuation portal never shortcuts (it connects two strips that a
  ped can physically step between across a ≤5 cm seam — already precedented: P8-1b places the same seam
  portal for area abutments).
- Restricted to `SidewalkSegment`↔`SidewalkSegment` — crossing↔sidewalk and crossing↔crossing are never
  touched by this pass (they still route through their walkingArea via P8-1b).

## 4. The threshold is PROVISIONAL (the load-bearing caveat)

**135° is validated only on the synthetic straight-stub witness and MUST be treated as provisional.** The
witness's stubs are straight — continuations sit at θ≈180°, corners at θ≈90°, trivially separable. **Real
sidewalks curve**: a gently-curving legitimate continuation might sit at 150°, a sharper legit bend at 125°,
while a real T-junction is ~90°. A threshold clean on the witness could still **under-connect** curving real
continuations or **clip a shallow real corner** (a bad shortcut). The synthetic boxes cannot validate the
threshold — only the real crop can. This is the same "witness is necessary, not sufficient" lesson as the
vehicle teleport witness.

Engineering consequences (mandatory):
- `ContinuationMinAngleDeg` is a **single named constant**, so tuning is a one-line change.
- The bake exposes a **seam-angle diagnostic** (opt-in): for every sidewalk↔sidewalk ≤5 cm near-pair, the
  end-tangent angle θ and the bridge/reject decision, so the distribution can be characterized on any net.
- **Validation handshake:** once P8-1c lands, the sub-area session validates it on the real Geneva crop —
  confirms it takes the real net 366 → ~213 (Mode-1 cleared) and hands back the **real-net seam
  end-tangent-angle distribution** so the threshold is tuned against actual curvature rather than guessed.
  Until then, 135° stands as the documented provisional value.

## 4.1 Real-crop validation result + threshold tuning PARKED (restore path)

**Status: both parts landed and validated on the real Geneva crop; threshold tuning parked per owner.**

Real-crop findings (sub-area session):
- **Part 2 (reachability filter) is the real-net density win: 544 → 2,773 (cap).** On real geometry the
  crowd was fragmentation-starved; drawing demand only from the dominant reachable component(s) recovers it
  to the dial. Density is now **knob-limited** on real nets, not fragmentation-limited.
- **Part 1 (spine continuation bridge) barely moves real geometry: 366 → 353 components.** It is kept for
  (a) coverage of the genuine collinear-continuation seams it *does* fix and (b) the committed
  `subarea-pedfrag2` witness regression, but on the real crop the residual is dominated by Mode-2/Mode-3
  (gaps > 5 cm, and >2 m isolated stubs) that adjacency cannot reach — which is exactly why Part 2 (not
  Part 1) carries the density recovery.

**Parked knobs (current provisional values — do not treat as final):**
- `PolygonGraph.ContinuationMinAngleDeg = 135.0` (Part 1 continuation gate).
- `NavmeshReachability.DefaultMinAreaFraction = 0.05` (Part 2 dominant-component area fraction).

**Restore path (to resume tuning later):**
1. Re-instrument the seam diagnostic on the real crop: for every `SidewalkSegment`↔`SidewalkSegment` ≤5 cm
   near-pair, dump the end-tangent angle θ and the bridge/reject decision (the design's opt-in seam-angle
   diagnostic — the sub-area session offered to run it once tuning resumes). Collect the **real-net θ
   distribution** — the data neither the synthetic witness nor this session has.
2. Tune `ContinuationMinAngleDeg` against that distribution (lower it if curving real continuations sit below
   135° and are being under-connected; raise it if shallow real corners near 135° are being clipped). It is a
   single named constant — a one-line change + re-run the witness (must stay 83→1) + the no-shortcut corner
   guard test.
3. Tune `DefaultMinAreaFraction` against the real component-size distribution (the sub-area session has it:
   ~623-poly core + ~151-poly region + singleton tail). 5% keeps both large regions; adjust if a real crop
   has a third genuinely-large region below 5% of the largest, or if the singleton tail creeps above it.
4. Since the low-value/high-value split is now known (Part 2 is the density lever, Part 1 is coverage), a
   future revisit could also weigh Mode-2 (widen `AbutProximityEps` beyond 5 cm for collinear continuations
   only) — but only with the real θ distribution in hand, to avoid re-triggering corner shortcuts.

## 5. Part 2 — demand-side reachable-component filter (the other half)

Adjacency bridging structurally caps the real crop at **~366 → ~213**: ~212 components are **Mode-3 isolated
stubs, >2 m from any other walkable polygon** — cropped-off network fragments that adjacency legitimately
cannot (and should not) reconnect. Reaching a single reachable surface additionally needs the **demand side
to ignore unreachable islands**:

- Compute connected components of the baked navmesh (already available:
  `SumoNavMesh.ConnectedComponentCount()`; extend to expose per-polygon component id).
- In `SubareaDemand` / `PedDemand`, **draw O/D endpoints only from the dominant connected component(s)** (by
  walkable area or endpoint count), dropping endpoints that land on tiny unreachable islands.
- Effect: the crowd fills the **reachable** sidewalk surface to the dialed density instead of being dragged
  down by unroutable islands (the peakLive-far-below-cap symptom). Deterministic (endpoint set is a pure
  function of the bake); additive/inert-default (no components dropped when the net is fully connected → the
  witness and every committed box are unchanged).

Part 2 is independent of the witness (which is fully bridgeable → 1 component after Part 1, so no islands to
filter). It is implemented + unit-tested here; full real-net validation is the sub-area session's (they have
the geometry).

## 6. Acceptance

1. **Witness `subarea-pedfrag2` → 1 (or a few) components** under the recorder, and **populates toward the
   cap** (peakLive 13 → toward 124), *not* routing-limited far below it.
2. **`synthetic_pedfrag` and the uniform grid stay at 1 component** *and* gain **no bad shortcut** — an
   explicit routing assertion (a path that must go through a walkingArea still does; no path leaves the
   walkable union), not merely an unchanged component count.
3. **Offline gate green**: full parity suite unchanged (navmesh is ped-only, off the parity path) and the
   ped suite green; a `NavmeshConnectivityTests` case pins the witness 83 → 1 and the two regression boxes at
   1, with the committed `scenarios/_ped/subarea-pedfrag2/` box.
4. **Deterministic + additive/inert-default**: every committed bake byte-identical except genuinely-new
   continuation bridges; no `System.Random`.

## 7. Open questions / notes

- Threshold form: compare `cos θ` against `cos(ContinuationMinAngleDeg)` to avoid a trig call per pair (θ≥135°
  ⇔ cosθ ≤ cos135°). Kept as an angle in the doc for clarity.
- Spine degeneracy: a `SidewalkSegment` always carries a ≥2-point Spine; if a spine is degenerate (<2 usable
  points) the pair is skipped (no tangent) — safe default (leaves it an island, same as today).
- Bent strips: the nearest-spine-end selection localizes the tangent to the abutting end, so a strip that
  bends far from the seam does not skew the angle.
- Part 2 "dominant component(s)": start with the single largest by endpoint-weighted walkable area; revisit
  if the real crop has two genuinely-large disjoint reachable regions (then keep all components above an
  area threshold).
