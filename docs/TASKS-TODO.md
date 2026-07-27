# TASKS-TODO.md — Active work queue (open items only)

The short, live queue. **Completed work + the full detail/characterization of everything below lives in
the archive `TASKS-DONE.md`** — this file is just the open items with pointers. Other sessions:
coordinate here (add/claim items), keep it short, move finished items' detail to `TASKS-DONE.md`.

Iron law: `dotnet test tests/Sim.ParityTests -c Release` = **755/4** with all 661 goldens byte-identical;
`Sim.Bench` hash **`BF3794A4704BCD79`** (par==single); no `System.Random`. `Sim.LiveCity.Tests` = **50/50**
(⚠ **NOT in `Traffic.sln`** — `dotnet build -c Release` does not build it; build that csproj explicitly or
you will test stale code). `Sim.Pedestrians.Tests` = **272/272**.

> **The bench hash moved with PR #13** (`D96213B7BB4021A7` → `BF3794A4704BCD79`) because the seven
> junction/overlap gates now default **ON**. Verified attributable by stashing only the `Engine` defaults and
> reproducing the old hash; determinism itself is unaffected (par == single). `Sim.Bench` runs
> `_bench/highway-dense`, which has **no SUMO reference**, so this is a re-pinned tripwire, not a
> verified-correct value — the parity statement is the goldens, and all 661 stayed byte-identical across the
> flip. `.github/workflows/ci.yml` carries the same note. Parity count 664 → 755 is new tests only.

**In-flight by session** (live-city cluster; full boundary + no-touch lists in
`docs/COORDINATION-livecity-realism-sessions.md`):

| Session | Branch | Status | Scope / tracker |
|---|---|---|---|
| realism-A/B | `claude/task-a-held-crowd-swerve` | **A DONE — MERGED to main** (PR #12) | Task A (stopped-car lateral wobble): targeted redo `Engine.SuppressHeldCrowdSwerve` (held static-ped crowd-swerve suppression), guarded by F4a. Crosswalk scope verified (`CrosswalkCrossingPedTests`). Parity 664/4, bench `D96213B7BB4021A7`, LiveCity 45/45 |
| car-yields-ped | `claude/car-yields-crossing-ped` | **to be started** | **car→ped YIELD (Task B-guard)**: a car STOPS for a ped crossing/in its path instead of weaving past at ~5 m/s. Edits `ComputeLateralEvasion` crowd-swerve gate + `CrowdLongitudinalConstraint`. Repro committed: `CrosswalkCrossingPedTests`. Clear of the two running sessions (§coordination). Brief: **`docs/LIVE-CITY-CAR-YIELDS-PED-HANDOFF.md`** |
| ped–vehicle avoidance | `claude/livecity-ped-vehicle-avoidance` | to be started | car↔ped coupling **minus the yield**: B-api (`ExternalObstacle`→`WorldDisc`) + #5/C5 (car→ped disc feed) · `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`. (B-guard → car-yields-ped; #4 → ped-LOD-lifecycle.) |
| ped-LOD-lifecycle | `claude/livecity-ped-lod-lifecycle-bylitj` | **STARTED** | **ped LOD promote/demote switching** (low↔high power): #3 (promote handoff — ped vanishes) + #4 (demote doesn't fire / route not restored) + #6 (idle clustering). Edit surface = `src/Sim.Pedestrians/Lod/` (+ demand + viz snapshot); **does NOT touch any car-side surface**. Brief: **`docs/LIVE-CITY-PED-LOD-LIFECYCLE-HANDOFF.md`** |
| F3 junction / density | `claude/f3-junction-overlap-handoff-okf5nu` | **MERGED to main (PR #13)** | junction overlap + gridlock + **junction DISCHARGE**. Seven junction/overlap gates now default **ON**; the arm-14 four-way circular wait is fixed; the density-diff harness (vs *honest* SUMO) is in. Discharge is measured but NOT fixed — next step is a per-vehicle SUMO-oracle trace, see `F3-SESSION-LOG.md` §6. Docs: `F3-SESSION-LOG.md` · `DENSITY-DIFF-HARNESS-{DESIGN,TASKS,TRACKER}.md` |
| arbitrary-net | `claude/discussion-eqp53m` | **complete — merged (PR #11)** | net import · `SumoRouteGraphNav` · capability degrade · single zone · `RegionPlan` · fixture + tests. Detail: `TASKS-DONE.md` → "Arbitrary road-net import" |

*W4 (multi-camera zones) = unallocated. Sections below without a session tag are unclaimed backlog —
not a repo-wide board; other `claude/*` branches are not tracked here.*

**Parallel-safe (ped-LOD-lifecycle vs the car-side sessions).** The LOD promote/demote mechanism
(`PedLodManager`, `InterestSource`, route controllers in `src/Sim.Pedestrians/Lod/`) is structurally
separate from every car-side session's no-touch surface. The one shared interface is
`PedLodManager.HighPowerFootprints` → `ICrowdFootprintSource` → `Engine.CrowdSource` — a **produce/consume**
seam: the LOD session *produces* the footprint source, the car sessions (realism-A/B Task A, ped–vehicle
C5) *consume* it. Rule: the LOD session may change promote/demote **internals** (timing, route re-derivation,
the disappear/idle fixes) freely, but must **not** change the `ICrowdFootprintSource` contract or
`HighPowerFootprints` semantics without pinging the car sessions. Two files are touched by more than one
session — coordinate by editing your **own** method/region: `LiveCitySim.cs` (integration wiring) and
`OrcaCrowd.cs` (LOD uses Add/Remove agent lifecycle; ped–vehicle uses `SetExternalObstacles` — different
methods). Parity is untouched either way (the whole ped/LOD path is gated on `CrowdSource != null`, which no
golden attaches → still **661/4** byte-identical).

---

## Test infrastructure

- [ ] **Live-city test env-var isolation** *(owner: junction/F3 PR#13 test authors)* — `Sim.LiveCity.Tests`
  probes (`HeadOfQueueStallProbeTests`, `LongHorizonGridlockDiagTests`, `ArbitraryNetStageATests`) set
  process-global `LIVECITY_*` env vars that `LiveCityConfig.ForRepoRoot` reads, and xUnit runs collections in
  parallel → they race/leak into other tests' config (notably the `DenseFlow…NoGridlock` throughput test, which
  went 431/707/718 for the same config). **Interim mitigation already in tree:** `TestParallelization.cs`
  disables assembly parallelization (green 3/3). **Proper fix TODO:** a snapshot/restore `EnvVarScope`
  `IDisposable` on every `LIVECITY_*`-setting test + a single non-parallel `[Collection]` for them, then drop the
  blanket disable (or move `LiveCityConfig` off env vars entirely). Full root-cause + evidence + fix options:
  **`docs/LIVE-CITY-TEST-ISOLATION-ENV-RACE.md`**.

---

## Live-city realism (high-realism-zone demo) — active
Detail: `docs/LIVE-CITY-REALISM-1-2-DESIGN.md` (shipped #1/#2), `docs/LIVE-CITY-REALISM-ATTEMPT-LOG.md`
(trail), `docs/LIVE-CITY-REALISM-AB-DESIGN.md` (A/B brief), `TASKS-DONE.md` → "Realism violations in
high-realism zones".

### Fixes on branch `claude/livecity-realism-fixes` — some cherry-picked here, rest available to integrate
A prior session (`claude/livecity-realism-fixes`) shipped several car↔ped fixes against the *pre-arbitrary-net*
`LiveCitySim`. Two SAFE, no-overlap ones are now **cherry-picked onto main** (verified parity **755/4** +
bench **`BF3794A4704BCD79`**, par==single):
- [x] **`Engine.MaxCrowdDiscs` 16→256** — the crowd-disc query buffer. At density a car had a median 39 / max
  131 crowd discs in range, so the old `stackalloc[16]` truncated the in-path disc ~90% of the time → cars
  drove *through* peds. Parity-inert (gated on `CrowdSource != null`). **The B / ped–vehicle sessions depend on
  this** — their crossing/ORCA reactions rely on the crowd query not truncating at density.
- [x] **Viewer click-to-identify** — `ScenePayload.VehIds` emitted by `VizReplayBuilder` (from
  `IReplicationSource.Names`) + amber ring in `template.js`. Click a car → its `__vehN` id (matches trace
  names). Was inert before (payload emitted no `vehIds`).

**Still on that branch, NOT cherry-picked (overlap the in-flight sessions or marginal — integrate if wanted).**
The pre-refactor implementations don't apply cleanly to the new `LiveCitySim`; treat as reference:
- **Crossing-gate radius `1.5 m`** (enlarge the `CrossingOccupancySource` disc from the 0.3 m point) + **feed
  paused low-power peds** on a crossing (drop the `WalkAnimTag`-only filter). → overlaps **car-yields-ped**
  (B-guard). (Paused-feed fixed 0 measured cases — the "9 paused" was a metric artifact; low value.)
- **Velocity-preserving ORCA footprint inflate** (`InflatedFootprintSource`, extra radius ~0.6 m; A/B sweep
  found 0.6 kills mid-junction ORCA drive-throughs AND *raises* throughput, 0.8+ cliffs it) → overlaps **B /
  ped–vehicle** (a world-space hard guard is the better long-term approach for internal lanes).
- **Diagnostics** `--live-city-{yieldtrace,orcatrace,cartrace,yielddump}` + `LiveCitySim.{CrowdDiscCountsNear,
  IsOnCrossingPolygon,IsOccupancyMarkedAt,CrossingCentroids}` — headless car↔ped repro tools (per-car
  authoritative dumps, ORCA drive-through classifier). Overlap the sessions' own `--live-city-cartrace/drcheck`;
  port selectively.
Full detail: `docs/LIVE-CITY-REALISM-1-2-DESIGN.md`, `docs/LIVE-CITY-REALISM-ATTEMPT-LOG.md` (both on main).

**Session ownership (coordinated 2026-07):** this branch (`claude/livecity-realism-fixes-vr4k4b`) owns
**A only** (A now DONE). **B + C5 (#5) are ONE car↔ped coupling workstream** → the **ped–vehicle avoidance**
session (`claude/livecity-ped-vehicle-avoidance`, to be started), NOT this one — one owner for one mechanism.
**The ped LOD promote/demote lifecycle (#3 + #4 + #6) is a SEPARATE, parallel-safe workstream** → the
**ped-LOD-lifecycle** session (`claude/livecity-ped-lod-lifecycle`): its edit surface (`src/Sim.Pedestrians/Lod/`)
does not overlap any car-side session's no-touch list, and it only *produces* the `ICrowdFootprintSource` the
car sessions consume (#4 was previously mis-bucketed with ped–vehicle; its root is the demote trigger + route
restore, not coupling). See the in-flight table's "Parallel-safe" note for the exact boundary. The arbitrary-net session (`claude/discussion-eqp53m`) owned net import +
`SumoRouteGraphNav`/`IPedNavigation` + the single realism zone + `RegionPlan` and has **delivered** them
(PR to main — see `TASKS-DONE.md` → "Arbitrary road-net import"), leaving the seams in place; its C5
enablement is **BLOCKED** for the ped–vehicle session (which will road-net-enable + zone-bound the fed disc
set on the seam left behind). Multi-camera zones (W4) also handed off. Full boundary + no-touch lists:
`docs/COORDINATION-livecity-realism-sessions.md`. Briefs: `docs/LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`,
`docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`.

- [x] **A — stopped car wiggles sideways at a crosswalk — DONE (targeted redo).** The wobble was
  `ComputeLateralEvasion`'s crowd-swerve steering posLat a full lane-width while a car is held (nearly)
  stopped by a ped (`_sublane` false in the demo, so the SL2015 driver named in the brief is dead code). The
  first attempt (`Engine.FreezeLateralWhenStopped`: freeze ALL lateral commit below `LaneChangeMinSpeed`) was
  **too blunt** and **caused car–car overlaps** — it also pinned cars **mid-lane-change** (straddling → `gap=Infinity`
  → followers creep in → veh17/26, 18/49, 117/26). **Reverted, blanket clamp removed.** **Shipped redo:**
  `Engine.SuppressHeldCrowdSwerve` (default false; demo opt-in **on**, `LIVECITY_HELDSWERVE=0` disables) —
  in the crowd-swerve branch, when ego is HELD (`BindingConstraint == 13`) AND the ped is laterally STATIC
  (`LatSpeed ≈ 0`), recentre and wait in-lane instead of swerving. Only recentres (can't straddle → F2
  structurally impossible); leaves at-speed dodges / passes / lane-changes untouched (empirically: held =
  `binder 13`, passing = `binder 3`). **Guard added:** F4a straddle detector
  (`DemoAuthoritative_NoStoppedCarStraddlesPastItsLane`). Verified: parity **661/4**, bench
  `D96213B7BB4021A7`, LiveCity **27/27**, no new/worse overlap class (worst 3.035 m F3 + max pairs/frame 4
  unchanged; fix adds only 0.74 m / 0.09 m normal-lane overlaps, shallower than 6 pre-existing). Detail:
  `docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md` §F2, `docs/LIVE-CITY-REALISM-AB-DESIGN.md` §Task A.
  **Crosswalk scope verified** (repro `tests/Sim.ParityTests/CrosswalkCrossingPedTests.cs`, findings §F2
  "Crosswalk scope"): the wobble is the *static/stopped-mid-crossing* ped case → **fixed**; a *moving*
  crossing ped never floats a stopped car (fix inert). The distinct "car weaves around a crossing ped at
  speed instead of stopping" is NOT the wobble → routed to **B** below.
- [ ] **B — car close-fast-passes / weaves around ORCA peds instead of stopping** *(ped–vehicle avoidance session — to be started)*.
  High-realism-zone world-space hard ped-safety guard (car-stops-before-ped, NOT lane-projection based) +
  unify the string `ExternalObstacle` dodge/stop onto the `WorldDisc` seam. **Also owns** the crosswalk
  residual from Task A's repro: a car **anticipatorily dodges a crossing ped at ~5 m/s** rather than yielding
  (crowd-swerve's "prefer swerve over hard-stop", `ComputeLateralEvasion`) — the hard guard must override it.
  Minimal unit repro: `CrosswalkCrossingPedTests`' crossing-ped setup. Briefs: AB-DESIGN §Task B,
  `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md` §4.
- [x] **Realism #3 — low-power peds DISAPPEAR on promotion** — FIXED (ped-LOD-lifecycle). Root: the promoted
  ped had `Model=FreeKinematic` on the wire but no pose sample yet (origin-snap → culled), and the crowd frame
  was fragmented by heartbeats interleaved among the samples (receiver kept only the last fragment → frozen
  peds). Fix: seed-on-switch in `HeadlessIg` + emit samples contiguously in `PedLodManager.Step`. Trace: wire
  mismatches 3627→0, ped fidelity ≤0.28 m. (task #25)
- [x] **Realism #4 — ORCA peds leaving the zone STAY ORCA and wander** — RESOLVED (ped-LOD-lifecycle). Trace
  evidence (400/1600 peds, static & moving zone, ≤250 s): NO server-side stuck-ORCA — demotion fires correctly
  and demoted peds rejoin on-graph routes; the visible wander was the #3 wire bug. So #4a (leaky-dwell/watchdog)
  was **dropped** as unnecessary; #4b off-graph route recovery (`PedLodManager.RecoverRoute`) was added as cheap
  hardening for the rare (0.2%) null-`FindPath` case. (task #25)
- [ ] **Realism #5 (= arbitrary-net task "C5"; distinct from Group-C C5 `keepClear` below) — ORCA peds
  don't dodge a car standing on the crosswalk**; needs a car→ped obstacle feed (mirror of the ped→car
  `CrowdSource`). *(ped–vehicle avoidance session)* (task #26)
- [x] **Realism #6 (LOW PRIORITY)** — low-power peds merge to a SINGLE junction point and idle — FIXED
  (ped-LOD-lifecycle). Root (trace): 12041/12199 idle rows were `animTag=wait` — signalized-crossing kerb waits,
  every ped held at the exact crossing-entry vertex. Fix: a per-ped seeded 2-D waiting BLOB at the kerb +
  diagonal cross (`PedDemand`, opt-in `CrosswalkWaitSpreadRadius`, demo-only). The 23-peds-on-one-point stack
  becomes a dense blob (busiest 0.5 m cell 2.5%). *(ped demand; no car-side surface)*
- [ ] **W4 — multiple / large / overlapping camera realism zones** *(handed off; unallocated — ped–vehicle
  avoidance or a later dedicated session)*. N ped `InterestSource`s, N-zone car LC-realism, `SetLcRealismZones` API, re-point
  the C5 disc-feed bound at the zone union, optional bit-identical `OrcaCrowd` disc index (the one `Sim.Core`
  touch, must stay parity-inert). Handoff: `docs/LIVE-CITY-MULTI-CAMERA-REALISM-ZONES-HANDOFF.md`.

## Demo integrity (from the 2026-07 replay review — realism-A/B session)
Full evidence + root causes: **`docs/LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md`** (F1–F4). **Resume/handoff state:
`docs/LIVE-CITY-DEMO-INTEGRITY-RESUME.md`** (read first when picking this up fresh). Diagnostics:
`--live-city-cartrace` (authoritative per-car) and `--live-city-drcheck` (DR-render + authoritative overlap
check). **Order finalized after two analyses — F3 is pre-existing CORE (localized vs `main`), and F3 masks
F2 in aggregate → the F2 guard must be targeted:** F4a → F1 → Task A redo (this session); **F3 routed to
core junction work**; F4b deferred until F3 fixed.

- [x] **F4a — targeted F2 straddle guard — DONE** (`DemoAuthoritative_NoStoppedCarStraddlesPastItsLane`).
  Detects F2 by its true signature: `PosLat` frozen unchanged past the lane edge (>1.2 m) for ≥10
  consecutive stopped ticks (raw peak `|PosLat|` can't separate — the crowd-swerve reaches ~5 m both ways).
  Verified: green freeze-off (0 ticks); FAILS freeze-on (58 ticks, Vehicle#19.1 @3.18 m); LiveCity 27/27.
- [x] **F1 — RESOLVED / DOWNGRADED (not a render bug).** Repro-first (authoritative `--live-city-cartrace`/
  `--live-city-drcheck`): the engine respects the red (veh80 stops on red, enters on GREEN); the player cannot
  overshoot position (`template.js` `clampBox`); the demo TLs are all `static`/`offset=0` so the rendered light
  is in lock-step with the engine (no desync). "veh80 drove through veh120 ignoring red" = a **misread** (veh80
  on green) **+ a real car–car overlap** (veh80's green crossing runs through stopped veh120/veh134, ~1.8 m) =
  **F3-family**, folded into F3 (garage-stub-into-junction / keep-clear sub-case). No render-layer fix. §F1.
- [x] **F2 — Task A redo — DONE**: targeted crowd-swerve suppression (`Engine.SuppressHeldCrowdSwerve`, NOT a
  blanket lateral freeze), guarded by F4a. Empirically discriminated by `binder 13` (held) vs `binder 3` (passing).
  See the **A** item above + `LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md` §F2. §F2.
- [ ] **F3 — pre-existing junction-overlap engine bug — ROUTE TO CORE JUNCTION WORK (not this session).**
  LOCALIZED: present on `main` too (identical worst pair `veh134/veh38`, 3.035 m) — long-standing, not a
  realism regression. Cars on crossing internal junction lanes overlap ~3 m. Into-occupied / conflict-point
  family (`LANE-CHANGE-OVERLAP-*`, `ISSUE2-JUNCTION-*`, `LIVE-CITY-15-INTO-OCCUPIED-DESIGN.md`). Blocks the
  clean zero-overlap invariant (F4b). Now also owns the F1 **keep-clear / garage-stub-into-junction** sub-case
  (veh80/veh120, veh80/veh134 ~1.8 m). **Self-contained handoff (reuses this branch): `docs/F3-JUNCTION-OVERLAP-HANDOFF.md`**
  — root cause = missing into-occupied admission gate in `JunctionYieldConstraint`'s foe loop
  (`Engine.cs:6890–7134`, gated on `RespondsTo`/`egoHasSignalPriority`); port `MSVehicle::checkLinkLeaderCurrentAndParallel`
  /`checkRewindLinkLanes`; NOT parity-inert (SUMO-diff + golden regen). §F3.
- [ ] **F4b — general zero-overlap invariant (DEFERRED until F3 fixed).** Tighten the committed authoritative
  test + add a DR-render overlap check, asserting ZERO once F3 is resolved. The current
  `DemoCarOverlapInvariantTests` stays as an F3 characterization + gross-regression tripwire meanwhile.
  **Bundled with F3 in `docs/F3-JUNCTION-OVERLAP-HANDOFF.md` §6** (the `--live-city-drcheck` DR pass is the
  ready-made infra). §F4.

## Junction DISCHARGE / max-density (session: F3 junction / density) — active

**MEASURED, not estimated.** Max sustainable open-loop inflow: **ours ≈1.4 veh/s, SUMO's 1.6–2.0**. And at
1.4, where *both* engines are steady, our halting fraction is **33.3%** against SUMO's **33.7%** — identical —
yet our trips take **247.7 s** to SUMO's **180.6 s**. Same stopping, same routes ⇒ **our cars ROLL at ~8.0 m/s
where SUMO rolls at ~11.0.** The problem is *not* junctions blocking; it is uniformly slower progress, which
inflates residency ~25% at every inflow and then tips into **collapse** (trips 4448 → 2938 → 1681) where SUMO
stays steady.

Detail — read in this order:
`docs/F3-SESSION-LOG.md` **§6** (next action) and **§9.125-130** (how this was established) ·
`docs/DENSITY-DIFF-HARNESS-{DESIGN,TASKS,TRACKER}.md` (the harness; TRACKER carries every measured table) ·
`docs/reports/density-inflow-sweep.txt` (the sweep) ·
`docs/CONSTRAINT-high-realism-artefact-ladder.md` (**binding** — what we may not copy from SUMO).

### DONE
- [x] **A1** three-column SUMO runner (`scripts/run-density-diff.sh`) — S-default / S-honest, cheat isolation
      asserted in-script.
- [x] **A3** open-loop demand (`CarInflowVehPerSec`, `--inflow/--series`, `scripts/sweep-inflow.sh`) — this is
      what made discharge measurable at all.
- [x] **B1** demand recorder → SUMO `.rou.xml`, so both engines see identical cars.

### NEXT — and it is a TRACE, not a hypothesis
- [ ] **TRACE-1 — per-vehicle SUMO-vs-us diff inside `jyArm 2`.** Open-loop at **1.4 veh/s** (both steady;
      never diagnose inside our collapse), same recorded demand, `--fcd-output` on the SUMO side and an
      equivalent dump on ours. Pick one vehicle whose trip is near our mean and well above SUMO's, diff
      step-by-step, and find **where** the seconds are lost — which edge, which junction, approach vs
      interior — together with our binder / `jyArm` at exactly those steps. **Only then name a mechanism.**
      Rationale: **seven** reasoned-from-source interventions have now been refuted here against **one**
      SUMO-oracle trace that found a real cause in minutes.
- [ ] **B2/B3** our global metrics + per-junction discharge in SUMO's schema (crossings per 60 s, queue,
      internal occupancy) — needed to turn TRACE-1's single-vehicle finding into a population claim.
- [ ] **C1/C2/C3** gap decomposition, sweep, ranked work list.
- [ ] **Equalise pedestrians** in the diff — SUMO got **none** while our runs had 160 blocking crossings, an
      uncontrolled variable favouring SUMO.
- [ ] **Reroute + insertion-refusal counters** — both currently **NOT MEASURED**, so demand fidelity between
      the engines is unquantified.

### REFUTED — do not re-attempt (each has its measurement)
- [x] ~~**G1 `KeepClearHeldPropagation`**~~ — the `checkRewindLinkLanes` gap its own NEED ranks
      "highest impact". Measured **worse**: trips 2938 → 2762. It makes admission *more* conservative, the
      opposite of widening a drain. Kept, default OFF.
- [x] ~~**`MinorApproachArrivalSpeed`**~~ — SUMO's nonzero arrival-speed target for minor links. **+67%
      throughput at 1.6 and eliminated the collapse — and broke 14 goldens.** The goldens are SUMO's output,
      so the change is unfaithful; `arrivalSpeed` is arbitration metadata, not step speed. Kept, default OFF,
      labelled REFUTED **because the +67% localises where the capacity hides: `jyArm 2` under load.**
- [x] ~~`addBlockedLink`~~ — dead code in SUMO 1.20.0 (only reader commented out at both call sites).
- [x] ~~entry-time ordering for non-bay foes~~ — provably inert.
- [x] ~~any capacity claim from **closed-loop** demand~~ — retracted "96% of SUMO"; the demo's spawn loop
      self-throttles and cannot express a deficit.

### ⚠ TWO HARD CONSTRAINTS ON EVERY ITEM ABOVE

**1. Both surfaces must accept a change; neither alone can.** One change this session passed every golden and
made the demo *worse*; another transformed the demo (+67%) and broke **14 goldens**. Run the goldens **and**
the open-loop discharge test, every time.

**2. Target SUMO's FLOW, never SUMO's METHOD.** SUMO's drain is partly wider because it **lets cars overlap
inside junctions** — **26** junction collisions that its own defaults (`collision.check-junctions=false`) do
not even check for, clustered on the exact lanes we wedge on. Plus `time-to-teleport=300` and
`collision.action=teleport`. Those are ladder rungs 3 and 4. Reject any port whose mechanism amounts to
permitting interpenetration or teleporting.

---

## Viewer / demo bugs
- [ ] **Raylib replay: scrubbing the timeline makes cars jerk/jump-back** and never recover. (task #10)

## Deferred (owner will action later)
- [ ] **Detach the live-city DEMO data from the LOCKED regression fixture** — `scenarios/_ped/demo_city/box`
  is both the demo dataset and a committed regression fixture. Detail: `TASKS-DONE.md` → "Deferred — detach
  the live-city DEMO data".

## Parity / realism roadmap — characterized, NOT yet briefed
Future SUMO-parity + realism ladder. Each is a one-liner here; the full characterization (references,
scenarios, scope) is in `TASKS-DONE.md`. Pick one → write its briefing → move the detail's status there.

- [ ] **Group A remaining** — A2 overtaking (speed-gain lane change); A-impatience (junction-yield
  arrival-time gap acceptance, DEFERRED). (`TASKS-DONE.md` → Group A)
- [ ] **Group C — realism beyond the deterministic phase-1 core** (`TASKS-DONE.md` → Group C):
  C1 statistical parity `sigma>0` (do first — unblocks the rest); C2 strategic route-driven lane changes;
  C4 remaining right-of-way (right-before-left, roundabouts, stop signs); C5 junction-blocking avoidance
  (`keepClear`); C6 actuated/adaptive TLs + yellow decision; C7 `speedFactor` distribution; C8 ballistic
  integration + `actionStepLength>1`; C9 cooperative lane changes; C10 continuous lateral changes;
  C11 alt car-following (IDM, ACC/CACC); C12 pedestrians & crossings / public transport.
- [ ] **Group D — FastDataPlane ECS readiness** (make the engine FDP-shaped; readiness not integration).
  (`TASKS-DONE.md` → Group D)
- [ ] **Group E remaining** — opposite-overtake OV deferred items (D1 cross-lane hard-brake backstop,
  D2/D3), see `OV-REMAINING.md` + `TASKS-DONE.md` → Group E "Remaining".

---
*Split from the old monolithic `TASKS.md` (grown to ~2.5k lines). This file = open items; `TASKS-DONE.md`
= archive with full detail. Keep this one short.*
