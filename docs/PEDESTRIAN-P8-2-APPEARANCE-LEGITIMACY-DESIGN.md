# PEDESTRIAN-P8-2-APPEARANCE-LEGITIMACY-DESIGN.md — the ped no-cheating spawn/despawn gate

**Status: design + mechanism landed; wiring next.** HOW for Stage **P8-2**, the "load-bearing new piece" of
sub-area integration. The one-screen design is `COORDINATION-pedestrian-x-subarea.md` §2; this doc is the
implementation plan and records what has landed.

## 1. The rule (from the coordination contract §0/§2)

No visible cheating: a ped may **appear or disappear only** where the viewer cannot tell — at the box
**fringe** (a crop-boundary walkable stub), at a **legitimate internal sink** (building entrance / transit
stop / parking board-alight the ped is actually using), or **off-camera**. This is *appearance legitimacy*,
**orthogonal to sim-LOD** (which is about compute/DR, not whether an entity may pop). It mirrors the vehicle
side's `RealismMask` exactly, for walkable space.

Predicate for a would-be spawn/despawn of ped `p` on walkable edge `e`:

```
MaySpawnOrDespawn(e) = isFringe(e) OR isSink(e) OR isOffCamera(e)
```

`isOffCamera(e) = e ∉ visibleWalkableSet` — the direct analogue of `RealismMask.MayPop`.

## 2. Same input signal as the vehicle mask

The host publishes ONE camera visible set per tick. Today it calls `Engine.SetVisibleEdges(visibleLaneEdgeIds)`
for vehicles. For peds it *additionally* yields the **visible walkable-edge set** (sidewalk / `crossing` /
`walkingArea` ids in the frustum) — same camera, mapped to walkable geometry. The camera stays a sim-LOD
interest source (`InterestField`, unchanged); it *also* drives this legitimacy gate. Captured once per host
tick (same snapshot discipline as `RealismMask`).

## 3. Mechanism — `PedSpawnPolicy` (LANDED)

`src/Sim.Pedestrians/PedSpawnPolicy.cs` — an immutable snapshot mirroring `Sim.Core.RealismMask`:

- built from `(fringeEdgeIds, visibleWalkableEdgeIds, sinkEdgeIds?)`;
- `MaySpawnOrDespawn(edgeId)` = off-camera **or** fringe **or** sink;
- **inert by default**: empty visible set → every edge off-camera → fully permissive → every existing ped
  scenario/test/golden unchanged (mirrors the engine's null-mask default). `PedSpawnPolicy.Permissive` is the
  explicit no-camera instance.

Covered by `tests/Sim.Pedestrians.Tests/PedSpawnPolicyTests.cs` (inert-default, on-camera-forbidden,
fringe-allowed, sink-allowed, off-camera-always-allowed). The fringe set is pinned to the real crop by
`SubareaBoxBakeTests` (P8-1): all 48 `manifest.subarea.fringe_edges` (ped=true) exist as baked sidewalks.

## 4. Wiring plan (NEXT — depends on P8-3 edge-aware demand + a point→edge resolver)

The gate is consulted at the two appearance moments; both take an **optional** `PedSpawnPolicy` defaulting to
permissive, so the change is inert until a host supplies a camera + fringe:

- **Spawn (`PedDemand.TrySpawnOne`).** Today origins are `Vec2` points; the gate is edge-keyed. Two paths to
  the origin edge: (a) **P8-3 makes demand edge-aware** (origins land at fringe/door edges — then the edge is
  known directly and fringe spawns pass trivially); (b) for point-based demand, resolve origin → walkable
  edge via the bake (`SumoWalkableSpace` containment → `BakedPolygon.Id` → lane→edge), then gate. A denied
  on-camera spawn **defers** to the next fringe/sink opportunity (no forced pop).
- **Despawn / end-of-route (`PedLodManager`).** A denied on-camera despawn does **not** vanish the ped: it
  (a) routes it to the nearest legitimate sink/fringe, or (b) holds it (low-power) until it walks off-camera.
- **Host plumbing.** The host builds the durable fringe set once from `manifest.subarea.fringe_edges`
  (ped=true) and pushes the per-tick visible walkable set alongside `Engine.SetVisibleEdges`. A small
  `SubareaManifest` reader (fringe extraction — the JSON shape `SubareaBoxBakeTests` already parses) is the
  natural home for the durable half.

**Determinism:** the policy is a pure function of `(edge, fringe, sink, visible)`; the visible set is
snapshotted once per tick. Deferral is deterministic (a denied appearance retries at the next legitimate
opportunity in the ped's own stream), so a seeded run is reproducible regardless of the camera path.

## 5. Task status

- [x] **Mechanism** — `PedSpawnPolicy` + tests (inert-default, all four predicate branches). Parity-inert
      (new type, no consumer yet; gate green).
- [ ] **Spawn wiring** — optional policy on `PedDemand`; origin→edge (via P8-3 edge-aware demand or a
      bake resolver); deny-defers. Inert with a null/permissive policy (bit-identical).
- [ ] **Despawn wiring** — `PedLodManager` end-of-route consults the policy; deny → route-to-sink or hold.
- [ ] **Host plumbing** — `SubareaManifest` fringe reader + the per-tick visible-walkable-set publish.

## 6. Invariants

- Inert-default: a null/permissive policy leaves every committed ped golden byte-identical.
- No `System.Random`; deterministic deferral keyed on the ped's own seeded stream.
- Appearance legitimacy stays **orthogonal to sim-LOD** — the camera remains an `InterestField` LOD source;
  this gate is a separate concern layered on the same visible set.
