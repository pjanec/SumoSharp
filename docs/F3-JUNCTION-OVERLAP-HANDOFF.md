# HANDOFF — F3 junction car–car overlap + F4b zero-overlap invariant (CORE)

> **STATUS: HISTORICAL TRAIL (2026-07-28)** — ⚠ **Several claims in here are DISPROVEN** — `docs/F3-SESSION-LOG.md` §4 says so explicitly and is the authority on what actually held (notably this brief's F4b "assert zero overlap" premise, later shown to be *stronger* than SUMO parity). Do not treat it as current guidance. Kept deliberately: it is a reasoned-from-source hypothesis that a trace later corrected, which is the exact class of record CLAUDE.md measurement-discipline #2 is built out of.


**Self-contained brief for a session that fixes a PRE-EXISTING core junction bug** and then turns on the
strict no-overlap demo invariant. Read top-to-bottom; assumes near-zero prior context. **This is CORE
engine / SUMO-parity work, not a live-city realism seam** — the parity iron law is in full force and this
fix WILL move junction behaviour, so treat it as a from-`/sumo/` port with a real golden-regeneration
decision, not a demo-gated knob. Facts marked **[verified]** were checked against source (file:line);
treat the rest as leads.

**Branch: reuse `claude/livecity-realism-fixes-vr4k4b`** (per owner). Its realism-A/B work (Task A / F2) is
already shipped and pushed; F3+F4b is a fresh workstream on the same branch. Doc prefix: `F3-JUNCTION-*`.
**Per `CLAUDE.md`: design-first.** This is a large engine change — produce and get agreement on the
`design → task-description → task-tracker` trio in `docs/` BEFORE editing `Engine.cs`.

---

## 0. What F3 is (and what it is NOT)

**F3 = two vehicles occupy the same world space at a junction** — up to ~3 m OBB penetration — because the
engine admits a car onto/through an internal (`:`-prefixed) junction lane **without ever checking whether the
physical conflict point is already occupied.** Admission is decided purely by *right-of-way* (the static
`<request>` response matrix) + arrival-window timing + signal priority. Nothing consults geometric occupancy.

It is **REAL and AUTHORITATIVE** (not a render artifact — the engine's own `Sample()` positions overlap) and
**pre-existing on `main`** (identical worst pair, so NOT a regression from any recent session). It is the
**explicitly-deferred residual** from the lane-change-overlap work (see §5): "port SUMO's internal-junction
foe/merge logic (Option A)", owner-accepted as deferred on 2026-07-21.

**NOT F3:** the lateral lane-change "into-occupied" cut-in veto (`MergeStoppedMinGap`, a different mechanism
with the same name), keepClear-exit blocking, far-routed-foe false positives, or the maneuver-straddle
freeze — all already solved (§5). Do not re-solve those.

## 1. The two confirmed patterns (repro these first)

Both from the live-city demo (`scenarios/_ped/demo_city/box`), reproducible headlessly. Owner saw them in a
`--live-city-demo` replay; they are authoritative, not DR/player artifacts (the player was exonerated — see
`LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md` §F1).

- **Pattern A — crossing internal lanes, ego does NOT yield.** Two cars cross one junction on *different*
  internal lanes whose road space overlaps; ego's link does not `RespondsTo` the foe's link, so the foe is
  skipped and ego drives through it. Confirmed: **`veh134/veh38` 3.035 m** (worst, step 197); **veh58 drives
  through stopped veh159** (veh159 stopped on `:d_4_2_4_1`, veh58 crossing `:d_4_2_7_0`).
- **Pattern B — green ego crosses through a stopped car on a stub/approach (keep-clear).** A car stopped on a
  garage-stub/approach lane sits in the swept path of a car crossing on green. Confirmed: **veh80/veh120**
  (veh120 motionless at `(2862.90,2851.60)` on `e_d_garage_stub_d_5_5_1`, red; veh80 crosses via `:d_5_5_6_1`
  on green through the identical pose) + **veh80/veh134**, ~1.8 m each. This was the "F1" report ("veh80 ran a
  red / drove through veh120") — resolved as a misread + this overlap.

**Repro commands** (build `src/Sim.Viz -c Release` first):
- `dotnet run --project src/Sim.Viz -c Release --no-build -- --live-city-drcheck 300` — prints the
  **AUTHORITATIVE** overlap pass (`worst≈3.03m pair __veh134/__veh38`) AND the DR-render pass. **[verified]**
- `dotnet run --project src/Sim.Viz -c Release --no-build -- --live-city-cartrace 90 __veh80 46 85` — per-tick
  authoritative state (`authSpd,lane,binder,tl,gap,pos1d,posLat,pos,angle`) — shows veh80 crossing on green
  through veh120's fixed pose. Swap the id for veh120/veh134/veh58/veh159. **[verified]**
- `scenarios/_diag/willpass-saturation` is the dense grid where the 2 residual crossing/merge overlaps that
  `LaneChangeOverlapDiagTests` (currently `[Fact(Skip)]`) counts also live — a smaller repro than the demo.

## 2. Root cause — the missing admission gate [verified]

The single admission gate for entering an internal lane is **`JunctionYieldConstraint`** (`Engine.cs:6642`,
called from `ComputeMoveIntent:5160`, binder tag 10). It returns a speed cap folded into the `Math.Min`
constraint stack. Its **foe loop** (`Engine.cs:6890–7134`) is the seam:

```
for (var j = 0; j < junction.IntLanes.Count; j++) {
    if (j == egoLink.Index || !request.RespondsTo(j)) continue;   // Engine.cs:6892  <-- THE GATE
    ...
}
```

- **`request.RespondsTo(j)`** = "ego formally YIELDS to link j" (the static `<request>` Response bit). Ego only
  ever *considers* a foe on a link it yields to. If ego is major (or wins a mutual-foe tie), the foe is
  `continue`d past — **even if a car is physically stopped on j in ego's swept path.** → Pattern A.
- **`egoHasSignalPriority`** (`Engine.cs:6723`, via `EgoLinkHasSignalPriority:2419`): a protected-green (`'G'`)
  ego skips the approaching-foe yield entirely (applied `6832`, `7046–7049`, `7109`). And a car stopped on a
  *stub/approach* lane is not on any internal foe lane, so it is never in the foe index at all → no foe found.
  → Pattern B.
- **`AdaptToJunctionLeader`** (`Engine.cs:7934`) is the **only** arm that reacts to a foe's *physical presence*
  on a crossing internal lane (car-following against it). But it is reached **only** through the
  `RespondsTo(j)` gate — so it never fires for the two F3 patterns.

**The gap:** there is **no into-occupied / physical-conflict-point admission check**. The `Foes` bit
(`JunctionRequest.FoeWith(j)` = "link j physically conflicts, irrespective of who yields") is **parsed but
used only by the right-before-left entry-cycle resolver** (`Engine.cs:5968`), never to brake ego for a
physically-present foe on a link ego does not yield to. **[verified: `FoeWith` grep shows only 5968 + a
comment.]**

**Where the fix goes:** a new check inside the foe loop (or a sibling constraint) that runs **regardless of
`RespondsTo(j)` and regardless of `egoHasSignalPriority`** — "is my swept internal-lane path / conflict point
currently occupied by a (near-)stopped or slower-arriving vehicle I cannot clear?" — braking ego to hold
before the conflict point. The geometry it needs **already exists**: `JunctionConflict` (`NetworkModel.cs:163`
**[verified]**) carries `CrossingPoint(X,Y)`, `EgoCrossingArc`/`FoeCrossingArc`, `EgoConflictSize`/
`FoeConflictSize`, `EgoLengthBehindCrossing`/`FoeLengthBehindCrossing` — the same fields `AdaptToJunctionLeader`
already consumes, currently gated behind the yield check. Iterate foes by `FoeWith(j)` (physical conflict),
not just `RespondsTo(j)` (yield).

## 3. Port from SUMO — this is a parity port, not an invention

**`/sumo/` is ABSENT in this VM** (CLAUDE.md's path is wrong here — the vendored copy is at **`<repo>/sumo/`**,
present **[verified]**). SUMO 1.20.0 is also installable for direct engine-vs-SUMO diffing (network-enabled;
the offline `dotnet test` loop must never call it). Port targets:

- **`sumo/src/microsim/MSLink.cpp`** — `opened()`, `blockedByFoe`, `getLeaderInfo`, `setRequestInformation`
  (the crossing geometry already ported into `JunctionConflict`). SUMO's real mechanism for "don't enter a
  junction if you'd hit a foe you can't clear" is **`MSVehicle::checkLinkLeaderCurrentAndParallel`** /
  `adaptToLeaders` over **all** foe links' leaders (not just yielded ones), plus **`checkRewindLinkLanes`**
  (don't commit into a junction lane whose exit you cannot fully clear — the box-block case). These are cited
  in the C# comments by path; obtain the exact source from `sumo/src/microsim/MSVehicle.cpp`
  (`planMoveInternal`, `checkLinkLeaderCurrentAndParallel`, `checkRewindLinkLanes`). **Read them; match the
  calculation order.**
- **`sumo/src/netbuild/NBRequest.cpp`** — response/foes bitstring convention (already mirrored in
  `JunctionRequest`, rightmost-bit).

**Design-first deliverable:** the design doc's job is to decide *which* SUMO mechanism this is (leader-check
vs rewind vs both), map it onto the existing `JunctionConflict`/foe-index data, and argue determinism +
parity. Do not start in `Engine.cs`.

## 4. Parity — the hard part (READ THIS)

Unlike the realism sessions, **F3 is NOT parity-inert.** It changes when cars enter junctions, so it CAN move
junction-scenario goldens. The discipline:

1. **Follow SUMO exactly** (CLAUDE.md prime directive). The fix should make us *more* SUMO-correct, not less.
2. Run the full offline gate: `dotnet test tests/Sim.ParityTests -c Release` (currently **661/4** byte-identical
   **[verified]**), `Sim.Bench` hash **`D96213B7BB4021A7`** (par==single), `dotnet test tests/Sim.LiveCity.Tests`
   (**27/27**, run **WITHOUT** `--no-build`).
3. **If a golden shifts:** it is either (a) the fix is SUMO-faithful and the OLD golden was already wrong at
   that junction → **regenerate goldens** (`scripts/regen-goldens.sh`, network-enabled, ends in a commit) after
   **diffing our trajectory against live SUMO** on that exact net+demand to confirm we now MATCH SUMO; or
   (b) the fix diverges from SUMO → the fix is wrong, revert/rework. **Never accept a golden shift without a
   SUMO diff proving we moved toward SUMO.** This decision is the crux of the task and must be owner-visible.
4. Expect many junction fixtures to be touched (`08-junction-straight`, `11-priority-junction`, `27-allway-stop`,
   `29/31-merge-yield`, `32/33-roundabout`, `34-keepclear`, `38-keepclear-crosstraffic`, `39-crossjunction-leader`,
   `44-multilane-junction-turn`, `51/52-emergency-foe`). Their existing parity tests (`RungC4*`, `RungB5JunctionFoe`,
   `RungCrossJunctionLeader`, `RungC5KeepClear`, etc.) are your regression guard — keep them green or regen with
   a SUMO diff.

**Related open NEEDs to reconcile (don't regress the over-yield direction):**
- `NEED-multilane-junction-passage.md` — on `-L 2` grids ~60% of vehicles deadlock at stop lines (the engine
  *over*-yields: a parallel through-lane read as a blocking foe). F3 is the *under*-yield dual. A physical-occupancy
  gate must not deepen this deadlock. Test on a multilane grid.
- `NEED-priorityjunction-farrouted-foe-falsepositive.md` — the `FindFoeVehicle` reservation-distance gate
  (`foeNotApproaching`, `Engine.cs:7008`) already fixed one over-yield; your new gate must respect it.

## 5. Prior art — what is solved vs what remains

- **`LANE-CHANGE-OVERLAP-{DESIGN,STATUS,TRACKER}.md`** — ported `MSLaneChanger::checkChange` `LCA_OVERLAPPING`
  block + keep-right veto + `CrossJunctionLeaderConstraint`; dense same-lane overlaps 197→2. **STATUS §3 /
  TRACKER Stage-3 explicitly name the 2 residual as junction crossing/merge overlaps and defer them
  ("Option A: port SUMO internal-junction foe/merge logic", owner-accepted 2026-07-21). `LaneChangeOverlapDiagTests`
  is `[Fact(Skip)]`, asserts overlap==0, currently reads 2.** ← **F3 IS this deferred Option A.** Un-skipping and
  greening that test is a natural F3 success sub-condition.
- **`ISSUE2-JUNCTION-KEEPCLEAR-DESIGN.md`** — `KeepClearConstraint` (`Engine.cs:7221`, binder 11) protects ego's
  *own downstream exit* from being blocked; it is occupancy-aware only for the exit lane and blind to `IsParked`
  vehicles (`LaneNeighborQuery.cs:76`). Not the crossing-overlap fix, but the closest existing occupancy gate —
  study it as a template for where a conflict-point occupancy check would live.
- **`LIVE-CITY-15-INTO-OCCUPIED-DESIGN.md`** — the *lateral* cut-in veto (same name, different mechanism). Not F3.
- **`LIVE-CITY-15-LANECHANGE-JUNCTION-FIX-DESIGN.md`** — maneuver-straddle bookkeeping fix. Not F3.

## 6. F4b — the strict zero-overlap invariant (do AFTER F3)

F4b is currently DEFERRED **because** F3 makes "zero" impossible (a zero assertion would fail on the
pre-existing F3 overlap). Once F3 is fixed, overlap-free becomes the true baseline — then:

1. **Flip the authoritative test to ZERO.** `tests/Sim.LiveCity.Tests/DemoCarOverlapInvariantTests.cs` today
   asserts overlaps are PRESENT (worst>0.5) and BOUNDED (worst<4.0, ≤7 pairs/frame) — a fail-first F3
   characterization + gross tripwire. Flip both assertions to **assert ZERO overlap** (beyond the ~5 cm numeric
   grazing threshold). Keep the F4a straddle guard (`DemoAuthoritative_NoStoppedCarStraddlesPastItsLane`) as-is.
2. **Add the DR-render overlap check.** The owner's requirement: "DR-caused overlaps are as bad visually as real
   ones." `RunLiveCityDrCheck` (`src/Sim.Viz/Program.cs:659` **[verified]**) already runs BOTH an authoritative
   OBB pass AND a DR-render pass (over `VizReplayBuilder.Build(...).Frames`, OBB via `ObbOverlap`, forward
   `(-sinθ,cosθ)`). Promote its DR-render pass into a committed test asserting ZERO. Architecture snag: the DR
   check needs `Sim.Viz`/`VizReplayBuilder`, and **no `Sim.Viz` test project is in `Traffic.sln`** — either add
   one, or expose the reconstruction to a project that is (see FINDINGS §F4 architecture note). Refactor the OBB
   loop in `RunLiveCityDrCheck` into a shared testable method to avoid duplicating the SAT math.
3. Optionally also assert (b) no car crosses a red stop line and (c) minGap respected, per FINDINGS §F4.

**Heading-convention lesson (do not re-hit):** the OBB forward axis is **`(-sinθ, cosθ)`**, NOT `(cosθ, sinθ)`
(the naive mapping rotates every box 90° → pervasive false overlaps: 3215→467 when fixed). Validated on veh80
(`angle=90` runs along world X). The committed `ObbOverlap` (both in `Program.cs` and
`DemoCarOverlapInvariantTests`) already uses the correct convention — copy it, don't re-derive.

## 7. Success conditions

1. **F3:** `--live-city-drcheck 300` AUTHORITATIVE pass reports **0** car–car overlaps (currently worst 3.035 m,
   ~116 pair-events/200 steps). Patterns A and B (veh134/veh38, veh80/veh120) both gone.
2. **SUMO-faithful:** any golden that shifts is proven (by a live-SUMO trajectory diff) to move TOWARD SUMO;
   goldens regenerated + committed via `scripts/regen-goldens.sh`. No unexplained golden change.
3. **No new deadlock:** the multilane-junction and far-routed-foe NEEDs do not regress; throughput on the demo
   (`DenseFlow_…NoGridlock`) and junction fixtures stays green.
4. **`LaneChangeOverlapDiagTests`** un-skipped and green (overlap==0), OR its residual explicitly re-characterized.
5. **F4b:** `DemoCarOverlapInvariantTests` flipped to assert ZERO (authoritative) + a committed DR-render
   zero-overlap test, both green.
6. Full gate green (with regenerated goldens if applicable): `Sim.ParityTests`, `Sim.Bench` hash, `Sim.LiveCity.Tests`.

## 8. Key files

- **`src/Sim.Core/Engine.cs`** — `JunctionYieldConstraint` (`:6642`, foe loop `:6890–7134`, the gate `:6892`),
  `AdaptToJunctionLeader` (`:7934`), `KeepClearConstraint` (`:7221`), `FindFoeVehicle`/`FindCrossFoeVehicle`
  (`:8014`/`:8030`), `BuildFoeApproachIndex` (`:8046`), `EgoLinkHasSignalPriority` (`:2419`),
  `ResolveRightBeforeLeftCycles` (`:~5959`, the only `FoeWith` user).
- **`src/Sim.Ingest/NetworkModel.cs`** — `JunctionRequest` (`:138`, `RespondsTo`/`FoeWith` `:141–142`),
  `JunctionConflict` (`:163`, the crossing geometry), `MergeConflict` (`:186`), `Junction`/`JunctionLink`.
- **`sumo/src/microsim/`** — `MSLink.{cpp,h}`, `MSRightOfWayJunction.{cpp,h}`, `MSVehicle.cpp`
  (`checkLinkLeaderCurrentAndParallel`, `checkRewindLinkLanes`, `planMoveInternal`).
- **Tests/fixtures** — `tests/Sim.ParityTests/Rung{B5JunctionFoe,C4i…C4vii,C5KeepClear,CrossJunctionLeader}*.cs`,
  `LaneChangeOverlapDiagTests.cs` (skipped); `scenarios/{08,11,27,29,31,32,33,34,38,39,44,51,52}-*`,
  `scenarios/_diag/willpass-saturation`.
- **F4b** — `tests/Sim.LiveCity.Tests/DemoCarOverlapInvariantTests.cs`, `src/Sim.Viz/Program.cs`
  (`RunLiveCityDrCheck` `:659`, `ObbOverlap`), `src/Sim.Viz/VizReplayBuilder.cs`.
- **Context docs** — `LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md` (§F1/§F3/§F4), `LANE-CHANGE-OVERLAP-STATUS.md`,
  `ISSUE2-JUNCTION-KEEPCLEAR-DESIGN.md`, `NEED-multilane-junction-passage.md`,
  `NEED-priorityjunction-farrouted-foe-falsepositive.md`.

## 9. Iron laws & boundary

- Parity is the iron law and F3 is NOT inert — every change is a SUMO port with a golden-diff decision (§4). No
  `System.Random`; per-entity seeded RNG. Offline `dotnet test` must never call SUMO. `sumo/` is read-only.
- **Coordinate on `Engine.cs`:** if a live-city session (ped–vehicle avoidance) is running concurrently, it owns
  `CrowdLongitudinalConstraint`/`CrossRegimeCoupling`/`ExternalObstacle`, and realism-A/B owns
  `ComputeLateralEvasion`'s `SuppressHeldCrowdSwerve` gate — both are far from the junction-yield foe loop, but
  you all edit `Engine.cs`, so rebase often and keep changes localized to the junction methods above.
- Design-first (§0): the `design → tasks → tracker` trio in `docs/` with owner sign-off precedes any edit.

## 10. Open questions for the design phase

1. Is the fix a **leader-check** (brake for any foe on a physically-conflicting link, like
   `checkLinkLeaderCurrentAndParallel`), a **rewind/box-block** (don't enter a junction lane you can't clear,
   like `checkRewindLinkLanes`), or both? Which does each F3 pattern need? (A ≈ leader-check on a non-yielded
   foe; B ≈ green ego must still not enter an occupied conflict point.)
2. Does the fix belong inside `JunctionYieldConstraint` (iterate `FoeWith` instead of only `RespondsTo`) or as a
   sibling constraint folded into the `Math.Min` stack (a new binder tag)? Determinism + ordering argument.
3. How to keep it from deepening the multilane over-yield deadlock (`NEED-multilane-junction-passage`)?
4. Will the fix shift goldens, and for which fixtures? Plan the SUMO-diff + regen up front, not as an afterthought.
