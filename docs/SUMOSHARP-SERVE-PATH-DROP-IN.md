# SumoSharp — remaining work to fully replace vanilla SUMO in sub-area preprocessing

**Audience:** the SumoSharp engine session. This is the follow-up to
`SUMOSHARP-HIGH-DENSITY-FEATURES.md` (P0/P1/P2/X1 — now **landed & golden-verified**). It specifies the
**last mile**: the three things still needed so SumoSharp can be a **drop-in for the vanilla `sumo`
binary** in the SumoData sub-area pipeline's *serve + replay* path (the *calibration* path is already
covered by the P0/P1 work). Reader decides what to build; this is the analysis + acceptance bar.

Status source: re-assessment of SumoSharp `main` @ `6d5d6ef` against the SumoData pipeline
(`PREPROCESSING-ENGINE-REQUIREMENTS.md §0c`). What's DONE: `.sumocfg`+multi-file routes,
`vTypeDistribution`, symbolic departs, `--summary-output`/`--statistic-output`, `device.rerouting`,
bounded `time-to-teleport`, `--fcd-output` (SUMO schema), X1 RealismMask, single-lot parkingArea.

---

## 0. How the SumoData pipeline invokes the engine (the exact contract)

Our Python (`auto_calibrate.py`, `preprocess.py`, `run-subarea-real.sh`, `sim_viz.py`) shells out to
the `sumo` binary in these shapes (all via list-form `subprocess`, so a differently-*named* binary is
fine as long as the **flags match**):

```
# calibration probe (ALREADY satisfied by P0-D + P1E/P1F):
sumo -c <cfg> --summary-output S.xml --statistic-output T.xml --end <N> --no-step-log true

# serve / verify (audit reads arrivalLane):
sumo -c <cfg> --tripinfo-output TI.xml [--summary-output S.xml --statistic-output T.xml] --end <N> --no-step-log true

# replay record:
sumo -c <cfg> --fcd-output F.xml --end <N> --no-step-log true
```
`<cfg>` is a real SUMO `.sumocfg` with `<input><net-file/><route-files/(comma list)><additional-files/></input>`,
`<time><begin/><step-length/></time>`, `<processing>` (`time-to-teleport`, `ignore-route-errors`,
`collision.action`), and a `<routing>` `device.rerouting`/`routing-algorithm` block. The additional
file contains `<parkingArea>`s; the routes contain `<vehicle>`s with `departSpeed="max"
departLane="best"`, some with `departPos="stop"` + a `<stop parkingArea=… duration=…>`.

**The single acceptance goal:** point the SumoData tools at SumoSharp (PATH or a `SUMO_BINARY` env —
the SumoData side will add that override) and have `preprocess.py --replay` run a real produced
`scenario.sumocfg` to completion, pass `audit_nocheat.py`, and match vanilla SUMO within tolerance.

---

## 1. GAP-1 — a `sumo`-compatible CLI shim (unblocks everything)

**Now:** `Sim.Run` takes a positional scenario *directory* + `--steps N` + `--fcd-out`; no `-c`, no
`--end`, flag name `--fcd-out` ≠ `--fcd-output`, no `--tripinfo-output`, no `--no-step-log`.
**Need:** a CLI that accepts the vanilla shape above. Minimal flag set to support:
`-c <cfg>`, `--begin <t>` (optional), `--end <t>` (sim end **time**, not step count → divide by
step-length), `--summary-output`, `--statistic-output`, `--fcd-output`, `--tripinfo-output`,
`--no-step-log <bool>` (accept & ignore), and tolerate unknown flags gracefully (warn, don't abort) so
minor extra flags we pass don't break it. The engine capability already exists (P0-D/P1E/P1F/FCD
writers) — this is argument parsing + wiring + a published/aliased entrypoint (ideally an exe
discoverable as `sumo` on PATH, or documented so SumoData sets `SUMO_BINARY`).
**Acceptance:** a script test that runs one of our `scenario.sumocfg` files with each flag combo above
and produces the four output files; existing golden parity scenarios keep passing.

## 2. GAP-2 — `--tripinfo-output` with `arrivalLane`

**Now:** tripinfo exists only in `Sim.BenchCity` (a benchmark tool, not the engine CLI) and omits
`arrivalLane`; `TripInfoRecord` has no `ArrivalLane`.
**Need:** an engine-CLI `--tripinfo-output` writer emitting SUMO-schema `<tripinfo>` per completed
vehicle with at least: `id, depart, arrival, arrivalLane, arrivalPos, arrivalSpeed, duration,
routeLength, waitingTime, timeLoss`. `audit_nocheat.py` **requires `arrivalLane`** (it checks every
completed trip died on a fringe edge or a parking edge); our believability stats read
`duration/routeLength/waitingTime/timeLoss`. Parked-forever sink vehicles simply don't appear until
they leave — that's expected and handled.
**Acceptance:** new parity scenario with a mix of through + parked-destination vehicles; the emitted
tripinfo matches vanilla SUMO's `arrivalLane`/timing within tolerance; `audit_nocheat.py` run against
it returns PASS.

## 3. GAP-3 — multi-occupant `parkingArea` (the no-cheating sink at real scale)

**Now:** parkingArea is scoped to the degenerate single-lot / empty-area case (one vehicle, straight
lane, no manoeuvre).
**Why it's on the serve path:** our `auto_parking.py` creates **one `parkingArea` per internal
origin/destination edge with `roadsideCapacity = (number of internal trips on that edge)`**, and
**many vehicles share the same parkingArea** — destinations park and stay (the sink), origins start
parked (`departPos="stop"`) and pull out. This is the mechanism that keeps internal trips off the
visible lanes (the no-cheating rule). So multi-occupant parkingArea is **required** to run our produced
scenarios (calibration doesn't use parking, but every served/replayed scenario does).
**Need:** general parkingArea semantics — up to `roadsideCapacity` vehicles resident at once; each
assigned a distinct lot slot; a parked vehicle is **off the running lane** (a following car passes it,
not blocked); `<stop parkingArea=… duration=…>` parks a moving car into a free slot; `departPos="stop"`
inserts a car already parked in a slot and it pulls out into a gap. Off-lane lateral parked position
(the `y`/lateral offset) matters for the follower-passes-parked-car case and for the FCD/replay
(parked cars should render beside the lane, not on it).
**Acceptance:** parity scenario with N>1 vehicles sharing one `parkingArea` (some arriving-and-staying,
some departing-from-parked) on a lane with through-traffic; presence/positions match vanilla within
tolerance; a following through-vehicle is not blocked by a parked one; `audit_nocheat.py` PASS (0
births/deaths on visible lanes). The end-to-end goal: `preprocess.py --replay` on a real box runs the
served scenario to completion via SumoSharp with the audit green.

## 4. Minor / conditional

- **`departLane="free"`/`"random"`** — currently throw (only numeric + `"best"` parsed). Add if our
  real `.rou.xml` uses them; cheap. (`departSpeed`/`departPos` symbolic forms are done.)
- **`<rerouter>` / `parkingAreaReroute`** — only needed if we adopt parking overflow/finite-dwell
  turnover; we don't today. Skip unless requested.
- **Multi-lane LC parity residual (scenario 46)** — pre-existing, sub-10 m transient, net still drains;
  not a serve-path blocker.

## 5. Suggested order & the definitive acceptance test

1. GAP-1 CLI shim (unblocks the SumoData tools calling SumoSharp at all).
2. GAP-2 tripinfo + `arrivalLane` (unblocks the audit).
3. GAP-3 multi-occupant parkingArea (unblocks running our served scenarios).

**Definitive integration test (spans all three):** take a `scenario.sumocfg` produced by SumoData
`preprocess.py` for a small Geneva box, run it through the SumoSharp CLI shim with
`--tripinfo-output`/`--fcd-output`, and confirm (a) it completes, (b) `audit_nocheat.py` returns
NO-CHEATING PASS, (c) aggregate flow/speed match a vanilla-SUMO run of the same cfg within the harness
tolerance. That green is "SumoSharp fully replaces vanilla SUMO in the sub-area pipeline."

## 7. Acceptance-test result (2026-07-18) — audit GREEN, aggregate parity OPEN

The definitive test in §5 was run: a real `preprocess.py`-produced ~1.5 km Geneva box, identical flags,
vanilla SUMO 1.20.0 vs SumoSharp branch `claude/sumosharp-drop-in-binary-vq7u9p`.

- **(a) completes — yes. (b) `audit_nocheat.py` NO-CHEATING — PASS** (0 birth/death/FCD violations).
  GAP-1 (CLI shim), GAP-2 (tripinfo + `arrivalLane`), GAP-3 (multi-occupant parkingArea) are all
  functionally correct enough to clear the audit. **Not** declared "definitive acceptance green" — (c)
  fails on two independent axes:

**Issue 1 — park-and-stay residency (BLOCKER, and it is *not* a vanilla-parity requirement).**
Sink cars carry `<stop parkingArea=… duration=100000>` = park **and stay** (the no-cheating sink).
SumoSharp removes them at parking arrival (completed trips 227 vs 53; peak `running` 278 vs 458) —
i.e. treats the long stop as finite and lets the car depart/vanish. **Product ruling (owner,
2026-07-18): cars must remain resident and rendered off-lane for the full horizon in *visible* surface
lots** — an empty surface lot beside busy streets breaks realism. Required semantics: a parkingArea
stop whose `duration` outlasts the sim keeps the vehicle parked, off the running lane, and *not*
emitted to tripinfo (it never arrives). Future opt-in: a lot flagged **hidden** (underground/covered)
*may* despawn its occupants; default is resident. This requirement is engine-independent — it holds
whether or not we ever run vanilla again.

**Issue 2 — excess deadlock/teleport (parity relaxes, realism does not).**
Mean relative speed 0.55 vs 0.84; **jam-teleports 33 vs 1–2** (all 33 are `jam` type); halting 175 vs
51 at end. Framing that matters for prioritisation: **if the whole sim path (calibrate → serve →
replay) runs on SumoSharp, matching vanilla's *density* is not required** — our calibrator finds
SumoSharp's *own* knee and dials demand to it, so a lower capacity just maps "100%" to fewer cars. What
does **not** calibrate away: (1) a `jam`-teleport fires only after a vehicle is wedged >120 s — that's a
genuine **deadlock**, not slow traffic, and each teleport is itself a **visible pop** (the exact cheat
the audit otherwise forbids) unless RealismMask hides it off-camera; (2) a deadlocked junction looks
unrealistic on camera at any density. 33 all-`jam` teleports (vs 1) points at a **junction
right-of-way / gap-acceptance / lane-change** divergence rather than merely conservative capacity —
worth chasing because fixing it raises usable density *and* removes cheat-events. Acceptance for a
SumoSharp-native pipeline is therefore: re-calibrate on SumoSharp, then confirm a replay at *its* knee
shows realistic queueing (no frozen-junction deadlocks, teleports back down to vanilla-ish single
digits).

**Issue 1 root cause (found in `Sim.Core/Engine.cs`):** `StopLineConstraint` only brakes for a stop when
`stop.LaneId == v.LaneId`, and there is no strategic lane-change toward the stop lane. A park-and-stay
car on lane 1 (parkingArea on lane 0) never brakes, drives off the end of its final edge, and the
position-based last-edge arrival removes it as "arrived". Fix = strategic LC toward the stop lane +
indefinite-residency removal semantics for a `duration` past sim-end.

**Work split (owner, 2026-07-18):** Issue 1 (park-and-stay residency / strategic LC toward the stop
lane) is the **serve-path** session's task (this branch); Issue 2 (junction deadlock / jam-teleport —
core micro-sim right-of-way/gap-acceptance) is delegated to the **sumo-core** session. A geometry-free
synthetic repro of BOTH lives in `scenarios/_repro/synthetic-parity/` (from
`experiments/subarea/synthetic_parity/`) — shareable, no real geometry.

## 6. Cross-links
- `SUMOSHARP-HIGH-DENSITY-FEATURES.md` — the already-landed P0/P1/P2/X1 features (same repo).
- `SUBAREA-FOR-PEDESTRIAN-SESSION.md` — the sub-area/no-cheating/RealismMask compatibility brief for
  the pedestrian session (companion; SumoData repo, copy alongside this one).
- SumoData repo: `PREPROCESSING-ENGINE-REQUIREMENTS.md` (§0c status), `SUBAREA-METHOD.md`,
  `experiments/subarea/{auto_parking.py,preprocess.py,audit_nocheat.py,sim_viz.py}` (the exact
  producer/consumer of the contract in §0).
