# F3 — junction car–car overlap: DESIGN (HOW)

**Status:** design, pending owner sign-off before any `Engine.cs` edit (per `CLAUDE.md` design-first).
**Requirements/WHAT:** `docs/F3-JUNCTION-OVERLAP-HANDOFF.md`. This document does not restate the WHAT.
**Branch:** `claude/f3-junction-overlap-handoff-okf5nu`.

This is **CORE engine / SUMO-parity work**. The parity iron law is in full force: every behavioural change
here is a port from `sumo/`, and any golden that shifts must be proven (by a live-SUMO trajectory diff) to
have moved *toward* SUMO before it is regenerated.

---

## 0. Measured baseline (this branch, reproduced before any change)

Established by running the committed instrument, not by trusting the handoff:

| Gate | Measured |
| --- | --- |
| `dotnet test tests/Sim.ParityTests -c Release` | **661 passed / 4 skipped / 0 failed** |
| `dotnet run --project src/Sim.Bench -c Release` | hash **`D96213B7BB4021A7`**, `deterministic=True`, par==single |
| `DemoCarOverlapInvariantTests` worst penetration | **3.035 m**, pair `__veh134 / __veh38`, step **197** |
| overlapping pairs in worst frame | 3 |
| total overlapping-pair events (200 steps) | 61 |

The severity figures match the handoff exactly (3.035 m / veh134+veh38 / step 197). Two differences from the
handoff are recorded here so nobody re-derives them:

- **Event count is 61, not 178.** The handoff's 178 was measured with a different merge state.
- **`--live-city-drcheck` and `--live-city-cartrace` DO NOT EXIST** on this branch, contrary to the handoff's
  `[verified]` marks. `src/Sim.Viz/Program.cs`'s `--live-city*` surface is only `--live-city` and
  `--live-city-demo`; `RunLiveCityDrCheck` was deleted and survives only in commit `d9b209b` and as the
  copied `ObbOverlap` inside `DemoCarOverlapInvariantTests`. **The committed test is the repro instrument**
  (better: offline, committed, no SUMO).

## 1. What the 61 overlaps actually are — F3 is 8 of them, not 61

Lane-classifying every overlap event (instrument: `tests/Sim.LiveCity.Tests/F3JunctionOverlapDiagTests.cs`,
always-passing diagnostic) splits the 61 into **four unrelated causes**:

| Bucket | Events | Worst | Cause |
| --- | --- | --- | --- |
| `BOTH-INTERNAL-DIFFERENT-LANE` | **8** | **3.035 m** | **TRUE F3** — two cars on crossing internal lanes of one junction |
| `ONE-INTERNAL-ONE-NORMAL` | 31 | 1.800 m | almost all `pos≈232.40, spd=0.00, tl=r` — a car stopped at a red stop line |
| `BOTH-NORMAL-SAME-LANE` | 14 | 1.800 m | not a junction case at all; includes *exactly co-located* cars |
| `BOTH-NORMAL-DIFFERENT-LANE` | 8 | 1.800 m | `e_d_6_5_d_5_5_2` × `e_d_garage_stub_d_5_5_1` — two **normal** lanes overlapping |
| `BOTH-INTERNAL-SAME-LANE` | 0 | — | — |

**Only the 8-event bucket is in F3's blast radius.** Consequences, stated up front because they change scope:

- The handoff's **Pattern B is misdiagnosed.** It describes veh80/veh120 as "green ego crosses via internal
  lane `:d_5_5_6_1` through a stopped car". The per-step trace shows that for steps **51–57** veh80 is on
  **`e_d_6_5_d_5_5_2`, a NORMAL lane**, overlapping the garage stub — no internal lane is involved. Only
  steps 58–59 touch `:d_5_5_6_1`. The veh80 family is predominantly **two normal lanes whose geometry
  overlaps**, which no junction admission gate can fix.
- Therefore handoff **success condition #1 ("0 car–car overlaps") and F4b ("flip the invariant to assert
  ZERO") are NOT achievable by fixing F3.** They are gated on the three non-F3 causes below, each of which
  needs its own workstream. See §7.

### 1b. The instrument has a half-length anchor bug (affects the *numbers*, not the engine)

`LiveCitySim.Sample()` copies `_lastSnapshot.PosX/PosY`, filled at `Engine.cs:2278` from
`LaneGeometry.PositionAtOffset(lane.Shape, v.Kinematics.Pos, v.Kinematics.LatOffset)`. `Kinematics.Pos` is
the **front-bumper** arc-length (SUMO `getPositionOnLane()`/FCD convention) and `PositionAtOffset` subtracts
nothing — so the sampled `(X,Y)` is the **front bumper**.

`ObbOverlap` (in both `DemoCarOverlapInvariantTests` and the deleted `RunLiveCityDrCheck`) treats `(X,Y)` as
the box **CENTRE**, building `±Length/2` about it. **Every vehicle box is therefore drawn shifted forward by
`Length/2` (~2.2 m).** The true centre is

```
centre = (X, Y) - (Length/2) * forward,   forward = (-sin θ, cos θ)
```

This is the same *class* of bug as the heading-convention error the handoff warns about (§6, "3215→467 when
fixed") — corrected on the heading axis, missed on the longitudinal anchor.

Direct consequence: a car stopped with its front bumper at the junction boundary gets a box poking ~2.2 m
*into* the junction, manufacturing overlaps against anything on an internal lane — which is exactly the
signature of the 31-event `ONE-INTERNAL-ONE-NORMAL` bucket (`pos≈232.40, spd=0.00, tl=r`).

**Scope of the bug: measurement only.** The engine's own longitudinal logic is anchor-consistent
(`gap = leaderPos - leaderLength - egoPos`), so the anchor error never entered the simulation. It inflates
the *reported* penetration depth and invents *phantom* events; it does not cause the F3 overlap.

The A/B (front-anchor vs centre-corrected, same trajectories) quantifies exactly how much of each bucket
survives; §7 records the outcome. **The F3 fix is judged on the centre-corrected numbers.**

## 2. Root cause of the 8 true F3 overlaps — a faithful-port gap

SUMO deliberately keeps **two** foe sets per link, from **two** different bitstrings
(`sumo/src/microsim/MSRightOfWayJunction.cpp:92-146`):

| SUMO member | Built from | Drives | Meaning |
| --- | --- | --- | --- |
| `myFoeLinks` | `SUMO_ATTR_RESPONSE` | `opened()`, `blockedByFoe()`, `hasApproachingFoe()` | "links I must **yield** to" — right-of-way arbitration |
| `myFoeLanes` | `SUMO_ATTR_FOES` | **`MSLink::getLeaderInfo`** | "lanes that **physically conflict**" — irrespective of who yields |

`MSLink::getLeaderInfo` (`sumo/src/microsim/MSLink.cpp:1349`, foe loop `:1373`) walks **`myFoeLanes`** — the
*physical* set — and inspects **every vehicle** on each such lane. Its skip conditions are geometric, and the
"foe won't pass" skip (`MSLink.cpp:1498-1509`) is explicitly **overridden by `inTheWay`**
(`MSLink.cpp:1440-1443`, the "foe's footprint is on my crossing point" predicate).

`MSVehicle::checkLinkLeader` (`sumo/src/microsim/MSVehicle.cpp:3395`) then gates adaptation on
(`MSVehicle.cpp:3429`):

```cpp
} else if (isLeader(link, leader, it->vehAndGap.second) || it->inTheWay()) {
```

**`|| inTheWay()` is the load-bearing clause: physical occupancy constrains ego even when ego has
right-of-way, and even on a protected green.**

### What we ported, and the one thing we got wrong

Our ingest is already correct: `NetworkParser.cs:312-317` builds `JunctionConflict` geometry for every pair
where **`request.FoeWith(j)`** — the physical set. `JunctionRequest` exposes both bits correctly
(`NetworkModel.cs:138-145`, rightmost-char = link 0).

Our occupancy reaction is also already correct: `AdaptToJunctionLeader` (`Engine.cs:7934`) is a faithful port
of `MSVehicle::adaptToJunctionLeader`, including the `gap < 0` → `StopSpeedFor` branch that handles "the foe
is physically in my crossing box right now".

**The single defect is the gate.** `Engine.cs:6890-6895`:

```csharp
for (var j = 0; j < junction.IntLanes.Count; j++)
{
    if (j == egoLink.Index || !request.RespondsTo(j))   // <-- Engine.cs:6892
    {
        continue;
    }
```

Everything — arbitration *and* occupancy — is behind `RespondsTo` (the **response** set). So
`AdaptToJunctionLeader`, our only occupancy-reactive arm, is **unreachable for a foe ego does not yield to**.
We collapsed SUMO's two sets into one. When ego is major over a physically-conflicting link and a foe is
sitting on that crossing internal lane, ego is never told the space is occupied and drives through it.

`FoeWith` is currently used **only** by `ResolveRightBeforeLeftCycles` (`Engine.cs:5968`) — never to brake
for a physically-present foe. That is the gap, and it is the deferred "Option A" from
`LANE-CHANGE-OVERLAP-*` (owner-accepted 2026-07-21).

Note `egoHasSignalPriority` (`Engine.cs:6723`) already does **not** gate the on-junction arm (only the
cautious-approach arm at `:6832` and the approaching-foe boolean at `:7109`), which already matches SUMO's
"`inTheWay` overrides priority". No change needed there.

## 3. The fix — restore SUMO's two-set split

Change the loop gate so the two mechanisms are separately gated, exactly mirroring §2's table:

```csharp
for (var j = 0; j < junction.IntLanes.Count; j++)
{
    if (j == egoLink.Index) continue;

    var respondsTo   = request.RespondsTo(j);   // SUMO myFoeLinks  -- right-of-way arbitration
    var physicalFoe  = request.FoeWith(j);      // SUMO myFoeLanes  -- physical conflict
    if (!respondsTo && !physicalFoe) continue;
```

then:

- **on-junction occupancy arm** (`Engine.cs:6969-6980`, `AdaptToJunctionLeader`) — runs whenever a
  `JunctionConflict` exists and a foe is on the foe internal lane, i.e. for `respondsTo || physicalFoe`.
  **This is the fix.** Mirrors `getLeaderInfo` over `myFoeLanes` + `checkLinkLeader`'s `|| inTheWay()`.
- **approaching-foe stop-line yield** (`:6981-7125`) — gated on `respondsTo` only. This is arbitration
  (`opened()`/arrival windows over `myFoeLinks`); widening it would invent yields SUMO does not perform and
  would deepen the `NEED-multilane-junction-passage` over-yield deadlock.
- **sameTarget merge arm** (`:6911-6924`) — gated on `respondsTo` only (unchanged reachability).
- **external-agent arm** (`:6947-6957`) — gated on `respondsTo` only (unchanged reachability).

**Why this is minimal and parity-safe:** for every `respondsTo` foe, control flow is *bit-for-bit unchanged*.
The only new behaviour is that a `FoeWith && !RespondsTo` foe **physically on its internal lane** now reaches
`AdaptToJunctionLeader`. The change is purely additive in the constraint stack (`Math.Min`), so it can only
ever *lower* a speed, never raise one.

### 3b. Mandatory companion: port `getLeaderInfo`'s geometric skip guards

`AdaptToJunctionLeader` currently has **no** "is this conflict still ahead of me?" guard, because the yield
gate made one unnecessary. Running it over the wider `FoeWith` set without those guards would be a bug:

- If ego is **already past** this conflict point, `distToCrossing < 0`, so `gap < 0` and the else-branch calls
  `StopSpeedFor(..., seen - egoLane.Length - PositionEps)` — a **negative** stop distance → hard brake / 0 →
  **ego freezes mid-junction at a conflict point it has already cleared.** That is a new deadlock, and
  precisely the over-yield direction `NEED-multilane-junction-passage.md` warns against.

SUMO guards this at `sumo/src/microsim/MSLink.cpp:1398` (skip #1):

```cpp
if (distToCrossing + crossingWidth < 0 && !sameTarget && (...)) continue;   // ego already past this crossing
```

and guards the symmetric foe-side case at `MSLink.cpp:1633` via
`pastTheCrossingPoint = leaderBackDist + foeCrossingWidth + sagitta < 0` → `continue`.

So the fix is **two** ported pieces, not one:

1. the gate split (§3), and
2. **skip #1 + `pastTheCrossingPoint`** ported into the occupancy arm — ego-past-crossing and
   foe-past-crossing both yield "no constraint" (`+∞`), not a brake.

(The foe-past case degrades gracefully today — a large positive `gap` makes the arm non-binding — but it is
ported explicitly to match SUMO's calculation order rather than relying on that accident. `sagitta` is a
curvature slack term over `myRadius`, which `JunctionConflict` does not carry; it is omitted and the omission
is recorded as a deliberate, documented deviation, consistent with the existing port's scope.)

### 3c. MEASURED OUTCOME — the port is incomplete, and the flag is off by default

§3 + §3b were implemented and measured. Result:

- **All 661 golden FCD parity tests stayed byte-identical.** Exactly as §5 predicted: single-vehicle-pair
  fixtures never reach the new arm.
- **5 gridlock diagnostics regressed** — the over-yield deadlock §3b warned about, in its worst form:
  `willpass-saturation` 0 → 290 stuck, dense-LC saturated grid → 304 stuck, dense drainage 290 → 235
  arrivals, `RblLeftTurns` gridlocked, `synthetic-junction2` 70 spurious teleports (62 yield-caused).

**Diagnosis:** widening the *set* is necessary but not sufficient. SUMO's gate is
`isLeader(...) || inTheWay()` and I had ported neither guard — braking for a foe *merely present* on a
conflicting lane, rather than one actually occupying the conflict point.

Porting `inTheWay` (`MSLink.cpp:1440-1443`) as a narrow predicate — crucially
`enteredTheCrossingPoint = leaderBackDist < leader->getLength()`, "the foe has actually REACHED the
conflict point" — recovered 2 of the 5 (dense drainage and RBL left-turns now pass). Implemented as
`FoeIsInTheWay`, and applied **only** to the newly-reachable `FoeWith`-without-`RespondsTo` case so that
`RespondsTo` foes keep the pre-F3 presence-only path and existing parity is bit-for-bit unchanged.

**The remaining 3 failures are the mutual-yield SYMMETRY problem** (290 stuck, 250 stuck, 3-vs-2 teleports).
Two cars in a mutual physical conflict each see the other in the way and both yield → deadlock. SUMO breaks
this symmetry in **`isLeader()`** (`MSVehicle.cpp:7343-7483`) by **junction entry time**, tie-broken by
speed and then by vehicle id. **We do not track junction entry time**, so completing the port requires new
per-vehicle timing state (`myJunctionEntryTime` / `myJunctionConflictEntryTime` equivalents) plus that
deterministic tie-break chain.

**Therefore the whole path sits behind `Engine.JunctionPhysicalOccupancyGate`, default `false`.** With the
flag off, `physicalFoe` is always `false`, the loop reduces exactly to the pre-F3
`!RespondsTo(j) => continue`, and every downstream branch takes its original path — byte-identical by
construction, verified: parity **661/4/0**, bench **`D96213B7BB4021A7`**, LiveCity **46/46**, Pedestrians
**272/272**. `CLAUDE.md` sanctions exactly this ("reverted or gated behind an explicit opt-in flag, never
silently accepted"). `LIVECITY_F3OCCUPANCY=1` enables it for A/B measurement.

The `FoeIsInTheWay` predicate and the §3b skip guards are SUMO-faithful and are kept **unconditional** in
`AdaptToJunctionLeader`; the full gate was re-run to prove they are parity-inert on their own.

### 3d. MEASURED A/B — the gate as implemented makes F3 WORSE. Do not enable it.

With the flag properly gated (see the trap in §3e), the demo A/B over 200 steps is:

| | flag OFF | flag ON |
| --- | --- | --- |
| total overlap events (front-anchor) | 61 | **86** |
| **`BOTH-INTERNAL-DIFFERENT-LANE` (the F3 target)** | **8** | **33** |
| worst penetration | 3.035 m | **3.385 m** |
| max overlapping pairs/frame | 3 | 6 |
| stopped cars/frame (last 10 steps, mean) | ≈19.7 | ≈26.2 |

The gate **quadruples** the very bucket it was built to eliminate, and deepens the worst penetration. Wholly
new deep overlaps appear on lane pairs that were previously clean (`:d_3_2_15_0`×`:d_3_2_21_0` 3.385 m,
`:d_3_4_1_0`×`:d_3_4_23_0` 3.214 m).

**Why — and this is the real lesson.** Braking for an occupied conflict point *without* symmetry-breaking
does not prevent the overlap; it **strands cars inside the junction**, where each stopped car becomes a fresh
obstacle on an internal lane for every crossing stream. Congestion rises (~33% more stopped cars/frame) and
the stranded cars overlap each other. **A yield that cannot resolve is worse than no yield.** This is the same
root cause as the 3 remaining gridlock failures, seen through the overlap metric instead of the throughput
metric.

**Conclusion: the FoeWith widening — even with the correct narrow `inTheWay` predicate — is
counterproductive on its own.** `isLeader()` is not a refinement to add later; it is **load-bearing**. Only
entry-time ordering makes exactly one of the two conflicting cars yield, so the conflict actually clears.

### 3e. Process trap worth recording: "all goldens byte-identical" ≠ "parity-inert"

The §3b skip guards were first added **unconditionally** and declared inert because the full golden suite was
byte-identical (661/4/0) and the bench hash unchanged. **That conclusion was wrong.** The live-city demo is
**not** a golden scenario, and the guards moved it measurably — front-anchor overlap events **61 → 94**, F3
bucket **8 → 27** — while every committed golden stayed byte-identical, because no committed fixture ever
reaches the "ego already past the conflict point with a RespondsTo foe" state the guards change.

The guards are now gated behind the same flag, restoring the demo baseline **exactly** (61 events / worst
3.035 m / `__veh134`/`__veh38` @ 197 / F3 bucket 8 — identical to the pre-change measurement), which is the
proof that "flag off" is now a true no-op **everywhere**, not merely across the golden suite.

**Rule for this codebase:** a change is parity-inert only when the goldens AND the non-golden behavioural
scenarios (the live-city demo, the dense/saturated gridlock diagnostics) are unchanged. Golden-only evidence
is necessary, not sufficient.

**Next task to finish F3: port `isLeader()`** (`MSVehicle.cpp:7343-7483`) with per-vehicle junction
entry-time state and the entry-time → speed → vehicle-id tie-break. That is the single remaining blocker, it
is a genuine engine addition rather than a tweak, and §3d shows the widening must not be enabled until it
lands.

## 4. Determinism & parity argument

- **No new state, no new iteration order.** The loop bound (`junction.IntLanes.Count`) and its order are
  unchanged; only two extra `bool`s per iteration, read from the statically-parsed `<request>` strings.
- **No RNG.** Nothing here draws; `IgnoresJunctionFoe` (ER2) keeps gating the arm exactly as today, so its
  existing per-entity seeded stream is untouched.
- **Order-independent reduction.** The arm folds through `Math.Min` into the existing binder-10
  `junctionYield` constraint — commutative, so foe-visit order cannot change the result. No new binder tag is
  introduced (this is the *same* junction-yield mechanism, correctly gated), which keeps the `#15` binder
  diagnostics stable.
- **Snapshot discipline preserved.** The arm reads the frozen start-of-step snapshot via
  `FindCrossFoeVehicle`, like every other arm. This matches SUMO's plan/execute separation, where
  `getLeaderInfo`'s approach data is deliberately one tick stale (`MSVehicle.cpp:5263`
  `setApproachingForAllLinks` runs *after* `planMoveInternal`).
- **`prePass` safety.** The new arm is not the approaching-foe yield, so it must **not** set
  `v.CrossingYieldTaken` (that flag exists solely so the real pass can relax a pre-pass approaching-yield via
  `!foe.WillPass`). The occupancy arm evaluates identically in both passes.

**Known limitation, inherited not introduced:** `FindCrossFoeVehicle` returns at most **one** foe per link
(`Engine.cs:8046-8105` keeps only the first two vehicles per internal lane). SUMO's `getLeaderInfo` scans
*every* vehicle on *every* foe lane. So the fix catches the first foe per conflicting link, not a queue of
them. This is the pre-existing single-foe-per-link scope limit (already documented at `Engine.cs:8003-8004`);
widening it is out of scope and is filed as a follow-up NEED.

## 5. Expected parity impact and the §4 decision

The change is inert for any (ego, foe-link) pair where ego already responds to the foe. It can only bite when
**ego is major/green over a physically-conflicting link AND a foe is simultaneously on that crossing internal
lane** — a state most single-vehicle-pair fixtures never reach.

Fixtures to watch (their tests are the regression guard): `08-junction-straight`, `11-priority-junction`,
`26-right-before-left`, `27-allway-stop`, `29/31-merge-yield`, `32/33-roundabout`, `34-keepclear`,
`38-keepclear-crosstraffic`, `39-crossjunction-leader`, `40-farrouted-foe`, `44-multilane-junction-turn`,
`51/52-emergency-foe`.

**Decision rule if a golden shifts (non-negotiable, per handoff §4):**
1. Run **SUMO 1.20.0** (the pinned version) on the identical net+demand.
2. Diff our trajectory against SUMO's at the divergence step.
3. Only if we moved **toward** SUMO → regenerate via `scripts/regen-goldens.sh` and commit, with the diff
   recorded in the task tracker. Otherwise the fix is wrong → rework.

**Toolchain: SUMO 1.20.0 IS available — use it.** `apt install sumo` gives **1.18.0**, which is NOT a valid
parity anchor against the **1.20.0** pin ("source and goldens MUST come from this exact version"). The pinned
build comes from pip and is present at:

```
/usr/local/lib/python3.11/dist-packages/sumo/bin/sumo        # Eclipse SUMO Version 1.20.0
/usr/local/lib/python3.11/dist-packages/sumo/bin/netconvert  # (duarouter, netgenerate, ... alongside)
```

installed via `pip install eclipse-sumo==1.20.0`. Put that `bin/` ahead of `/usr/bin` on `PATH` (or point
`SUMO_HOME` at the package dir) so `sumo`/`netconvert` resolve to 1.20.0 and not apt's 1.18.0 — the bare
`sumo` on `PATH` is the WRONG version. Golden diffing and `scripts/regen-goldens.sh` are therefore unblocked.

## 6. Success conditions (what "done" means for F3)

1. The 8 `BOTH-INTERNAL-DIFFERENT-LANE` events → **0**, measured **centre-corrected** (§1b).
2. Parity **661/4** with **no** golden change; or every change SUMO-diff-justified and regenerated (§5).
3. Bench hash **`D96213B7BB4021A7`**, par==single.
4. `Sim.LiveCity.Tests` green (run **without** `--no-build` — not in `Traffic.sln`).
5. **No new deadlock:** `NEED-multilane-junction-passage` and `NEED-priorityjunction-farrouted-foe-falsepositive`
   do not regress; demo throughput (`DenseFlow_…NoGridlock`) stays green. Guard §3b explicitly.

## 6a. EVIDENCE-BASED FIX PLAN (two hypotheses tested, both refuted; this is what the data says)

Two fix attempts were implemented and measured. **Both made things worse.** Recording them so nobody
re-runs the experiment:

| attempt | F3 bucket (front-anchor) | worst | verdict |
| --- | --- | --- | --- |
| baseline (no change) | 8 | 3.035 m | — |
| H1: `FoeWith` widening + narrow `inTheWay` | **33** | 3.385 m | WORSE |
| H2: H1 + `isLeader`'s first clause (`!egoOnInternal`) | **27** | 3.385 m | still WORSE |

**H3 ("cars stopped ON internal lanes are the dominant cause, so port `checkRewindLinkLanes`") was tested
BEFORE writing any code — and REFUTED.** Attribution of the 13 centre-corrected F3-bucket events:

| class | events | worst |
| --- | --- | --- |
| `STOPPED-FOE` (≥1 car below 0.5 m/s) | 5 | 1.987 m |
| **`BOTH-MOVING`** | **8 (62%)** | 1.696 m |

Stopping inside a junction is real but *not* dominant for F3: 14 distinct (vehicle, internal-lane) pairs,
206 vehicle-steps, **2.2%** of all stopped vehicle-steps. So `keepClear`/rewind work is NOT the lever for
the F3 bucket.

### What the evidence actually points at, in value order

**(1) HIGHEST VALUE — a stuck-vehicle bug: cars parked inside a junction for ~100 steps with NOTHING ahead.**

| vehicle | internal lane | consecutive stopped steps | min speed | `GapAhead` | `NextMouthGap` |
| --- | --- | --- | --- | --- | --- |
| `__veh127` | `:d_3_4_5_0` | **95** (steps 98–192) | 0.000 | **+Inf** | **+Inf** |
| `__veh140` | `:d_5_4_12_0` | **75** (steps 113–187) | 0.000 | **+Inf** | **+Inf** |

`GapAhead = +Inf` and `NextMouthGap = +Inf` for **every step of both runs**: no leader, no blocked exit
mouth. These cars are stopped in the middle of a junction with **no obstacle in front of them at all**, for
nearly half the run. That is not keepClear, not car-following, and not an admission-gate question — some
constraint is pinning them at ~0 speed indefinitely. Payoff if fixed:
- the **5 deepest F3-bucket events**, including the bucket's worst (**1.987 m**, `__veh5` at 10.40 m/s vs
  `__veh127` at 0.00), and
- **60 of the 62** `ONE-INTERNAL-ONE-NORMAL` events (that bucket is 60/62 `STOPPED-FOE`) — the single
  largest overlap bucket in the demo.

Method: instrument `BindingConstraint` / `JunctionYieldArm` (both already recorded per-vehicle for diag #15)
for `__veh127` across steps 98–192 and read off which constraint is returning ~0. It is one lookup, not a
port. **Do this first.**

**(2) THEN the genuine simultaneous-admission residue.** The 8 `BOTH-MOVING` events are the true "both cars
admitted into crossing paths at once" case — the actual F3 as conceived. Note the magnitudes are **small**:
worst 1.696 m, and 5 of the 8 are 0.497–0.602 m. Three of those pairs also show *identical* speeds
(2.600/2.600, 2.600/2.600, 3.900/3.900), which smells related to N2 (co-located vehicles) and should be
checked before assuming an admission-gate cause. Only this residue needs the full occupancy port —
`FoeWith` + `inTheWay` + **`isLeader` with real junction entry-time state** (`myJunctionEntryTime` /
`myJunctionConflictEntryTime`, tie-broken by speed then vehicle id, `MSVehicle.cpp:7433-7475`).

**Why (1) must precede (2):** H1/H2 both failed by *braking* cars, which grows the stuck-in-junction
population — the very thing (1) is about. Adding an occupancy brake on top of a stuck-vehicle bug parks a
second car in the junction. Any occupancy gate must be re-measured only after (1) is fixed.

**(3) Independently, fix N1 (the OBB anchor).** Until then no overlap number in the repo means what it says.

## 6b. F4b IS NOT ACHIEVABLE AS SPECIFIED — SUMO does not guarantee zero OBB overlap

This is the most consequential finding of the session, and it invalidates F4b's premise rather than its
implementation. Verified in the vendored SUMO 1.20.0 source:

- **`--collision.check-junctions` defaults to `false`** (`sumo/src/microsim/MSFrame.cpp:391`:
  `oc.doRegister("collision.check-junctions", new Option_Bool(false))`). SUMO performs **no 2-D footprint
  overlap check on junctions at all** in its default configuration.
- SUMO's default safety model is **1-D longitudinal**: `MSLane::detectCollisions` computes
  `gap = victimBack - colliderPos - minGapFactor * minGap` (`MSLane.cpp:1884`) from positions *along a
  lane*. No shapes. Vehicle **width** enters only under the sublane model
  (`if (MSGlobals::gSublane)`, `MSLane.cpp:1917`), and `gSublane` defaults to `false`.
- Even when `collision.check-junctions` **is** enabled, the `getBoundingBox`/`getBoundingPoly` overlap test
  is a **post-hoc detector** that fires `collision.action` (default `teleport`) *after* the overlap has
  occurred. It does not constrain the motion model to prevent overlap.
- **Internal lanes overlap by construction.** `MSLink::setRequestInformation` intersects internal-lane
  shapes geometrically (`intersectsAtLengths2D`, `MSLink.cpp:334-345`) and derives `conflictSize` from the
  **foe lane's width** (`MSLink.cpp:354-366`); `DIVERGENCE_MIN_WIDTH 2.5` (`MSLink.cpp:68-69`) treats
  sibling internal lanes closer than 2.5 m as overlapping *by default*. Crossing paths sharing road space
  is what a junction *is*.
- SUMO's own docs say so (`sumo/docs/web/docs/Simulation/Safety.md:100-103`): collisions *on* an
  intersection "are only registered when setting the option `--collision.check-junctions`".

**Conclusion:** "zero car–car OBB overlap" is **strictly stronger than SUMO parity**. A car turning through
a tight junction legitimately sweeps its footprint over stopped cars' boxes in genuine SUMO output too.
Asserting zero would mean asserting something SUMO neither provides nor attempts — in direct tension with
`CLAUDE.md`'s prime directive to follow SUMO on anything behavioral.

The SUMO-faithful invariant is **1-D**: `gap >= 0` along the lane axis per `collision.mingap-factor`. That
is worth asserting and is a real bug-catcher. A 2-D OBB check is legitimate only as a *diagnostic tripwire*
with an empirically-calibrated ceiling (what `DemoCarOverlapInvariantTests` already is), or as explicit
`--collision.check-junctions` **detection** parity — never as a zero-overlap guarantee.

**Recommendation:** replace F4b's "flip to assert ZERO" with (a) a 1-D `gap >= 0` invariant, plus (b) keeping
the 2-D check as a calibrated tripwire with the F3 bucket tracked separately. §7's N1–N3 must still be fixed
for the tripwire's numbers to mean anything.

## 7. Out of scope for F3 — the other three overlap causes

Each is real, distinct, and blocks F4b. Filed separately rather than silently folded in:

- **N1 — OBB anchor bug (measurement).** §1b. Fix `ObbOverlap` call sites to back-shift by `Length/2`.
  Cheap, and it re-baselines every overlap number in the repo — including the handoff's headline 3.035 m.
- **N2 — co-located vehicles (engine, non-junction).** Cars at *identical* `pos` on the *same* lane at
  identical speed (`__veh56`/`__veh84` at `pos=27.83, spd=16.67`; `__veh83`/`__veh121` at `pos=52.97`).
  Two vehicles perfectly superposed is a genuine engine defect with nothing to do with junctions.
- **N3 — overlapping *normal*-lane geometry (net authoring).** `e_d_6_5_d_5_5_2` × `e_d_garage_stub_d_5_5_1`
  physically overlap in world space. Both cars obey their own lane correctly; this is a demo-net authoring
  defect, fixable only in the net, not the engine.

**F4b (flip the invariant to assert ZERO) is therefore blocked on N1+N2+N3, not on F3.** After F3 lands, the
honest move is to re-characterise the invariant against the centre-corrected baseline with the residual
non-F3 buckets named — not to assert a zero the engine cannot yet deliver.
