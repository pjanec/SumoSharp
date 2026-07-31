# FOLLOWER-LC-DEFERRAL — resume here (the Entry 32 fix)

> **STATUS: FIXED AND SHIPPED (July 31, Entries 34 + 34b, commits `05653f4` + `bc381db`).**
> The speedGain-RIGHT arm is ported (it was ABSENT, not broken — §3(c)'s audit answer), the
> continuation `neighDist` reads SUMO's exact 0.0804 rolling deltaProb, and SUMO's strategic
> stay complex (:1131-1150 offset override + :1398/:1411 with the jam term) is ported in BOTH
> directions. L2 stopped-LC rate **1.155 → 0.861** (SUMO 0.410), oracle followers change at
> speed, goldens byte-identical, suite 779/5/0, deterministic 5/5 hashes. Full characterisation:
> journal Entry 34 BEFORE / AFTER / 34b. This page is kept as the METHOD record; do not
> re-implement from it.

**Read this first, cold. It is self-contained.** Branch **`claude/sumosharp-traffic-bugs-g1y9hl`**,
handoff at `ad085a4`. Gate state: `dotnet test tests/Sim.ParityTests -c Release` =
**779 passed / 5 skipped / 0 failed**, all 661 goldens byte-identical, engine deterministic
(parallel == serial, Entry 30). The full trail is `JUNCTION-REALISM-SESSION-JOURNAL.md`
Entries 21, 22, 30, **32** (the finding this doc operationalizes) and 33.

## 1. The task

Fix the last measured half of the owner's "lateral lane change while standing" artefact: **our
engine defers follower lane changes to standstill that SUMO makes at speed on the approach.**
The strategic path is already fixed and shipping (`UrgentStrategicLeaderFollow`, Entry 31); the
demo's dead-stop *share* already matches vanilla SUMO (12.3% vs ~12%, Entry 33). What remains is
the follower *rate*: L2 stopped-LC rate 1.155 vs SUMO's 0.410 per 1000 stopped-vehicle-steps.

This is a **design-first** change (CLAUDE.md): behavioral edits to two lane-change paths. Write
the journal BEFORE entry (Entry 34) with predictions before touching source.

## 2. The lockstep oracle (committed — use it, do not rebuild it)

`scenarios/_diag/keepright-standing/` — 2-lane 200 m edge into a red light (80 s red, then
green), 100 m exit edge, six cars (`f.0`…`f.5`) depart on lane 1, right lane empty. **Both
engines are byte-identical until the first lane-change difference** (t=17), so per-vehicle
cross-engine comparison is VALID here — the prerequisite Entries 21/22 lacked.

```bash
# ours (build first -- Sim.Run is NOT in Traffic.sln since the July 29 reorg):
dotnet build -c Release src/Sim.Run/Sim.Run.csproj
dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_diag/keepright-standing --parity --steps 200 --fcd-out /tmp/ours.fcd.xml
# SUMO oracle:
sumo -c scenarios/_diag/keepright-standing/config.sumocfg --fcd-output /tmp/sumo.fcd.xml
# first divergence + per-vehicle traces:
python3 scripts/fcd-divergence-onset.py /tmp/ours.fcd.xml /tmp/sumo.fcd.xml
SUMOSHARP_TRACEVEH=f.3 dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_diag/keepright-standing --parity --steps 100 2> /tmp/f3.trace   # [keepright] lines
# SUMO's internal accumulators, live (pip install traci; getter NEGATES, MSLCM_LC2013.cpp:2120):
#   traci.vehicle.getParameter(vid, "laneChangeModel.keepRightProbability")
#   traci.vehicle.getParameter(vid, "laneChangeModel.speedGainProbabilityRight")
```

**SUMO's measured behaviour on this net (the oracle targets):**

| veh | what SUMO does | accumulator trail (internal sign) |
|---|---|---|
| f.0 (head) | **keepRight**, fires t=17 — ONE step after halting | rolling −0.08/step (16 s ≈ −1.3); stopped boost 0.4/step (acceptanceTime floors at 7 s); crosses −2.0 immediately. **Correct behaviour — do not "fix" prompt head-car stopped changes** |
| f.3 | **speedGain-right, fires AT SPEED t=21→22 (5.5→3.7 m/s)** | keepRight: −0.08/step to −0.80, **freezes at −0.81** when f.0 appears as right-lane leader (the secure-gap cut, working); speedGainRight then ramps **−0.23 / −0.72 / −1.05 / −1.46** (t=18..21), fires next step |
| f.5 | speedGain/keepRight at ~0.14 m/s, t=25 | — |
| ours today | f.0 at t=18 (fine, 1 step late); **f.3/f.4 never change on approach**; f.5 changes STOPPED at t=83 (discharge) | our keepRight rate 0.0508/step = **63% of SUMO's** — freezes at −0.624, below threshold |

**Term-checked arithmetic (Entry 32):** our `acceptanceTime` (97.23) and Euler `brakeGap`
(28.56) match SUMO **exactly**. The entire keepRight rolling-rate gap is `neighDist`:
ours = `rightLane.Length` (200); SUMO = the right lane's best-lanes continuation (300 = lane +
exit edge). 0.4·((300−28.56)/13.89)/97.23 = 0.0804 ✓ the observed 0.08.

## 3. The fix shape (both halves in ONE change — they are a coupled pair)

**History you must respect:** Entry 21 tried half (a) alone → deltaProb saturated at 0.4/step,
reverted. Entry 22 tried `resetState()` alone → reverted. Both were judged on RACY-era numbers
(pre-Entry-30 determinism) and an invalid cross-engine side-by-side (Entry 22's own correction).
The pair is self-consistent only together: (a) sets the rolling rate, (b) clamps it in traffic.

- **(a) `neighDist` ← the right lane's best-lanes continuation length.**
  Site: `Engine.cs:13226` (`var neighDist = rightLane.Length;` with a "deferred" scope note).
  The quantity is already computed and cached per lane: `v.KeepRightStayRightContLength`
  (filled at `Engine.cs:13152-13158` via `KeepRightStrategicStay`/`ComputeBestLanes`; note
  it is measured from LANE START — SUMO's `neigh.length` semantics, used raw in the formula,
  NOT pos-relative; rule 2 at :13204 subtracts pos, the deltaProb formula does not).
  Verify the cache is valid for non-turn-lane cases before relying on it.
- **(b) `neighLead` ← right-lane leader INCLUDING the continuation past the lane end.**
  Site: `Engine.cs:13237` (`neighbors.GetNeighborLeader(v, rightLane.Handle)` — single-lane
  only). SUMO's `getRealRightLeader` looks past the lane end (Entry 21 §"where the evidence
  points": ego at pos 59.20 of a 59.20 m lane finds NO leader here and always finds one in
  SUMO — inverting the cut exactly where the artefact fires on L2). The machinery exists:
  generalize `BuildActualDownstreamSpan` (T2.6, takes ego's lane today) to an arbitrary source
  lane, walk with `TryFindCrossJunctionLeader` (~`Engine.cs:10208`), keep the cut's gap
  semantics (`neighLeadGap − secureGap`, `fsds = min(fsds, fullSpeedGap/(vMax−leadSpeed))`,
  only when `leadSpeed < vMax`) at `Engine.cs:13238-13246`.
- **(c) Audit the speedGain-right rolling fire against the oracle.** Our speedGain accumulator:
  `Engine.cs:12774-13054` (`relativeGain` at :12958, accumulate at :12980, SUMO ref
  MSLCM_LC2013.cpp ~1682/1818-1864). Why does our f.3 not fire while rolling? The oracle gives
  the exact per-step target (−0.23/−0.72/−1.05/−1.46). Candidates to CHECK, not assume: our
  `neighLaneVSafe` for the empty right lane while braking behind a queue; threshold
  (−2.0, `changeProbThresholdRight`); a veto on the commit path. Trace first
  (`SUMOSHARP_TRACEVEH` — if the speedGain path has no trace line yet, add one like
  `[keepright]`'s, committed).
- Cleanup while there: the `[keepright]` trace block is DUPLICATED verbatim
  (`Engine.cs:13252-13288`) — emit once.
- **After (a)-(c) land:** revisit Entry 22's `resetState()` omission (zeroes BOTH accumulators
  on every committed change — table in Entry 22, do not re-derive); its −10 arrivals cost may
  reverse on the fixed engine.

## 4. Acceptance gates (all must hold; record predictions BEFORE measuring)

1. **keepright-standing**: followers change AT SPEED like SUMO's f.3/f.5 (approach-window
   changes, not discharge-window); our keepRight deltaProb reads 0.0804/step while rolling with
   an empty continuation; f.0's prompt stopped change is PRESERVED.
2. **Goldens**: 661 byte-identical, suite 779/5/0 — or, if any golden moves, a SUMO-side diff
   proving the new trajectory is closer to SUMO (parity tolerance is the iron law; a golden
   move needs `scripts/regen-goldens.sh` + explicit justification, not a tolerance edit).
3. **L2 stopped-LC rate**: materially below 1.155 toward SUMO's 0.410, **denominator reported**
   (`scripts/detect-stopped-lane-change.py <fcd> --vtypes-from scenarios/_diag/junction-realism-L2`).
4. **26-net battery**: `scripts/run-net-regression.py --out X --compare
   docs/reports/net-regression-urgentfollow-on.txt` (the CURRENT reference; the
   keepclear-direction baseline carries 4 rows of pre-insertion-fix rot, Entry 29). No
   stuckDwell regression anywhere; arrivals within noise.
5. **Determinism repeat-hash** (Entry 30's standing lesson): ≥4 identical runs of L2 →
   identical FCD hashes, plus one `--max-parallelism 1` run matching (drive via
   `src/Sim.Sumo` drop-in for the flag; build it explicitly too).
6. **Demo spot-check** (Entry 33's instrument): `LIVECITY_WITNESS=1 dotnet run --project
   src/Sim.Viewer -c Release -- --mode live-city --smoke --frames 400` — overlaps stay 0 at
   checkpoints, dead-stop share stays ≈12%, no gridlock. Closed-loop: realism metrics only.

## 5. Traps (each has already cost a session)

- **Builds**: `Traffic.sln` no longer contains `Sim.Run`/`Sim.Sumo`/`Sim.Viewer`/`Sim.Viz`
  (July 29 reorg) — `dotnet build -c Release <csproj>` each explicitly or you measure stale
  code (Entry 30 lost a full round to this). `tests/Sim.ParityTests` IS in the sln.
- **TraCI getter NEGATES** both probability getters (Entry 21's published-wrong-claim).
- **Env gates are process-global** — pin in both arms; check `env | grep -E "SUMOSHARP|LIVECITY"`.
- **Cross-engine per-vehicle comparison is valid ONLY inside a lockstep window**
  (`scripts/fcd-divergence-onset.py` measures it). On L2 it is ~0 steps; on
  keepright-standing it runs to the artefact.
- **The old "23× keepRight / 4.6× speedGain" split is stale** (pre-flip, racy era). Post-flip
  L2 histogram (`SUMOSHARP_LCLOG=1`): keepRight 200 / speedGain 172 / strategic 165 stopped
  commits.
- Reasoned hypotheses are ~0-for-16 in this workstream. The oracle targets in §2 are the
  ground truth; when a number disagrees with your reading of the code, trust the number.

## 6. Adjacent open items (do not scope-creep into this fix)

- Ped-on-RED near-collision anomaly (Entry 33) — untraced, possibly predates the branch.
- `city-*` residual overlaps — re-trace on the fixed engine (Entry 29 halved L2's).
- Back-protrusion invisibility (Entry 27a) — second-order after T2.6.
- Pedestrian amplifier — needs a LiveCitySim harness on the repro net (`Sim.Run` has no peds).
- CLAUDE.md's build-trap list is stale re: the July 29 solution reorg (worth a docs pass).
