# JUNCTION-FOE-LANE — design: port MSLink foe-lane link-leaders (the Geneva overlap/pass-through fix)

Status: **DESIGN — awaiting owner sign-off before any implementation** (CLAUDE.md ways-of-working).
Requirements/WHAT: the owner's July 31 Geneva-terrain report, reproduced and quantified in journal
**Entry 35** (read it first — this doc does not restate the measurements). Tasks:
`JUNCTION-FOE-LANE-TASKS.md`. Tracker: `JUNCTION-FOE-LANE-TRACKER.md`.

## 1. The defect, in one sentence

Once a vehicle is INSIDE a junction, no plan-phase constraint reads the occupancy of internal
lanes that geometrically conflict with its path — neither CROSSING lanes (a stopped left-turner is
driven through: Entry 35's 17 stopXmove pair-steps, SUMO 0) nor SAME-TARGET lanes (two movements
land overlapped on the shared arrival lane: 22/22 deep rear-end onsets, SUMO 0).

What we DO have, and why each misses this: `CrossJunctionLeaderConstraint` walks EGO'S OWN
downstream lanes only; the `[merge]` arm follows the ARRIVAL lane's rearmost, not the other
internal lane converging into it; `InternalJunctionAdmissionConstraint` (binder 14) gates a cont
BAY's second stage only; `JunctionYieldConstraint`/willPass gates APPROACHING foes at entry, not
foes already on conflicting internal lanes; keepclear reads downstream space past the junction.

## 2. SUMO's mechanism (the port source — all in the vendored tree)

- **`MSLink::setRequestInformation`** (`MSLink.cpp`): at net build, each link with an internal
  lane precomputes `myFoeLanes` — the internal lanes of conflicting links — and `myConflicts`
  (`ConflictInfo` per foe lane: the conflict-zone geometry as `lengthBehindCrossing` — distance
  from the FOE lane's end back past the conflict point — paired with ego-side distances from
  `getLengthsBeforeCrossing`). Both CROSSING and SAME-TARGET (merging) foe lanes are included;
  same-target conflicts get the special `CONFLICT_DUMMY_MERGE` handling.
- **`MSVehicle::planMoveInternal`** (`MSVehicle.cpp:3403`): while iterating upcoming links,
  `link->getLeaderInfo(this, seen, ...)` returns `LinkLeaders` — vehicles currently ON foe lanes,
  each with a gap measured from ego's position to the CONFLICT POINT (minGap-adjusted,
  `:7386-7388`) — and ego folds them through `adaptToLeader` into vSafe. A stopped crossing foe ⇒
  ego stops before the conflict zone. A same-target foe ⇒ ego follows it at the merge gap, so two
  movements can never land co-located.
- **Tie-breaking / deadlock avoidance**: for mutual conflicts the leader is decided by
  `MSVehicle::isLeader` (ET/CET entry-time ordering + speed + id tie-break) — ALREADY PORTED
  (F3 T2.3, `IsLeader`/`ResponseFor`, flag-gated) — and by the link response matrix.
- **MUST VERIFY IN SOURCE before coding (do not assume)**: whether `myFoeLanes` derives from the
  geometric `foes` bitstring (prioritized links also brake for foes physically in the zone) or the
  `response` matrix (only yielders brake). This decides whether a prioritized straight brakes for
  a stalled turner — the exact owner scenario — and the answer must come from
  `setRequestInformation`, not from reasoning (score in this workstream: ~0-for-18).

## 3. The port shape (one new plan-phase arm + ingest geometry)

1. **Ingest (parity-inert alone)**: per (entry link, foe internal lane) pair, compute the conflict
   geometry from the net's internal-lane SHAPES (polyline intersection — SUMO's own method, so the
   artefact ladder permits it): ego-side distance-to-conflict-start and foe-side conflict interval.
   Reuses F3 T2.1's `LinkIndexByInternalLane` / `EntryConnectionByLink` and the junction `foes`
   bitstrings already parsed for willPass. Same-target pairs: conflict = the merge point (lane
   ends). Storage sized like SUMO's (per link, small vectors).
2. **Plan-phase arm** (new constraint or a `CrossJunctionLeaderConstraint` sibling), gated
   `SUMOSHARP_FOELANE` (EnvGate, default OFF until measured): for ego's current/next link(s)
   within the same lookahead the cjl walk uses, enumerate foe internal lanes; for each occupant
   in the frozen `LaneNeighborQuery.OnLane` snapshot, compute ego's distance to the conflict
   point; if the foe occupies (or, per the verified SUMO rule, will not have cleared) the
   conflict interval, fold `MaximumSafeStopSpeed` to the conflict point (crossing) or
   `MaximumSafeFollowSpeed` at the merge gap (same-target) into the plan min-fold.
3. **Mutual-yield break**: apply the arm only in the direction the verified SUMO rule says; where
   it is mutual, break with the ported `IsLeader` (F3) — its ET/CET inputs are already stamped on
   every vehicle. A symmetric both-brake wedge is the known hazard class (a prior gate once wedged
   four cars 4890 steps): the battery's stuckDwell column is the specific gate against it.
4. **Determinism**: all reads are the frozen snapshot + ego state; output is ego's own vSafe fold.
   Repeat-hash (≥4 runs + serial) is a mandatory gate (Entry 30 discipline).

## 4. Acceptance gates (Entry 35's instrument, both arms, plus the standing set)

1. `classify-junction-overlaps.py` on city-organic-L2 (1000 steps) and city-mixed-1k (1200):
   stopXmove pair-steps → ~0 (SUMO 0); deep rear-end onsets → ~0 (SUMO 0); crossLane bothMove
   substantially toward SUMO's 4; no new sameLane class growth.
2. Goldens 661 byte-identical at default-OFF **by construction**, and at ON within tolerance or
   SUMO-diff-justified (single-lane junction goldens have no conflicting co-occupancy, so ON
   should be inert there — verify, don't assume).
3. Battery vs `docs/reports/net-regression-entry34-stays.txt`: **stuckDwell 0 everywhere** (the
   symmetric-wedge gate), arrivals within noise, overlaps column down on the city-* rows
   (city-organic-L2 currently 7).
4. Determinism repeat-hash, both gate states.
5. Live-city demo smoke: overlaps 0 at checkpoints, no gridlock, dead-stop share ≤ ≈12%.
6. The owner's visual check on Geneva terrain (the report that started this) — the final gate.

## 5. Explicitly out of scope

Back-protrusion partial occupancy (`myPartialVehicles`, Entry 27a) — adjacent but separate;
pedestrian foe lanes (crossings) in getLeaderInfo; `myInternalLinkFoes` approaching-foe gating
(the F3 carve-out stays carved out — occupancy first, approach-gating second, per
F3-INTERNAL-JUNCTION-DESIGN §5's blast-radius argument); the ped-on-RED anomaly (Entry 33).
