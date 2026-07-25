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
   lateral position, so on this fixture it first binds at t=3 with only a 1.3 m gap at 5 m/s (a
   3.7 m/s² emergency stop), not a driver-like early yield.

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

- it is **not a guarantee** — it is a behaviour that depends on the reactive current-overlap release, so
  geometries where the ped enters ego's neighbourhood outside the threat window (alongside, at the lane
  edge, a ped promoted to ORCA late) can still produce a close pass;
- the stop is a **late 3.7 m/s² emergency brake**, not a driver-like early yield.

The design below therefore has a *behaviour* layer and a *guarantee* layer.

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

### 3.2 L2 — `CrowdYieldConstraint` (new, binder 14): the *guarantee*

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
the stop is a smooth Krauss deceleration to `MinGap` behind the conflict point instead of a late emergency
brake.

Why this is **stable** (no brake/release oscillation, the `GateOrcaPedsOnCrossing` lesson): the only ego
feedback is through `tte`, and it is *monotone in the safe direction* — a slower ego gets a LONGER
look-ahead, so slowing down never makes ego decide the ped has cleared. The `CrowdYieldRefSpeed` floor
(2.0 m/s) keeps a stopped ego looking a bounded distance ahead rather than infinitely. Ped **velocity is
preserved** throughout (`predLat` uses `latVel`, the virtual leader uses the ped's longitudinal speed), so
ego yields to where the ped WILL be and releases the instant its corridor track is clear — this is the
explicit fix for the "velocity-0 over-brake" that cost 15% throughput.

**(b) World-space proximity cap — the hard "never close AND fast" backstop.** For each disc that is not
fully behind ego's rear bumper, the exact rectangle-to-disc clearance is computed **in world space** (ego's
body frame from `LaneGeometry.PositionAtOffset`'s naviDegree heading — NOT a lane-membership test, per the
owner's framing), and ego's speed is capped:

```
cap = CrowdYieldCreepSpeed + max(0, clearance - CrowdYieldNearDistance) * CrowdYieldProximityGain
    = 1.5 m/s + max(0, clearance - 1.5 m) * 3.0 /s
```

i.e. **at 1.5 m from a pedestrian, never faster than 1.5 m/s**; the cap stops binding beyond ~2.7 m for a
5 m/s car, so it only ever bites in the close regime. Discs fully behind ego's rear are dropped so ego is
not trailed/slowed by peds it has already passed. This term is what makes success condition 1/2 a
*guarantee* rather than an emergent behaviour: whatever the swerve does, a close pass is a slow pass.

`FinalizeSpeed`'s `vMin` clamp (`KraussModel.cs:406-408`) bounds the resulting deceleration at
`emergencyDecel`, so a raw cap can never teleport speed to 0.

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
   the crowd threat scan, and the new binder 14) short-circuits to `+Infinity` / `continue`.
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
| `SwervePredictionHorizon` | 4.0 s (existing) | reused as the anticipation cap |

## 6. What this session does NOT do

- **B-api** (retiring the string `ExternalObstacle` onto `WorldDisc`) — left to the ped–vehicle session
  (handoff §8 Q4), it is an API refactor with its own parity surface.
- **C5** (ped-avoids-car disc feed) — the ped side; owned by the ped–vehicle session (handoff §5).
- Junction methods (`JunctionYieldConstraint`, `AdaptToJunctionLeader`, `KeepClearConstraint`) — owned by
  the F3 session. This change touches `ComputeLateralEvasion`, a new sibling of
  `CrowdLongitudinalConstraint`, and the fold line in `ComputeMoveIntent`.
