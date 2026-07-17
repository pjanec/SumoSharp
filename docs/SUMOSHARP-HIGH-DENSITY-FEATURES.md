# SumoSharp — features needed for optimal high-density sub-area traffic

**Audience:** an autonomous Claude Code session working **in the SumoSharp repo**. Copy this file
into the SumoSharp repo (e.g. `docs/`) so that session has it. It is self-contained: it explains the
product context, *why* each feature is needed (with measured numbers), the exact current-state
evidence (file paths at checkout `137a047`), the acceptance test for each, and implementation/
thread-safety notes. It also lists **useful extras vanilla SUMO does not have** (attention-aware
popping) which are out of scope for parity but in scope for the product.

The reader decides what to build; this is the analysis, not a work order.

---

## 0. Context — what this engine is being asked to do

A separate project ("SumoData") produces **believable traffic inside a user-selected sub-area** of a
large road network, fully automatically, with one hard rule: **no visible cheating** — a car may
appear/disappear only at the cropped box **fringe** or **off-road inside a parkingArea**, never popping
on a visible travel lane. Believability, per the product owner, means **realistically dense traffic
that still flows** — a jam that never clears is *not* believable.

The pipeline crops a box, deduces demand from road-network topology (no demand files), and runs a
SUMO micro-simulation. SumoSharp is the C# engine port intended (eventually) to replace the vanilla
`sumo` binary for the **run** step, exploiting its multi-core speed. The tools
(`netconvert`/`randomTrips`/`duarouter`) stay vanilla — this document is only about the **engine run**.

**Current decision (SumoData side):** vanilla SUMO 1.20.0 stays the proven engine *until* we need to
modify simulation behaviour itself and use it in preprocessing — the motivating case is the
"attention-aware popping" extra in §5. So the parity features below are not urgent plumbing; they are
the prerequisite for (a) running the high-density config on SumoSharp at all and (b) the extras. Build
per the owner's steer.

### Why "high density" is the whole point
Measured on a real 3×3 km Geneva box (deduced demand, teleport off = strict no-cheating): the network
**reliably clears only up to ~2.7 veh/lane-km** before *stochastic* junction deadlock (permanent,
because teleport-off removes SUMO's escape valve). Turning on **`device.rerouting` + a 120 s teleport
valve** raised that to **~7 veh/lane-km with <1 % of vehicles teleporting** — ~2.6× denser, still
clearing. Those two behaviours are *the* high-density levers. SumoSharp today has effectively neither
(details below), so without them it is capped at the low strict knee and can deadlock permanently.

---

## 1. Acceptance model — behavioural parity vs vanilla SUMO 1.20.0

The owner requires **behavioural parity with vanilla SUMO** as the automated success check for every
feature that SUMO *has* (i.e. everything except the §5 extras). Use the repo's existing mechanism:

- Each `scenarios/NN-name/` holds `config.sumocfg` + `net.net.xml` + `rou.rou.xml`; golden outputs are
  regenerated from the **pinned pip SUMO 1.20.0** (`SUMO_VERSION`), and `tests/Sim.ParityTests`
  compares SumoSharp against the golden. The `Sim.Harness` parsers (`SummaryOutputParser.cs`,
  `TripInfoParser.cs`, `FcdParser.cs`) already read SUMO output for this comparison.
- For each feature below, **add a dedicated parity scenario** (named in the feature's Acceptance) with
  golden data from vanilla SUMO, and wire it into the `dotnet test` parity path. Parity = trajectories
  / per-step aggregates match golden within the harness's existing tolerances.
- For **density/stability** (no-deadlock at scale), use the separate `BENCHMARK_SPEC.md` harness
  (statistical, up to ~15k concurrent, not vehicle-for-vehicle) — the right tool to show
  rerouting+teleport let a dense plateau *drain* rather than lock.
- **Extras (§5) are excluded from parity** (vanilla SUMO cannot express them) — verify them with
  functional + statistical tests instead (defined per-extra).

Golden generation must use the SUMO config keys the SumoData pipeline actually sets (listed per
feature), so parity is against *our* usage, not a toy.

---

## 2. P0 — plumbing prerequisites (needed to even run/measure our scenarios)

These are not "high-density behaviours" but nothing below can be tested without them, because our
scenarios are loaded and measured through them.

### P0-A. `.sumocfg <input>` + multi-file `route-files` + `additional-files`
- **Current:** `Sim.Run` takes exactly one `.net.xml`/`.rou.xml`/`.sumocfg`;
  `src/Sim.Ingest/ScenarioConfigParser.cs` reads only `<time>`/`<processing>`/`<random_number>` —
  never `<net-file>`/`<route-files>`/`<additional-files>`.
- **We need:** load a `.sumocfg` whose `<route-files>` is a comma-list
  (`vType.config.xml,vType_pedestrians.xml,vTypeDist.config.xml,box.rou.xml`) plus
  `<additional-files>` (parkingAreas). This is how every SumoData run is configured.
- **Acceptance:** a parity scenario whose `config.sumocfg` references multiple route files + an
  additional file loads and runs identically to golden.
- **Priority:** P0 (prerequisite). **Effort:** low–moderate (parser + loader wiring).

### P0-B. `<vTypeDistribution>` resolution
- **Current:** no reference to `vTypeDistribution` anywhere in `src/`. Our demand uses
  `type="civ_vehicle"`, a `<vTypeDistribution id="civ_vehicle" vTypes="...">` — it never resolves.
- **We need:** parse `<vTypeDistribution>` and sample member vTypes per vehicle (SUMO uses the
  `probability` weights; seedable for parity).
- **Acceptance:** parity scenario using a vTypeDistribution reproduces the golden type assignment
  (with the same seed) and resulting trajectories.
- **Priority:** P0. **Effort:** low.

### P0-C. Symbolic depart attributes `departSpeed="max"`, `departLane="best"`, `departPos="stop"`
- **Current:** `src/Sim.Ingest/DemandParser.cs` defers these ("Task 3+"); it does
  `ParseNullableDouble(vehicleEl,"departSpeed") ?? 0.0`, so the literal `"max"` silently becomes
  `0.0`. Our pipeline sets `departSpeed="max" departLane="best"` on **100 %** of trips (and
  `departPos="stop"` for parked origins).
- **Why it matters for density:** inserting a car **at rest** onto a busy lane seeds a backward
  shockwave → artificial jams. `departSpeed="max"` injects it already at traffic speed;
  `departLane="best"` picks the emptiest lane. Without these, dense insertion *itself* manufactures
  gridlock, invalidating any density measurement. `departPos="stop"` is required for the no-cheating
  parked-origin cars.
- **Acceptance:** parity scenario with symbolic departs matches golden insertion speed/lane/position.
- **Priority:** P0 (also a confirmed correctness bug). **Effort:** low.

### P0-D. Engine writers for `--summary-output` and `--statistic-output`
- **Current:** the **harness** can *parse* these (`Sim.Harness/SummaryOutputParser.cs`,
  `SummaryStepRecord.cs`) for golden comparison, but the **engine run** (`Sim.Run`) has no writer;
  only `Sim.BenchCity` emits a partial summary (running/meanSpeed, missing
  halting/stopped/meanSpeedRelative) and no teleport tally.
- **We need:** `--summary-output` with per-step `running, halting, stopped, meanSpeed,
  meanSpeedRelative`; and `--statistic-output` with `<teleports total=…>`. **These are the calibration
  signals** — the SumoData auto-calibrator reads peak `running-stopped` (density) and `teleports total`
  (the pop budget). Without them SumoSharp cannot be calibrated even if it runs.
- **Acceptance:** the two output files are byte-comparable (within numeric tolerance) to golden on a
  parity scenario; the specific attributes above are present.
- **Priority:** P0. **Effort:** low–moderate (aggregate accessors already exist; add serializers).

---

## 3. P1 — the two high-density behavioural levers (the core of this work)

### P1-E. `device.rerouting` — periodic, congestion-reactive replanning
- **Current:** only a **one-shot Dijkstra reroute** triggered by a *persistent external obstacle*
  blocking a future edge (the B2/B3 path in `Sim.Core` — `ObstacleStore.cs`, reroute plumbing in
  `Engine.cs`/`CommandBuffer.cs`). This is **not** SUMO's periodic device.
- **We need** SUMO's `device.rerouting` semantics as our config uses them:
  `device.rerouting.probability=1.0`, `device.rerouting.period=30`,
  `device.rerouting.adaptation-steps=18`, `routing-algorithm=astar`. Behaviourally: every `period`
  seconds, each equipped vehicle re-routes from its current edge to its destination on a graph
  weighted by **live, smoothed edge travel times**, and switches if a better route exists.
- **Why we need it (numbers):** ~25 % lower concurrent load for the same demand (957→695 veh at one
  test point), it fills side-streets that otherwise look dead, and it is half of what lifts the knee
  from 2.7 to ~7 veh/lane-km.
- **Sub-parts (this is real engineering, not "add a router"):**
  1. **Live edge travel-time aggregation** — per-edge smoothed mean travel time updated from current
     speeds each step/interval; `adaptation-steps` is the smoothing window. SUMO's default weights are
     the device's own measured edge times.
  2. **Periodic per-vehicle reroute** — astar on the weighted graph, gated by `probability`, offset so
     vehicles don't all reroute on the same tick.
  3. **Thread-safety** — must integrate with the `Parallel.For` stepping (see §6) without racing on the
     shared edge-weight table; snapshot/double-buffer the weights per interval.
- **Acceptance:** new parity scenario `scenarios/NN-reroute-congestion` — a network with a
  congestible shortcut + alternate, `device.rerouting` on, golden from vanilla SUMO — SumoSharp must
  reproduce the route-switching and the resulting flow split within tolerance. (The existing
  `15-reroute` is obstacle-based; this is the *congestion* case.)
- **Priority:** P1 (highest-value behaviour). **Effort:** high.

### P1-F. Bounded teleport valve — `time-to-teleport` (jam) + counter
- **Current:** `time-to-teleport` is **parsed but inert** — it lives in
  `src/Sim.Ingest/ScenarioConfig.cs` (`double TimeToTeleport`) and defaults in `Engine.cs`, but no
  mechanism reads it to act; there is no teleport behaviour in the stepping loop.
- **We need** SUMO's jam-teleport: a vehicle stuck (speed ≈ 0, blocked, unable to move) for longer than
  `time-to-teleport` seconds is **teleported** — lifted off its lane and re-inserted on a downstream
  edge of its route (or removed if `time-to-teleport.remove` is set). Also the **teleport tally** for
  `--statistic-output` (`<teleports total=…>`), which is our pop budget.
- **Why we need it:** it is the anti-deadlock relief valve. Under teleport-off, high density
  deadlocks permanently; the bounded valve (120 s) lets the rare residual jam resolve, keeping the net
  draining. With rerouting it fires on <1 % of vehicles up to ~7 veh/lane-km. It is also the seam the
  §5 extra hooks into.
- **Sub-parts:** per-vehicle stuck timer; teleport action (remove from lane → place downstream / or
  despawn), thread-safe; jam vs yield vs wrong-lane classification (jam is the one we rely on);
  `time-to-teleport.remove` variant; teleport counter surfaced to output (P0-D).
- **Acceptance:** parity scenario `scenarios/NN-teleport-jam` — a deliberately deadlocking micro-net
  (e.g. tight junction spillback), `time-to-teleport=120`, golden from vanilla SUMO — SumoSharp must
  teleport the same vehicles at the same times and report the same `teleports total` (± tolerance),
  and the net must clear as in golden.
- **Priority:** P1. **Effort:** moderate–high.

---

## 4. P2 — investigate-then-fix (confirm these are real gaps under density before building)

My analysis is from *reading* source, not running SumoSharp at density. These two shape *where* the
deadlock knee sits; verify empirically (run our dense deduced demand and diff behaviour vs vanilla)
before investing.

- **P2-G. Junction behaviour at saturation** — deadlock forms at junctions (spillback, right-of-way
  cycles). SumoSharp has junction models (priority, right-before-left, TLS, allway-stop, keepClear —
  scenarios 08/11/26/27/34), but several `NEED-*.md` docs flag junction yield/impatience/saturation
  work. Weak saturation behaviour lowers the achievable density. **Verify** parity on a *saturated*
  junction (queues backing through), not just free-flow, then fix gaps. The teleport valve partly
  masks this, so it's P2 not P1.
- **P2-H. `max-depart-delay` / insertion-backlog policy** — whether a car that can't insert is dropped
  after N s or backlogs indefinitely. Affects dense-scenario fidelity and where "offered load" turns
  into "carried load". Confirm SUMO-parity of the backlog/drop behaviour.

---

## 5. Useful extras — capabilities vanilla SUMO does NOT have (no parity possible)

These are the reason to own an engine at all. They are **out of scope for parity** (there is no golden
to match); verify functionally + statistically. Build only on the owner's steer.

### X1. Attention-aware / camera-based selective popping (the flagship extra)
- **Idea:** the pop budget need not be spent uniformly. Maintain a **realism mask** over the network
  (per-edge or per-zone) that follows the camera / player attention. In the **high-realism zone**
  (what the user is looking at) enforce strict no-cheating — **no teleport, no on-lane spawn/despawn**;
  in the **low-realism zone** (off-camera) allow cheating freely — teleport eagerly, spawn/despawn on
  lanes, de-jam aggressively.
- **Why:** the global gridlock knee becomes a property of *the visible area only*. Jams and their
  resolution are pushed off-camera; the hero area runs denser than the global knee, and the <1 % pops
  that buy that density become **invisible**. This is the capability that vanilla SUMO's *global*
  `time-to-teleport`/insertion controls fundamentally cannot express — hence the owned port.
- **Depends on:** P1-F (teleport must exist as an engine action first) and the spawn/despawn path.
- **Design sketch:**
  - A `RealismMask` interface: `bool MayPop(edgeId)` / `bool MayTeleport(edgeId)`, settable at runtime
    (per step) from an external camera frustum → set of visible edges. Double-buffered for thread
    safety.
  - Gate the P1-F teleport action and the insertion/removal path on the mask. In the visible zone,
    fall back to no-cheating behaviour (hold the car / route it out via the fringe).
  - Optional: a **pop budget accounting** so off-camera popping is bounded and logged.
- **Acceptance (functional/statistical, not parity):** with a scripted moving camera over a dense run:
  (1) **zero** teleports/on-lane spawns/despawns occur on any currently-visible edge; (2) the net still
  drains; (3) achievable visible-area density exceeds the global no-cheating knee; (4) as the camera
  pans, popping migrates to the newly-hidden region. Unit tests on `MayPop`/`MayTeleport` gating.
- **Priority:** owner-driven; this is the strategic extra.

### X2. Secondary extras (record for later)
- **Per-zone rerouting aggressiveness** — stronger/cheaper rerouting off-camera.
- **Deterministic multi-thread replay** — if the parallel engine can guarantee reproducible results
  across thread counts, calibration becomes stabler (fewer seeds needed near the knee).
- **Direct in-memory trajectory export** (skip FCD XML) for the replay/viz path — a perf/plumbing
  nicety, not behaviour.

---

## 6. Threading — how these features must be built, and the perf context

SumoSharp parallelizes **within one simulation** via `Parallel.For` (not domain-decomposed processes);
per the perf docs it plateaus at **~8 threads on a 16-core/24-thread box (≈2–3.5× vs single-thread
SUMO, memory-bandwidth-bound; 16 threads ≈ 8, 24 worse)**, and the speedup only materialises when a
single run has high-hundreds→thousands of concurrent vehicles — exactly the high-density case.

Implications for the features above:
- **All new per-vehicle behaviours must be thread-safe under `Parallel.For`.** Rerouting's live
  edge-weight table and the teleport action mutate shared state — use per-interval snapshots /
  double-buffering / command-buffer deferral (the repo already has a `CommandBuffer.cs` pattern —
  prefer routing mutations through it).
- **Determinism matters for parity:** parallel reduction order can perturb floating-point results;
  keep the parity scenarios small enough that the harness tolerance holds, and prefer deterministic
  accumulation where a golden must match.
- **Interaction with SumoData's calibrator:** it currently runs many *independent* engine processes in
  parallel (one per density probe). With a multi-threaded engine the optimum is a
  (concurrent-probes × threads-per-probe) split; for our typically small/medium probes (~280–1 100
  concurrent) the guidance is to keep probes process/task-parallel with ~1–2 threads each up to ~cores,
  and only go few-probes×8-threads for large boxes — to be benchmarked on the target machine. (This is
  a SumoData-side concern; noted so the engine team knows small-sim concurrency is a real usage mode,
  which SumoSharp's own docs never benchmarked.)

---

## 7. Suggested dependency order (owner steers what to actually do)

1. P0-A/B/C/D (plumbing) — so our dense scenarios load and are measurable.
2. P1-E `device.rerouting` — the biggest density lever; needs the edge-weight aggregation.
3. P1-F teleport valve + counter — completes the "dense but drains" guarantee; unlocks X1.
4. P2-G/H — verify-then-fix junction saturation & insertion backlog if density targets aren't met.
5. X1 attention-aware popping — the strategic extra, once teleport exists.

Each of 1–4 lands with a vanilla-SUMO parity scenario as its automated acceptance gate; 5 lands with
functional/statistical tests (no vanilla parity).

## 8. Reference (SumoData side, for numbers/context)
The measured knees, the rerouting/teleport sweep, and the calibration method live in the SumoData repo
docs: `SUBAREA-METHOD.md`, `RESULTS-gridlock-sweep.md`, `RESULTS-autocalibrate.md`,
`PREPROCESSING-ENGINE-REQUIREMENTS.md` (the source of the parity checklist and threading numbers here).