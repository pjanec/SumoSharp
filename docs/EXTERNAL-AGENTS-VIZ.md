# External Agents — simulating & visualizing (viz-session guide)

This is the integration guide for driving **external agents** (pedestrians, navmesh/RVO crowd agents,
live detections) into the traffic engine and visualizing them. External agents are **not** SUMO
vehicles and are not in any golden — they are the "live reactivity" seam (DESIGN.md *Two futures*,
Group B). You (the viz/crowd layer) create and move them; the SUMO cars **react** (stop, follow,
swerve, spill to the next lane, yield at junctions, reroute). Everything is deterministic and
parallel-safe, and **completely inert when no agent is present** — the committed parity suite stays
byte-identical and the `Sim.Bench` determinism hash is unchanged.

---

## 1. The model: `ExternalObstacle`

An agent is one `ExternalObstacle` (`src/Sim.Core/ExternalObstacle.cs`), keyed by a caller-held
`ObstacleHandle` (`src/Sim.Core/ObstacleHandle.cs`, a generational slot handle — see §2), positioned
**lane-relative** (the engine's source of truth) — never in world x/y:

| Field | Meaning | Units / convention |
|---|---|---|
| `Id` | legacy string field, always empty for an agent added through this API — the caller's actual key is the `ObstacleHandle` `AddObstacle`/`AddMovingObstacle` returns (see §2) | string |
| `LaneId` | the lane the agent occupies (e.g. `"e0_0"`, an internal `":C_3_0"`, …) | SUMO lane id |
| `FrontPos` | the agent's **downstream (front) edge** along the lane | metres from lane start |
| `Length` | along-lane extent (a person ≈ 0.5 m; a stalled car = its length) | metres |
| `Width` | **lateral** extent. `0` = **full-lane block** (car stops dead) | metres |
| `LatPos` | lateral **centre**, lane-centre-relative, **positive = LEFT of travel** | metres |
| `Speed` | along-lane velocity (a walking ped, a rolling car) | m/s |
| `LatSpeed` | **lateral** velocity (a ped *lunging* across the lane), positive = LEFT | m/s |
| `MaxDecel` | braking capability (only used when `Speed != 0`) | m/s² |
| `StartTime`,`EndTime` | active window `[StartTime, EndTime)`; both default "always" | seconds |

The agent's back edge (what a follower must not cross) is `FrontPos - Length`. The lateral footprint
is `[LatPos - Width/2, LatPos + Width/2]`.

**Coordinate conventions (must match, or collisions read wrong):**
- **Along-lane** `FrontPos` grows in the direction of travel, exactly like a vehicle's `pos`.
- **Lateral** `LatPos`/`LatSpeed` are metres from the lane centreline; **+ is LEFT of travel**. A lane
  of width `W` spans `[-W/2, +W/2]`. A pedestrian on the **right** sidewalk enters near `-W/2`; on the
  **left**, near `+W/2`. This is the same convention as the cars' own `Kinematics.LatOffset`.

---

## 2. The API (`IEngine` / `Engine`)

The API is **handle-based**, not string-keyed: `AddObstacle`/`AddMovingObstacle` take an `int
laneHandle` (not a lane-id string) and no caller-supplied id — they **return** an `ObstacleHandle`
(`src/Sim.Core/ObstacleHandle.cs`, a generational `{Index, Generation}` slot reference) that the caller
must retain and pass back into every subsequent `UpdateObstacle`/`RemoveObstacle` call for that agent.
Resolve a lane's string id to its `laneHandle` once, at setup, via `engine.GetLane(laneId)` — the
per-step path never touches a string:

```csharp
// Once at setup: resolve the lane string id to its int handle.
int laneHandle = engine.GetLane(laneId);

// Stationary agent (a pedestrian standing in the lane). latPos/width optional (default = full lane).
// Retain the returned handle -- it is the only way to Update/Remove this agent.
ObstacleHandle h = engine.AddObstacle(laneHandle, frontPos, length,
                   startTime = -inf, endTime = +inf, latPos = 0, width = 0, latSpeed = 0);

// Moving agent — the engine dead-reckons FrontPos += Speed*dt and LatPos += LatSpeed*dt each step
// between your corrections. speed = along-lane, latSpeed = lateral (lunge), maxDecel = its braking.
ObstacleHandle h = engine.AddMovingObstacle(laneHandle, frontPos, length, speed, maxDecel,
                         startTime = -inf, endTime = +inf, latPos = 0, width = 0, latSpeed = 0);

// Per-step correction from your crowd sim (preferred over relying on dead-reckoning):
engine.UpdateObstacle(h, frontPos, speed);                          // longitudinal only
engine.UpdateObstacle(h, frontPos, speed, latPos);                  // + lateral position
engine.UpdateObstacle(h, frontPos, speed, latPos, latSpeed);        // + lateral velocity (lunge)

engine.RemoveObstacle(h);   // agent left the roadway -> cars resume
engine.ClearObstacles();
```

**Contract.** `AddObstacle`/`AddMovingObstacle` always create a new slot and hand back a fresh
`ObstacleHandle` — there is no add-or-replace-by-id; the caller owns the handle's lifetime and is
responsible for calling `RemoveObstacle` when the agent leaves. `UpdateObstacle`/`RemoveObstacle` on a
stale or already-removed handle (generation mismatch) are inert no-ops rather than errors — the
"inert-when-absent" contract extends to the handle itself, not just to "no agent present." Call
`UpdateObstacle` once per step with your crowd layer's fresh reading; between corrections the engine
extrapolates the reported velocities (it does **not** simulate a walker). An agent only moves while it
is **active** (`[StartTime, EndTime)`) — before `StartTime` it stays at its added position, so
`startTime` gives you a clean "jumps in at t=T".

**Threading.** Call these from the Input phase (before `Run`'s step, or between steps). The engine
freezes obstacle positions once per step before planning, so reads are consistent and order-independent.

---

## 3. What the cars do (behaviors already wired)

| Situation | Behavior | Where |
|---|---|---|
| Agent ahead **in the car's path**, car **can stop** | brakes to a Krauss gap behind it (B1) | `ObstacleConstraint` |
| Walking agent ahead | follows it at a safe gap (B5-i) | `ObstacleConstraint` |
| Agent in path, **too close to stop**, room in lane | **swerves within the lane** and passes (B6) | `ComputeLateralEvasion` |
| Agent fills the lane, **adjacent lane safe** | **spills into the neighbour lane** (gap-checked) and passes | `ComputeLateralEvasion` + `NeighborSpillSafe` |
| Agent fills the lane, nowhere safe | brakes to a stop behind it | fallback |
| Agent **lunging** laterally (faster than the 2 m/s swerve) | **dodges to the side it is vacating** (predictive) | `PredictedLatPos` |
| Agent at a junction internal lane | car **yields** at the junction (B5-iii) | `JunctionYieldConstraint` |
| Agent blocking the target of a lane change | the lane change is **vetoed** (B5-ii) | `TargetLaneBlockedByObstacle` |
| Agent parked on a **future** route edge | car **reroutes** around it after a threshold (B3) | reroute pass |

The swerve engages **only** when braking alone cannot avoid the agent (the "sudden jump" case); a
stoppable agent is simply braked for. Two tunables in `Engine.cs`: `SwerveMaxLateralSpeed` (2.0 m/s,
how fast the car slides sideways) and `SwerveLateralGap` (0.5 m, clearance kept from the agent);
`SwervePredictionHorizon` (4 s) caps how far a lunge is extrapolated.

---

## 4. Visualizing the agent

The engine renders the **cars** for you: a car's emitted `x/y` (in the `TrajectorySet` / FCD) already
includes its lateral offset, so a swerve shows up as the car's `y` drifting off its lane centre — no
work needed on the viz side to see cars dodge.

The engine does **not** emit the agents (they aren't vehicles), so **the viz draws them from its own
crowd data**. To place an agent in world space *consistently with the engine's collision model*, use
the exact same lane→world transform the engine uses for vehicles
(`Sim.Ingest.LaneGeometry.PositionAtOffset`):

```csharp
// world position of the agent's centre:
var laneShape = network.LanesById[agent.LaneId].Shape;          // the lane polyline
var centreAlong = agent.FrontPos - agent.Length / 2.0;          // agent centre along the lane
var (x, y, angleDeg) = LaneGeometry.PositionAtOffset(laneShape, centreAlong, agent.LatPos);
// draw a box of (Length x Width) centred at (x,y), heading angleDeg (navi degrees: 0=N, CW)
```

`PositionAtOffset(shape, along, latOffset)` walks the lane polyline to `along`, then shifts
perpendicular (left-normal) by `latOffset` — the identical perpendicular convention the cars use, so
the drawn agent box and the cars' footprints line up exactly with the overlap math that decides
stop/swerve. (If you'd rather the engine emit agents into the export stream for replay, that's a small
addition to `EmitTrajectory` — ask and it can be wired.)

---

## 5. Worked example — pedestrian jumps off the sidewalk

```csharp
// Resolve the lane once at setup:
int laneHandle = engine.GetLane("e0_0");

// Car cruising on lane "e0_0" (width W). A pedestrian jumps in at t=15, ~8 m ahead, from the RIGHT
// sidewalk, and lunges LEFT across the lane at 2.5 m/s (faster than the car's 2 m/s swerve):
ObstacleHandle ped1 = engine.AddMovingObstacle(
    laneHandle: laneHandle,
    frontPos: carPosAtT15 + 8.0, length: 0.5,
    speed: 0.0, maxDecel: 2.0,          // not walking forward, but braking-capable
    startTime: 15.0, endTime: double.PositiveInfinity,
    latPos: -1.0,                        // starts on the right
    width: 0.8,
    latSpeed: 2.5);                      // lunging LEFT

// each subsequent step, feed your crowd sim's real reading:
engine.UpdateObstacle(ped1, frontPos: pedFront, speed: 0.0, latPos: pedLat, latSpeed: pedLatSpeed);

// when the pedestrian reaches the far sidewalk:
engine.RemoveObstacle(ped1);
```

The car brakes hard and swerves to the side the pedestrian is *vacating*, clears it (a 0.5 m gap is
kept), then recentres and re-accelerates — all visible in the car's emitted `x/y`.

---

## 6. Guarantees & scope

- **Deterministic & parallel-safe.** All reactions read only the frozen start-of-step snapshot; no
  `System.Random`; results are independent of vehicle/thread order.
- **Inert when absent.** With no agent injected, every reaction path is skipped — committed parity
  scenarios are byte-identical and the `Sim.Bench` hash is unchanged.
- **Behavioral, not SUMO-parity.** There is no golden for "an agent appeared at t=12"; these are
  validated by property tests (`RungB1/B5/B6…LateralEvasionTests`): no-overlap, resumes-on-clear,
  correct dodge side, gap-safe spill.
- **Not (yet) wired:** a *committed* lane-change-to-overtake (the swerve is a transient dodge that
  recentres); agents are not emitted into the export stream (the viz draws them from its own data).
  Both are small follow-ups — ask if you need them.
