# DESIGN — cars yield to pedestrians in their path (crosswalk safety, Task B-guard)

**HOW it will work.** The WHAT is `docs/LIVE-CITY-CAR-YIELDS-PED-HANDOFF.md` (session brief) and
`docs/LIVE-CITY-REALISM-AB-DESIGN.md` §Task B (owner requirement: *"in the high-realism zone a car must
NEVER crash into a ped, nor pass one at close distance / high speed"*). This document is the mechanism,
the data flow, and the parity argument. Task list: `LIVE-CITY-CAR-YIELDS-PED-TASKS.md`. Tracker:
`LIVE-CITY-CAR-YIELDS-PED-TRACKER.md`.

---

## 1. The measured repro (first-hand, this session)

`tests/Sim.ParityTests/CrosswalkCrossingPedTests.cs`'s fixture (`scenarios/_fixtures/bridge-crossing-normal`:
one 7.2 m lane, centreline y = -3.6, +x; car `v0` departs x=0 at maxSpeed 5; one `OrcaCrowd` ped disc,
r = 0.6, crossing x=22 from y=+2 to y=-12 at 1.3 m/s). Per-tick AUTHORITATIVE trace, with the body-to-disc
clearance computed in world space (the car body is the rectangle `x ∈ [Pos-Length, Pos]`, `y ∈ [Y±Width/2]`
— note `Pos`/`VehicleState.X` is the FRONT bumper, SUMO convention):

```
 t  pos    spd   posLat  binder   car(x,y)         ped(x,y)        clearance
 0   2.60  2.60   0.00     3     ( 2.60,-3.60)   (22.00,  0.70)     19.10
 1   7.60  5.00   1.10     3     ( 7.60,-2.50)   (22.00, -0.60)     13.83
 2  12.60  5.00   1.41     3     (12.60,-2.19)   (22.00, -1.90)      8.80
 3  17.60  5.00   1.41     3     (17.60,-2.19)   (22.00, -3.20)      3.80
 4  18.90  1.30   1.41    13     (18.90,-2.19)   (22.00, -4.50)      2.81
 5  22.80  3.90   0.00     3     (22.80,-3.60)   (22.00, -5.80)      0.70   <-- CLOSE-FAST-PASS
 6  27.80  5.00   0.00     3     (27.80,-3.60)   (22.00, -7.10)      2.12
```

**The defect, in one line: minimum clearance 0.70 m at 3.90 m/s** — the car weaves around the crossing
pedestrian at full speed (posLat 0 → 1.41 while Speed = 5), takes a single tick of crowd brake (binder 13
at t=4), then snaps back to the centreline (2.0 m/s lateral cap ⇒ a full recentre in one 1 s step) and
accelerates *past* the ped while the ped is still inside the lane (lane spans y ∈ [-7.2, 0]; the ped is at
y = -5.8). This is the behaviour the owner's rule forbids.

## 2. Root cause (verified against source)

Two independent mechanisms combine:

1. **The crowd swerve is preferred over stopping.** `Engine.ComputeLateralEvasion` (`Engine.cs:9079`)
   deliberately skips the "still ahead + can brake in time ⇒ stay centred" gate for a crowd threat
   (`Engine.cs:9250-9256`, `!threatIsCrowd && …`). Q6 option (b) chose *dodge* over *stop*.
2. **The longitudinal crowd brake releases the moment the swerve succeeds.**
   `CrowdLongitudinalConstraint` (`Engine.cs:8572`, binder 13) only treats a ped as a virtual leader
   **while ego's CURRENT lateral footprint overlaps it** (`Engine.cs:8602`:
   `|latOff - v.LatOffset| >= egoHalf + disc.Radius ⇒ skip`). Ego swerving off-centre ends the overlap, the
   brake releases, ego re-accelerates. It is also *reactive*, not anticipatory — it uses the ped's CURRENT
   lateral position, so on this fixture it first binds at t=3 with only a 1.3 m gap at 5 m/s (an abrupt
   3.7 m/s² stop), and -- worse -- at coarse steps it can miss the overlap sample entirely (§3.2a).

Task A's `SuppressHeldCrowdSwerve` (`Engine.cs:9273`) already suppresses (1) — but only for a ped that is
**held + laterally static** (`BindingConstraint == 13 && |LatSpeed| < 1e-9`). A ped that keeps walking never
trips it, which is exactly why the committed repro test documents the fix as *inert* for this case.

### 2.1 Control experiment (run first-hand, then reverted)

Widening Task A's gate to `SuppressHeldCrowdSwerve && threatIsCrowd` (drop the held/static conditions) and
re-running the same trace:

```
 3  17.60  5.00   0.00     3   clearance 3.80
 4  18.90  1.30   0.00    13   clearance 2.50   (stops at exactly MinGap behind the ped's near edge)
 5  18.90  0.00   0.00    13   clearance 2.76   HELD
 6  21.50  2.60   0.00     3   clearance 2.05   resumes as the ped leaves the lane (y=-7.1, edge -7.2)
 7  26.50  5.00   0.00     3   clearance 3.30
```

Minimum clearance **2.05 m** (vs 0.70), maximum speed inside 3 m **2.6 m/s** (vs 3.90), and the car resumes
within one step of the ped clearing — **no stall**. So suppressing the swerve is *sufficient* to produce a
real yield on this fixture. Two things it does **not** give us:

- it is **not a guarantee** — it is a behaviour that rides on binder 13's *reactive, current-overlap*
  release. Geometries where the ped never lands in that sample (alongside, at the lane edge, a car that
  covers 5 m in one step) still produce a close pass; §3.2a pins three of them where binder 13 fires
  **zero** times and the car holds 5.00 m/s straight through the crossing;
- it does nothing at all for a car that is off-centre for some other reason (mid-lane-change, give-way).

The design below therefore has a *behaviour* layer (L1, this suppression) and a *guarantee* layer (L2).

---

## 3. Mechanism

Three pieces, all inside the existing `CrowdSource`-gated crowd path, all additionally gated on a new
**world-space yield zone** whose radius defaults to 0 = off.

### 3.0 The zone gate (`Engine.SetCrowdYieldZone`)

```csharp
// Realism knob. Radius <= 0 (default) => the whole Task-B guard is off => byte-identical.
public void SetCrowdYieldZone(double centreX, double centreY, double radius);
private bool InCrowdYieldZone(double wx, double wy);
```

The engine tests ego's OWN world position against the zone, so no per-step host plumbing is needed (unlike
`SetLowRealismLaneChange`, which the host must push per vehicle per step). This matches the owner's
"high-realism zone" framing and the zone the viewer already highlights. `LiveCitySim` pushes it in the ctor
and from `SetLcRealismZone`, so the yield zone == the camera-driven LC realism zone the user can see.

Deliberately a **pure function of frozen state** (ego's own position + a scalar zone), so it is
order-independent and parallel-safe like every other constraint.

### 3.1 L1 — swerve suppression in-zone (the *behaviour*: yield, don't weave)

In `ComputeLateralEvasion`, immediately after Task A's held-static gate:

```csharp
if (threatIsCrowd && InCrowdYieldZone(egoWX, egoWY))
{
    return DriftToward(curLat, 0.0, maxStep);   // stay/return centred; the brake below holds us
}
```

Rationale: the threat set is already exactly *"a lane-CENTRED ego would hit this ped's predicted lateral
position"* (`Engine.cs:9218-9222`) — i.e. "a ped in my path". Refusing to dodge it keeps ego laterally
overlapping, which keeps `CrowdLongitudinalConstraint` (and L2's yield term) engaged, so ego brakes and
holds behind the ped and recentres. This is the generalisation of `SuppressHeldCrowdSwerve` from "held +
static ped" to "any ped in my path, in-zone", exactly as the handoff §2 predicted.

`LateralManoeuvre` stays **false** here (recentring is not a manoeuvre) — same convention as Task A.

**Not a gridlock risk by construction:** a ped outside the ±(egoHalf + pedRadius) centred footprint is not a
threat at all, so the car is unaffected by kerb/sidewalk peds. A ped walking ALONG the lane is treated by
`CrowdLongitudinalConstraint` as a *moving* leader (its own longitudinal speed, not a dead stop), so ego
trails it rather than deadlocking. Whether that costs throughput is **measured**, not assumed — see
`DenseFlow…NoGridlock` and `carArrivedTotal` in the task list.

### 3.2 L2 — `CrowdYieldConstraint` (new, binder 16): the *guarantee*

A new constraint folded into `ComputeMoveIntent`'s `Math.Min` chain right after binder 13 — strictly more
conservative, never faster, so it cannot break any existing bound. `+Infinity` (inert) unless
`CrowdSource != null` **and** the zone radius > 0 **and** ego is inside the zone. It carries two terms, both
computed from the same single `QueryNear` sweep:

**(a) Anticipatory in-path yield.** For each disc ahead (`offset - r >= ego.Pos`):

```
longDist = max(offset - r - ego.Pos, 0)
tte      = min(longDist / max(ego.Speed, CrowdYieldRefSpeed), SwervePredictionHorizon)   // 2.0 m/s, 4 s
predLat  = latOff + latVel * tte
H        = egoHalf + r + CrowdYieldLateralMargin                                          // 0.30 m
inPath   = |latOff| < H  ||  |predLat| < H  ||  sign(latOff) != sign(predLat)
```

`inPath` is "the ped's lateral track over [now, arrival] intersects ego's safety corridor" — the third
clause catches a ped that traverses the whole corridor within `tte`. The nearest such ped becomes a virtual
leader at `back = offset - r` through the SAME `FollowSpeedFor` call `CrowdLongitudinalConstraint` uses, so
it decelerates to `MinGap` behind the conflict point exactly as it would for a stopped car.

**What this buys over binder 13 (measured, not assumed).** It does NOT brake *earlier* on a given
geometry — it shares Krauss's safe-speed curve, so on the §1 fixture (1 s steps) both first bind at the
same tick. What it adds is *coverage*: binder 13 tests the ped's CURRENT lateral position against ego's
CURRENT footprint, so a 5 m/s car at a 1 s step can walk clean over a crossing ped without the overlap ever
being sampled. `CrowdYieldZoneTests.CrossingPedBinder13Misses_ZoneOffDrivesThroughAtSpeed_ZoneOnYields`
pins three such geometries: with the zone off the car holds **5.00 m/s for the entire crossing and binder 13
never fires once**; with it on, binder 16 binds and the car yields. It is also *sticky* — the corridor is
centred on the LANE, not on ego's current offset, so an off-centre ego (mid-lane-change, a give-way shift)
does not release it the way binder 13 does.

Why this is **stable** (no brake/release oscillation, the `GateOrcaPedsOnCrossing` lesson): the only ego
feedback is through `tte`, and it is *monotone in the safe direction* — a slower ego gets a LONGER
look-ahead, so slowing down never makes ego decide the ped has cleared. The `CrowdYieldRefSpeed` floor
(2.0 m/s) keeps a stopped ego looking a bounded distance ahead rather than infinitely. Ped **velocity is
preserved** throughout (`predLat` uses `latVel`, the virtual leader uses the ped's longitudinal speed), so
ego yields to where the ped WILL be and releases the instant its corridor track is clear — this is the
explicit fix for the "velocity-0 over-brake" that cost 15% throughput.

**(b) World-space proximity cap — the hard "never close AND fast" backstop.** For each disc that is not
fully behind ego's rear bumper, the exact rectangle-to-disc clearance is computed **in world space**
(`VehicleFootprint.ClearanceToDisc`, ego's body frame from `LaneGeometry.PositionAtOffset`'s naviDegree
heading — NOT a lane-membership test, per the owner's framing), and ego's speed is capped by
`Engine.ProximitySpeedCap`:

```
cap(c) =  0                                    for c <= 0        (contact: full stop)
          creep * c / near                     for 0 < c < near  (ramp)
          creep + (c - near) * gain            for c >= near     (relax)
        with near = 1.5 m, creep = 1.5 m/s, gain = 3.0 /s
```

i.e. **at 1.5 m from a pedestrian, never faster than 1.5 m/s; touching one, stopped**; the cap stops
binding beyond ~2.7 m for a 5 m/s car, so it only bites in the close regime. Discs fully behind ego's rear
are dropped so ego is not trailed by peds it has already passed. `FinalizeSpeed`'s `vMin` clamp
(`KraussModel.cs:406-408`) bounds the resulting deceleration at `emergencyDecel`, so a raw cap can never
teleport speed to 0.

**Evaluated on the PREDICTED clearance, not just the current one.** Because braking is bounded, a cap keyed
only on the current clearance is always met one step late. The demo run measured exactly that: with the
current-clearance-only form, one car spent a single 0.5 s sample at 2.70 m/s and 1.36 m while braking at its
emergency limit toward the creep speed — the cap was being obeyed as fast as physics allowed, and still
produced a nominal violation. So `c` above is the **worse of** the current clearance and the clearance ego
will have `CrowdYieldCapHorizon` (1.0 s) from now, with ego carried forward along its heading and the agent
along its own velocity (all in the body frame, `VehicleFootprint.ClearanceFromBodyFrame` +
`VectorToBodyFrame`). Ego's advance uses `max(speed, CrowdYieldRefSpeed)`, so it is monotone INCREASING in
speed: going faster looks further and caps harder. That is the stable direction — braking can never
un-trigger the cap and re-accelerate ego into a pedestrian.

With the prediction in, the demo's residual went to **0** (see §7).

### 3.3 Data flow / ordering

Nothing new: `CrowdYieldConstraint` reads only the frozen snapshot (`v`'s own kinematics + `CrowdSource`'s
read-only disc query) and returns a scalar into the existing plan-phase `Math.Min` fold, exactly like
binders 12/13. No structural mutation, no cross-vehicle read, no RNG, no wall-clock ⇒ order-independent and
parallel-safe. It runs on the willPass pre-pass too, the same as binder 13 (a pre-pass that ignored the cap
would predict a pass ego will not actually make).

---

## 4. Parity / determinism argument

Three independent gates, each alone sufficient:

1. `CrowdSource == null` for every committed golden and for `Sim.Bench` — the whole crowd path (binder 13,
   the crowd threat scan, and the new binder 16) short-circuits to `+Infinity` / `continue`.
2. `CrowdYieldZoneRadius <= 0` by default — nothing on a parity path calls `SetCrowdYieldZone`.
3. L1 lives inside the `threatIsCrowd` branch, itself inside the `CrowdSource`-gated scan.

⇒ `Sim.ParityTests` **664/4** byte-identical and `Sim.Bench` hash **`D96213B7BB4021A7`** (par == single)
unchanged. Determinism: no `System.Random`, no wall-clock, all arithmetic on frozen per-ego state; the disc
sweep takes the nearest by `back` with the same strict-`<` tie-break the existing crowd scan uses.

## 5. Constants (one place, `Engine.cs` realism-knob block)

| name | value | why |
|---|---|---|
| `CrowdYieldLateralMargin` | 0.30 m | corridor half-width = egoHalf + pedR + this (1.80 m for a 1.8 m car / 0.6 m ped) — 0.3 m wider than the existing swerve threat test, not wide enough to catch kerb peds |
| `CrowdYieldRefSpeed` | 2.0 m/s | look-ahead floor so a stopped ego still anticipates; bounds the hold |
| `CrowdYieldNearDistance` | 1.5 m | "close" — inside this, creep only |
| `CrowdYieldCreepSpeed` | 1.5 m/s | the speed a car may pass a ped at, at touching distance |
| `CrowdYieldProximityGain` | 3.0 /s | cap slope; unbinds at ~2.7 m for a 5 m/s car |
| `CrowdYieldCapHorizon` | 1.0 s | how far the proximity cap looks ahead, so the cap is reachable under braking |
| `SwervePredictionHorizon` | 4.0 s (existing) | reused as the anticipation cap |

## 6. Measured results

**Isolated crosswalk repro** (`CrosswalkCrossingPedTests`, the §1 fixture):

| | zone OFF (= every golden) | zone ON |
|---|---|---|
| worst clearance while moving > 2 m/s | **0.70 m** | **2.00 m** |
| speed at that moment | **3.90 m/s** | 3.67 m/s |
| max abs posLat (the weave) | 1.41 m | **0.00 m** |
| holds while the ped is in the lane | no | yes (Speed 0.00 at t=5) |
| back at maxSpeed after the ped clears | — | 1 tick |

**Demo scale** (`DemoPedYieldInvariantTests`, real `LiveCitySim`, the demo's OWN crowd density -- 800 peds,
600 steps at Dt=0.5; close-fast-pass = clearance < 1.5 m while > 2.0 m/s):

| | baseline (`LIVECITY_PEDYIELD=0`) | fixed |
|---|---|---|
| in-zone close-fast-passes | **200** | **70** (-65%) |
| of which HEAD-ON (ped ahead, in ego's corridor) | 10 | 7 |
| net-wide close-fast-passes | 3968 | 3739 |
| `ArrivedTotal` (throughput) | 173 | **175** |

**An earlier version of this measurement was underpowered and its headline was wrong.** At 160 peds / 300
steps the baseline produced 7 in-zone events and the fixed arm 0, which read as "the guard eliminates
close-fast-passes". It does not -- that sample just did not contain the hard cases. The test now runs at
the demo's real density and asserts the reduction that is actually there (a >= 40% cut), not zero.

Two structural reasons the remaining events are NOT reachable by tuning this guard:

1. **Out-of-zone cars cannot see pedestrians at all.** The car-side feed is
   `Composite(PedLodManager.HighPowerFootprints, CrossingOccupancySource)`; peds promote to HighPower via
   the InterestSource, which IS the LC-realism zone. The measured cross-tab is unambiguous -- every
   `HighPower` event is in-zone, every `LowPowerWalking`/`Paused` event is out-of-zone. Arming the yield
   NET-WIDE was measured too (a third probe arm) and barely helped: 3739 -> 3458, because the cars still
   have no pedestrian data. That is a ped-LOD feed question, not a car-yield question. It also means the
   net-wide column above is largely NOT a defect: ~85% of those events are `offside` (the ped is beside the
   road, not in the car's path), which on a city net with kerbside footways is ordinary traffic.
2. **`ICrowdFootprintSource.QueryNear` truncated arbitrarily. FIXED -- see §8.**

**Gates:** `Sim.ParityTests` 680 passed / 4 skipped (= the 664/4 baseline plus exactly the 16 tests added
here, no pre-existing test perturbed); `Sim.Bench` hash `D96213B7BB4021A7`, par == single; all 48
`Sim.LiveCity.Tests` green.

### 6.1 Known residual, and whose it is

Even with the guard on, a pedestrian can still end up geometrically overlapping a car — because
**pedestrians do not yet avoid cars**. That is C5 (the ped-avoids-car disc feed), explicitly NOT started and
owned by the ped–vehicle avoidance session (§7). What the car side now guarantees is that it is never the
one doing the fast approaching: it stops at contact and creeps below 1.5 m.

## 7. What this session does NOT do

- **B-api** (retiring the string `ExternalObstacle` onto `WorldDisc`) — left to the ped–vehicle session
  (handoff §8 Q4), it is an API refactor with its own parity surface.
- **C5** (ped-avoids-car disc feed) — the ped side; owned by the ped–vehicle session (handoff §5).
- Junction methods (`JunctionYieldConstraint`, `AdaptToJunctionLeader`, `KeepClearConstraint`) — owned by
  the F3 session. This change touches `ComputeLateralEvasion`, a new sibling of
  `CrowdLongitudinalConstraint`, and the fold line in `ComputeMoveIntent`.


---

## 8. Follow-up: `QueryNear` returned an arbitrary subset, not the nearest

Found while explaining §6's residual HEAD-ON events (a car at 16.5 m/s with a pedestrian inside its own
corridor -- a ped the guard was, on paper, watching for).

**The defect.** `QueryNear` is the only window a vehicle has onto the pedestrian crowd, and every consumer
passes a small fixed span (`stackalloc WorldDisc[16]` in `CrowdYieldConstraint`,
`CrowdLongitudinalConstraint`, and `ComputeLateralEvasion`'s crowd scan). All three implementations filled
that span in **enumeration order and stopped when it was full**:

- `OrcaCrowd.QueryNear` walked agent SLOTS, so an agent in a high slot index was invisible however close it
  was. At 800 peds a car has far more than 16 inside its ~66 m query radius, so which sixteen it saw was
  decided by slot index -- and the pedestrian in front of the bumper routinely was not among them.
- `CompositeFootprintSource.QueryNear` CONCATENATED its children, so once the first child (the promoted
  ORCA crowd) saturated the span the second (`CrossingOccupancySource`) got **zero** slots -- starved
  precisely in the dense-crowd case it exists for.
- `CrossingOccupancySource.QueryNear` had the same break-when-full loop.

`tests/Sim.ParityTests/CrowdQueryNearTests.cs` was written as failing repros of exactly these (a ped 2 m
ahead in a high slot; a 1.5 m mover in a starved second child) before any fix.

**The fix.** The interface contract is tightened to *"when more movers are in range than fit, the NEAREST
win, ordered nearest-first, ties broken by enumeration order"*, and all three implementations route through
one shared accumulator, `Sim.Core.Bridge.WorldDiscQuery.InsertNearest` -- a bounded insertion that
recomputes distances from the discs already held, so it is zero-alloc and needs no parallel distance array.
Ties keep the incumbent, which is what makes the result stable and reproducible run-to-run. The composite
now merges its children through the same accumulator instead of concatenating (single-child wiring still
hands the caller's span straight to the child, so it is unchanged).

The cost is that `OrcaCrowd.QueryNear` can no longer exit its scan early -- a late slot holding a close
agent must be able to displace an early slot holding a distant one. Measured on the 800-ped demo A/B, wall
time moved from ~28 s to ~31 s per 600-step arm (~10%). `OrcaCrowd` already has an opt-in uniform spatial
hash (`UseSpatialHash`) used by the ORCA neighbour gather; wiring `QueryNear` onto it would remove the scan
cost and is the obvious next step if that 10% ever matters.

**Effect on the demo (800 peds, 600 steps, same harness as §6):**

| | before the QueryNear fix | after |
|---|---|---|
| in-zone close-fast-passes (baseline -> guarded) | 200 -> 70 | 207 -> **27** |
| of which HEAD-ON (ped ahead, in ego's corridor) | 10 -> 7 | 8 -> **0** |
| `ArrivedTotal` (baseline -> guarded) | 173 -> 175 | 175 -> 174 |

**Zero cars now drive at a pedestrian inside the zone.** The remaining 27 in-zone events are all ABEAM
(pedestrian beside a passing car, not in its path) -- dominated by peds walking into cars, which is C5's
territory (§7).

**Parity:** `QueryNear` has no caller on any golden or bench path (the whole crowd seam is
`CrowdSource`-gated), so `Sim.ParityTests` stays byte-identical -- 684 passed / 4 skipped, = the 664/4
baseline plus the 20 tests this branch added -- and `Sim.Bench` keeps hash `D96213B7BB4021A7`, par ==
single. All 272 `Sim.Pedestrians.Tests` (the heaviest `OrcaCrowd` users) stay green.

### 8.1 A test-isolation bug this surfaced

Raising the demo A/B to 800 peds / 600 steps made `LiveCitySimTests.TwoRuns_SameConfig_AreByteExactDeterministic`
flaky. It was NOT engine non-determinism: the yield A/B flipped `LIVECITY_PEDYIELD` with
`Environment.SetEnvironmentVariable`, which is process-global, and xunit runs test classes in parallel --
so the determinism test could build its two sims either side of the flip and legitimately diverge. The
toggle is now a real config knob (`LiveCityConfig.PedYieldEnabled`, still defaulted from `LIVECITY_PEDYIELD`
in `ForRepoRoot`, matching how every other demo knob in that file is wired) and the tests set the config,
never the environment. Full suite green twice in a row afterwards.


### 8.2 Two fixes for one symptom? No -- one contract and one knob

While this branch was in flight, main landed an independent fix for the same symptom: `MaxCrowdDiscs`
16 -> 256 (f9c837c), on the measurement that a car at 10x ped density had a median of 39 and a maximum of
131 crowd discs in range, so the 16-slot buffer truncated the in-path one and cars drove through
pedestrians. That is the same defect §8 describes, attacked from the other side: make truncation RARE
rather than make it CORRECT.

Keeping both looked like duplication, so it was measured -- the demo A/B (800 peds, 600 steps) re-run at
four buffer sizes with the nearest-first contract active:

| `MaxCrowdDiscs` | 16 | 32 | 64 | 256 |
|---|---|---|---|---|
| in-zone close-fast-passes, baseline arm | 240 | 235 | 203 | 203 |
| in-zone close-fast-passes, guarded arm | 22 | 21 | **14** | **14** |
| of which HEAD-ON (car driving AT a ped) | **0** | **0** | **0** | **0** |
| wall time per 600-step arm | 28/27 s | 28/27 s | 27/27 s | 28/27 s |

They are not two fixes for one problem. They are **one correctness contract and one quality knob**:

- **The nearest-first contract is what makes it safe.** HEAD-ON is zero at *every* buffer size, including
  the original 16. No car is blind to the pedestrian in front of it regardless of how small the buffer is,
  because the buffer now spends its slots on the closest movers -- which is exactly what all three
  consumers (nearest in-path leader, nearest threat, min-over-clearance proximity cap) actually read.
- **The buffer size is now a fidelity/cost dial with graceful degradation**, not a cliff. It still buys
  something real (total in-zone count 22 -> 14 from 16 to 64 slots, as the proximity cap and lateral scan
  see more neighbours) and saturates at 64 for this density. Before the contract it was the ONLY thing
  standing between the demo and cars driving through people, and it could fail silently at any density
  above its size -- 131-of-256 at 10x was already uncomfortably close.

An earlier hypothesis -- that a 256-slot buffer would be measurably SLOWER than a small one under
`InsertNearest`, since the O(1)-reject path only engages once the buffer is full -- was **wrong**: wall
time is flat across 16..256. So there is no perf argument for shrinking it.

Decision: **keep 256, keep the contract, and document the relationship** (see the constant's own comment).
256 costs nothing measurable and preserves the headroom f9c837c measured at 10x density; 64 would be
measurably identical at 800 peds if the ~10 KB of stack per call site ever matters.
