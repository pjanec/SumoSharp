# JUNCTION-FOE-LANE — resume here (the Geneva overlap/pass-through fix, mid-implementation)

**Read this first, cold. It is self-contained.** Branch **`claude/sumosharp-traffic-bugs-g1y9hl`**,
handoff at `90dbb98`. Suite: `dotnet test tests/Sim.ParityTests -c Release` = **779 / 5 / 0**.
Everything in this workstream is gate-scoped under **`SUMOSHARP_PHYSOCCUPANCY`**
(`Engine.JunctionPhysicalOccupancyGate`, default **OFF**); gate-off is **byte-identical** to the
pre-workstream engine (city-organic-L2 FCD hash `c768d7f6dd8535f46f170956737a2921`, re-verify after
any edit). Trail: journal **Entries 35, 35b** (evidence + the six-step measurement ladder), design
`JUNCTION-FOE-LANE-DESIGN.md`, live state `JUNCTION-FOE-LANE-TRACKER.md`. Owner sign-off: given
("go autonomously").

## 1. The task

The owner's two Geneva-terrain reports, reproduced offline (Entry 35): **(a)** queue-tail
stacking = same-junction DOUBLE-LANDINGS (two movements exit one junction the same step onto the
shared arrival lane) — **FIXED under the gate** (F2.2: onsets 12 → 5, deadlock-free);
**(b)** pass-through of a car blocked mid-junction — decomposed: the dominant sub-class (26–34 of
~37 stopXmove pair-steps) is a turner WAITING in a first-stage cont **bay** whose corridor
netconvert draws on top of sibling movements, and which appears in NO foes row. **The bay half is
the open work**: its geometry, physical index, and constraint arm are committed, but the HOLD
TIMING is unresolved. That is what this doc hands off.

## 2. The instrument loop (run these, exactly)

```bash
# builds -- Sim.Run/Sim.Sumo are NOT in Traffic.sln (measure stale code otherwise):
dotnet build -c Release src/Sim.Core/Sim.Core.csproj src/Sim.Run/Sim.Run.csproj
# A/B on the workhorse net (deterministic engine -- identical cmd => identical FCD):
dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /tmp/off.fcd.xml
SUMOSHARP_PHYSOCCUPANCY=1 dotnet run --project src/Sim.Run -c Release --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /tmp/on.fcd.xml
python3 scripts/classify-junction-overlaps.py /tmp/on.fcd.xml     # the Entry 35 classifier
python3 scripts/analyze-junction-realism-fcd.py /tmp/on.fcd.xml   # DRAINED-vs-GRIDLOCK + dwell
```

**The scoreboard** (city-organic-L2, 1000 steps, classifier pair-steps / landing onsets):

| arm state | bothMove | bothSlow | stopXmove | landings | flow |
|---|---|---|---|---|---|
| gate OFF (= shipped default) | 145 | 23 | 17 | 12 | drained |
| honest SUMO (same net) | 4 | 0 | 0 | 0 | drained |
| ON, committed code as-is | ~121–130 | blows up | 19–35 | 4–6 | **GRIDLOCKS** (see §3) |
| ON, stopped-only hold variant | 130 | 17 | 35 | 6 | drained (holds too late) |
| ON, any-body hold variant | 119 | 652 | 13 | 4 | GRIDLOCK (dwell 634) |

## 3. ⚠ THE COMMITTED HOLD PREDICATE IS THE GRIDLOCKING VARIANT

The bay arm as committed holds for occupants with `Speed <= 2.0` not past `BayArcEnd + 1.0` —
the last-measured variant, which **gridlocks city-organic-L2 with the gate ON** (41 stuck,
dwell 634). This is deliberate: the gate is OFF so it is inert, and the variant is kept so the
gridlock episode can be traced. **Do not tune the threshold further** — three points on that dial
are already measured (Entry 35b's ladder); the trade-off is structural: any hold early enough to
prevent the overlap collapses saturated throughput, any hold late enough to preserve flow misses
occupants that stop one step after ego commits.

## 4. Where the code is (search anchors in `src/`)

- **Bay arm**: `Engine.cs`, search `F2.1b` — inside `JunctionYieldConstraint` after the IntLanes
  foe loop, `jyArm = 7`. The hold predicate to be replaced is the `cand.Kinematics.Speed > 2.0`
  block.
- **Physical occupancy index**: `_physOnLaneFirst/Second`, filled in `BuildFoeApproachIndex`
  (the route-pool indexes first-mask bay occupants behind distant approaching vehicles — measured;
  never revert to `FindFoeVehicle` for physical questions).
- **Bay geometry**: `BayConflict` (NetworkModel + the parser pass, search `F2.1b`),
  `PolylineGeometry.TryCorridorOverlap` (proximity-sampled — centerline crossing CANNOT see
  near-parallel bay corridors; threshold 2.0 m). Bay lane id = cont link's
  `Connection.From + "_" + FromLane` (the JunctionLink.Connection of a cont link is the
  second-hop INTERNAL connection).
- **F2.2 (done)**: merge reachability `!respondsTo && !physicalFoe` + `arbitration:` param on
  `SameTargetMergeConstraint` (PHASE 0 stays RespondsTo-only) + `IsLeaderByEntryOrder` PHASE-1
  tie-break (antisymmetric ⇒ deadlock-free). Do not touch.
- **Env plumbing**: `SUMOSHARP_PHYSOCCUPANCY` in `Sim.Run/Program.cs` + `SumoShim.cs`,
  `docs/ENV-GATES.md` row, completeness test green.

## 5. The designed next step — F2.1c, wait-point relocation (NOT attempted yet)

Stop treating the symptom (ego braking for a stopped bay body) and remove the cause (a stopped
body in a shared corridor): **when a bay cannot physically shelter a waiting car, the turner must
wait BEFORE the junction, not in the bay.**

1. **Ingest**: flag each bay lane whose WAIT REGION (`[bayLen − carLen − margin, bayLen]`,
   carLen ≈ 5, margin ≈ 0.5) intersects the bay-side interval `[BayArcStart, BayArcEnd]` of any
   `BayConflict` referencing it → `degenerate bay` (per-lane bool or set on the Junction).
   On city-organic-L2 expect most bays to qualify (they are 3.6–7.8 m and overlap from arc 0).
2. **Engine**: `InternalJunctionAdmissionConstraint` (binder 14 — the arm that today holds a cont
   vehicle IN the bay when stage 2 is not clear; search `InternalJunctionAdmissionConstraint` /
   `prePass ? !foe.WillPassPrev : !foe.WillPass`): for a DEGENERATE bay, apply the SAME
   stage-2-clear test but hold at the **junction entry** (approach-lane stop line — the same
   `StopSpeedFor(... egoDistToEntry ...)` form the yield arms use), so the turner never enters
   the bay until it can traverse both stages. Gate-scoped under the same
   `JunctionPhysicalOccupancyGate`.
3. Then the bay arm's residual job shrinks to transient movers; re-measure the whole classifier —
   the hold-timing dial may become irrelevant.
4. **Before any of that**: ONE episode trace of the recurring gridlock — reproduce with the gate
   ON (`--steps 1000`, deterministic), find the dwell-634 vehicle (analyzer prints it), trace it
   (`SUMOSHARP_TRACEVEH`) and its blocker chain (`[lccommit]`/binder diags) to name the cycle.
   The same site wedged in BOTH gridlocking variants — it may be one specific junction shape, and
   knowing the cycle validates (or falsifies) the F2.1c interleave argument before it is built.

Risks to watch (measured hazards, not hypotheticals): entry-holds feed back into willPass/
admission dynamics (the L2 collapse class of Entry 26's naive flag); battery stuckDwell is the
standing wedge gate; `city-3000`'s closed-loop is the capacity canary.

## 6. Acceptance gates (F3.1, before any default flip)

1. Classifier, city-organic-L2 + city-mixed-1k, gate ON: stopXmove → ~0 (SUMO 0), landings ≤ 2,
   bothMove materially toward SUMO's 4, bothSlow ≈ OFF-baseline (no capacity collapse).
2. Flow: DRAINED on both nets, dwell ≤ OFF-baseline (16), battery vs
   `docs/reports/net-regression-entry34-stays.txt` — stuckDwell 0 everywhere, arrivals in noise.
3. Gate-off byte-identical (hash above) + suite 779/5/0 + goldens untouched.
4. Determinism repeat-hash (≥4 runs + `--max-parallelism 1` via `src/Sim.Sumo`), both gate states.
5. Live-city demo smoke (overlaps 0, no gridlock, dead-stop ≈ 9–12%).
6. Owner Geneva re-check — the report that started this is the final gate.

## 7. Traps (each already cost time THIS workstream)

- `Sim.Run`/`Sim.Sumo`/`Sim.Viewer` are NOT in `Traffic.sln` — build csproj explicitly.
- Env gates are process-global; `env | grep SUMOSHARP` before any A/B; set the gate explicitly in
  BOTH arms.
- The F3-era "JunctionPhysicalOccupancyGate measured counterproductive, do not retry" verdicts
  predate the sibling gates shipping default-ON and the Entry-30 determinism fix — re-measure,
  don't inherit them; but ALSO don't inherit this session's numbers after any engine edit.
- The pool-based foe indexes answer "who is ROUTED through", not "who is STANDING ON" — use
  `_physOnLane*` for physical questions.
- Scratch FCDs die with the VM; the engine is deterministic — regenerate with the exact commands
  in §2 rather than trusting stale files.
- Reasoned mechanism hypotheses are ~0-for-20 in this workstream. The classifier + analyzer pair
  is the ground truth; when a number disagrees with your reading of the code, trust the number.
