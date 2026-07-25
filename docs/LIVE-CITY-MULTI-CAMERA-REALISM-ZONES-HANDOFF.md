# HANDOFF — multiple / large / overlapping camera-frustum realism zones

**Audience:** a separate SumoSharp coding session that will own the **multi-camera realism-zone** capability.
**Author:** the "arbitrary SUMO road-net import" session (see `LIVE-CITY-ARBITRARY-NET-{SCOPING,DESIGN,TASKS,
TRACKER}.md`). **Nature:** a scoped handoff — goal, requirements, verified facts, the exact seams left for
you, the work-split, and open questions. **It is NOT your design.** Per `CLAUDE.md`, do your own design-first
trio (`design -> tasks -> tracker`) in `docs/`, get owner agreement, then implement.

Facts marked **[verified]** were confirmed against the source this session (file:line refs given). Treat
everything else as a lead.

---

## 1. Goal

Let `LiveCitySim` drive the high-realism (full-ORCA peds + cooperative-LC cars) LOD from **one or more
camera-frustum "realism zones"** that are **movable, potentially large** (a distant or flat-angle camera sees
a big area), and **often overlapping** (two or more cameras watching the same scene), while keeping
performance bounded and determinism/parity intact.

Today `LiveCitySim` supports exactly **one** realism zone (`SetLcRealismZone(x,y,r)`); this work generalizes
that to N zones.

## 2. Why this is separable from arbitrary-net import

The arbitrary-net feature ships and meets its definition of done with a **single** zone: import a net → peds
route on the ped graph, cross at crosswalks, and (high-power) avoid nearby cars, all within one realism zone.
Multi-camera is a consumer-driven capability that rides on top of the same seams. Sequencing it after keeps
each design coherent. See `LIVE-CITY-ARBITRARY-NET-DESIGN.md` §12 for the boundary from the other side.

## 3. What the arbitrary-net session delivers to you (the seams) [verified]

- **Ped promotion is already multi-source in the engine.** `InterestField` is a first-class multi-source,
  grid-indexed field with **any-source-wins** semantics — a ped promotes if inside ANY source's
  `PromoteRadius`, demotes only when outside EVERY source's `DemoteRadius`; proper hysteresis; **no source
  cap** (`src/Sim.Pedestrians/Lod/InterestField.cs:59,209-240`; consumed in `PedLodManager.Step`
  `:334-394`). `InterestSource.Position` is mutable; radii are readonly (rebuild to resize)
  (`InterestSource.cs`). **The only thing missing is that `LiveCitySim` registers a single `_orcaSource`**
  (`LiveCitySim.cs:191-194,367-388`).
- **ORCA agents are already spatially partitioned** — spatial hash + region decomposition + parallel step,
  all bit-identical to serial (`src/Sim.Core/Orca/OrcaCrowd.cs`; enabled via `PedLodManager` `:115-138,163`).
- **Cars are already spatially partitioned** — `Engine.RegionPlan`/`RegionGrid` (G×G grid, one parallel task
  per region, bit-identical; `src/Sim.Core/Engine.cs:538-635`). The arbitrary-net session **enables** this
  for large nets (default on for `ForDataset`).
- **The ped-avoids-car disc feed exists and is zone-bounded** — `LiveCitySim.Step` projects live vehicles to
  `WorldDisc[]` and passes them as `externalEntities`, bounded to cars near the **single** realism zone
  (design §5.8). It is left behind a small helper so you can swap the single zone for the **zone-set union**.
- **The single-zone surface is unchanged in shape** — `_lcZone*` scalars, `SetLcRealismZone`, one
  `InterestSource`. You generalize these; nothing else in the arbitrary-net work touches them.

## 4. What you own (NOT built by the arbitrary-net session)

- **R-A — N ped promotion sources.** Replace `LiveCitySim`'s single `_orcaSource` with a managed set of
  `InterestSource`s (one per active camera zone). The engine already unions them (§3); this is a
  `LiveCitySim` bookkeeping change (add/move/remove sources as cameras appear/move/vanish).
- **R-B — N-zone car LC-realism.** The car lane-change realism test is a **single circle** today
  (`IsLowRealismLaneChangePos` + `_lcZoneX/_lcZoneY/_lcZoneR`, `LiveCitySim.cs:357-359,406-416,477-486`).
  Generalize to "high-realism iff inside ANY of N zones" over an N-zone structure.
- **R-C — `SetLcRealismZones([...])` consumer API.** A multi-zone replacement for the single
  `SetLcRealismZone(x,y,r)`, keeping the old single-zone method working (delegate to a 1-element set) so the
  current consumer keeps compiling. Coordinate with the BIG/Spectacle consumer contract.
- **R-D — Re-point the C5 disc-feed bound at the zone-set union.** The arbitrary-net C5 bounds the disc feed
  at the single zone; swap in the union of the active zone set (a one-line change at the seam C5 leaves).
- **R-E — (IF profiling requires) a spatial index over external discs in `OrcaCrowd`.** Today each agent
  scans **ALL** external discs every step — `O(agents × #discs)`, no index, re-copied per step
  (`OrcaCrowd.cs:731-745,799-808`; the range cutoff prunes constraints, not the scan). A large frustum with a
  dense car field is where this bites. A spatial index (mirroring the agent spatial hash) makes it
  `O(nearby)`. **It must be bit-identical** — the range cutoff already fixes which discs become constraints;
  the index only skips scanning far ones. This is the one item that touches `Sim.Core`; treat it as a gated,
  parity-proven optimization, not a behavior change. Verify it is actually needed (profile) before building.

## 5. Hard constraints (inherited)

- **Determinism / parity iron law:** `dotnet test tests/Sim.ParityTests` stays **byte-identical** (count
  tracks the base — 657/4 after realism-1); the
  `Sim.Bench` hash is unchanged. Parity/bench drive `Engine` directly (never `LiveCitySim`), so zone work is
  invisible to them — *provided* the `OrcaCrowd` disc index (R-E), if built, is bit-identical.
- **No `System.Random`** — zone add/move/remove and any-zone tests are deterministic and order-independent.
- **Demo stays byte-identical** — `ForRepoRoot` keeps a single zone, disc feed off, `RegionPlan` off; the
  pinned dense-flow liveness + scene tests must not move.
- **netstandard2.1** across the consume chain; the consumer contract
  (`VehicleSource`/`PedSource`/`LocalLanes`/`Time`/`Step`/`Dispose` + the realism-zone setter) is preserved.
- **No `Sim.Core` motion-math edits** — R-E is an index, not a math change.

## 6. Suggestions (non-binding)

- Model the zone set as a small list of `(centre, radius)` with a stable id per camera; `SetLcRealismZones`
  diffs the incoming set against the live `InterestSource`s (move in place where a radius is unchanged,
  Remove+Register where it changed — mirroring the current single-zone `SetLcRealismZone` logic).
- Keep the ped `InterestSource` radii and the car LC-zone radii in lockstep per camera (as the single-zone
  code does today: ped promote radius follows the LC zone radius).
- For R-E, profile first on a realistic large frustum; the zone-bounded feed (R-D) may already keep `#discs`
  small enough that no index is needed.

## 7. Open questions for your session

1. How many simultaneous cameras must be supported, and is there a sane upper bound to size the zone set for?
2. When zones overlap heavily, do you cap total promoted-ped count (perf ceiling) or honor every zone
   regardless (the current single-zone owner stance was "honor the radius, no matter perf")?
3. Does the BIG/Spectacle consumer want per-camera zone ids back (to tint/label), or just the union effect?
4. Is R-E (disc index) actually needed at your target camera sizes, or does R-D suffice? (Profile.)

## 8. Where to look

- Seams you build on: `src/Sim.Pedestrians/Lod/{InterestField.cs, InterestSource.cs, PedLodManager.cs}`,
  `src/Sim.Core/Orca/OrcaCrowd.cs`, `src/Sim.Core/Engine.cs` (`RegionPlan`/`RegionGrid`).
- Single-zone surface you generalize: `src/Sim.LiveCity/LiveCitySim.cs`
  (`_orcaSource`/`_orcaSourceId` ctor `:191-194`, `SetLcRealismZone` `:367-388`, `_lcZone*` `:357-359`,
  `IsLowRealismLaneChangePos` `:406-416`, per-step classify `:477-486`, the C5 disc feed at `:451`).
- The boundary from the other side: `LIVE-CITY-ARBITRARY-NET-DESIGN.md` §5.8, §5.9, §12.
- Prior art on the single-zone camera model: `docs/LIVE-CITY-CAMERA-REALISM-ZONE-DESIGN.md`,
  `docs/LIVE-CITY-15-PER-AREA-LOD-DESIGN.md`.

## 9. Definition of done (suggested)

`LiveCitySim` accepts N movable, possibly-large, overlapping realism zones; peds promote to ORCA inside the
union, cars run high-realism LC inside the union; the ped-avoids-car feed is bounded to the union; the demo
(single zone) is byte-identical; parity byte-identical (657/4) + bench hash intact; consumer contract preserved; any
`OrcaCrowd` disc index (if built) proven bit-identical.
