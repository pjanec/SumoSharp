# PARTIAL-OCCUPANCY-DESIGN — the myPartialVehicles port (boundary-spanning bodies stay visible)

**Status: DESIGN FOR OWNER REVIEW — no code until signed off.** Owner direction (Aug 2, after
the Entry-53 trace): *"use what SUMO is having. vanilla SUMO still works more correctly than our
implementation."* This is the faithful port, not a spot patch.

Trail: `JUNCTION-REALISM-SESSION-JOURNAL.md` Entries 52–53. Companion instruments already
committed: `LIVECITY-OVERLAP` (class counts), `[veh]` (per-step winning binder).

## 0. The problem, in one trace

A vehicle occupies exactly ONE lane in this engine — the lane under its FRONT bumper. A car
whose front is 1.8 m onto its exit lane has 3.2 m of body hanging back across the junction
boundary, and that tail is INVISIBLE to every same-lane query on the internal lane behind it.
The only thing that can see it is the cross-junction leader walk, which breaks on
`seen > lookahead` — and a STOPPED follower's lookahead (~2.1 m) is routinely shorter than its
distance to the boundary. Measured consequence (Entry 53, `__veh206`): a strict
freeFlow/e-stop alternation ratcheting the follower 0.65 m per cycle INTO the leader's body,
freezing 1.6 m deep; 329 simultaneous such pairs at 4000-car saturation (Entry 52) — the
owner's "too big tolerance to driving through junction blockers".

## 1. What SUMO does (the reference, vendored at /sumo)

- **Two containers per lane** (`MSLane.h:125`): `myVehicles` ("vehicles completely on the
  lane") and `myPartialVehicles` ("vehicles intersecting the lane but with front on another
  lane").
- **The vehicle maintains the back-lane list** — `MSVehicle::myFurtherLanes` +
  `updateFurtherLanes` (MSVehicle.cpp:4829, driven from the enterLaneAtMove path :4595): on a
  boundary hop the lanes the body still covers each get `setPartialOccupation(veh)`
  (MSLane.cpp:359 — just an insert into `myPartialVehicles`); as the tail clears a lane,
  `resetPartialOccupation` (MSLane.cpp:378) removes it.
- **Readers merge both containers** via `AnyVehicleIterator` (MSLane.cpp:165-210), which walks
  `myVehicles` and `myPartialVehicles` in back-position order. getLeaderInfo, the follower
  scans, insertion checks, and the collision check all see partials; per-lane length SUMS
  (`myBruttoVehicleLengthSum`) deliberately do NOT (a body is summed once, on its front lane).

## 2. The port

### 2a. Derivation, not statefulness

SUMO maintains `myFurtherLanes` incrementally. Our neighbor query is REBUILT each step
(`LaneNeighborQuery.Refill`) from frozen per-vehicle state — so partials are DERIVED there,
statelessly: for each vehicle with `Pos < VType.Length`, walk BACKWARD through its route pool
(`LaneSeqPool[LaneSeqStart + LaneSeqIndex - 1, -2, ...]`, the same pool every constraint walks
forward), registering on each prior lane while un-covered body length remains. No new mutable
state, no hop/arrival bookkeeping, nothing to desync — determinism is inherited from the frozen
snapshot exactly like every other Refill read. (Lateral/shadow occupation during a continuous
lane change — SUMO's `myShadowLane`, MSLaneChanger — is a DIFFERENT mechanism and explicitly
out of scope here; it is the second contributor to the queue class, Entry 53.)

### 2b. Registration frame

A partial is registered on a back lane with an EXTRAPOLATED front position:
`pos_on_backlane = backLane.Length + Pos` (front is `Pos` metres past this lane's end). Every
existing gap computation (`leaderBackPos = pos - Length`) is then automatically correct in that
lane's frame — the back lands at `backLane.Length - (Length - Pos)`, which is the physical
truth. No reader changes its arithmetic.

### 2c. Separate container, explicit opt-in (SUMO's own shape)

`LaneNeighborQuery` gets a per-lane PARTIALS list beside the main bucket (mirroring
myVehicles/myPartialVehicles), and readers opt in explicitly:

| Reader | Opt-in | SUMO analog |
| --- | --- | --- |
| `GetLeader` (same-lane follow, incl. the packed SPATIAL branch) | **yes — phase 1** | getLeaderInfo sees partials |
| `GetRearmost` (cross-junction leader walk) | **yes — phase 1** | getFollowers/leader scans see partials |
| `OnLane` count/scan sites: `LaneSpaceTillLastStanding`, `LaneBruttoVehLenSum`, `FindFoeVehicle`, bay-corridor occupancy | **phase 2, audited one by one** | mixed: space walks yes, length sums NO (double-count) |
| Insertion checks (`TryInsertOnLane`) | **phase 2** | isInsertionSuccess sees partials |

Phase 1 alone cures the traced ratchet (GetRearmost) and closes the same-lane blindness
(GetLeader). Phase 2 sites each get their own A/B because several currently "work" only
because they are blind (e.g. a keepClear sum that suddenly counts a hanging tail changes the
box-blocking decision).

### 2d. Gate and defaults

Engine property `PartialOccupancyGate`, wired as `LIVECITY_PARTIALVEH` / `SUMOSHARP_PARTIALVEH`
(EnvGate, both hosts, ENV-GATES row, bench curated list). **Proposed default: ON after the
ladder passes** — this is a correctness port toward SUMO (CLAUDE.md rule 4), and the owner's
direction is explicit. Shipped OFF only if a golden moves (see 3).

## 3. Parity argument and the ladder

The goldens were produced by real SUMO, which HAS partial vehicles. Our 661 goldens are
byte-identical today WITHOUT them — therefore in every golden scenario the partial-visibility
difference never binds (2–5 vehicles, no saturated exits). Expectation: **byte-identical
goldens with the gate ON.** If any golden moves, that scenario had a real latent SUMO-parity
gap that this port just exposed — it gets investigated, not waved through (tolerance iron law).

Ladder (all four surfaces + the new instrument):

1. Full sln suite + `Sim.Bench` hash, gate OFF: byte-identical BY CONSTRUCTION (no reader
   consults the container).
2. Same, gate ON: expected byte-identical (argument above); any diff is investigated first.
3. F3 battery + hour-horizon, gate ON: arrivals not degraded, stalls 0.
4. **The commissioning measurement** — standard Geneva capture (4000 cars, reroute OFF, F3 ON),
   `LIVECITY-OVERLAP` A/B: junction class 329 → target < 100 at t≈1780 (phase 1 removes the
   ratchet, not pre-existing landings); merge class not worse; queue class not worse.
5. D2 re-run (`LIVECITY_RINGBREAK=1`): the frozen queue>2.5m class (492) should collapse — the
   breaker's landing now SEES the queue tail — re-opening the D2 default-ON question afterwards.

## 4. Tasks

- **T1** — `LaneNeighborQuery`: partials container + backward-walk registration in `Refill`
  (and `RefillRegion`; a partial registers on lanes OUTSIDE its region's set — resolved by
  registering during the same pass from the owning vehicle, the container is per-lane so no
  cross-region write conflict beyond what Refill already handles; if region-parallel writes
  collide, partials fall back to a serial post-pass — measured, not assumed).
  Success: unit test — a vehicle at Pos 1.8/len 5 on lane B is returned by lane A's partial
  scan at extrapolated pos `A.Length + 1.8`; gate OFF ⇒ container empty.
- **T2** — `GetLeader` + packed-branch equivalence + `GetRearmost` opt-in behind the gate.
  Success: Entry-53 repro — `__veh206` trace shows NO freeFlow step while the blocker stands
  (the [veh] alternation is gone); goldens byte-identical gate ON (ladder 2).
- **T3** — hosts/gates/docs: EnvGate wiring both hosts, ENV-GATES row, bench curated list.
  Success: EnvGateDocumentationTests green.
- **T4** — the ladder (3/4/5 above), journaled Entry 54 BEFORE/AFTER with these numbers as
  predictions.
- **T5 (phase 2, separate sign-off)** — per-site audit of the OnLane/insertion readers.

## 5. Tracker

- [ ] T1 container + derivation
- [ ] T2 phase-1 readers + ratchet repro + goldens
- [ ] T3 gates/docs
- [ ] T4 ladder + journal
- [ ] T5 phase-2 audit (needs its own go)
