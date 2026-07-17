# HIGH-DENSITY-P1E-DESIGN.md — `device.rerouting` (periodic congestion-reactive rerouting)

Design doc for P1-E. WHAT/WHY: `docs/SUMOSHARP-HIGH-DENSITY-FEATURES.md` §3 P1-E + `docs/HIGH-DENSITY-PLAN.md`
§1 (P1-E) / §"Owner steer". This is the HOW. **Owner signed off (with three day-1 additions below);
implementation proceeds.** All SUMO citations verified against the vendored 1.20.0 source (`sumo/src/...`).

## 0.5 Owner-approved design decisions (adopted day 1)

After a design discussion the owner directed three deliberate departures from a strict "match SUMO
exactly" approach, all consistent with the standing perf-over-parity-when-gated principle:

1. **Per-vehicle reroute jitter (gated).** SUMO fires each vehicle's reroute at `depart + k·period`
   with **no** per-vehicle offset, so a burst of near-simultaneous departures reroutes in lockstep —
   a *wave* that (a) spikes CPU every `period` s (idle between; wastes cores) and (b) causes
   **synchronized overreaction / flip-flop** (all cars divert to the same alternate at once, overload
   it, divert back next wave). We add a per-vehicle phase offset (hash of entity id → offset in
   `[0, period)`) so reroutes spread uniformly across the period: flattens CPU into a steady stream
   and breaks the overreaction (more realistic — real drivers share no clock). **Gated:** off = the
   SUMO-faithful schedule (`depart + k·period`); on = the jittered production schedule. Default off so
   the faithful anchor scenario matches SUMO.

2. **Acceptance is behavioural/statistical at the end-to-end level** (see revised §6). Exact parity is
   kept only for the deterministic *machinery* (router, smoothing) and one small SUMO-faithful anchor
   scenario. Bit-exact trajectory is NOT a hard gate — it would lock the production engine to SUMO's
   quirks (the wave, the `isDelayed` latch) for no product value. The real end-to-end bar is
   behavioural: the flow splits, peak load drops, the network drains — comparably to SUMO.

3. **Route-slot recycling (no id explosion at 10k).** No-improvement-gate means a reroute conceptually
   re-installs a route every `period` per vehicle; minting a fresh synthetic route id each time
   (today's `RegisterRerouted`) grows `_routesById` unboundedly at 10k vehicles × long runs. Each
   vehicle gets **one reusable synthetic route slot** it overwrites in place (plus the identical-edge-
   list short-circuit, which skips most reroutes), so memory stays bounded regardless of run length.

(A further **overreaction-dampening** idea — hysteresis / logit route choice beyond jitter — is
recorded as a possible later extra, NOT in P1-E scope.)

## 0. Scope & config

The SumoData config sets: `device.rerouting.probability=1.0`, `device.rerouting.period=30`,
`device.rerouting.adaptation-steps=18`, `routing-algorithm=astar` (adaptation-interval defaults to 1s).
Behaviour: every `period` s, each equipped vehicle re-routes from its **current edge** to its
(unchanged) destination on a graph weighted by **live, smoothed edge travel times**, and installs the
result. This is distinct from the existing obstacle-triggered one-shot reroute (`UpdateReroutes`,
`Engine.cs:3256-3412`), which stays untouched.

## 1. SUMO semantics — verified (the spec to port)

**A. Equip + periodic schedule** (`MSDevice_Routing.cpp:135-149,223-237,277-297`):
- Equipped by probability (`=1.0` → all vehicles).
- First periodic reroute fires at **`depart + period`**, then **every `period`** thereafter. **No RNG
  phase jitter** — herd desync is emergent from differing depart times. (`device.rerouting.synchronize`
  defaults **false**; only if true does SUMO snap `start -= start % period` to a global boundary.)
- **Skip-if-stale-weights guard**: a reroute is a no-op if the weights haven't changed since this
  vehicle's last routing (`myLastRouting >= MSRoutingEngine::getLastAdaptation()`).

**B. The reroute** (`MSBaseVehicle.cpp:259-406`, `MSVehicle.cpp:1405-1416`):
- Source = the vehicle's **current edge**, bumped to the *next* edge if it's already within its
  brake-gap of the junction (`getRerouteOrigin`). Destination = route's last edge (unchanged).
- **NO improvement gate.** `savings = previousCost − routeCost` is computed as *output metadata only*;
  there is no `if (savings>0)`. The freshly-computed path **always replaces** the current one, unless
  it fails a **structural** check (empty; current edge not on the new route; committed mid-junction).
  When the new edge list **equals** the current one, SUMO short-circuits (`MSBaseVehicle.cpp:438`) — no
  new route object. → We must mirror: always install, short-circuit on identical edge list.

**C. Edge-weight smoothing** (`MSRoutingEngine.cpp:113-167,216-291`), our `adaptation-steps=18` path:
- Per-edge dense tables seeded to **free-flow speed** (`edge.getMeanSpeed()` = lane speed limit when
  empty). `myPastEdgeSpeeds[edge]` = a length-`N=18` **ring buffer** seeded with that free-flow speed.
- Every `adaptation-interval` (1s), at **end of timestep**, for each **delayed** edge:
  `myEdgeSpeeds[id] += (currSpeed − myPastEdgeSpeeds[id][k]) / N; myPastEdgeSpeeds[id][k] = currSpeed;`
  then `k = (k+1) % N`. `currSpeed = edge.getMeanSpeed()` sampled once (occupancy-weighted mean over the
  edge's lanes' vehicles; = speed limit when empty). **Port the incremental recurrence exactly** (not a
  recomputed sum/N — float drift must match).
- **`isDelayed()` is a permanent one-way latch** (`MSEdge.h:711-713`): set the first time *any* vehicle
  ever enters a lane on the edge, never reset. An edge that has never seen a vehicle is never updated
  (stays at free-flow seed); one that has is updated every interval forever (converging back toward
  free-flow when it empties, since `getMeanSpeed()`→limit). Port this latch exactly.
- **Effort the router reads** (`getEffort`): `max(length / max(smoothedSpeed, ε), minimumTravelTime)`,
  where `minimumTravelTime = length / vehicleMaxSpeed(+ timePenalty)` is a per-vClass floor.

**D. A\*** (`AStarRouter.h:128-278`): heuristic = euclidean-distance / network-max-speed (no landmark
table in our config) — admissible **and consistent** ⇒ no node re-expansion ⇒ **returns the identical
optimal-cost path a Dijkstra returns on the same weights.** So A* is a pure optimisation; the router is
exactly testable against the existing `NetworkRouter` Dijkstra.

**E. Threading** (`MSNet.cpp:755-828`): reroute tasks read the shared weight table; a `waitForAll()`
barrier drains them **before** the single end-of-step writer (`adaptEdgeEfforts`) runs. Single-writer/
many-reader via **temporal phase separation**, never per-access locks.

## 2. New pieces, mapped onto SumoSharp seams

| # | Piece | SUMO ref | SumoSharp seam |
|---|-------|----------|----------------|
| 1 | Config keys (`device.rerouting.*`, `routing-algorithm`) | option registration | `ScenarioConfig`/`Parser` (mirror `<processing>`/`time-to-teleport`) — additive, default off |
| 2 | Per-edge live smoothed-speed table + ring buffer + `isDelayed` latch | `MSRoutingEngine::adaptEdgeEfforts` | **new** dense handle-indexed arrays (à la `LanesByHandle`); updated in a new **end-of-step** pass |
| 3 | Effort fn + A* over the network graph, weights injected | `getEffort` + `AStarRouter` | reuse `NetworkRouter`'s adjacency; **generalise `EdgeCost` to an injected weight fn**, add A* (or an A* variant) |
| 4 | Periodic per-vehicle reroute trigger (equip, period, skip-stale, no-gate install, identical-list short-circuit) | `MSDevice_Routing` | **new** pass beside `UpdateReroutes`, **before `PlanMovements`**, reusing `RegisterRerouted`/`CommandBuffer.ReplaceRoute` |

## 3. Step-loop placement & the hard phase-ordering rule

- **Periodic reroute pass**: run at the existing `UpdateReroutes` point — **before** `PlanMovements` —
  and **flush `CommandBuffer` before `PlanMovements`**, so a vehicle that reroutes this step plans on
  its new route this step (matches SUMO's begin-of-step events → `planMovements`).
- **Edge-weight update pass**: run at **end of step**, after `ExecuteMoves`/`DecideSpeedGainChanges`
  settle, so a reroute always reads the *previous* step's fully-settled weights, never a mid-write
  snapshot (this is the temporal analog of SUMO's `waitForAll` barrier). **This relative order is a
  correctness requirement, not a detail** — getting it wrong breaks parity silently.
- `getLastAdaptation()` analog = the sim-time of the last end-of-step weight update; the per-vehicle
  skip-stale guard compares against it.

## 4. Thread-safety & the owner's fast/parallel requirement

The default faithful path is **already parallel and parity-safe** — no separate "fast mode" needed for E:
- **Reroute pass**: collect the vehicles due this step into a batch; `Parallel.For` over the batch, each
  running A* as a **pure read** of the frozen edge-weight snapshot into its **own** per-vehicle scratch
  (candidate edge list). No shared writes. Then a **serial** pass applies each result via
  `RegisterRerouted` + `CommandBuffer.ReplaceRoute` (add a lock there, or keep the record serial — matching
  `ChangeLane`'s existing discipline), one `Flush()` before `PlanMovements`. Because each A* is a pure
  function of (frozen snapshot, origin, dest), the result is **independent of thread order** → parallel is
  bit-identical to serial. This directly satisfies the "buffer the due-this-tick vehicles, fan A* out
  across cores" steer, and the 10k-vehicle herd is just a large batch.
- **Weight-update pass**: each edge's ring-buffer update is independent → parallelisable over edges,
  deterministic (fixed per-edge order). `currSpeed = getMeanSpeed()` is a per-edge reduction in fixed
  lane/vehicle order → deterministic.
- **Router state** per-thread/thread-local (no shared open/closed sets).
- **Gated fast-but-different (optional, later):** a cheaper cadence or approximate weights could be an
  opt-in CLI flag per the owner's standing principle, but is **out of P1-E scope** — the default is both
  faithful and parallel.

## 5. Determinism / parity argument

The reroute switch is **discrete edge-list equality**, not a float threshold — so it is robust to tiny
numeric noise *provided the edge weights themselves match*. The whole chain is bit-portable on a
deterministic (single-thread or fixed-reduction) run: `getMeanSpeed` (fixed-order reduction) → the exact
incremental moving-average recurrence → effort fn → A* (= Dijkstra exact). Therefore **exact trajectory
parity is the target** for the P1-E scenario, with statistical parity as a **fallback** only if a
genuine float-order divergence is observed. (This resolves Q2 toward the two-tier plan, leaning exact.)

## 6. Acceptance (revised per owner decision §0.5.2)

**Tier 1 — exact unit tests on the deterministic machinery (hard gate):**
- **A\* router**: given a fixed static weight table, returns the *same path* as `NetworkRouter`'s Dijkstra
  on the same weights (several hand-built graphs incl. a congestion-vs-alternate case). Exact.
- **Smoothing**: given a fixed sequence of per-edge `currSpeed` samples, `myEdgeSpeeds` matches the
  hand-computed ring-buffer recurrence (incl. the free-flow seed and the `isDelayed` latch behaviour).
- **Effort fn**: `max(length/max(v,ε), minTT)` on fixed tuples.

**Tier 2 — end-to-end, TWO scenarios:**
- **(a) SUMO-faithful anchor `scenarios/NN-reroute-congestion` (jitter OFF).** Small net: a short
  "shortcut" and a longer alternate between one OD, both driveable; enough demand to congest the
  shortcut so its smoothed travel time rises above the alternate's; `device.rerouting` on
  (`probability=1, period=30, adaptation-steps=18, routing-algorithm=astar`), jitter off. Golden from
  vanilla SUMO 1.20.0. **Best-effort exact `(lane,pos,speed)` parity** (deterministic: sigma=0, fixed
  depart). If a genuine float-order divergence appears, fall back to `parityMode:"statistical"` on the
  route split + throughput and document why. This is the regression anchor + "we can still reproduce a
  SUMO run." Distinct from `15-reroute` (obstacle-based).
- **(b) Behavioural check (the real product bar), jitter ON and OFF.** On the same (or a slightly
  denser) net, assert the *outcome* rather than the trajectory: (i) the flow **splits** (a non-trivial
  fraction takes the alternate once the shortcut congests — feature genuinely exercised, not a no-op);
  (ii) network throughput / mean travel time is **no worse** with rerouting than without; (iii) with
  jitter **on**, reroutes are spread across the period (no single-tick wave carrying >X% of the period's
  reroutes) AND the split/throughput is at least as good as jitter-off. Functional/statistical, not a
  golden. This is where "different-but-better" is validated.

(Density/no-deadlock at scale is the `BENCHMARK_SPEC.md` statistical harness's job, not here.)

## 7. Config-parsing additions

`ScenarioConfig` gains (additive, defaults = off/SUMO-default): `RerouteProbability` (0),
`ReroutePeriod` (0 = disabled), `RerouteAdaptationSteps` (180), `RerouteAdaptationInterval` (1),
`RoutingAlgorithm` ("dijkstra"). Parsed from `<processing>` `device.rerouting.*` / `routing-algorithm`
`value=` attributes. Absent → rerouting inert → every existing scenario byte-identical.

## 8. Faithfulness risks (must honour)

1. **Phase order** (reroute reads previous-step weights; weight write is end-of-step) — hard requirement.
2. **`isDelayed` permanent latch** — port exactly (never-touched edges stay at free-flow; once-touched
   edges update forever). Not "update occupied edges."
3. **No improvement gate** — always install (short-circuit only on identical edge list).
4. **Moving-average incremental recurrence** — `avg += (new−oldest)/N`, not a recomputed mean (float drift).
5. **`getRerouteOrigin` brake-gap bump** — reroute from the next edge when within brake-gap of the junction.
6. **`minimumTravelTime` floor** — SumoSharp has no per-vClass min-travel-time-with-penalty concept yet;
   confirm it's `length / vType.maxSpeed` (timePenalty 0 for our nets) or design it before porting `getEffort`.

## 9. Task breakdown (each closes on its success condition)

- **P1E-1** config keys (§7) — `device.rerouting.probability/period/adaptation-steps/adaptation-interval`,
  `routing-algorithm`, **plus** the gated jitter flag (§0.5.1, e.g. `device.rerouting.jitter` — non-SUMO,
  our own key, default off). Unit test: parser reads the keys; absent → inert/byte-identical.
- **P1E-2** edge-weight aggregation (§1C, seam #2) — unit tests: seed, ring-buffer recurrence, `isDelayed`
  latch, end-of-step timing. Deterministic, exact.
- **P1E-3** A* router + effort fn (§1C/1D, seam #3) — unit tests: A*==Dijkstra on fixed weights; effort fn;
  admissibility preserved (effort floored at `length/maxSpeed`).
- **P1E-4** periodic reroute trigger + parallel batch + integration (§1A/1B, §3, §4) — wired before
  `PlanMovements`; **per-vehicle route-slot recycling** (§0.5.3, not a fresh id per reroute); no-gate
  install + identical-list short-circuit; skip-stale; **gated jitter offset** (§0.5.1). Parallel batch A*
  over a frozen weight snapshot; serial apply via `CommandBuffer.ReplaceRoute`.
- **P1E-5** scenarios (§6 Tier 2): **(a)** `scenarios/NN-reroute-congestion` faithful anchor + golden;
  **(b)** behavioural test (flow split, throughput-no-worse, jitter spreads reroutes). Full suite green.

## 10. Open questions — resolved
- **Q2** → RESOLVED (§0.5.2, §6): exact machinery + one faithful anchor; behavioural/statistical end-to-end.
- **Scenario authoring** → RESOLVED: owner OK'd hand-authoring a synthetic net (`netconvert`).
- **`pre-period` / pre-insertion rerouting** (`device.rerouting.pre-period`, default 60): **DEFER +
  document.** Our config doesn't set it; port only if the anchor scenario's golden shows it affects the
  result. Recorded here so it's not silently forgotten.

---

## 11. P1E-6 — pre-insertion rerouting (multi-lane faithfulness) [owner-approved follow-up]

**Why.** SUMO's `device.rerouting` reroutes each equipped vehicle **at departure** (pre-insertion,
`MSDevice_Routing::preInsertionReroute`, `MSDevice_Routing.cpp:240-274`, gated by `pre-period`,
default 60), not only periodically. Consequences we must match for real (multi-lane) SumoData nets:
1. On a **multi-lane** road the vehicle's strategic lane choice depends on its route (which turn it
   is heading for). A vehicle that only reroutes periodically (from `depart+period`) pre-positions
   for its *initial* route until then, landing in a different lane than SUMO's already-rerouted
   vehicle — the exact divergence observed on the 2-lane `45-reroute-congestion` variant (lane index
   differed; pos/speed were bit-identical).
2. Pre-insertion route choice is load-bearing for the doc's "~25% lower concurrent load" benefit
   (vehicles pick a better route *at departure* based on current congestion).

**Design.** At a vehicle's insertion (when `time >= depart`, before it is placed on a lane), if
equipped and `ReroutePeriod>0`: run ONE reroute from the departure edge (`route.Edges[0]`) to the
destination edge on the **current** `_edgeWeights` snapshot (same A*/Dijkstra + effort as the
periodic pass), and install it (route-slot recycling + lane-sequence re-resolve) BEFORE the
insertion path reads the vehicle's lane sequence — so the vehicle inserts and pre-positions on the
rerouted route. The periodic schedule (`NextRerouteTime = depart+period`) is unchanged (SUMO does
both). Reuse the periodic machinery; parallelise the route computation over the due-to-insert batch
(the herd applies here too), then serial install. Deterministic: pure function of settled prev-step
weights + A*.

**Faithfulness note.** SUMO's `pre-period` schedules pre-insertion within a horizon and can reroute a
still-waiting vehicle repeatedly; we do a single reroute at actual insertion (the common case with
`pre-period` ≥ typical wait). If a scenario shows this matters, revisit. Gate identical-list
short-circuit as in periodic (§1B).

**Acceptance (P1E-6):**
- Restore/duplicate `45-reroute-congestion` as a **multi-lane** net (2-lane prefix + detour); golden
  from vanilla SUMO 1.20.0; SumoSharp reproduces `(lane,pos,speed)` within tolerance — i.e. the
  lane-index divergence is gone. (Keep a single-lane variant too if useful for isolating the
  mechanism.)
- Functional test: an equipped vehicle's effective route at insertion reflects a pre-insertion
  reroute when the departure-time weights favour an alternate (distinct from the periodic path).
- Full suite green; inert (byte-identical) when `ReroutePeriod<=0`.
