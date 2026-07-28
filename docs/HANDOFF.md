# HANDOFF.md — Session bootstrap for the next orchestrator

> **STATUS: ARCHIVED (2026-07-28)** — Session bootstrap from when the gate was **64 green** (it is now 775). The Group-D milestone it describes is recorded in `docs/TASKS-DONE.md`, and its "method that works" orchestration loop has since been codified directly into `CLAUDE.md` §Subagents. Kept in place because a `.cs` comment points here; read `docs/TASKS-TODO.md` for the live queue instead.


You are the **orchestrator** picking up this project in a fresh session with no prior chat history.
This is your cold-start briefing. Read it fully, then read `CLAUDE.md` (rules), `DESIGN.md`
(architecture + "Two futures"), and `TASKS.md` (work queue — the "Realism roadmap" + "Group D"
sections are the authority for what's left). Those files are the authority; this one tells you where
things stand, the method that works, and what to do next.

## Your role

Orchestrate; don't do the high-volume work yourself. Reserve your (Opus) context for planning,
sequencing, and final-gate judgment. Route work to the subagents in `.claude/agents/`:
- **algorithm-porter** (Sonnet) — ports a named `sumo/` source file to C#. Context-isolated: hand it
  exact file paths, the derived formula, the target values, and the done-condition every time.
- **harness-runner** (Sonnet) — builds, runs `dotnet test`, reports parity diffs. Never fixes.
- **parity-reviewer** (Opus) — final gate: accept / revert / gate-behind-flag.

Dependent tasks run **sequentially**, one subagent at a time — the value is fresh context, not
concurrency.

## Where things stand (done, on `main`)

**The lane-based SUMO-parity core is complete.** `dotnet test` = **64 green** (no SUMO, no network).
Committed parity rungs, each matched to the golden precision floor (1e-3):

- **Base (rungs 1–11):** Krauss free-flow, car-following (`maximumSafeFollowSpeed`), stops
  (`maximumSafeStopSpeedEuler` + hold/resume), gap-gated FIFO insertion, platoon shockwaves,
  keep-right LC2013 + command buffer, multi-edge/internal-lane junction traversal, static traffic
  lights (red as a stop-line constraint), parameter cross-check.
- **Group A (parity extensions):** **A1** multi-vClass vType resolver, **A2** speed-gain/overtaking
  lane change, **A3** emergency vehicle ignores red.
- **9b:** priority-junction yielding (minor yields major — the deferred hard rung, `RUNG9B.md`).
- **Group B (live external-input reactivity, behavioral bar):** **B1** stop before a static external
  obstacle, **B2** Dijkstra routing layer, **B3** reroute-around-prolonged-blockage.

**Group D — FastDataPlane ECS readiness (D1–D9) is complete.** This was a representation/perf refactor
track, every rung **byte-identical** (verified against the `Sim.Bench` 500-step determinism hash
`909605E965BFFE59`, unchanged across all nine rungs). It is **READINESS, NOT INTEGRATION** — no
`Fdp.Core`/`FastDataPlane` dependency was added; the seams are in-house drop-in points the owner wires
to FDP later. What now exists in `src/Sim.Core`:
- Dense `int` lane handles (D2), component structs + side tables off the entity (D3), a zero-alloc hot
  path (D4, ~206 B/veh-step), `Entity` handle + `CommandBuffer` deferred structural mutations (D5),
  `SystemPhase`-tagged systems over an `ActiveVehicleQuery` (D6).
- **`IWorld` / `ICommandBuffer`** adapter seam (D7) — the engine runs through the interface.
- **`Engine.UseParallelPlan`** (D8, default OFF) — opt-in `Parallel.For` plan phase, race-free by the
  plan/execute + frozen-snapshot invariant.
- **`ISimExportObserver` / `VehicleExportSnapshot`** export seam (D9) — per-frame component snapshots
  for a future `IDescriptorTranslator`-style consumer; empty observer list by default → zero added cost.
- Perf harness: `src/Sim.Bench` (not in the test loop) + `scenarios/_bench/highway-dense/BASELINE.md`,
  plus determinism guards `RungD1BenchmarkDeterminismTests` / `RungD8ParallelDeterminismTests` /
  `RungD9ExportObserverTests`.

**Remaining work** is the `TASKS.md` "Realism roadmap": **B5** (external-agent interop — the project's
stated direction) and **Group C** (realism beyond the deterministic phase-1 core, C1–C12). None is
briefed yet; all are characterized in `TASKS.md`.

## THE METHOD THAT WORKS (use it for every parity task — it landed every hard rung)

A tight de-risking loop. Do not skip step 3 — it is why keep-right, the traffic light, and 9b hit
exact parity on the first porter pass.

1. **Isolate.** Build the MINIMAL scenario that exercises exactly one new behavior — fewest vehicles,
   `sigma=0`, deterministic (fixed depart/seed, Euler, teleport off). Build nets with `netconvert`
   from tiny node/edge files.
2. **Golden.** Generate goldens with SUMO at `--precision 6` (via `scripts/regen-goldens.sh`, or the
   per-scenario command each `provenance.txt` records) + `scripts/dump-scenario-vtypes.py` for
   `golden.vtype.json`. Commit the scenario + goldens as a `[net]` step (its own commit) BEFORE
   writing any engine code.
3. **Instrument + reverse-engineer.** Extract SUMO's EXACT intermediate values, reduce them to a
   formula, and verify it by hand against the golden BEFORE porting:
   - First choice: **TraCI** getters / `getParameter` (exposed the keep-right `keepRightP` and A2's
     `speedGainLP`). Run `sumo` under `traci` (the wheel ships it under `<pkg>/sumo/tools`).
   - When a value is not exposed via TraCI (e.g. junction `gap`/`distToCrossing` for 9b), **build the
     vendored `sumo/` with the relevant `DEBUG_*` `#define`s** and read its per-step debug prints.
   - Grep the vendored source for constants (`#define`) rather than guessing.
4. **Delegate the port.** Give algorithm-porter: the exact `sumo/...` source paths, the derived
   formula + confirmed constants, the per-step **target values for self-verification**, which
   reducer/seam it plugs into, and the **inert-when-absent guard** that keeps prior rungs unchanged
   (the new constraint returns `+inf` / no-op when its trigger is absent).
5. **Gate.** Send the working-tree change to parity-reviewer: parity within tolerance, faithfulness to
   source (not a curve-fit), no regression, and the committed-vs-ephemeral / plan-execute invariants.
   Only commit on ACCEPT.
6. **Commit green; update `TASKS.md` status.** One task = one committed, green, checkout-and-continue
   state. Push; keep `main` fast-forwarded.

## The validation bar has THREE modes now — pick the right one per task

The remaining work is NOT all trajectory-exact parity. Choose the bar deliberately (it decides whether
you even need a golden):

- **Exact parity (1e-3 vs golden `--precision 6`).** The base rungs, Group A, 9b, and the parity-axis
  Group C items (**C2, C3, C4, C5, C6, C8, C9, C11**, and the SUMO-analog parts of C12). Use the
  6-step method above. `parityMode` = the existing exact mode in `tolerance.json`.
- **Statistical / ensemble parity.** Anything with `sigma>0` or per-vehicle RNG (**C1, C7**). There is
  no single trajectory to match — validate aggregate/ensemble properties (mean + spread of speed/flow
  over N seeds, or the fundamental diagram). `tolerance.json` already carries a `parityMode` field for
  this; **C1 builds the statistical mode into the harness and is the gate for it.**
- **Behavioral / property tests (no golden).** External inputs that never appear in any offline SUMO
  run (**B5**, and where **C10** leaves SUMO's lane model). Validate behavior directly: no overlap,
  the SUMO vehicle yields/brakes/avoids correctly and resumes when clear, no deadlock. This is the
  Group B bar — read the `TASKS.md` Group B framing note + `DESIGN.md` "Two futures" first.

## Two organizing facts that drive the realism order (from `TASKS.md`)

1. **The determinism ladder.** Phase 1 is `sigma=0`/Euler/`actionStepLength=1` for EXACT parity.
   Almost everything realistic (stop-and-go waves, capacity, heterogeneous speeds, gap acceptance)
   needs `sigma>0` + per-entity seeded RNG → the statistical bar. **C1 is the gate** that unblocks it.
2. **Lane-plan vs edge-plan.** Routing (B2) and LC so far are EDGE-level. Correct multi-lane traffic
   needs a LANE plan (which lane reaches the next connection, honoring `<connection fromLane/toLane>`).
   **C2 is the gate** for that.

Keep every new feature **inert-when-absent** so the deterministic parity scenarios (rungs 1–11, A1–A3,
9b) stay the byte-for-byte correctness anchor — same discipline as 8b/10/B1–B3/A3, and the same
byte-identical discipline Group D held to. If a change would move a committed scenario out of
tolerance, it is reverted or gated behind an explicit opt-in flag, never silently accepted.

## Order of work (from `TASKS.md` "Suggested realism order")

`C1 → C2 → B5 → C3/C4 → C5/C6 → C7/C9 → C8/C11 → C10 → C12`.

**Recommended first move: B5, then C1.** B5 (external-agent interop) is fully unblocked today (its
prereqs B1 + 9b + A2 are all done), is the **project's stated direction** ("SUMO lane-based vehicles
respect the non-SUMO navmesh/RVO agents"), and rides the *behavioral* bar — so it needs no golden and
no determinism-ladder shift. C1 is the other highest-leverage item because it unblocks the entire
statistical half of the realism ladder. If you want the realism ladder's parity items in dependency
order instead, do **C1 → C2** first (the two gates) and slot B5 in whenever the interop is the priority.

Per-task pointers (full characterization is in `TASKS.md`):

- **B5 — moving external agents as dynamic obstacles / foes (generalizes B1). BEHAVIORAL bar, no
  golden.** B1 already stops a SUMO vehicle behind a STATIC external obstacle (a virtual stopped leader
  on a lane). Generalize to a **moving** agent driven OUTSIDE SUMO. Reuse the B1 `_obstacles` surface:
  extend `ExternalObstacle` with velocity/heading + a per-step update. Feed it into three places that
  already exist: (a) a **dynamic leader/follower on a lane** → `FollowSpeed` with `predSpeed≠0` and a
  `predMaxDecel`; (b) a **cross-lane blocker** vetoing lane changes → A2's `IsTargetLaneSafe` /
  neighbor query; (c) a **junction foe** the reducer yields to → 9b's `JunctionYieldConstraint` as an
  approaching foe. Validate behaviorally (no overlap; yields/brakes/avoids; resumes when clear).
  Inert-when-absent. This is the core two-way sharing the project aims at.
- **C1 — statistical parity / driver imperfection (`sigma>0`). The determinism-ladder shift; do first
  to unblock the rest.** Port Krauss dawdling (`MSCFModel_Krauss::dawdle`) + a per-vehicle SEEDED RNG
  (CLAUDE.md: no `System.Random`; seed per entity so results are thread-order-independent). Add a
  statistical `parityMode` to the harness — either reproduce SUMO's `RandHelper` per-vehicle stream for
  trajectory-exact parity (hard) or, more realistically, an ensemble/aggregate tolerance. Prereq for C7.
- **C2 — strategic (route-driven) lane changes + lane-to-lane continuity. The #1 lane-based realism
  gap.** Today a vehicle can sit in a lane that cannot reach its route. Port LC2013's STRATEGIC block
  (`LCA_STRATEGIC`/`LCA_URGENT`, `getBestLanes`/`bestLaneOffset`) so a vehicle moves into a lane that
  continues its route. Requires **lane-level** routing (honor `<connection fromLane/toLane>`; B2 is
  edge-level). Reuses A2's neighbor query + the post-move LC phase. Parity axis.
- **C3–C12** — merges/zipper (C3), remaining right-of-way (C4), keepClear/don't-block-the-box (C5),
  actuated TLs + yellow decision (C6), `speedFactor` spread (C7, needs C1), ballistic +
  `actionStepLength>1` (C8, own goldens), cooperative LC (C9), sublane/continuous lateral → the
  navmesh/RVO bridge (C10, large, own phase — seams 1 & 2 were built for it), IDM/ACC/CACC CF models
  (C11), pedestrians/crossings/PT (C12). Each is characterized in `TASKS.md`; pick the bar per the
  three-mode guide above.

## Environment facts (learned the hard way — don't rediscover)

- **.NET 8 SDK** is NOT committed. Install per fresh VM: `sudo apt-get update` (image index can be
  stale → 404s) then `sudo apt-get install -y dotnet-sdk-8.0`. Microsoft's `dotnet-install.sh` is
  blocked by the egress proxy; use apt. If a fresh session can't build, that's the fix — not the repo.
- **SUMO** installs via `pip install eclipse-sumo==1.20.0` (`scripts/install-sumo.sh`). The Python API
  (`traci`, `sumolib`) is under the wheel's `.../sumo/tools`, not on `sys.path`;
  `scripts/dump-scenario-vtypes.py` shows the discovery logic. **`libsumo` is NOT in the wheel** — use
  `traci`.
- **Vendored SUMO source is at `sumo/` (repo-relative), NOT `/sumo/`** — the docs' `/sumo/` is
  shorthand. Read from `<repo-root>/sumo/...` (resolve the root with `git rev-parse --show-toplevel`).
- **Goldens are `--precision 6`** so the 1e-3 tolerance is a real bar; the engine emits **full double
  precision** and must NEVER round to match a coarse golden.
- **Emit-before-plan timing.** The engine emits the FCD row at the TOP of the step, BEFORE
  plan/execute — so this step's plan produces the row tagged `time+dt`. Time-of-day signals (traffic
  light) are sampled at `time+dt`; insertion uses emit-time `time`; stops are duration-relative.
- **The offline `dotnet test` loop needs NO network and NO SUMO.** SUMO/TraCI steps are deliberate,
  network-enabled, and end in a committed golden — never inside the test loop.
- **The Group-D determinism hash is your byte-identical oracle for refactors.** Any change that claims
  to preserve behavior must leave `Sim.Bench`'s 500-step hash `909605E965BFFE59` unchanged in both
  single-threaded and parallel mode. Run `dotnet run --project src/Sim.Bench -c Release` to check.

## Rules you must not break (from `CLAUDE.md`)

- **Parity tolerance is the iron law.** No change pushes any committed scenario outside its
  `tolerance.json`. Optimizations that move a trajectory are reverted or gated behind an opt-in flag.
- **Follow SUMO on behavior; deviate only where ECS parallelism structurally forces it.** Port from the
  vendored source; verify formulas/constants against it — never trust remembered values.
- **Plan writes only each ego's own `MoveIntent`; execute applies; structural mutations go through the
  command buffer at step end.** No `System.Random` (per-entity seeded RNG only; phase 1 is `sigma=0`).
  This invariant is now load-bearing for real: `Engine.UseParallelPlan` parallelizes the plan phase on
  exactly this guarantee — a shared-state write in `ComputeMoveIntent`'s call tree would break it.
- **Committed vs ephemeral:** only committed files persist (VM is volatile). The offline test loop is
  hermetic. Goldens are committed, never computed at test time.
- **One rung = one committed green state.** New features are inert-when-absent so prior rungs stay
  behavior-identical. Gate each parity rung through parity-reviewer.
- **Keep the ECS-readiness seams intact and byte-identical.** `IWorld`/`ICommandBuffer` (D7),
  `UseParallelPlan` (D8), and `ISimExportObserver` (D9) are the FDP drop-in points — extend the engine
  through them, don't route around them, and don't add an `Fdp.Core` dependency unless the owner
  explicitly asks to move from readiness to integration.

## First actions in a fresh session

1. Confirm build health: have harness-runner run `dotnet test` (expect **64 green**). If build fails,
   the SDK setup isn't wired — fix the environment (apt), not the repo.
2. Decide the first rung: **B5** (unblocked, high-priority, behavioral — the stated interop direction)
   or **C1** (the determinism-ladder gate that unblocks statistical realism). Both are highest-leverage;
   B5 needs no golden, C1 builds the statistical harness mode.
3. Run the method: isolate the scenario, set the correct validation bar (exact / statistical /
   behavioral), reverse-engineer against the vendored source where there's a SUMO analog, delegate the
   port, gate, commit green, update `TASKS.md`. Continue the suggested realism order above.
