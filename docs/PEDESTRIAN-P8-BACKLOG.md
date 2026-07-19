# PEDESTRIAN-P8-BACKLOG.md — sub-area integration: remaining & potential work (return-to-later)

Single index of what is **deferred** or **not yet built** in the pedestrian sub-area / distribution / recording
tracks, so any of it can be picked back up without re-deriving context. Each item names WHY it is parked and
WHAT unblocks it. Living doc — tick/move items into the relevant design doc's task list when they are taken up.

Status of the shipped work is in `PEDESTRIAN-TRACKER.md` Stage P8 and the per-stage design docs; this file is
only the *not-done* remainder.

## Deferred — blocked on a seam or another session

- **P8-2 spawn deny-defer gate** (`PEDESTRIAN-P8-2-APPEARANCE-LEGITIMACY-DESIGN.md` §4/§5 "Spawn wiring").
  *Mechanism (`PedSpawnPolicy`) has landed*; the wiring into `PedDemand.TrySpawnOne` is **held**.
  *Why parked:* on the recorded sub-area path it is a **no-op** — P8-3 endpoints are fringe/POI edges, so every
  spawn is already appearance-legitimate by construction, and POI edges *are* the legitimate internal sinks.
  The gate only bites for arbitrary point-based demand whose origin resolves to an on-camera open sidewalk.
  *Unblocks when:* a **live host publishes a per-tick visible-walkable-edge set** (the ped analogue of
  `Engine.SetVisibleEdges`). Until a real camera exists there is nothing for the gate to deny.
  *Shape when taken up:* optional `PedSpawnPolicy` on `PedDemandConfig` (default `Permissive` → inert,
  bit-identical); a denied on-camera origin **defers** to the ped's next scheduled spawn (deterministic, keyed
  on the ped's own stream); for the weighted path the origin edge is already known (`Endpoint.EdgeId`), for the
  point path resolve origin→edge via the bake (`SumoWalkableSpace` containment → `BakedPolygon.Id` → edge).

- **P8-2 despawn route-to-sink / hold** (`PEDESTRIAN-P8-2…` §4/§5 "Despawn wiring").
  *Why parked:* the weighted demand already **despawns at legitimate destinations** (POI/fringe endpoints), so a
  gated despawn only matters for a **forced / jam despawn**, which the ped system does not have yet. Building
  route-to-sink now would be speculative machinery with no caller.
  *Unblocks when:* a forced/jam-despawn path exists (or a host camera makes on-camera arrival possible) **and**
  the visible-walkable set is published. *Shape:* `PedLodManager` end-of-route consults the policy; deny →
  re-route to nearest sink/fringe, else hold low-power until off-camera.

- **P8-2 host plumbing** (`PEDESTRIAN-P8-2…` §4). The **durable half is done** (`SubareaManifest` reads the
  fringe set). The **per-tick half** — host builds the visible walkable-edge set (sidewalk/crossing/walkingArea
  ids in frustum) and publishes it alongside `Engine.SetVisibleEdges` — is **not built**; it belongs in a real
  host/viewer integration, not the offline engine. Same trigger as the two gate items above.

- **P8-4b dynamic per-crossing throughput guard** (`PEDESTRIAN-P8-4-DENSITY-DESIGN.md` §3).
  The **static** guard is live (the P8-4a dial is clamped to a LoS-C safe ceiling). The **dynamic** guard —
  throttle/deny spawns feeding a *specific* crossing already at its vehicle-calibrated discharge capacity — is
  deferred. *Why parked:* needs the **live vehicle-flow seam at the crossing** (calibrated car side,
  **SumoData-owned**) and the **P4 vehicle-yields-at-crossing** engine behaviour (watch item,
  `COORDINATION-pedestrian-x-subarea.md` §7). *Unblocks when:* P4 lands and the crossing headroom signal is
  exposed; coordinate re-calibration with the SumoData session at the same time.

- **P8-5 scenario/manifest slot-in + shared car+ped FCD replay** — **owned by the sub-area session.**
  *Ped side LANDED:* `SubareaFcdRecorder` + `PersonFcdWriter` emit the `<person>` FCD stream (the P8-3×P8-4
  recorder), driven by `Sim.Viz --ped-subarea-fcd <out> [--dial d] [--seconds s] [--box dir]`. **Remaining
  (theirs):** merge the person rows with the vehicle FCD into one `Sim.Viz` timestep stream, the manifest
  slot-in, and the shared car+ped **edge/coordinate contract** (how car + ped rows co-exist in one timestep).
  Stay off it unless asked. Our recorder's person rows are world-frame x/y/angle/speed so they drop into that
  stream directly; `edge`/`pos` are omitted pending that shared edge space (see the person-FCD item above).

## Potential / opportunistic (not required, no blocker)

- **Real-OSM box** re-verification: re-run P8-1/P8-3/P8-4 against a genuine cropped OSM box (not the synthetic
  handoff crop) when one is available in a SumoData-access context. The pipeline is ready (`deduce_pois.py`
  path); only a real box is missing. Would validate POI weights and `unreachableSkips` on non-synthetic
  geometry.
- **`SubareaDemand` origin≠destination fairness:** the zero-length guard advances to the next endpoint index on
  a coincident O==D draw (deterministic, rare). If a future box shows measurable bias from this, switch to a
  re-draw on the ped's own stream instead of an index bump. No evidence it matters yet.
- **`PedDensityKnob` mean-trip auto-measure:** `MeanTripSeconds` is a caller-supplied constant (default 90 s).
  A box could measure its own mean trip time from a warm-up run and feed it back, tightening the rate→cap
  relationship. Cosmetic; the cap is the load-bearing safety number, not the rate.
- **Person FCD edge/pos attribution:** the recorder emits world-frame `x/y/angle/speed` (+`type`), omitting
  SUMO's `edge`/`pos` (needs a world→edge resolver mid-route and a shared car+ped edge space). Add them if a
  consumer needs edge-keyed person rows — folds naturally into the P8-5 shared edge space.

## Cross-track deferrals (recorded elsewhere, indexed here)

- **P6-2 phase-2 SoA-reorder** (OrcaCrowd region decomposition) — DEFERRED; full context and the return-to
  banner + P6-2-6 task are in `PEDESTRIAN-P6-2-REGION-DESIGN.md`. Region-task parallelism is bit-identical but
  fell short of the throughput target (best ~1.08× vs 1.4× goal); the SoA-reorder is the next lever if/when the
  crowd throughput is actually needed.
- **Distribution across machines** (vehicle-type split and spatial-topology split over DDS) — feasibility only,
  `docs/DISTRIBUTED-COUPLING-FEASIBILITY.md`. Guardrails documented so nothing precludes it; no implementation
  scheduled.
- **Recordability / playback** — feasibility only, `docs/RECORDABILITY-PLAYBACK-FEASIBILITY.md` (recording the
  DDS replication surface as the playback format). The P8-3×P8-4 person-FCD recorder is a first concrete step
  in that direction for the sub-area path.
