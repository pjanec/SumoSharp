# COORDINATION — pedestrian/crosswalk work × high-density traffic work

**Purpose:** two parallel workstreams touch the same core files. This note maps the collision surface so
the **pedestrian/crosswalk session** and the **high-density traffic session**
(`claude/sumosharp-high-density-cont-cpi2q4`) do not step on each other (merge conflicts, or one silently
breaking the other's parity goldens). Written by the high-density session, 2026-07-17.

## 1. Where each workstream lives (low overlap)

- **High-density traffic (this session):** vehicle car-following + **lane-change model** + **junction
  right-of-way** + insertion/teleport/rerouting, all SUMO-parity. Main file: `src/Sim.Core/Engine.cs`
  (car-following, `DecideSpeedGainChanges`/`ApplyKeepRightDecision`, `JunctionYieldConstraint`,
  `CheckJamTeleports`, `InsertDepartingVehicles`), plus `src/Sim.Ingest/NetworkModel.cs` (connections,
  best-lanes), and `tests/Sim.ParityTests/RungHD*`, `scenarios/4[1-9]-*`/`50-*`.
- **Pedestrians/crosswalks (other session):** SUMO-style `<crossing>`/walkingarea parsing + pedestrian
  movement + **vehicle-yields-to-pedestrian at crossings**. Today there is **NO pedestrian-crossing
  logic in the vehicle engine** — the only "crossing" code in `Engine.cs` is **rail** crossings
  (`_railCrossing*`, `BuildRailCrossingInfo`), which is unrelated. Pedestrian/crowd infra that DOES exist
  is a separate subsystem: `src/Sim.Core/{Orca,Mixed,Unified,Bridge}/`, `src/Sim.Evac/`,
  `src/Sim.ExtDemo/` (ORCA crowds, mixed traffic, external agents) — the high-density session does NOT
  touch these.

## 2. The COLLISION SURFACE (shared code — coordinate here)

Three seams where crosswalk work and high-density work meet in the SAME functions:

1. **Junction right-of-way — `Engine.cs`: `JunctionYieldConstraint` / `FindFoeVehicle` /
   `CrossJunctionLeaderConstraint` / the willPass pre-pass (`ComputeWillPass`) / `MSLink` conflict
   machinery.** A vehicle yielding to a pedestrian on a `<crossing>` hooks in HERE (SUMO models a
   crossing as a junction link the vehicle yields to). This is the hottest overlap: the high-density
   session actively edits this region (willPass, P2G-3 cross-junction leader). **Protocol:** pedestrian
   crossing-yield should enter as its OWN constraint/foe-source ANDed into the vPos min (like the
   existing `ObstacleConstraint`/`CrossJunctionLeaderConstraint` do), NOT by rewriting
   `JunctionYieldConstraint`'s vehicle-foe logic. That keeps the two additive and conflict-free.

2. **Lane-change decision — `Engine.cs`: `DecideSpeedGainForVehicle`.** SUMO calls
   `adaptSpeedToPedestrians(lane, thisLaneVSafe)` / `adaptSpeedToPedestrians(neighLane, neighLaneVSafe)`
   right here (`MSLCM_LC2013.cpp:1678-1679`) — a pedestrian on a lane lowers that lane's LC-incentive
   speed. If the pedestrian session wants that fidelity it edits the SAME method the high-density session
   is tuning (P2G-3). **Protocol:** add it as a small, clearly-commented `adaptSpeedToPedestrians`-style
   MIN applied to the already-computed `thisLaneVSafe`/`neighLaneVSafe`, inert when no pedestrian is on
   the lane (default), so it composes with the high-density LC changes rather than colliding.

3. **Net ingest — `src/Sim.Ingest/NetworkModel.cs` + the net parser.** `<crossing>`/walkingarea lanes are
   a distinct lane type. The high-density best-lanes/continuation code (`ComputeBestLanes`,
   `ResolveLaneSequence*`) must SKIP crossing/walkingarea lanes when building a VEHICLE route (they are
   not driveable by cars). **Protocol:** pedestrian ingest adds the crossing lane type + a
   `Lane.IsPedestrianOnly`-style flag; the high-density route/best-lanes code already filters by
   `allowsVehicleClass` semantics, so tag crossings so those filters exclude them — do not change the
   existing vehicle-route resolution shape.

## 3. What each side should rely on from the other (stable seams)

- **Pedestrian session needs from us:** a stable way to (a) enumerate the vehicles approaching/at a
  junction link (for pedestrian gap-acceptance) and (b) impose a yield on a vehicle at a crossing. Reuse
  the existing additive-constraint pattern (`vPos = Math.Min(vPos, XConstraint(...))`) and the
  `ExternalObstacle` machinery (already a per-lane speed constraint a non-vehicle can impose) rather than
  a new bespoke hook. `Engine.AddObstacle`/the obstacle store may already be enough to model a
  pedestrian-in-crossing as a transient obstacle — check before adding new engine surface.
- **High-density session needs from pedestrians:** the crossing-yield must be **inert when no pedestrian
  is present** so all committed vehicle goldens stay byte-identical, and must not reorder or mutate the
  existing vehicle-foe resolution. New pedestrian scenarios/goldens should be their own
  `scenarios/`/tests, not edits to the committed vehicle-parity ones.

## 4. Practical git protocol
- Both on separate feature branches; rebase/merge `main` frequently.
- If both must edit `Engine.cs` junction RoW or `DecideSpeedGainForVehicle` in the same window,
  coordinate order (land one, rebase the other) — these are the two functions most likely to conflict.
- Run the FULL `dotnet test` suite before every push (both suites must stay green): a crossing change
  that shifts a vehicle golden, or an LC change that shifts a pedestrian golden, is a real regression.
