# TASKS-TODO.md — Active work queue (open items only)

The short, live queue. **Completed work + the full detail/characterization of everything below lives in
the archive `TASKS-DONE.md`** — this file is just the open items with pointers. Other sessions:
coordinate here (add/claim items), keep it short, move finished items' detail to `TASKS-DONE.md`.

Iron law: `dotnet test tests/Sim.ParityTests -c Release` = **781/5** with all 661 goldens byte-identical
(775/4 before the traffic-bugs branch, which adds tests and perturbs none);
`Sim.Bench` hash **`A134ED3716DDE7BC`** (par==single); no `System.Random`. `Sim.LiveCity.Tests` = **90/90**
(**IS in `Traffic.sln`** since `f4f39a4` — plain `dotnet test -c Release` from the root runs it; the old
"not in the sln, build explicitly" warning is stale and cost a session a wrong process lesson). `Sim.Pedestrians.Tests` = **324/324**. `demos/City3D/CityLib.Tests` (also not in
the sln) = **186 pass / 4 skip** in ~45 s. The four skips are `[RealTimeFact]`s — render-loop tests that
cost ~1 s of wall clock per simulated second because `DrClock` tracks wall time and the scenarios are
`step-length = 1`. **Run them (2 m 20 s, then 190/190) after any change to `Sim.Viewer.Motion`,
`CityLib.Reconstructor`, or the render-clock/playout plumbing:** `CITY3D_REALTIME_TESTS=1 dotnet test …`.
Reasoning + why a `--filter` alone cannot enable them: `demos/City3D/CityLib.Tests/RealTimeFactAttribute.cs`.
⚠ Also **clear `~/.nuget/packages/sumosharp.*` before repacking City3D**, or the version-pinned local feed
serves a stale engine and you measure code you are not looking at.

> **The bench hash moved AGAIN at journal Entry 34** (`BF3794A4704BCD79` → `A134ED3716DDE7BC`, commit
> `05653f4`, the speedGain-RIGHT lane-change arm shipping default-ON) — attributed by the 3D-test
> session's two-point bisect (`45a2214` old / `05653f4` new) and reproduced at `fa6a865`; par == single
> holds and all 661 goldens stayed byte-identical, same re-pinned-tripwire status as the move below. It
> went UNDOCUMENTED for five entries — when the bench hash moves, re-pin it in THIS block in the same
> commit.
>
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
| car-yields-ped | `claude/live-city-car-yields-ped-i4rczr` | **DONE — PR open to main** | **car→ped YIELD (Task B-guard)** delivered + the `QueryNear` nearest-first fix it uncovered. Demo @800 peds: in-zone close-fast-passes **203 → 14**, of which car-driving-AT-a-ped (HEAD-ON) **11 → 0**; throughput unchanged; parity 775/4, bench `BF3794A4704BCD79`, LiveCity 53/53. Docs: **`LIVE-CITY-CAR-YIELDS-PED-{DESIGN,TASKS,TRACKER}.md`** (open items in the TRACKER's "Still worth doing") |
| ped–vehicle avoidance | `claude/livecity-ped-vehicle-avoidance` | to be started | car↔ped coupling **minus the yield**: B-api (`ExternalObstacle`→`WorldDisc`) + #5/C5 (car→ped disc feed) · `LIVE-CITY-PED-VEHICLE-AVOIDANCE-HANDOFF.md`. (B-guard → car-yields-ped; #4 → ped-LOD-lifecycle.) |
| ped-LOD-lifecycle | `claude/livecity-ped-lod-lifecycle-bylitj` | **STARTED** | **ped LOD promote/demote switching** (low↔high power): #3 (promote handoff — ped vanishes) + #4 (demote doesn't fire / route not restored) + #6 (idle clustering). Edit surface = `src/Sim.Pedestrians/Lod/` (+ demand + viz snapshot); **does NOT touch any car-side surface**. Brief: **`docs/LIVE-CITY-PED-LOD-LIFECYCLE-HANDOFF.md`** |
| F3 junction / density | `claude/f3-junction-overlap-handoff-okf5nu` | **MERGED to main (PR #13)** | junction overlap + gridlock + **junction DISCHARGE**. Seven junction/overlap gates now default **ON**; the arm-14 four-way circular wait is fixed; the density-diff harness (vs *honest* SUMO) is in. Discharge is measured but NOT fixed — next step is a per-vehicle SUMO-oracle trace, see `F3-SESSION-LOG.md` §6. Docs: `F3-SESSION-LOG.md` · `DENSITY-DIFF-HARNESS-{DESIGN,TASKS,TRACKER}.md` |
| arbitrary-net | `claude/discussion-eqp53m` | **complete — merged (PR #11)** | net import · `SumoRouteGraphNav` · capability degrade · single zone · `RegionPlan` · fixture + tests. Detail: `TASKS-DONE.md` → "Arbitrary road-net import" |
| external-net viewer / 3-D elevation + engine perf + threaded tick | `claude/handoff-docs-implementation-pmdu9z` | **PR CANDIDATE for main** — code done, gated, and Stage 2 GPU-verified | arbitrary-net loading in City3D (`NetPath`/`ForSumocfg`), float recenter, live density dials, ped elevation end-to-end, lane provenance, **z made mandatory** (breaking), baked **terrain field**; **coupled cars+peds engine perf** (5 k + 20 k at ~114 ms/step, RTF ~4.4×, alloc 17.4×/5.5× down); **threaded engine tick** (Stages 1–3 + A22) with the render-clock and self-pump fixes the GPU run required; **parallel car+ped reconstruction** and road-mesh tiling in the viewer. Gate: parity **775/4**, bench `BF3794A4704BCD79` par==single, `Sim.Pedestrians.Tests` **324/324**, `Sim.LiveCity.Tests` **90/90**, `CityLib.Tests` **186 pass / 4 skip** (190/190 with `CITY3D_REALTIME_TESTS=1`). GPU: 3 858 cars + 20 726 peds, **0/2000 spikes**, p99 = 1.20× p50. Docs: **`EXTERNAL-NET-VIEWER-{DESIGN,TASKS,TRACKER}.md`** · **`LIVE-CITY-PERF-{DESIGN,TRACKER,SESSION-LOG}.md`** · **`LIVE-CITY-THREADED-TICK-DESIGN.md`** (§8 = what actually landed). GPU sign-off **DONE** (owner) for both halves. Open items below: Geneva low-power ped z=0 (downgraded — the target IG ground-clamps), three unexercised §8.2 GPU items. |

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
**Coordinator orientation + owner-replay recipe: `docs/LIVE-CITY-REALISM-COORDINATOR-HANDOFF.md`** (start
here for the cross-session picture + the ZIPPED ~1k-ped/~300-car Sim.Viz replay recipe).
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
  **Superseded as the safety mechanism by the car-yields-ped branch**, which made
  `ICrowdFootprintSource.QueryNear` return the *nearest* movers rather than an arbitrary enumeration-order
  subset (all three implementations, via `WorldDiscQuery`). Measured: the safety property (zero cars driving
  AT a ped) holds at **every** buffer size including the original 16 — it comes from the contract, not the
  number — while the buffer stays a fidelity knob that saturates at 64 for 800 peds. So this is now
  headroom, not the thing standing between the demo and cars driving through people. Sweep + reasoning:
  `docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md` §8, §8.2.
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
- [x] **B-guard — car close-fast-passes / weaves around ORCA peds instead of stopping — DONE**
  *(car-yields-ped session)*. Three pieces, all behind a world-space yield zone (`Engine.SetCrowdYieldZone`,
  radius 0 = off, wired to the camera-driven LC-realism zone): **L1** suppresses the crowd swerve in-zone so
  a car holds behind a ped in its path instead of weaving past it; **L2** `CrowdYieldConstraint` (**binder
  16**) adds an anticipatory yield against the ped's *predicted* corridor track — which catches conflicts
  binder 13's current-overlap sample structurally misses — plus a world-space proximity cap (stop at
  contact, creep below 1.5 m) evaluated on the *predicted* clearance so it is reachable under braking.
  Repro (`CrosswalkCrossingPedTests`): 0.70 m clearance @ 3.90 m/s → 2.00 m @ 3.67 m/s, zero weave, holds
  while the ped is in the lane, back at maxSpeed one tick after it clears. Demo @800 peds
  (`DemoPedYieldInvariantTests`): in-zone close-fast-passes **203 → 14**, HEAD-ON **11 → 0**, arrivals
  unchanged, `DenseFlow…NoGridlock` green. Detail + the open items:
  **`docs/LIVE-CITY-CAR-YIELDS-PED-{DESIGN,TASKS,TRACKER}.md`**.
  **Still open from the original B scope** (→ ped–vehicle avoidance session): **B-api**, unifying the string
  `ExternalObstacle` dodge/stop onto the `WorldDisc` seam — deliberately not folded in (own parity surface).
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

### Opened by the car-yields-ped session (measured; see that TRACKER's "Still worth doing" for the evidence)
- [ ] **Out-of-zone cars are BLIND to pedestrians.** `CrowdSource = Composite(HighPowerFootprints,
  CrossingOccupancy)` and peds promote to HighPower only inside the LC-realism zone, so outside it a car
  sees a ped only if that ped is on a crossing. Measured cross-tab @800 peds: every `HighPower` event
  in-zone, every `LowPowerWalking`/`Paused` event out-of-zone; arming the yield **net-wide** as a probe arm
  barely helped (3739 → 3458) because the cars have no data. This is a **ped-LOD feed** decision with a real
  perf cost, not a car-yield change — it bounds how far any car-side ped safety work can go. *(unallocated;
  natural fit = ped-LOD-lifecycle or ped–vehicle)*
- [ ] **`OrcaCrowd.QueryNear` is a full scan** since the nearest-first contract removed its early exit.
  **MEASURED — it does NOT scale badly, and an earlier note here saying it would was wrong.** The scan is
  over the crowd the car can actually see, which is `HighPowerFootprints` = the **promoted** population
  only, and `OrcaCrowd.Count` is a slot high-water mark. Measured (200 steps, warm):

  | total peds | promoted (live) | slots scanned | ms/step (160 cars) |
  |---|---|---|---|
  | 800 | 7 | 67 | 12.1 |
  | 1600 | 38 | 132 | 15.6 |
  | 3200 | 123 | 275 | 35.0 |

  Doubling CARS is invisible in wall time (160 → 320 cars: 12.1 → 12.4 ms at 800 peds; 35.0 → 34.6 at
  3200), so the O(cars × agents) term is below the noise floor — the growth with ped count is the
  **ped-side ORCA/LOD** cost, not this scan. At dt=0.5 s, 35 ms/step is ~15× inside the real-time budget.
  **Do not "optimise" this now: at 67–275 slots a 121-cell grid lookup plus the order-preserving sort
  would very likely be SLOWER than the linear scan.** Revisit only when one of these lands, and measure
  first:
    * **much larger / multiple realism zones** (W4) — promoted count scales with zone area, and the owner's
      standing requirement is "honor the zone radius, no matter perf";
    * **feeding low-power peds to the car side** (the "out-of-zone cars are blind" item above) — that makes
      `CrowdSource` the WHOLE population, at which point the scan really is O(total peds) per car and the
      grid becomes necessary. **These two items are coupled: fixing the blindness is what makes this
      urgent.**

  If it is done, the existing `UseSpatialHash` grid (already ON for the demo crowd in `PedLodManager`, and
  rebuilt every `Step`, so it is already paid for) is the vehicle — but it is **not a flag flip**:
  `GridCandidates` is agent-indexed with a hard-coded 3×3 ring sized for `NeighbourDist = 15 m`, whereas
  `QueryNear`'s radius reaches ~66 m (an 11×11 ring); the grid is rebuilt BEFORE the crowd commits its
  move while the engine queries AFTER, so a query must inflate the ring by `maxSpeed × dt` or it
  reintroduces exactly the silent-miss class the nearest-first contract just removed; and the candidate
  list must stay sorted ascending by index, as `GridCandidates` already does, to keep the nearest-k
  tie-break (enumeration order) deterministic.
- [ ] **`MaxCrowdDiscs` 256 → 64** — measured identical at 800 peds for 4× less stack per call site, and
  with the nearest-first contract the degradation is graceful. Kept at 256 only for the 10×-density headroom
  f9c837c measured. Low priority (wall time is flat across 16…256).
- [ ] **One home for the vehicle-pose convention.** `Sim.Ingest/VehicleObb.cs` (box↔box) states — correctly
  — that the naviDegree + front-bumper conventions must have exactly ONE implementation, beside the
  `LaneGeometry` that defines them. `Sim.Core/VehicleFootprint.cs` (box↔disc, added by car-yields-ped) is a
  second encoding of the same two conventions in a different assembly. Correct and rotation-tested today;
  consolidate before a third appears.
- [ ] **Stale brief:** `archive/LIVE-CITY-CAR-YIELDS-PED-HANDOFF.md` §7 cites `--live-city-orcatrace` /
  `--live-city-cartrace`, which no longer exist in `src/Sim.Viz/Program.cs` (removed by the T1–T3 viz
  refactor). Any handoff pointing at them needs the same correction.

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

## External-net viewer / 3-D elevation (session: `claude/handoff-docs-implementation-pmdu9z`)
Everything in this cluster is **built, gated and pushed**; what is left is the one thing this
environment structurally cannot do. Design/tasks/tracker:
`docs/EXTERNAL-NET-VIEWER-{DESIGN,TASKS,TRACKER}.md` (the follow-ups are **Stage E**, E1–E5).

- [ ] **Geneva low-power peds still report z = 0 — DOWNGRADED from showstopper (owner, on-GPU).** The
  original report was that ~10 000 peds on Geneva's arterials sit at elevation 0 instead of ~400 m.
  **It is no longer a blocker: the target IG GROUND-CLAMPS**, so the wrong height is hidden downstream
  and the scene looks right. Still worth fixing — we are shipping a z we know is wrong, and any consumer
  that does NOT clamp (or that needs the height for anything but placement) gets a bad number.
  - **Partially fixed** (`789a4b8`): `HeadlessIg.ReconstructElevationAt` was reordered so
    timeline-bearing peds — including ones **promoted** to high-power, which keep their timeline and
    previously fell through to `return 0.0` — read the surface off the timeline channel. That fixed the
    promoted high-power population only.
  - **Still open: the low-power crowd** (the thousands). Working hypothesis, **not yet confirmed**: their
    `ActivityTimeline` walk legs carry an **empty `Elevations` channel** on the RouteGraph (external-net)
    path, so `HeadlessIg.TimelineElevationAt` finds no channel and returns `0.0`. If so the real fix is
    upstream in the ped timeline elevation bake, not in `HeadlessIg`.
  - **NEXT STEP IS AN INSTRUMENT, NOT A HYPOTHESIS** (CLAUDE.md measurement-discipline #2). Log, per ped:
    `PedDrModel`, whether `Timeline.Elevations` / `PathZ` are populated, and the returned z — on the
    Geneva cut. Two of three attempts at this bug so far were reasoned rather than traced, and both were
    incomplete. Files: `src/Sim.Pedestrians/Lod/{HeadlessIg,ActivityTimeline,ActivityTimelineWire,
    PathArcMotion}.cs`, and wherever `WalkSegment.Elevations` is populated for RouteGraph nav vs the demo
    Navmesh path. Background: `docs/handoffs/WIN-GPU-VISUAL-TEST-terrain-and-ped-z.md` §7.
  - Perf interaction: widening that branch in `789a4b8` sends **more** peds through
    `TimelineElevationAt`. The per-ped double geometry scan that made costly has since been fixed
    (`9987aba`, single-scan + parallel ped reconstruction), so this no longer gates on it.

- [x] **E5 — visual sign-off on a GPU. DONE (owner, on-GPU).** Part A (3-D terrain, ped heights, the
      baked grid, tinted zones) confirmed working on the Geneva data; Part B (the threaded tick) verified
      at 3 858 cars + 20 726 peds — **0/2000 spikes**, p99 = 1.20× p50, 2 Hz sustained in real time, with
      smooth motion after the render-clock fix (`5159667`). Checklist:
      `docs/handoffs/WIN-GPU-VISUAL-TEST-terrain-and-ped-z.md`. Three §8.2 items remain unexercised —
      see below.

- [x] **The deliberate C5·SC1 contract break needs no ack — owner confirms no session has unmerged
      work.** The 4-out-param `TryGetRenderPose` overload is deleted and every call site edited to
      `out _`; the contract's success condition said they would compile unedited. Owner's call, reasoning
      in `EXTERNAL-NET-VIEWER-DESIGN.md` §4.1.1. No conflict risk remains.

- [x] **The three `CityLib.Tests` `ReconstructorS2Tests` failures — FIXED. They were TEST bugs, not
      reconstructor bugs.** Traced rather than reasoned about, and the trace is worth keeping because all
      three had the same shape: the assertion did not measure what its own comment said.
      1. **Wrong pacing (the root cause of two of them).** The wall-clock frame loop slept a hardcoded
         **15 ms**, but every scenario it drives has `step-length = 1`, so one `sim.Tick()` is a whole
         second — the loop ran the sim at **~22× real time**. `DrClock` advances its render clock at *wall*
         rate scaled by a fitted wall↔sim rate and caps catch-up at `frameDt · simRate · 3` (`DrClock.cs:255`,
         a deliberate anti-jump guard) with `frameDt` clamped to 0.1 s. At 15 ms/frame that cap is 0.045
         sim-s/frame against a 0.333 feed, so the clock fell ~1 s behind and never recovered. Measured:
         stopped-pivot **6.57 m → 2.49 m** (L/2 = 2.50) and junction stray **1.01 m → 0.13 m** purely by
         sleeping `dt / FramesPerTick`. **The reconstructor was correct the whole time.**
      2. **Stoppedness filtered on the wrong side.** Both stopped-vehicle tests filtered on the
         *reconstructed* speed — a pose from `Delay` seconds ago — while comparing against the *live*
         snapshot. So the frames right after the light turns green, where the car has already pulled away in
         the snapshot, were admitted as "stopped". Worth 4.83 m of the pivot max and 0.61 m/frame of the
         creep max. Comparing two instants needs both to satisfy the premise.
      3. **A frame-rate-dependent threshold written as an absolute.** The creep bound was `max per-frame
         metres < 0.12`, reasoned from a 60 Hz loop ("~0.2 m at 13 m/s / 60 Hz") — so at the correct 333 ms
         pacing the same physical hold covers 20× the distance and it failed at 0.61 while the body was
         settling at centimetres per second. Now a **speed**, measured against the frame's own Stopwatch
         duration (not the nominal sleep, which overshoots under load), with two bounds: max < 1.5 m/s
         catches the original gross bug (a driving car is ~13 m/s), median < 0.2 m/s separately pins the
         steady state, because one settle frame as the 0.6 s smoothing constant converges is not creep and
         only the median can tell them apart. Measured: max 0.53, median 0.035.
      **Cost, and how it is contained:** the wall-clock loops are real-time now, so they cost ~1 s of wall
      clock per simulated second — the suite went 28 s → 2 m 18 s. Tick counts were trimmed to the minimum
      each assertion needs (48→31, 44→36, 60→40), and the four are now **`[RealTimeFact]`, SKIPPED unless
      `CITY3D_REALTIME_TESTS=1`** — so the default run is **186 pass / 4 skip in ~45 s**, and the full
      **190/190** is one env var away. Skipped rather than excluded on purpose: they show up in the output
      with their reason, so "not run" is visible instead of being a silent absence.
      ⚠ These failures had been **masked** for an unknown period: `demos/City3D/build.sh` writes
      `SumoSharp.*.0.1.0.nupkg` at a version that never changes, so NuGet's global cache served a stale
      engine. Always clear `~/.nuget/packages/sumosharp.*` before repacking.

## Viewer: decouple the engine tick from the render thread (owner will execute Stages 2–3 later)

Design of record, with the full architecture, the hazard list and per-stage success conditions:
**`docs/LIVE-CITY-THREADED-TICK-DESIGN.md`**.

**The measured symptom.** Owner, timed with a metronome at ~**4 000 vehicles + 8 000 peds**: a **100–200 ms
hiccup ~110 times per minute**, smooth in between. 110/min = **1.83 Hz** ≈ the **2 Hz** tick. **Not GC**
(pause ~0.9% of wall, zero gen2). Cause: `Tick()` → `LiveCitySim.Step()` runs **synchronously on the Godot
main thread** inside `_Process` (`demos/City3D/Viewer/Main.cs:1740-1745`), so the frame blocks for a whole
engine step — and the surrounding `while` runs *several* steps in one frame when behind, so falling behind
compounds. The magnitude is simply one engine step at that scale (headless: 114 ms/step at 5 k + 20 k), so
no engine optimization is a prerequisite for fixing the smoothness.

- [x] **Stage 1 — frame-time instrument + 1–20 Hz engine tick-rate slider.** *(in flight this session)*
      HUD + `--frame-log` CSV (frame ms, p50/p95/p99, **count of frames > 3× p50**, sim ticks per frame);
      slider showing **requested vs ACHIEVED** Hz (20 Hz needs a ≤50 ms step, so the ceiling at 5 k + 20 k
      is ~8.8 Hz). Needs a settable timestep on `LiveCitySim` — `Step()` currently takes no `dt`.
- [x] **Stage 2 — run the tick on its own thread; zero-alloc car handoff.** **IMPLEMENTED + gated; the
      ON-SCREEN half is NOT verified** (needs a GPU + Stage 1's instrument — see the new item below).
      What landed, and the deviations, are in **`LIVE-CITY-THREADED-TICK-DESIGN.md` §8**: the published
      snapshot is a **lock**, not the lock-free triple buffer §5 proposed (the hand-rolled version had a
      real stale-slot bug when the consumer polls faster than the producer — a test caught it, §8.1); the
      vehicle records are **not** triple-buffered, the replication bus was made concurrent + pooled instead
      (§8.2); `Tick`/`Sample`/`SampleCars`/`SampleCrossingSignals` now **throw** once threaded (§8.6).
      A22 shipped with it, and capping parallelism was proven trajectory-inert: **11 889 car+ped samples
      bitwise identical**, uncapped vs capped. *Original spec, for the record:* Producer thread runs `Step()` in a loop; publish by **triple buffering + `Interlocked.Exchange`**
      (three preallocated `VehicleRecord[]` slots + count/simTime/step; producer fills the spare, consumer
      claims one and holds it so it can never be overwritten mid-read; grow only on warmup ⇒ **zero
      steady-state allocation**). Also replaces `PublishFrame`'s existing per-step `movers.ToArray()`.
      Handoff volume is **≤ ~800 KB/tick ≈ 30–60 µs of memcpy** (~0.05% of a step), so copying is free —
      the only requirement is preallocated destinations. Render clock = published sim time + wall delta,
      **never allowed past the newest published sim time**, with the existing playout-delay slider (default
      1 s) as the jitter absorber; otherwise DR extrapolates past known state and you get rubber-banding
      instead of stutter. **Four things are NOT thread-safe today and must be fixed, not assumed:**
      (1) `InMemoryReplicationBus._queue` is a plain `Queue<Entry>`, **not** concurrent;
      (2) `PedPublisher._events` is a plain `List<PedEvent>` of **reference types** appended per ped per step;
      (3) the render thread currently **writes** to the sim — `PushLcZone()` → `SetLcRealismZone(...)`, and
      `SampleCars()` hands back `LiveCitySim`'s **shared reused scratch buffer**; (4) `TlStateByLane` is a
      `Dictionary` read every frame while the tick mutates it.
      ⚠ **Do Stage 2 together with A22 (cap engine parallelism).** Both parallel regions are currently
      unbounded (up to all 24 logical cores), so the producer thread will crowd out the renderer and the
      display driver — a threaded tick that still saturates every core can leave frame hitches in place.
      Leave ~2–4 cores for render + driver.
      *Success:* on Stage 1's numbers, at the same scenario/counts, **frames > 3× p50 → ~0** and p99
      approaches p50; no DR regression (must not reintroduce the #7 cruise stutter or #8 backward creep).
- [x] **Stage 3 — zero-alloc ped handoff.** **IMPLEMENTED, scoped down (§8.3).** The `_events` history is
      drained + cleared every step (**A6** closed), the per-step batch list is reused, and the ped bus is
      concurrent with pooled payload buffers. Measured: **0 new buffers over 60 steps after warmup**,
      retained history **0 after every one of 120 steps** (4 151 events genuinely published, peak batch 67),
      wire-vs-sim ped poses agreeing to **0.092 m** worst over 15 161 paired samples.
      **NOT done, deliberately:** the struct-array payload + a second `HeadlessIg` apply path. That bus
      exists to round-trip the real wire codecs, and a parallel apply path doubles the surface the
      server==IG identity rests on. The remaining per-tick allocation is the `PedEvent` records themselves
      (reference types, one per published ped per step) — a wide change through `HeadlessIg`'s pattern
      matching and many tests, so it is its own task.

- [x] **The Stage-2 ON-SCREEN verification — DONE ON GPU 2026-07-28. PASS, after one required fix.**
      Geneva cut (28 276 lanes), RTX 5080, startup line
      `tick on producer thread, engine parallelism capped at 20 of 24 cores`.
      Measured at **3 858 cars + 20 726 peds**, last 2 000 settled frames:

          spikes(>3x p50)   0 / 2000          (was accumulating ~2/s, one per tick)
          p50 / p95 / p99   46.3 / 50.0 / 55.6 ms      (p99 = 1.20x p50)
          sim_ticks         184 / 2000 frames (0 on ~91%)
          sim_time          0.5 s per ~10-11 frames = 2 Hz sustained in real time

      Owner confirms 4 k cars + 20 k peds now move smoothly. Both §8.2 headline criteria met. Logs:
      `<scratchpad>/run3_oneclock.csv` (after), `run1_jumpy_evidence.csv` (the broken intermediate).
      ⚠ **A measured BEFORE run was NOT captured** — threading was already the default when this session
      started, so the baseline is the design §1 metronome observation (100–200 ms, ~110/min at ~4 k cars +
      8 k peds), not a CSV.
- [x] **Stage-2 follow-up fix — ONE render clock for cars AND peds** (`5159667`). **This was required to
      make Stage 2 pass**, so anyone reading the ticked box above should read this too. Stage 2 wired the
      new clamped `_renderSimClock` to the **peds** only (`pedNow = _renderSimClock`) and left the **cars**
      on `Reconstructor`'s private `DrClock`, which fits its own wall↔sim rate from
      `LatestVehicleSampleTime` off the replication bus — two clocks over two handoffs in one scene, which
      cannot stay in agreement. Symptom on GPU: cars took a kick impulse per publish then decelerated
      ("caterpillar", worse with density, **not smooth even at low load**) while the frame loop was
      provably clean (16.7 ms p50, `sim_ticks` 0, 60 fps). Fix: optional `renderSimClock` on
      `Reconstructor.Reconstruct`; query instant becomes `renderSimClock − delaySeconds` through the
      existing `DrClock.ResolveAt` seam. Default path untouched (DDS/replay/scenario unchanged).
      **Note for anyone revisiting:** `frameDtOverride`'s deterministic branch is *not* the fix — its
      instant only moves when a packet arrives, so at 2 Hz it would freeze ~0.5 s then jump.
- [x] **`InMemoryReplicationBus.HistoryView` concurrent modification — FOUND AND FIXED** (`9987aba`,
      `LiveCitySim.SelfPumpVehicleBus`). Recorded in full because the diagnosis matters more than the fix:
      `InvalidOperationException: Collection was modified` at `HistoryView.GetEnumerator()+MoveNext()` ←
      `CityLib.Reconstructor.Reconstruct` ← `Main.ProcessLiveCity`, escalating to **13 per run at 10 000
      cars**, each aborting that frame's whole car pass.
      **The offending pump was `LiveCitySim.Step()`'s own `_vehBus.Source.Pump()`** — the sim self-pumped
      the bus it publishes to, harmless while `Step()` and the consumer shared a thread and a straight race
      once a producer thread owned `Step()`. So Stage 2's claim that *"every dictionary `PumpCore` mutates is
      touched exclusively on the consuming thread"* was **false as landed**, and that comment was the reason
      nobody looked here. Fix: `SelfPumpVehicleBus` (default **true**, so `Sim.Host.App` / `Sim.Viz` / the
      LiveCity tests are unchanged); the threaded viewer sets it **false** and pumps from its consumer, once
      per frame, inside `Reconstruct`.
      ⚠ **The originally-guessed fix direction — "hand the consumer an immutable history snapshot" — would
      have hidden this rather than fixed it**: a snapshot per frame removes the *symptom* while leaving two
      threads mutating the same dictionaries. Worth remembering next time a concurrency symptom invites a
      defensive copy.
- [ ] **Remaining §8.2 items not yet exercised on GPU:** the sim-Hz slider swept 1 → 20 with achieved-Hz
      tracking and *stopping* below the request under load; `H` zone cycle (Central → Follow → Locked) with
      the ring tracking the camera; repeated clean quits including *while* dragging a slider at high
      density. Cars/peds/fill sliders and one clean quit were exercised.

## Engine performance — coupled cars+peds live-city (overnight 2026-07-28)

**Detail lives in `docs/LIVE-CITY-PERF-SESSION-LOG.md`** (append-only: goals, the harness with every
command + why to run it, the measurement protocol, and one entry per attempt with BEFORE/AFTER numbers —
including the NULLs and the failed hypotheses). Companions: `LIVE-CITY-PERF-{DESIGN,TRACKER}.md`.
Threaded-tick work has its own doc: `LIVE-CITY-THREADED-TICK-DESIGN.md`.

**Shipped overnight** (all gated: parity 775/4, bench `BF3794A4704BCD79` par==single, LiveCity 80/80,
Pedestrians 317/317, city-3000 0-stuck + aggregate PASS; each paired-A/B'd with behavioural counters
proven identical): target **5 000 cars + 20 000 peds now runs at ~114 ms/step, RTF ~4.4× at 2 Hz, with
0/60 frames over 3× median** (was 11/60). Ped-only 20 k: 110.5 → 47.5 ms/step. Car-side allocation
10.17 MB → 586 KB/step; coupled 264 → 48 MB/step; GC pause 9.0% → 2.5%.

**NOT STARTED — open items, each one line + its log ID:**
- [ ] **A19 · `MaxNeighbours` is uncapped — the largest remaining lever, but BEHAVIOURAL.** ORCA considers
  *every* agent within 15 m (~283 at pocket density) where RVO2 ships a default of **10**; `orcaCrowdStep`
  is still ~50% of wall on the ORCA-heavy scenario. Changes ped trajectories ⇒ must ship **opt-in, off by
  default** (CLAUDE.md rule 3) with a behavioural argument, not just a speed number. **Owner decision.**
- [ ] **A18 · attribute the residual allocation.** `engine.plan` ~520 B/car/step and `engine.execute`
  ~370 B/car/step are **unexplained**. Note `PERF-ROADMAP.md`'s "the plan phase is allocation-free" claim
  is **falsified for this host** (it was measured car-only with `CrowdSource` null). Do not guess — my
  source-reasoned guesses went 0-for-3 before a gate bisection found the real cause.
- [ ] **A21 · ped spawn is O(ped-graph size) per spawn**, and the graph scales with **junction count**. A
  40×40 grid cost ~390 ms/step for ~12 spawns, which is what forced the committed bench net down to 15×15.
  ⚠ *Earlier note that this explained the Geneva viewer hiccup was WRONG* — the owner measured that hiccup
  at **~4 000 vehicles + 8 000 peds**, not the 160-car default I assumed from the startup log, and a
  100–200 ms step at that scale is ordinary step cost (cf. 5 k + 20 k = 114 ms/step). A21 stands on its own
  merits as a spawn-cost bug; its contribution to that hiccup is unmeasured.
- [ ] **A10 · `Engine._bestLanesCache` is silently defeated** in this host: `SpawnVehicle` mints a unique
  `RouteId` per vehicle, so the memo never shares across vehicles and also **never shrinks** (unbounded
  growth). Re-key on edge-sequence content — byte-identical by `ComputeBestLanes`' own signature (it takes
  the edge list, never a route id). ⚠ **Also check first:** the key ignores `stopOverride`, which changes
  the result — if any live caller passes it, the cache is returning **wrong values** today (a correctness
  bug that outranks the perf work).
- [ ] **A6 · `PedPublisher._events` is never cleared** — one heap record per sample/switch/heartbeat, for
  every ped that ever lived, retained for the whole process lifetime. Unbounded gen2 growth. Consumers read
  a cursor into it, so a drain design must update them together. (Stage 3 of the threaded-tick work covers
  this.)
- [ ] **A5 · per-step O(N) allocation in `PedLodManager.Step`** — `new List<int>(_peds.Keys)` +
  `new Dictionary<int,Vec2>(N)` **every step** (~755 KB/step at 20 k peds). Reusable buffers; byte-identical
  provided iteration order is untouched (ids are sorted ascending for determinism).
- [ ] **A16 · `engine.insert` is O(pending × active)** — failed insertions are retried every step and each
  retry runs `ResolveBestDepartLane`, which scans `ActiveVehicles()` (live because the host spawns with
  `departBestLane: true`). **Saturation-only:** 15.8% of wall on the gridlocked demo net, 0.8% at the
  target. Bites exactly when a user cranks density past capacity. Fix = per-edge memo invalidated on each
  successful insertion (a plain within-step memo would *not* be byte-identical).
- [ ] **A22 · engine parallelism is UNCAPPED in the live-city host — likely using too many cores.**
  `Engine.MaxParallelism` (`Engine.cs:926`) and `OrcaCrowd.MaxParallelism` (`OrcaCrowd.cs:272`) both default
  to `MaxDegreeOfParallelism = -1` (TPL default ⇒ up to **all 24 logical processors**), and **nothing in
  `LiveCitySim`, the viewer, or `Sim.BenchLiveCity` sets either** — only `Sim.BenchCity`/`BenchCrowd`/
  `BenchPedLod`/`SumoShim` expose a cap. There are now **two** unbounded parallel regions per step: car
  `plan`+`willPass` (auto ≥256 vehicles) and ped ORCA plan (auto ≥256 high-power agents, enabled in
  `1c51c25`).
  **Evidence that 24 is too many** — `PERF-HANDOVER.md`'s on-target sweep on this box (city-3000, car-only):
  serial 11.48 s · 2t 7.90 · 4t 6.34 · 8t **5.68** · 16t 5.67 · 24t **6.13**; efficiency 73% / 45% / 25% /
  13% / 8%. So **24 is slower than 8**, 16 buys nothing, and the efficiency knee is ~4 — matching the
  owner's recollection that ~4 cores was most effective before peds. Aggravating: this box is a **hybrid**
  Core Ultra 9 275HX (8 P-cores at logical `{0,1,10,11,12,13,22,23}` + E-cores), and unbounded TPL schedules
  a bandwidth-bound loop onto E-cores.
  **Viewer-specific and the reason this matters beyond throughput:** the tick currently runs *on the render
  thread*, so during a tick the engine saturates all 24 logical cores including those Godot and the display
  driver need — plausibly **lengthening the measured hiccup**. It does not fully go away with threading
  either: a producer thread's `Parallel.For` still crowds out the renderer, so a cap belongs **inside** the
  Stage-2 design (leave ~2–4 cores for render + driver), not beside it.
  **Actions:** (a) add `MaxParallelism` to `LiveCityConfig`, plumbed to **both** `Engine` and `OrcaCrowd`
  (they are separate settings); (b) expose it in the viewer next to the tick-rate slider; (c) add
  `--max-parallelism` to `Sim.BenchLiveCity`; (d) sweep **4/6/8/12/24 at ~4 000 cars + 8 000 peds** (the
  owner's actual viewer configuration) and pick the knee. `DOTNET_PROCESSOR_COUNT` can sweep this with
  **zero code change** as a first read. Measure on a quiet box — never during a build.
  **Safety:** thread count must never change results. `Sim.Bench`'s `hashA == hashPar` covers the car side,
  and `tests/Sim.ParityTests/OrcaParallelStepTests.cs:119` already asserts ORCA bit-identity **under an
  explicit `MaxParallelism` cap**, so both surfaces are guarded.

- [ ] **A20 · sweep for other `stackalloc … : new …[]` thresholds.** This defect class produced the two
  biggest wins of the night (17.4× and 5.5×): a threshold that silently stopped covering its caller's
  runtime-sized span. Finding a third by accident would be luck, not method.
- [ ] **A7 · pack ORCA's hot neighbour triple** (`pos`, `vel`, `radius` — read together, currently in three
  separate arrays) onto one cache line. Only if it still dominates after A19. Note this is the **opposite**
  of per-field SoA, which `PERF-HANDOVER.md` #4 measured and rejected for the car foe reads.
- [ ] **A14 was a NULL** (ORCA `ScratchSet` pooling, −0.8%, reverted) and **ped region decomposition was
  measured at 1.08× vs a 1.4× target** — do not re-attempt either.

## Viewer / demo bugs
- [ ] **Raylib replay: scrubbing the timeline makes cars jerk/jump-back** and never recover. (task #10)

## Deferred (owner will action later)
- [ ] **Detach the live-city DEMO data from the LOCKED regression fixture** — `scenarios/_ped/demo_city/box`
  is both the demo dataset and a committed regression fixture. Detail: `TASKS-DONE.md` → "Deferred — detach
  the live-city DEMO data".

## ⚠ The drop-in binary runs three junction gates OFF that everything else runs ON

**Found 2026-07-28 while inventorying the env gates for `docs/ENV-GATES.md`. Verified in source, not
inferred.** This is an engine-behaviour bug in the `sumosharp` drop-in CLI — the binary the SumoData
pipeline invokes via `SUMO_BINARY` — not a documentation problem.

`src/Sim.Sumo/SumoShim.cs` sets three gates with the two-state form
`GetEnvironmentVariable(name) == "1"`, which **forces `false` whenever the variable is absent**. All three
`Engine` defaults are now `true`:

| Gate | `Engine` default | via `SumoShim`, env unset |
| --- | --- | --- |
| `SUMOSHARP_CONTTURNFIX` → `ContTurnInsideJunctionGate` (`Engine.cs:12968`) | `true` | **`false`** (`SumoShim.cs:260`) |
| `SUMOSHARP_ISLEADERFIX` → `JunctionIsLeaderGate` (`Engine.cs:13110`) | `true` | **`false`** (`SumoShim.cs:267`) |
| `SUMOSHARP_INTERNALJUNCTIONFIX` → `InternalJunctionAdmissionGate` (`Engine.cs:13148`) | `true` | **`false`** (`SumoShim.cs:274`) |

All three source comments still assert `Unset/non-"1" => false, the Engine default`. That was true when
written and false from the same commit onward.

**The irony is the useful part of the diagnosis.** PR #13 (`604ad72`) both flipped the seven gates ON *and*
introduced `LiveCitySim.EnvGate` specifically to prevent this, with a comment naming the failure mode
exactly: a two-state override "silently FORCES OFF whenever the variable is absent… became a live bug the
moment the defaults flipped to true", and the resulting report "would have looked like a failed fix rather
than a wiring mistake." `LiveCitySim` was fixed in that commit. `SumoShim` was not.

**Why CI is green:** the tests that touch these gates (`InternalJunctionAdmissionEndToEndTests`,
`LowDensityTeleportTests`) set them explicitly to `"1"`, so nothing exercises the unset shim path. The
goldens go through `Engine` directly, so they are unaffected — which is exactly why this survived.

**The fix is one line each** (switch to `EnvGate`-style fallback), **but it is behavioural**: it changes the
drop-in binary's default trajectories. So it needs the full treatment, not a quiet edit — goldens **and**
the open-loop discharge test, per the two hard constraints in the discharge section below. Worth checking
whether it explains any SumoData-side measurement taken through `SUMO_BINARY`, since every one of those
runs had three gates off.

- [x] **DONE (journal Entry 19).** The three `SumoShim` gates now use `EnvGate(name, engineDefault)`; the
      three now-false comments are corrected; `SumoShimUnsetGateFallbackTests` covers the **unset** shim
      path behaviourally (unset must be byte-identical to explicitly-at-defaults, with a vacuity guard);
      and `EnvGateDocumentationTests.GatesWhoseEngineDefaultIsTrue_AreNotReadWithTheTwoStateForm` now
      asserts the safe read form for the three default-true gates — verified to FAIL, naming file:line,
      when one read is reverted. Not asserted blanket-wide on purpose: the two-state form is correct for
      a default-`false` gate, and four legitimately use it.
      Gate after the change: **776 passed / 5 skipped / 0 failed**, 26-net battery **unchanged**.
      Reference: `docs/ENV-GATES.md` §"The three-state trap".

## Surfaced by the docs audit (2026-07-28) — real open work that lived only in a doc

These were found by the housekeeping pass described in `docs/DOCS-HOUSEKEEPING-PLAN.md`, which read all
268 docs and asked of each whether it still holds. Each item below was **verified against the source
first-hand** before being listed here — a doc claiming something is broken is a lead, not a fact. They are
recorded here because their detail lives in a doc that is now archived or banner-stamped, and the one
outcome the audit was not allowed to produce was losing a known bug.

### Engine correctness (each has a `NEED-*` note with the full trace)
- [ ] **`Engine.LinkStateChar` reads the wrong hop for a `cont` link** — it misses SUMO's
  `getCorrespondingEntryLink()`, so a continuation link inside a junction is asked for its own state
  rather than its entry link's. Full write-up, including why it matters for signal-driven yielding:
  **`docs/NEED-linkstatechar-cont-entry-link.md`**. Method at `Engine.cs:~14098`.
- [ ] **The stuck-reroute is blind to vehicles inside junctions** — `Engine.cs:10169` hard-`return`s when
  the stuck vehicle sits on an internal lane, so the one recovery path that could clear a wedged junction
  never fires for exactly the vehicles that wedge it. Accounts for 2 of the 5 vehicles in the recorded
  repro, and where it *does* fire it is one-shot. **`docs/NEED-stuck-reroute-blind-inside-junctions.md`**
  §"the fix"; cross-referenced from `F3-SESSION-LOG.md` §19 as a pre-existing root cause. Related to the
  **F3** item above — read that first.

### Determinism / configuration hygiene
- [ ] **`scenarios/44-multilane-junction-turn/config.sumocfg` does not pin the integrator.** Every other
  phase-1 scenario sets `<step-method.ballistic value="false"/>` explicitly; this one relies on the
  default. It passes today, so this is a latent tripwire rather than a live bug — but CLAUDE.md's
  determinism rule is that these are set *in the scenario config*, not inherited. Add the element and
  confirm the golden stays byte-identical.
- [ ] **`SumoShim.cs:259` reads `SUMOSHARP_CONTTURNFIX` straight from the environment**, bypassing any
  config surface, and `SumoShim.cs:60`/`:262` document the choice as deliberate. That is exactly the
  process-global gate pattern CLAUDE.md measurement-discipline #10 warns about: an inherited shell value
  is indistinguishable from a measured one, so any A/B that forgets to set it explicitly in *both* arms is
  silently invalid. Either fold it into the config object or add it to the gate-echo list the benches print.

### Pedestrians
- [ ] **PED-REALISM-1 — low-power peds pass through each other.** The `LateralWeave` production promotion
  shipped (W1–W4, `PedDemandConfig.EnableWeave`), which addresses the *appearance* of interaction, but the
  underlying pass-through is not solved: low-power peds have no mutual avoidance at all. Design and the
  §8 production-seam requirements: **`docs/PEDESTRIAN-LOWPOWER-AVOIDANCE-DESIGN.md`**; tracked in
  `PEDESTRIAN-TRACKER.md` as PED-REALISM-1.
- [ ] **P8-2 — `PedSpawnPolicy` is built but has no consumer.** The appearance-legitimacy gate exists as a
  mechanism and is unreferenced from `PedDemand.cs` / `PedLodManager.cs`, so nothing enforces it yet.
  `docs/PEDESTRIAN-P8-2-APPEARANCE-LEGITIMACY-DESIGN.md` (the doc is honest about this; the tracker marks
  it `[~]`).
- [ ] **P8-4b — dynamic per-crossing throughput guard**, deferred pending the vehicle-calibration seam.
  `docs/PEDESTRIAN-P8-4-DENSITY-DESIGN.md`.
- [ ] **P6-2 phase 2 — the ORCA SoA reorder**, deliberately deferred after phase 1 measured **1.08×**
  against a 1.4× target. `docs/PEDESTRIAN-P6-2-RESULTS.md` carries the numbers. ⚠ Do not re-attempt phase 1
  (region decomposition) — that is the measured NULL recorded as A14's sibling above.

### Trailing items on otherwise-finished work
- [ ] **VIEWER-KINEMATIC-SMOOTHING T3.3 — owner desktop sign-off still pending** (Godot 3D + Raylib 2D).
  Everything else in that tracker is done; the box is `[~]` for the sign-off alone.
  `docs/VIEWER-KINEMATIC-SMOOTHING-TRACKER.md`.
- [ ] **IgBridge F3 (optional) — look-ahead is under-used on sustained curvature** (roundabouts): the
  current hard-reject should become a temporal smooth. `docs/IGBRIDGE-RESUME.md` §F3, which the other
  IgBridge docs treat as the live working state.
- [ ] **HIGH-DENSITY-CALIBRATION Stage 4 residual + Stage 6 re-run.** Stage 4 is `[~]`: main throughput was
  cracked in session 2 but a residual remains. Stage 6 landed (`ca8d515`) and is **PENDING** one thing this
  repo cannot do — SumoData re-running the real box against it. `docs/HIGH-DENSITY-CALIBRATION-TRACKER.md`,
  design §2.3.9.
- [ ] **`docs/CALIBRATION-KNEE-INDEX.md` records a falsified conclusion as its finding.** Its
  `arterial-tjunction` row (line ~62, repeated ~79) still cites "turn-lane mis-segregation" as the
  FCD-traced root cause. `docs/GETBESTLANES-RESUME.md`'s own session-5 update **falsified** that
  ("the knee's dominant cause is NOT lane-choice") and re-attributed it to keep-right rule 2 — but the
  correction was never propagated to the index, so the index is now the more prominent and more wrong of
  the two. Fix the index; keep the refuted hypothesis in the resume doc, per CLAUDE.md
  measurement-discipline #2.
- [ ] **`docs/LIVE-CITY-PERF-TRACKER.md` has 16 unticked boxes and 0 ticked**, including **P0**
  (`src/Sim.BenchLiveCity`), which demonstrably exists and is the instrument every perf number in the
  section above was measured with. The tracker was never updated as the work landed, so it now reads as
  "nothing started" for work that hit its target. Reconcile it against the "Engine performance" section
  above — but tick only what can be substantiated first-hand, per that tracker's own stated rule.

### Not a bug — a reviewer claim that did NOT survive checking
`F3-JUNCTION-OVERLAP-TRACKER.md`'s **T1.6–T1.9** were reported by the audit as "unchecked but actually
done". **False** — they are genuinely open and explicitly blocked on each other (T1.5a's flag is blocked on
T1.7; T1.9 is blocked on T1.8). The unticked boxes are correct and must stay unticked. Recorded here so the
next pass does not "fix" the tracker by ticking them.

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
