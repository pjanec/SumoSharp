# GENEVA-ANALYSIS-RESUME-3 — state after the overlap-hunt session (supersedes RESUME-2)

**Predecessors:** `GENEVA-ANALYSIS-RESUME.md` (commissioning), `GENEVA-ANALYSIS-RESUME-2.md`
(rings root-caused). Read THIS instead. Trail: `JUNCTION-REALISM-SESSION-JOURNAL.md`
**Entries 48–56** — every entry has BEFORE-predictions and AFTER-measurements; the class
decompositions are measured, not guessed.

## 0. Engine state (verify before believing)

Branch **`claude/sumosharp-traffic-bugs-g1y9hl`**, all pushed through **`796aad1`** (remote
renamed: `github.com/pjanec/SumoSharp.git`). Full `dotnet test -c Release` green at head:
ParityTests 782/5 (goldens byte-identical), LiveCity 92/92 (incl. hour-horizon), Peds 324,
Host 6, Viewer.Motion 19, DotRecast 2. `Sim.Bench` hash **`A134ED3716DDE7BC`** (par==single) —
unchanged through EVERY commit of both sessions. Geneva cut: `D:\Work\GenevaCut\
geneva_city.sumocfg` (28 276 lanes; harness: `docs/GENEVA-HEADLESS-HARNESS.md`, whose §0
blocker is FIXED — `Sim.Viewer --mode live-city --smoke --sumocfg <cfg>` runs witness-rich
Geneva headless).

## 1. What shipped this session (all gate-verified, owner-approved where behavioural)

| Commit | What |
| --- | --- |
| `41867d1` | **PARTIAL-OCCUPANCY phase 1, DEFAULT ON** (`docs/PARTIAL-OCCUPANCY-DESIGN.md`, owner GO): myPartialVehicles port — boundary-spanning tails registered in `LaneNeighborQuery` (serial engine pass, frozen route pool, extrapolated-front-pos frame §2b); readers = same-lane leader fold (both packed/GetLeader branches) + cross-junction rearmost. Gates `LIVECITY_PARTIALVEH`/`SUMOSHARP_PARTIALVEH`. |
| `796aad1` | **RingBreakGate DEFAULT ON** (owner ok; D2 from `docs/DEADLOCK-RING-DESIGN.md`, landed `460e2da` prior session). Kill switch `LIVECITY_RINGBREAK=0`. |
| instruments | `LIVECITY-OVERLAP` (OBB class counts: queue-depth-bucketed/merge/junction/lateral, 20 s witness cadence), `[veh]` (per-step winning binder/blocker for `LIVECITY_TRACEVEH`), `[jyrow]` (per-foe-link reachability bits, trace-gated, pass-tagged p/r). |

**Measured (standard capture: 4000 cars/2000 peds, `LIVECITY_F3OCCUPANCY=1 LIVECITY_WITNESS=1
LIVECITY_REROUTE=0`, 3600 steps, closed-loop):** overlaps 401 → **50 pairs (−87%)**; junction
class 329 → 34; arrivals triangle 2961 (old) / 2635 (partials only — honest, the drive-through
cheat removed) / **3072 (partials+ringbreak, the shipped defaults)**. D2's frozen-landing
blocker: 492 → 8. **Owner 3D verdict: "best result I have ever seen with SumoSharp"; queue
half-stacking GONE; fewer fully-gridlocked junctions than ever.**

## 2. Root causes found this session (do NOT re-derive)

1. **Entry 53 — the stopped-lookahead ratchet** (was the dominant junction drive-through):
   `TryFindCrossJunctionLeader` breaks on `seen > lookahead`; a STOPPED ego's lookahead (~2.1 m)
   can't reach the next lane start, so a leader whose tail hangs back across the boundary
   blinks invisible/visible per step → freeFlow/e-stop alternation ratcheting 0.65 m/cycle into
   the body. CURED by the partials port (SUMO's myPartialVehicles keeps the tail registered).
2. **Entry 55 — the merge co-location**: two streams on different internal lanes sharing one
   target lane, both crossJxnLeader-following the SAME stopped leader on it; when it departs
   both release in lockstep and land co-located; `colocationSymmetryBreak` untangles post-hoc.
   `[jyrow]` PROVED the pair is reached (respondsTo=True foeWith=True conflict=none →
   sameTargetMerge branch). PHASE 1 has the entry-order tie-break but NEVER FIRED → the
   `foeMerging.LaneId == foeInternalLaneId` guard failed: **`FindFoeVehicle` (single-foe
   first-match) returned a different route-matching vehicle than the ON-LANE merger.**
3. Instrument lesson re-learned: a `!prePass` filter on a trace print shows NOTHING for stopped
   vehicles — fusion-eligible vehicles only get the PRE-pass (T1.8). `[jyrow]` tags passes p/r.

## 3. The owner's hunt queue (his words: crossings first)

1. **Crossing class — "cars go full speed through another one blocked in junction is exactly
   what I would not like to see."** Trace targets from the pv1 capture (Entry 56):
   `:35673_0_0@14.9 × :35673_1_0@13.7`, `:30268_8_0 × :30268_5_2`, `:36220_7_0 × :36220_9_2`
   (depths ~1.8 m). Hunt shape: `[veh]`-trace the MOVING member through the intersection window;
   name the arm that admitted it past the standing body (adaptToJxnLeader mapping vs
   FoeIsInTheWay vs the F3 crossing arms). Reproduce with the standard capture env +
   `LIVECITY_PARTIALVEH=1 LIVECITY_RINGBREAK=0` (rb0-style logs) — note pv1 logs used
   RINGBREAK=0; with today's default you must SET `LIVECITY_RINGBREAK=0` to reproduce them.
2. **Merge class**: next instrument = print `FindFoeVehicle`'s pick for foeLink=1 (junction
   30268) in the release window t=639.5–640.5 (repro: standard capture env + RINGBREAK=0 +
   `LIVECITY_TRACEVEH=__veh411`, frames 1284; deterministic). Candidate fix (SUMO-faithful,
   design-first if it grows): the merge arm consults the foe lane's rearmost OCCUPANT (neighbor
   query, partials included) before the route-matched approaching foe — SUMO's getLeaderInfo
   walks lane occupants, never a single route-matched candidate.
3. **Residual queue stackings (few)**: consistent with the mid-lane-change lateral-footprint
   window (Entry 53's second contributor) — phase-2 / shadow-lane territory.
4. **Partial-occupancy phase 2** (T5 in the design, needs its own owner go): insertion checks,
   keepClear space walks, lane-change shadow occupation.
5. Parked from earlier: keepClear block-loop trace (`:30143`, class 3 of Entry 48); honest-SUMO
   open-loop comparison at `:35479`/`:30143`/`:35019` (playbook in RESUME §3, never run).

## 4. Method reminders that earned their keep THIS session

- Trace first; the reasoned-hypothesis score got worse again (my P2 in Entry 52 and the PHASE-1
  tie in Entry 55 were both refuted by the next instrument). One traced vehicle decides.
- BOTH surfaces + the OVERLAP instrument for any overlap-relevant change; label demand model.
- Gates are process-global; the standard-capture logs in `out/geneva-overlap-*.log` were taken
  at specific gate combinations — with the new defaults (partials+ringbreak ON) you must set
  `LIVECITY_RINGBREAK=0`/`LIVECITY_PARTIALVEH=0` EXPLICITLY to reproduce old arms.
- Witness/capture determinism is exact (same env+steps ⇒ same vehicles/positions/times);
  TRACEVEH replays are surgical. `[veh]`/`[jyrow]`/`LIVECITY-OVERLAP` are committed instruments.
- Run the FULL sln suite before any default push (done for both default flips this session).

## 5. If the next session only reads three things

1. This doc.
2. Journal Entries 52–56 (the overlap hunt: instrument → classes → ratchet → merge → targets).
3. `docs/PARTIAL-OCCUPANCY-DESIGN.md` (the shipped mechanism + phase-2 scope).
