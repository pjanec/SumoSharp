# HIGH-DENSITY-HANDOFF.md — checkpoint handoff for the continuation session

**Checkpoint (session 2 — 2026-07-17):** feature branch
`claude/sumosharp-high-density-cont-cpi2q4`, fast-forwarded to `main`. Full suite **green: 571 passed /
3 skipped / 0 failed** (`Sim.ParityTests`) + 1 (`Sim.Host.Tests`). This session added **P2-G**
(keep-right leader veto), **P2-H** (max-depart-delay), and **X1** (attention-aware popping), plus a
completed diagnosis of the residual **P2G-3** multi-lane gap. (Session 1 checkpoint was `baa0a73`,
556 green.)

This document is the single entry point for the next session. It explains WHAT is done, HOW the
work is done (rules), and WHAT remains, so a fresh session can continue on the same branch without
re-deriving context.

---

## 1. The mission (unchanged)

Make SumoSharp run **optimal high-density sub-area traffic** the way vanilla SUMO does — with
`device.rerouting` + a bounded `time-to-teleport` valve — at **behavioural parity to SUMO 1.20.0**,
and lay groundwork for engine-only "extras" (attention-aware popping). The product context, the
measured density numbers (strict no-cheating clears ~2.7 veh/lane-km; rerouting + 120s teleport
valve reaches ~7 at <1% pops), and the P0/P1/P2/extras breakdown are in
**`docs/SUMOSHARP-HIGH-DENSITY-FEATURES.md`** (the source-of-truth spec). Verified gap findings +
owner steers are in **`docs/HIGH-DENSITY-PLAN.md`**.

## 2. Operating rules (READ — these govern how to work here)

- **Repo bar is behavioural parity to SUMO 1.20.0.** Offline `dotnet test` runs the engine against
  **committed goldens**; SUMO is NEVER needed for the test loop. SUMO (pip `eclipse-sumo==1.20.0`)
  is installed ONLY to regenerate/author goldens and for investigation. See `CLAUDE.md`, `docs/DESIGN.md`.
- **Design-first.** For each new feature: ground it in the vendored SUMO source (`/sumo/src/...`),
  write a design doc, then implement. No ad-hoc dev. (Docs: `HIGH-DENSITY-P0/P1E/P1F-DESIGN.md`.)
- **Every SUMO-parity feature lands with a dedicated `scenarios/NN-*` case**: inputs + a golden
  regenerated from vanilla SUMO 1.20.0, wired into `tests/Sim.ParityTests`, passing within
  `tolerance.json`. Commit the code + scenario + golden together.
- **Additive / gated / byte-identical.** New behaviour is gated (config default = off / spec-kind =
  Given) so **all prior goldens stay byte-identical**. Verify with the full suite every time.
- **Owner steers (standing):**
  1. **Performance may beat exact SUMO parity in production — but only behind a CLI flag.** The
     parity-faithful path is the default; faster-but-different is opt-in. (e.g. `device.rerouting.jitter`.)
  2. **RNG-insensitive parity** (Q1b): don't clone SUMO's PRNG draw order; make parity scenarios
     deterministic; validate sampling statistically.
  3. Rerouting must be **fast + parallel** (10k vehicles; batch + `Parallel.For` over a frozen
     snapshot). Done for P1-E.
  4. **Behavioural/statistical acceptance is fine at the end-to-end level** where bit-exact trajectory
     would just lock us to SUMO quirks; keep exact for the deterministic machinery + a faithful anchor.
- **Orchestration loop (conserve Opus budget):** Opus plans/decomposes/reviews; **Sonnet subagents do
  the implementation + source-reading volume** (delegate with a precise self-contained spec + the
  exact acceptance gate). **Opus reviews HARD** — read the diff, re-run the suite, verify the
  scenario golden independently; never trust a subagent's "done" report.
- **Git:** work on `claude/sumosharp-high-density-0r91xo`. Commit per feature. `git config
  user.email noreply@anthropic.com && user.name Claude`. **Avoid backticks in `git commit -m`
  bodies** (bash command-substitutes them). Push with `-u origin <branch>` + retry. Merging to main
  requires explicit owner permission (granted for this checkpoint).
- **Golden regen:** `pip install eclipse-sumo==1.20.0`; then per scenario:
  `sumo -c config.sumocfg --fcd-output golden.fcd.xml --fcd-output.acceleration --precision 6
  --save-state.times 1 --save-state.files golden.state.xml [--summary-output ... --statistic-output ...]
  --no-step-log true`. XML comments must NOT contain `--` (SUMO rejects it). Write `provenance.txt`.
- **Build/test:** `dotnet build Traffic.sln -c Release`; `dotnet test Traffic.sln -c Release`. SDK on
  a fresh VM: `apt-get update && apt-get install -y dotnet-sdk-8.0` (the `dotnet-install.sh` endpoint
  is blocked; use apt). `netconvert` (from the SUMO pip install) builds synthetic nets.

## 3. What is DONE (all committed + on main, each SUMO-golden-verified)

| Item | What | Scenario | Parity | Key files |
|------|------|----------|--------|-----------|
| **P0-A** | multi-file `.sumocfg <input>` (net/route/additional-files) + DemandParser merge + `LoadScenario(cfg)` | `41-multifile-cfg` | exact | ScenarioConfig(Parser), DemandParser, Engine.LoadScenario, Sim.Run |
| **P0-C1** | symbolic `departSpeed="max"`/`departLane="best"`/lane `departPos="stop"` | `42-symbolic-depart` | exact | `DepartValue.cs`, DemandParser, Engine insertion, EngineSnapshot |
| **P0-B** | `<vTypeDistribution>` (both syntaxes) + per-entity seeded member draw | `43-vtypedist` | exact + statistical | DemandParser, DemandModel, Engine.ResolveEffectiveTypeId |
| **P0-D** | `--summary-output` (running/halting/stopped/meanSpeed/meanSpeedRelative) + `--statistic-output <teleports>` + harness parsers/comparator | `44-summary-output` | exact | Sim.Harness Summary*/Statistic*, Engine aggregates, Sim.Run |
| **P1-E** | `device.rerouting`: live edge-weight smoothing (ring-buffer + `isDelayed`), A* (≡ Dijkstra), periodic **+ pre-insertion** reroute, parallel batch, route-slot recycling, gated jitter | `45-reroute-congestion` (single-lane **bit-exact**), `46-reroute-multilane` (route-split behavioural) | exact machinery + route-exact | `RerouteEdgeWeights.cs`, NetworkRouter (A*), Engine (UpdatePeriodicReroutes/PreInsertionReroute/UpdateRerouteEdgeWeights) |
| **P1-F** | bounded teleport valve (`time-to-teleport` jam): frontmost-stuck-per-lane, strict `>`, transfer queue + virtual-proceed, `time-to-teleport.remove`, jam counter | `47-teleport-jam` | **bit-exact** | Engine (CheckJamTeleports/TeleportVehicle/ProcessTransferQueue), VehicleRuntime.InTransfer, Sim.Run/StatisticWriter |
| **P0-C2** | parkingArea `departPos="stop"` (no-cheating parked origins) | `48-parking-depart` | exact | `ParkingArea.cs`, DemandParser, Engine.ResolveParkingAreaStops |
| **P2-G** | keep-right target-lane **LEADER** safety veto (MSLaneChanger::checkChange) — kills the spurious keep-right that drove the dominant multi-lane divergence (**82 m → 2.4 m** on the control); leader-only, since the follower half needs cooperative LC (see REMAINS) | `49-multilane-keepright` | **bit-exact** | Engine.ApplyKeepRightDecision (IsTargetLaneSafe leader gate) |
| **P2-H** | `max-depart-delay`: a pending vehicle waiting past the delay is evicted, not retried forever (MSInsertionControl.cpp:168) | `50-max-depart-delay` | **bit-exact** | ScenarioConfig(Parser).MaxDepartDelay, Engine.InsertDepartingVehicles/EvictOverdueDeparture, DiscardedDepartureCount |
| **X1** | attention-aware selective popping (NON-parity extra): `RealismMask` (immutable snapshot + volatile swap) gating jam-teleport + on-lane spawn + a new off-camera **de-jam despawn** action; runtime-only controls, inert by default | *(functional tests, no golden)* | functional | `RealismMask.cs`, Engine (SetVisibleEdges/ClearVisibleEdges/DejamDespawn, DejamDespawnTime/BudgetPerStep/Count), `RungHDx1RealismMaskTests` |

Design docs: `HIGH-DENSITY-P0-DESIGN.md` (P0-A/B/C1/C2/D), `HIGH-DENSITY-P1E-DESIGN.md` (rerouting,
incl. §0.5 the 3 owner-approved decisions + §11 pre-insertion), `HIGH-DENSITY-P1F-DESIGN.md` (teleport),
`HIGH-DENSITY-P2G-DESIGN.md` (keep-right leader veto), `HIGH-DENSITY-P2H-DESIGN.md` (max-depart-delay),
`HIGH-DENSITY-P2G3-DESIGN.md` (scenario-46 residual DIAGNOSIS — deep, deferred), `HIGH-DENSITY-X1-DESIGN.md`
(attention-aware popping).

**Net effect:** the SumoData high-density config now loads + runs end-to-end in SumoSharp
(multi-file cfg + vTypeDistribution + symbolic departs + parked origins + rerouting + teleport +
calibration outputs), each validated against a vanilla SUMO 1.20.0 golden.

**Scenario numbering wart:** HD scenarios reuse numbers 41–48, which COLLIDE numerically with
pre-existing scenarios of the same numbers (e.g. `41-forced-turn-lane` vs `41-multifile-cfg`). Dir
names are unique so tests/regen work fine; it's cosmetic. If it bothers you, renumber the HD set to
60+ (churns goldens/tests) — otherwise leave it.

## 4. What REMAINS (the continuation work)

**P2-G (dominant facet), P2-H, and X1 all landed this session** (see §3). What remains on multi-lane
parity are two DEEP LC-model extensions, both fully diagnosed, both closing the SAME residual — a
**sub-10 m transient overtake lag near junctions** on `scenarios/46-reroute-multilane`. Neither is a
correctness/gridlock issue (the net routes, drains, and does not gridlock); scenario 46 passes today as
a behavioural gate. These are owner-decision items — do NOT start either without an explicit go, and
design-first + scope heavily (each rivals a P1 rung in effort).

### P2G-3 — cross-junction leader anticipation in the speedGain incentive (DIAGNOSED; deep)
- **Root cause is nailed** (`docs/HIGH-DENSITY-P2G3-DESIGN.md`, instrumented): scenario 46's residual is
  a `speedGain` overtake the engine never fires. NOT the neighDist gate (a continuation-distance fix was
  implemented + instrumented — gate passes at 82.5 — yet the change still never fires) and NOT cooperative
  LC (SUMO's own lane-change log: reason `speedGain`, zero cooperative). The engine's LC incentive reads
  only **same-edge** leaders, so `thisLaneVSafe` loses the slow leader across the junction internal lane
  (`:K_0_0`) and the accumulator never builds; SUMO's `getRawSpeed` anticipates the leader across the
  best-lanes continuation. Fix = a continuation-aware leader lookup in the LC decision's vSafe. The
  continuation-distance change was **reverted** (byte-identical everywhere → no anchor; its keep-right half
  also re-broke the saturated grid 0→90 stuck).

### P2G-2 — cooperative lane-changing (LCA_COOPERATIVE / informFollower) (deep)
- Separate from P2G-3. The engine has no "make room for a blocked vehicle" arm. It is why the P2-G
  keep-right veto is **leader-only**: adding the follower half without cooperation over-brakes a saturated
  `-L2` grid into gridlock (verified: `willpass-saturation` 0→30 stuck). Needed for follower-side veto
  fidelity and dense-follower parity; NOT scenario 46's residual. SUMO refs in
  `MSLCM_LC2013.cpp` (`informFollower`/`saveBlockerLength`/`amBlockingFollowerPlusNB`, `myVSafes`).

### X1 follow-ups (non-parity; measurement, not gates)
- The dense **moving-camera statistical report** — "achievable visible-area density exceeds the global
  no-cheating knee" (`SUMOSHARP-HIGH-DENSITY-FEATURES.md` §5 acceptance (3)) — a host-side benchmark
  measurement, not a unit assertion. The functional gating (zero pops on visible edges, migration, de-jam,
  spawn) is done + tested (`RungHDx1RealismMaskTests`).
- Optional: wire `SetVisibleEdges` from a real camera frustum in `Sim.LiveHost`; per-zone rerouting
  aggressiveness (spec X2).

### Deferred sub-items (documented where they live; pick up if a scenario needs them)
- **P2-G**: `departLane="free"`/`"random"` ingest still throws (only numeric/`"best"` parsed) — a small,
  separate parser gap surfaced during the P2-G diagnosis; real SumoData `.rou.xml` may use it.
- **P1-E**: teleport-classification split is jam-only here — N/A (that's P1-F); the `getRerouteOrigin`
  brake-gap bump (§8 risk 5) not ported (matches existing obstacle-reroute); pre-insertion is a single
  reroute at insertion, not SUMO's full `pre-period` horizon (fine for our configs).
- **P1-F**: yield/wrongLane teleport classification deferred (jam-only; `total==jam`); full off-road/
  parked vehicle state (lateral shift, not-blocking-a-follower) deferred — matters only for a
  *follower past a parked/teleporting car* and the `y` coord (not compared).
- **P0-C2**: off-road/lateral parked state deferred (same reason).

## 5. Orientation quick-reference
- **Plan + verified gaps + owner steers:** `docs/HIGH-DENSITY-PLAN.md` (has the per-item tracker, updated).
- **Designs:** `docs/HIGH-DENSITY-P0-DESIGN.md`, `-P1E-DESIGN.md`, `-P1F-DESIGN.md`, `-P2G-DESIGN.md`,
  `-P2H-DESIGN.md`, `-P2G3-DESIGN.md` (diagnosis + attempted fix), `-P2G2-COOPERATIVE-LC-DESIGN.md`
  (the coordinated dense LC model + the config-gate decision + full gain/cost analysis), `-X1-DESIGN.md`.
- **Cross-session coordination:** `docs/COORDINATION-pedestrian-x-highdensity.md` — the collision surface
  between this work and the pedestrian/crosswalk session (junction RoW, the LC decision, net ingest) and
  the additive-constraint protocol to keep them conflict-free. READ if touching junction RoW or
  `DecideSpeedGainForVehicle` while pedestrian work is in flight.
- **Spec:** `docs/SUMOSHARP-HIGH-DENSITY-FEATURES.md`.
- **Repo rules / architecture:** `CLAUDE.md`, `docs/DESIGN.md`.
- **HD tests:** `tests/Sim.ParityTests/RungHD*.cs` + `RungHDx1RealismMaskTests.cs`. **HD scenarios:**
  `scenarios/4[1-8]-*` (P0/P1) + `49-multilane-keepright` (P2-G) + `50-max-depart-delay` (P2-H).
- **Existing multi-lane-gap analyses:** `docs/NEED-multilane-*.md` — NOTE these are STALE (they predate
  the C4-viii willPass pre-pass and this session's P2-G work; the current multi-lane residual is
  diagnosed in `-P2G3-DESIGN.md`, not these docs).

## 6. Recommended next order (continuation)
1. **X1 dense measurement** or **live-host camera wiring** — cheap, delivers visible value on the extra.
2. **P2G-3** cross-junction leader anticipation OR **P2G-2** cooperative LC — only on explicit owner go;
   each is a deep LC-model extension closing the same sub-10 m scenario-46 residual (not a correctness
   issue). Design-first, scope heavily, adversarially guard the saturated-grid diagnostic.
3. **`departLane="free"`** parser fix — small, unblocks more real SumoData `.rou.xml` inputs.
