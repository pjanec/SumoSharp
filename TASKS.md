# TASKS.md — Work queue for coding sessions

Each task is a **self-contained briefing**. A subagent starts from near-zero context, so a
task must name every input it needs: the `/sumo/` reference file, the target C# files, the
scenario, the command, and the numeric done-condition. Do tasks in order. One task = one
committed, green state you can check out later and continue from.

Read `CLAUDE.md` (rules) and `DESIGN.md` (architecture) before starting any task.

Legend: **[net]** = needs network + human (golden regen / vendoring); everything else is
the offline `dotnet test` loop.

---

## Task 0 — Bootstrap the harness (green on a blank checkout)

**Goal.** A committed test harness that passes `dotnet test` on a fresh clone into an empty
VM, **without SUMO and without any simulation engine existing yet**. This is the
checkout-and-continue baseline everything else grows from.

**Why it can be green with no engine and no SUMO.** The harness proves itself with a
*self-test*: feed the comparator two synthetic trajectories that are identical (assert zero
diff) and two that are deliberately offset (assert the diff is detected and localized).
This exercises the whole comparison path offline.

**Create the solution + projects:**
- `Sim.Core` — ECS components/systems/integration (empty scaffolding for now; no models yet)
- `Sim.Ingest` — `.net.xml` / `.rou.xml` parsers (empty scaffolding for now)
- `Sim.Harness` — FCD parsing + trajectory comparison (implement now)
- `Sim.ParityTests` — xUnit test project (implement the self-test now)

**Implement in `Sim.Harness`:**
- An **FCD parser**: read a SUMO `--fcd-output` XML into an in-memory model. Per timestep,
  per vehicle, capture: `id, lane, pos, speed, x, y, angle` and (when present)
  `acceleration`. Structure it for lookup by `(vehicleId, time)`.
- A **trajectory comparator**: given two trajectory sets + a tolerance config, return, per
  attribute: max-abs error, RMSE over the trajectory, and the **first timestep** where any
  attribute exceeds tolerance (or "no divergence"). Compare only vehicle/time pairs present
  in both; report any presence mismatch (missing/extra vehicles or steps) explicitly.
- A **`tolerance.json` schema** + loader: per-attribute tolerances (`pos`, `speed`, `x`,
  `y`, `angle`, `acceleration`) plus a `parityMode` field (`"exact"` | `"statistical"`)
  and an optional `comparedAttributes` list (phase 1 uses `["lane","pos","speed"]` — see
  DESIGN.md "layered comparison metric").

**Define the engine seam (no implementation):**
- `IEngine` in `Sim.Core`: loads a scenario (net + rou + cfg paths), runs N steps, and
  emits a trajectory set in the **same in-memory shape** the FCD parser produces, so engine
  output and golden output are directly comparable. Leave it unimplemented — later tasks
  fill it in.

**Implement in `Sim.ParityTests` (the self-test):**
- Construct two identical synthetic trajectory sets → assert comparator reports zero diff /
  no divergence.
- Construct two sets differing by a known offset at a known step → assert the comparator
  reports that attribute over tolerance and pinpoints the correct first-divergence step.
- Round-trip test: parse a tiny hand-written FCD XML fixture (commit it under
  `scenarios/_fixtures/`) and assert field values load correctly.

**Also create (committed, but not exercised by the test loop):**
- `scripts/install-sumo.sh`, `scripts/regen-goldens.sh`, `SUMO_VERSION` — already drafted;
  place them and make the shell scripts executable (`chmod +x`).
- `.gitignore` for `bin/`, `obj/`, NuGet caches, and any local SUMO install dir.

**Done-condition.** Fresh clone into an empty VM → `dotnet test` **passes on the self-test
alone**, with no SUMO installed and no engine implemented. Commit.

---

## Task 1 [net] — Vendor SUMO + generate the rung-1 golden

**Human/network step**, done once outside the offline loop.

- Vendor SUMO source at the tag matching `SUMO_VERSION` into `/sumo/` (see CLAUDE.md).
- Author the rung-1 scenario under `scenarios/01-single-free-flow/`:
  - `net.net.xml` — one straight edge, one lane, long enough to reach cruising speed
    (e.g. 1000 m), a single speed limit.
  - `rou.rou.xml` — one `<vType>` (passenger defaults; `sigma="0"`) and one vehicle
    departing at a fixed time/speed.
  - `config.sumocfg` — fixed `step-length`, Euler stepping, teleport off, no randomness.
  - `tolerance.json` — `parityMode="exact"`, `comparedAttributes=["lane","pos","speed"]`,
    tight tolerances (e.g. `pos` 1e-3 m, `speed` 1e-3 m/s).
- Run `scripts/regen-goldens.sh` → produces `golden.fcd.xml`, `golden.state.xml`,
  `provenance.txt`. **Commit them.**

**Done-condition.** `/sumo/` present at the correct tag; rung-1 scenario + goldens
committed with provenance stamped at `SUMO_VERSION`.

---

## Task 2 — Ingest + engine skeleton wired to rung 1

**Reference:** `/sumo/src/microsim/MSLane.cpp`, `/sumo/src/microsim/MSVehicle.cpp` for how
position is represented (lane-relative arc-length `pos`, global x/y derived).

- Implement `.net.xml` parsing in `Sim.Ingest` for the rung-1 subset: edges, one lane with
  its `shape` polyline, length, speed limit. Store the network as immutable arrays (see
  DESIGN.md), not entities.
- Implement `.rou.xml` parsing: `<vType>` attributes and a single vehicle/route.
- Implement `IEngine` enough to: place the vehicle, step with **fixed dt**, and emit a
  trajectory in the comparator's shape. Longitudinal position is lane-relative `pos`;
  derive x/y by walking the lane polyline (needed only if x/y are compared — phase 1
  compares `lane,pos,speed`, so x/y derivation can be minimal/stubbed).
- **Lateral field discipline (future-proofing, costs nothing now):** include a `LatOffset`
  in the transform and always write 0. Do not add lateral kinematics yet.

**Done-condition.** Engine runs rung 1 and emits a trajectory the harness can compare
(expected to be OUT of tolerance until Task 3 — that's fine; this task is plumbing).

---

## Task 3 — Krauss car-following, single vehicle free-flow parity

**Reference (read before porting):**
- `/sumo/src/microsim/cfmodels/MSCFModel_Krauss.cpp`
- `/sumo/src/microsim/cfmodels/MSCFModel.cpp` (base: `maximumSafeStopSpeed*`,
  `finalizeSpeed`, accel/decel bounds)
- Cross-check the safe-speed formula against the paper cited in the project docs
  ("SUMO's Interpretation of the Krauß Model"). **Do not trust remembered formulas** — the
  correct exact form is `v_safe = -b*tau + sqrt((b*tau)^2 + V^2 + 2*b*g)`; the Taylor form
  in the Gemini docs was transcribed with a misplaced gap term. Port from source, verify
  against golden.

**Implement the plan/execute contract (this is the load-bearing part):**
- **Plan phase:** each vehicle computes its next speed from the **start-of-step** state of
  its neighbors, writing the result to its own `MoveIntent` only. No shared writes. (Even
  single-threaded, honor this — it's what makes threading a later scheduling change, not a
  rewrite. See DESIGN.md.)
- **Execute phase:** apply intents, integrate position with the configured method (Euler in
  phase 1), advance `pos`.
- For rung 1 there is no leader, so `v_safe` is unconstrained by a leader; the vehicle
  accelerates (bounded by `accel`) toward `speedFactor * speedLimit` and holds. This
  isolates the acceleration bound + speed cap + integration before following matters.
- **Multi-constraint reducer (build the shape now):** compute next speed as the min over a
  *collection* of speed constraints, even though the collection has size 1 here. Junctions
  and (later) shadow lanes feed the same reducer. See DESIGN.md "seam 1".

**Verify vType init first:** diff your resolved passenger defaults against
`golden.state.xml` (accel 2.6, decel 4.5, sigma 0.5→but forced 0 here, tau 1.0,
minGap 2.5). Ruling out an init bug up front saves chasing trajectory drift.

**Done-condition.** `dotnet test` shows rung-1 trajectory within `tolerance.json` on
`lane,pos,speed` for all steps. Commit. This is the first real parity milestone — it proves
the plan/execute contract, the integration step, and the reducer shape on the smallest
possible surface.

---

## Next batch (define fully when Task 3 is green)

Kept here as a roadmap, not yet as briefings. Each becomes a self-contained task with
its own `/sumo/` references and scenario when we reach it:

4. **Two-vehicle following** — Krauss safe speed with a real leader; steady-state gap.
   Adds leader lookup from the per-lane sorted list. Ref: `MSLane` leader/follower logic.
5. **Approach to a stopped vehicle / dead end** — the discrete `maximumSafeStopSpeedEuler`
   overshoot-prevention math in isolation. The subtle one; nail it alone.
   - Note from rung 4 review: the leader constraint passes `predMaxDecel = leader vType
     `decel``. That is correct while `apparentDecel == decel` (the phase-1 default). If any
     vType overrides `apparentDecel`, revisit whether SUMO uses `getCurrentApparentDecel()`
     rather than `getMaxDecel()` for the leader term (`MSCFModel::maximumSafeFollowSpeed`).
   - Also: `maximumSafeFollowSpeed`'s emergency-decel correction (`decel!=emergencyDecel`) is
     ported but was unexercised by rung 4 — rung 5's hard stop is the first real test of it.
6. **Insertion spacing** — departure FIFO + gap-gated insertion. DONE.
   - Note from rung 6 review: `TryInsertOnLane`'s per-lane break-on-first-failure assumes a
     blocked earlier departure blocks all later ones — exact when all departures share one
     insertion point (as here). If a future scenario puts vehicles at DIFFERENT departPos on
     the same lane, revisit (SUMO retries each pending vehicle independently).
7. **Platoon shockwave** — still `sigma=0`, deterministic; multi-vehicle propagation.
8. **Two lanes + LC2013** — first structural change via command buffer; first real use of
   the multi-constraint reducer with a lateral intent. Ref: `MSLCM_LC2013`. **PARTIALLY DONE.**
   - 8a (scenario `06-two-lane-cruise`): two-lane `.net.xml` ingest + per-lane emission,
     vehicle stays right. Green, no engine change (parser already reads all `<lane>`).
   - 8b (scenario `07-keep-right-change`): the command buffer + lateral intent (seam 4,
     discrete instant lane-index snap) + a MINIMAL faithful slice of `MSLCM_LC2013`'s
     keep-right block, reproducing the exact accumulator so a single empty-road vehicle
     changes right at t=6. Right-neighbor guard leaves all single-lane rungs untouched.
   - **Deferred LC2013 work** (each its own future rung, needs its own scenario+golden):
     strategic (route/connectivity-forced) changes + general best-lanes (`getBestLanes`),
     cooperative changes, speed-gain overtake (the tactical block + `mySpeedGainProbability`),
     safety/blocker vetoes and the neighbor follower/leader gap checks (need a 2-lane
     scenario WITH traffic), `lanechange.duration>0` continuous lateral, and multi-edge lane
     continuity. Also: the command buffer currently applies each vehicle's lane swap inline
     in `ExecuteMoves` (fine for one changer); revisit batching if a scenario has multiple
     simultaneous changers competing for a gap (DESIGN.md conflict-resolution tie-break).
9. **Priority intersection** — right-of-way matrix + link-leader yielding, feeding the
   reducer. Ref: `MSRightOfWayJunction`, `MSLink`. **PARTIALLY DONE.**
   - 9a (scenario `08-junction-straight`): multi-edge routing + internal-lane traversal
     (route expands to a lane sequence via each `<connection>`'s `via`; pos carries over across
     lane boundaries). A major-road vehicle drives straight through, no yielding. Green.
   - **9b — priority yielding — DEFERRED** (the sole remaining hard rung). **See `RUNG9B.md` for
     the full cold-start plan** (mechanism, geometry, the reference scenario to rebuild, the 3-step
     decomposition, de-risking notes, done-condition). Summary characterization below.
     Scenario probed: major `WJ JE` (priority),
     minor `SJ JN` (yields); the minor brakes 13.89→9.433→4.933→2.033 as the major approaches,
     threads through just behind it, then accelerates.

     **Mechanism (reverse-engineered from source).** The minor yields by treating the crossing
     major as a virtual "link leader": `MSVehicle::checkLinkLeader` → `adaptToJunctionLeader`
     (MSVehicle.cpp). For a crossing foe with gap≥0 it computes
     `vsafeLeader = MAX2(followSpeed(gap=leaderInfo.second, leaderSpeed, leaderDecel),
     MIN2(v2, vStop))` where `vStop = stopSpeed(distToCrossing − minGap)`,
     `leaderPastCPTime = (distToCrossing − leaderInfo.second) / max(leaderSpeed, haltingSpeed)`,
     `vFinal = MAX2(speed, 2*(distToCrossing − minGap)/leaderPastCPTime − speed)`,
     `v2 = speed + ACCEL2SPEED((vFinal − speed)/leaderPastCPTime)`; then `v = MIN2(v, vsafeLeader)`.
     Geometry (this net): internal lanes cross at (201.60, 198.40) — `:J_1_0` (minor,
     x=201.60 vertical) meets `:J_2_0` (major, y=198.40 horizontal), 5.60 m into each; so
     `distToCrossing = 201.60 − pos` for both.

     **Why it is NOT a single-pass port (the blockers).** Reproducing 9.433 exactly needs, and
     none of these exist in the engine yet:
     1. **Junction conflict geometry** (`MSLink::myConflicts`: crossing point, `lengthBehind-
        Crossing` for both lanes, `conflictSize`/widths, `myRadius`/`sagitta`). netconvert/NBNode
        precomputes this at build time; it is NOT in the `.net.xml`. Must be reimplemented from
        the internal-lane shapes/widths (the crossing point itself IS derivable from geometry, as
        above, but the conflict widths/sagitta are more work).
     2. **Approaching-vehicle registration** (`MSLink::setApproaching`/`getApproaching` →
        `ApproachingVehicleInformation` with `arrivalTime`/`leaveTime`/`willPass`). Each vehicle
        registers its planned arrival/leave at every link it approaches; the minor reads the
        major's registration to decide. This is a genuinely two-vehicle-coupled, stateful pass —
        a new phase between plan and execute.
     3. **`MSLink::getLeaderInfo`** (~396 lines) — the foe-vehicle gap (`leaderInfo.second`) +
        `distToCrossing` computation, with many special cases (contLane, sameTarget/sameSource,
        inTheWay, pastTheCrossingPoint, indirect turns). This is where the exact gap convention
        lives; hand-derivation did not reproduce 9.433 (got 9.39 via followSpeed(0) or 11.57 via
        the vStop branch), i.e. the gap/priority conventions need the real getLeaderInfo, not a
        guess.
     4. **Priority / `isLeader`** (`MSLink::opened`, response/foe matrix, `myJunctionEntryTime`
        ordering) to decide the major is the leader.

     Recommended decomposition when tackled: (9b-i) parse the junction `<request>` response/foes
     matrix + compute conflict geometry from internal-lane shapes; (9b-ii) add the approaching-
     registration pass (arrivalTime/leaveTime) as new engine infrastructure; (9b-iii) port
     getLeaderInfo's gap for the single-crossing case + adaptToJunctionLeader; iterate against the
     golden (target 9.433/4.933/2.033). Also decide here the junction-determinism policy
     (match-SUMO-order vs deterministic tie-break-by-id; DESIGN.md "parallelization").
10. **Traffic light** — `<tlLogic>` state machine; red light as a stop-line constraint. **DONE**
    (scenario `09-traffic-light`; red → `stopSpeed(seen − DIST_TO_STOPLINE_EXPECT_PRIORITY 1.0)`,
    green → traverse; TL sampled at `time+dt` for the emit-before-plan ordering).
    - Note from rung 10 review: `TrafficLightState.IsRedOrYellow` matches only `'r'`/`'y'`;
      widen to `'u'` (red-yellow) and `'Y'` (yellow-major) before a scenario uses those phases.
      Yellow "stop if you can brake, else go" decision logic is also not yet built (no yellow here).
11. **Parameter-extraction cross-check pass** — automated diff of C# vType defaults vs the
    committed golden across all scenarios, run before trajectory tests as a fast fail. **DONE**
    (`ParameterCrossCheckTests`, a data-driven `[Theory]` over every scenario's
    `golden.vtype.json` `vTypes`; subsumes the former per-scenario init cross-checks). Note: it
    diffs against `golden.vtype.json` (the empirical libsumo/TraCI dump), NOT `golden.state.xml`,
    because `--save-state` does not expand implicit vType defaults (see the IMMEDIATE-task finding
    / DESIGN.md). Rung 1's pure-defaults reference stays covered by `VTypeInitCrossCheckTests`.

At rung 8+ decide explicitly the junction determinism policy (match-SUMO-order vs
deterministic-tie-break-by-id); see DESIGN.md "parallelization". Sublane/laneless mode is a
whole separate phase layered on top — no task here should preclude it (see the four seams).
