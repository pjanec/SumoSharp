# HIGH-DENSITY-P1F-DESIGN.md — bounded teleport valve (`time-to-teleport` jam)

Design doc for P1-F. WHAT/WHY: `docs/SUMOSHARP-HIGH-DENSITY-FEATURES.md` §3 P1-F. This is the HOW,
grounded in the vendored SUMO 1.20.0 source (`sumo/src/...`). Design-first: implement after this is
agreed. P1-F is the anti-deadlock relief valve — under teleport-off, high density deadlocks
permanently; a bounded valve (our config: `time-to-teleport=120`) lets the rare residual jam resolve
so the net keeps draining. It is also the seam the §5 X1 extra hooks into.

## 1. SUMO semantics — verified (the spec to port)

**A. Jam trigger** (`MSLane::executeMovements`, `MSLane.cpp:2172-2308`). Per lane, per step, exactly
**one** candidate: `firstNotStopped` = the **frontmost vehicle still stuck on this lane** after this
step's movements/removals (`MSLane.cpp:2239-2246`; the lane's leader-most non-stopped vehicle). For
our config (only `time-to-teleport>0`, everything else off) the sole firing branch is
**`r1 = ttt>0 && firstNotStopped.getWaitingTime() > ttt`** (`MSLane.cpp:2260`) — a **strict `>`** on
integer `SUMOTime`. With 1 s steps and `ttt=120`, it fires the step the accumulated waiting time
**exceeds** 120 (i.e. at 121 s stuck). Only the frontmost stuck vehicle of a lane teleports in a step.

**B. Waiting-time counter** (`MSVehicle::updateWaitingTime`, `MSVehicle.cpp:4081-4093`):
`if (vNext <= 0.1 && (!isStopped()||isIdling()) && accel <= 0.5*maxAccel) waitingTime += dt; else
waitingTime = 0;`. Hard reset the moment the vehicle moves (>0.1 m/s) or accelerates away. An
intentionally-`<stop>`-ped vehicle does **not** accumulate (so a parked blocker never teleports;
its queued follower does). **Already ported** in SumoSharp — see §2.

**C. Teleport action** (`MSVehicleTransfer`, `MSVehicleTransfer.cpp:54-208`):
1. Remove the vehicle from its lane and **jump it onto lane 0 of the next route edge** `succEdge(1)`
   (`add`, `:73`). If there is no next edge (teleport past the last edge) → end the trip (remove).
2. Enqueue it in a transfer list. **Every step** (`checkInsertions`, called from `MSNet.cpp:825`),
   try `freeInsertion` on the free lane of the current (jumped-to) edge at **pos 0**, speed =
   `min(laneSpeedLimit, vType.maxSpeed)`. On success → back on the road (teleport ends).
3. If it can't insert (that edge is also jammed), **virtual-proceed**: after
   `travelTime(edge, TeleportMinSpeed=1 m/s)` elapses, jump to the next edge and retry — hopping
   downstream at 1 m/s until a free lane is found (or past the last edge → remove).
4. **Ordering**: the transfer list is **sorted by vehicle numerical id** before each step's attempts
   (`MSVehicleTransfer.cpp:98`, "for repeatable parallel simulation") — reproduce this exactly.

**D. `time-to-teleport.remove`** (`MSGlobals::gRemoveGridlocked`, `MSLane.cpp:2295-2297`): when set,
the vehicle is simply **removed** from the net (no downstream re-insertion). Config flag not parsed
in SumoSharp yet.

**E. Counter** (`MSVehicleControl`, `.h:468-480`, `.cpp:561-564`): `registerTeleportJam/Yield/
WrongLane`; `total = collision+jam+yield+wrongLane`. Classification (`MSLane.cpp:2272-2294`):
`wrongLane` (on a lane not leading to the route) / `yield` (next link is a minor/no-priority link) /
else **`jam`**. A straight single-lane queue behind a blocked leader is **`jam`** — our primary case.

**F. Step order** (`MSNet.cpp:784-825`): jam-teleport-add happens inside `executeMovements` (after
speeds/positions settle this step, before `changeLanes`); the transfer re-insertion pass runs later
the same step, after regular insertion.

## 2. SumoSharp seams (what exists, what's new)

**Reuse as-is:**
- `VehicleRuntime.WaitingTime` (`Engine.cs:7915-7917`, `ExecuteMoveVehicle`) — already the byte-port
  of `updateWaitingTime` (`vNext<=HaltingSpeed && accel<=0.5*Accel ? +=dt : 0`). **Caveat:** the
  SUMO `!isStopped()` factor is currently omitted (documented "no scenario schedules a stop"). P1-F's
  jam scenario uses a `<stop>`-ped blocker → we must add the `!isStopped()` guard so the *parked
  blocker* never accumulates waiting time (else it would teleport instead of the follower). Verify
  this stays byte-identical for existing scenarios (they either have no stops or the stopped vehicle's
  WaitingTime is never read).
- `ScenarioConfig.TimeToTeleport` (parsed, default -1 = off).
- `Engine.TeleportCount` (P0-D seam, currently 0) + `StatisticWriter` jam/yield/wrongLane params.
- `CommandBuffer.Destroy` → the `time-to-teleport.remove` variant.
- Route/lane-sequence machinery (`_laneSeqPool`, `LaneSeqIndex`, `ResolveLaneSequenceHandlesWithArrival`)
  to find `succEdge(1)` and build the continuation from the jumped-to edge.

**New:**
1. Config: parse `time-to-teleport.remove` (bool, default false) into `ScenarioConfig`.
2. `WaitingTime` `!isStopped()` guard (above).
3. **Jam-check phase** (new, in `AdvanceOneStep` **between `ExecuteMoves` and
   `DecideSpeedGainChanges`**, gated on `TimeToTeleport>0`): group active vehicles by `LaneHandle`,
   pick each lane's **frontmost non-stopped** vehicle (max `Pos`), and if its `WaitingTime >
   TimeToTeleport` (strict `>`), teleport it (or `Destroy` if `remove`). Increment the jam counter.
4. **`CommandBuffer.Kind.Teleport`** (new): atomically relocate the vehicle (new `LaneId`/`LaneHandle`,
   `Pos=0`, `Speed=min(laneLimit, vMax)`) **and** swap its lane-sequence slice to the continuation
   from the jumped-to edge (`ChangeLane`'s relocation + `ReplaceRoute`'s slice-swap in one command).
5. **Transfer queue + re-insertion pass** (new persistent side-table + a step pass after
   `InsertDepartingVehicles`, sorted by entity index): the `MSVehicleTransfer::checkInsertions`
   analog — `freeInsertion` at pos 0 on the jumped-to edge; virtual-proceed at 1 m/s if blocked.
6. Counter wiring: `Engine.TeleportCount` (+ jam sub-count) → `StatisticWriter`.

## 3. Determinism / parity argument

The jam decision reads settled post-`ExecuteMoves` state (`WaitingTime` already updated this step),
is per-lane independent, and uses a strict `>` on the accumulated time — deterministic. The transfer
re-insertion is order-sensitive across simultaneously-teleporting vehicles competing for one
downstream lane, so the transfer queue is processed **sorted by entity index** (SUMO's numerical-id
sort). All structural mutations (teleport relocate, destroy, re-insert) go through the CommandBuffer.
Exact parity target: same vehicle teleported at the same step to the same downstream edge/pos, and
same `teleports total`/`jam`.

## 4. Scope for P1-F (jam-only; defer yield/wrongLane)

The acceptance scenario is a **single-lane jam** (no junction lane-choice, no minor-link yield), so it
is unambiguously the **`jam`** bucket. Implement jam classification only; `yield`/`wrongLane` (which
need junction/link-priority reasoning) are **deferred** (report 0), documented. This matches the
feature doc ("jam is the one we rely on"). Full `MSVehicleTransfer` virtual-proceed IS implemented
(needed for faithfulness when the downstream edge is also jammed), but the acceptance scenario is
designed so the primary golden exercises the simple free-downstream re-insertion; a secondary
assertion can cover a blocked-downstream virtual-proceed if feasible.

## 5. Acceptance — `scenarios/NN-teleport-jam`

A deterministic deadlock micro-net: a single-lane edge A→B where a **leader parks** (a long-duration
`<stop>`) near the end of A, and a **follower** (route A→B) queues behind it. The follower can't pass
(single lane), accumulates `WaitingTime`, and at 121 s (ttt=120) **teleports** onto B (pos 0). The
parked leader (`isStopped`) never accumulates, never teleports. Golden from vanilla SUMO 1.20.0
(`time-to-teleport=120`): the follower's position jumps discontinuously A→B at the teleport step;
`--statistic-output` shows `jam=1`, `teleports total=1`. SumoSharp must teleport the **same vehicle at
the same step to the same position**, and report the same count.
- Config keys: `time-to-teleport=120` in `<processing>`, phase-1 determinism (sigma=0, fixed depart,
  Euler, action-step 1). All-single-lane (avoids the pre-existing P2-G multi-lane confound).
- Parity: FCD trajectory within tolerance (the discontinuous jump must land at the same step/edge/pos)
  + `TeleportCount == golden teleports total (=1)`.
- Also a unit test on the strict-`>` boundary (WaitingTime=120 → no teleport; 121 → teleport) and the
  `time-to-teleport.remove` variant (vehicle removed, not re-inserted).

## 6. Faithfulness risks
1. **Strict `>` boundary** — teleport at `WaitingTime > ttt` (121s for ttt=120), not `>=`.
2. **`!isStopped()` in the waiting-time guard** — the parked blocker must NOT accumulate (else it
   teleports instead of the follower). This is the one change to the existing `WaitingTime` code;
   verify byte-identical for the existing suite.
3. **Frontmost-stuck-per-lane** — only the lane's leader-most non-stopped vehicle is eligible, not
   every halted vehicle; must reflect post-`ExecuteMoves` state (arrived/lane-changed excluded).
4. **Transfer queue is persistent + id-sorted** — a teleporting vehicle can virtual-proceed across
   several edges over multiple steps; a naive "reinsert once on the immediate next edge" diverges
   when that edge is also blocked. Sort by entity index before each step's attempts.
5. **`succEdge(1)` past the last edge** → remove (end trip), same as the `remove` variant's despawn.

## 7. Task breakdown
- **P1F-1** config (`time-to-teleport.remove`) + `WaitingTime` `!isStopped()` guard — unit tests;
  existing suite byte-identical.
- **P1F-2** jam detection phase + `CommandBuffer.Kind.Teleport` + transfer queue/virtual-proceed +
  counter wiring — unit tests (strict-`>` boundary, remove variant, transfer-queue relocation).
- **P1F-3** `scenarios/NN-teleport-jam` + golden + parity gate (FCD + teleport count). Full suite green.
