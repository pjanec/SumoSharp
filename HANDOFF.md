# HANDOFF.md — Session bootstrap for the next orchestrator

You are the **orchestrator** picking up this project in a fresh session with no prior chat history.
This is your cold-start briefing. Read it fully, then read `CLAUDE.md` (rules), `DESIGN.md`
(architecture + "Two futures"), `TASKS.md` (work queue + "Extended roadmap"), and `RUNG9B.md` (the
one deferred hard rung). Those are the authority; this file tells you where things stand, the method
that works, and what to do next.

## Your role

Orchestrate; don't do the high-volume work yourself. Reserve your (Opus) context for planning,
sequencing, and final-gate parity judgment. Route work to the subagents in `.claude/agents/`:
- **algorithm-porter** (Sonnet) — ports a named `/sumo/` source file to C#. Context-isolated: hand it
  exact file paths, the derived formula, and target values every time.
- **harness-runner** (Sonnet) — builds, runs `dotnet test`, reports parity diffs. Never fixes.
- **parity-reviewer** (Opus) — final gate: accept / revert / gate-behind-flag.

Dependent tasks run **sequentially**, one subagent at a time — the value is fresh context, not
concurrency.

## Where things stand (done, on `main`)

Ten parity rungs are committed **green** and merged to `main` (`dotnet test` = 28 passing, no SUMO,
no network). The engine faithfully matches SUMO to the golden precision floor across: Krauss
free-flow, car-following (`maximumSafeFollowSpeed`), stops (`maximumSafeStopSpeedEuler` + hold/
resume), gap-gated FIFO insertion, platoon shockwaves, keep-right LC2013 + the command buffer,
multi-edge/internal-lane junction traversal, traffic lights (red as a stop-line constraint), and a
consolidated parameter cross-check pass. Architecture in place: the multi-constraint reducer (seam
1), the plan/execute double buffer, the command buffer for structural mutations (seam 4),
lane-relative position with a lateral field (seam 2), and the `IEngine` seam. Code lives in
`src/Sim.Core`, `src/Sim.Ingest`, `src/Sim.Harness`, `tests/Sim.ParityTests`; scenarios + goldens in
`scenarios/`.

**Remaining work** is the "Extended roadmap" in `TASKS.md`: the deferred hard rung **9b** (junction
yielding, `RUNG9B.md`) plus **Group A** (parity extensions) and **Group B** (live external-input
reactivity).

## THE METHOD THAT WORKS (use it for every task — it landed every hard rung)

A tight de-risking loop. Do not skip step 3 — it is why keep-right and the traffic light hit exact
parity on the first porter pass.

1. **Isolate.** Build the MINIMAL scenario that exercises exactly one new behavior — fewest vehicles,
   `sigma=0`, deterministic (fixed depart/seed, Euler, teleport off). Build nets with `netconvert`
   from tiny node/edge files.
2. **Golden.** Generate goldens with SUMO at `--precision 6` (via `scripts/regen-goldens.sh`, or the
   per-scenario `sumo -c ... --fcd-output ... --precision 6 --save-state.* ...` command each
   `provenance.txt` records) + `scripts/dump-scenario-vtypes.py` for `golden.vtype.json`. Commit the
   scenario + goldens as a `[net]` step (its own commit) before writing any engine code.
3. **Instrument + reverse-engineer.** Extract SUMO's EXACT intermediate values and reduce them to a
   formula, then verify it by hand against the golden BEFORE porting:
   - First choice: **TraCI** getters / `getParameter` (this exposed the keep-right accumulator
     `keepRightP` and confirmed constants). Run `sumo` under `traci` (the wheel ships it under
     `<pkg>/sumo/tools`; the dump scripts show how to add it to `sys.path`).
   - When a value is not exposed via TraCI (e.g. junction `gap`/`distToCrossing` for 9b), **build
     the vendored `sumo/` with the relevant `DEBUG_*` `#define`s enabled** and read its per-step
     debug prints. `RUNG9B.md` names the exact defines.
   - Grep the vendored source for constants (`#define`) rather than guessing (e.g.
     `DIST_TO_STOPLINE_EXPECT_PRIORITY 1.0`, `KEEP_RIGHT_TIME 5.0`, `NUMERICAL_EPS 0.001`).
4. **Delegate the port.** Give algorithm-porter: the exact `/home/user/Traffic/sumo/...` source
   paths, the derived formula + confirmed constants, the per-step **target values for
   self-verification**, which reducer/seam it plugs into, and the **inert-when-absent guard** that
   keeps prior rungs unchanged (the pattern used by rung 8b's right-neighbor guard and rung 10's
   no-TL-connection guard — the new constraint returns `+inf` / no-op when its trigger is absent).
5. **Gate.** Send the working-tree change to parity-reviewer: verify parity within tolerance,
   faithfulness to source (not a curve-fit), no regression in prior rungs, and the committed-vs-
   ephemeral / plan-execute invariants. Only commit on ACCEPT.
6. **Commit green; update `TASKS.md` status.** One task = one committed, green, checkout-and-continue
   state. Push; keep `main` fast-forwarded if that is the workflow.

## Environment facts (learned the hard way — don't rediscover)

- **.NET 8 SDK** is NOT committed. Install per fresh VM: `sudo apt-get update` (the image's index can
  be stale → 404s) then `sudo apt-get install -y dotnet-sdk-8.0`. Microsoft's `dotnet-install.sh`
  endpoint is blocked by the egress proxy; use apt. If a fresh session can't build, that's the fix —
  not a repo change.
- **SUMO** installs via `pip install eclipse-sumo==1.20.0` (`scripts/install-sumo.sh`). The Python API
  (`traci`, `sumolib`) is under the wheel's `.../sumo/tools`, not on `sys.path` by default;
  `scripts/dump-scenario-vtypes.py` shows the discovery logic. **`libsumo` is NOT in the wheel** —
  use `traci`.
- **Vendored SUMO source is at `sumo/` (repo-relative), NOT `/sumo/`** — the docs' `/sumo/` is
  shorthand. Always read from `/home/user/Traffic/sumo/...` (a prior porter wasted effort on the
  literal `/sumo/`).
- **Goldens are `--precision 6`** so the 1e-3 tolerance is a *real* bar; the engine emits **full
  double precision** and must NEVER round to match a coarse golden (this was a corrected mistake —
  see the "Raise golden FCD precision" commit).
- **Emit-before-plan timing.** The engine emits the FCD row at the top of the step, BEFORE
  plan/execute — so this step's plan produces the row tagged `time+dt`. Time-of-day signals (traffic
  light) must be sampled at `time+dt`; insertion uses emit-time `time`; stops are duration-relative.
- The **offline `dotnet test` loop needs NO network and NO SUMO.** SUMO/TraCI steps are deliberate,
  network-enabled, and end in a committed golden — never inside the test loop.

## Order of work (from `TASKS.md` "Extended roadmap" — follow it, don't re-derive)

`A1 → 9b → A2 → B1 → B2 → B3 → A3 → B4`. The full characterization of each is already in `TASKS.md`
(and `RUNG9B.md` for 9b). Per-task pointers:

- **A1 — multi-vClass vType resolver. START HERE** (cheapest, unblocks all non-passenger work,
  no new algorithm). `VTypeDefaults.ResolvePassenger` throws for any non-`passenger` vClass. Extend
  it to the other vClass default tables, porting the `switch` branches in
  `sumo/src/utils/vehicle/SUMOVTypeParameter.cpp` (`getDefaultAccel`/`getDefaultDecel`/
  `getDefaultEmergencyDecel`/`getDefaultImperfection`) and `SUMOVehicleClass.cpp`
  (`getDefaultVehicleLength`). Add one scenario per new vClass (e.g. a truck free-flow) with its
  `golden.vtype.json`; `ParameterCrossCheckTests` already iterates every scenario, so it extends for
  free. Method step 3 is trivial here (values are the static default tables). Rename
  `ResolvePassenger` → `Resolve` and dispatch on vClass.
- **9b — junction yielding.** Follow `RUNG9B.md` exactly: (9b-i) foe matrix + conflict geometry →
  (9b-ii) approaching-vehicle registration pass → (9b-iii) `getLeaderInfo` gap + `adaptToJunctionLeader`.
  Build `sumo/` with the `DEBUG_PLAN_MOVE_LEADERINFO` defines for step 3. Decide the junction-
  determinism policy up front. Target: minor brakes `13.89→9.433→4.933→2.033`.
- **A2 — overtaking.** The speed-gain half of `MSLCM_LC2013::_wantsChange` + the target-lane safety
  veto (`checkChangeBeforeCommitting`); first LC rung with traffic on the target lane, so extend
  `LaneNeighborQuery` to return the adjacent lane's leader AND follower. De-risk via TraCI
  `getParameter(..., "laneChangeModel.speedGainLP")` (same trick as keep-right's `keepRightP`).
- **B1..B4 — live external-input reactivity (DIFFERENT bar).** Read the `TASKS.md` Group B framing
  note + `DESIGN.md` "Two futures" FIRST: these react to inputs not in any offline SUMO run, so
  golden-FCD parity does not directly validate them — use **behavioral/property tests**, reserving
  parity for sub-behaviors with a SUMO analog. B1 (stop before an external obstacle) is the cheapest
  and is almost entirely reuse of the reducer as a virtual leader/stop. B2 (a live routing layer) is
  new infrastructure — validate the router alone before wiring reroute triggers.
- **A3 — priority/emergency vehicles.** Do after A1 + 9b + A2 (it builds on the vClass resolver and
  the junction + LC foe machinery).

## Rules you must not break (from `CLAUDE.md`)

- **Parity tolerance is the iron law.** No change pushes any scenario outside its `tolerance.json`.
- **Follow SUMO on behavior; deviate only where ECS parallelism structurally forces it.** Port from
  the vendored source; verify formulas/constants against it — never trust remembered values.
- **Plan writes only each ego's own `MoveIntent`; execute applies; structural mutations go through the
  command buffer at step end.** No `System.Random` (per-entity seeded RNG only, and phase 1 is
  `sigma=0`).
- **Committed vs ephemeral:** only committed files persist (VM is volatile). The offline test loop is
  hermetic. Goldens are committed, never computed at test time.
- **One rung = one committed green state.** New features are inert-when-absent so prior rungs stay
  byte-for-byte behavior-identical (guard pattern above). Gate each rung through parity-reviewer.
- **Group B changes the validation model** (behavioral/property tests, optional/inert features) — keep
  the parity scenarios untouched as the correctness anchor.

## First actions in a fresh session

1. Confirm build health: have harness-runner run `dotnet test` (expect 28 green). If build fails, the
   SDK setup isn't wired — fix the environment (apt), not the repo.
2. Start **A1** (multi-vClass resolver) — smallest, highest-leverage win, and a clean warm-up in the
   method: build a truck free-flow scenario, dump `golden.vtype.json`, extend `VTypeDefaults`, let
   `ParameterCrossCheckTests` pick it up, gate, commit green.
3. Then **9b** via `RUNG9B.md`, and continue the order above, gating each through parity-reviewer and
   committing green.
