# TASKS — cars yield to pedestrians in their path (Task B-guard)

Work breakdown for `docs/LIVE-CITY-CAR-YIELDS-PED-DESIGN.md`. Each task names its design reference (a
section, not a copy), the files it touches, its dependencies, and **success conditions the implementor must
fulfil**. Tracker: `LIVE-CITY-CAR-YIELDS-PED-TRACKER.md`.

Baseline numbers every task is measured against (§1 of the design, reproduced first-hand):
**min body-to-ped clearance 0.70 m at 3.90 m/s** on the `bridge-crossing-normal` crossing-ped repro.

---

## Stage 0 — Repro (done before design, per the session brief)

### CY-0 — Authoritative world-space repro trace
*Design ref:* §1. *Files:* none (throwaway trace).
**Success:** a per-tick trace of the crossing-ped fixture showing the close-fast-pass with correct body
geometry (`Pos`/`VehicleState.X` is the FRONT bumper). **DONE — 0.70 m @ 3.90 m/s at t=5.**

### CY-0b — Control experiment
*Design ref:* §2.1. *Files:* none (reverted).
**Success:** measured effect of unconditional crowd-swerve suppression. **DONE — 2.05 m / 2.6 m/s, resumes
in one step, no stall.**

---

## Stage 1 — Engine: the zone gate

### CY-1 — `SetCrowdYieldZone` + `InCrowdYieldZone`
*Design ref:* §3.0. *Files:* `src/Sim.Core/Engine.cs` (realism-knob block). *Depends:* —

Add the three backing fields, the setter, and the private predicate. Radius `<= 0` ⇒ off.

**Success conditions**
1. `dotnet build` clean (no new warnings).
2. A unit test asserts: radius 0 ⇒ `InCrowdYieldZone` false everywhere; radius 10 at (0,0) ⇒ true at
   (0,0) and (6,8) (on the boundary), false at (7,8).
3. `Sim.ParityTests` still 664/4 (nothing calls the setter).

---

## Stage 2 — Engine: L1, the yield behaviour

### CY-2 — Suppress the crowd swerve in-zone
*Design ref:* §3.1. *Files:* `src/Sim.Core/Engine.cs` — `ComputeLateralEvasion` (~`:9273`). *Depends:* CY-1

Hoist ego's world position out of the `CrowdSource is not null` scan so the new gate can read it; add the
gate directly after Task A's held-static gate.

**Success conditions**
1. On the crossing-ped repro **with the zone enabled**, the car's `posLat` stays `0` for every tick
   (`max |posLat| < 1e-9`) — it never weaves.
2. With the zone **disabled** the trajectory is byte-identical to `main` (assert tick-for-tick against a
   zone-off run, precision 12) — proving the gate, not the refactor, is what changed behaviour.
3. `Sim.ParityTests` 664/4 byte-identical.

---

## Stage 3 — Engine: L2, the guarantee

### CY-3 — `CrowdYieldConstraint` (binder 14) — anticipatory in-path yield
*Design ref:* §3.2(a). *Files:* `src/Sim.Core/Engine.cs` — new method beside
`CrowdLongitudinalConstraint` (`:8572`) + the fold line in `ComputeMoveIntent` (`:5182`). *Depends:* CY-1

**Success conditions**
1. Returns `+Infinity` when `CrowdSource == null`, when the zone radius `<= 0`, or when ego is outside the
   zone — asserted by a unit test that flips only the zone and observes an identical trajectory.
2. On the repro with the zone on, the car begins decelerating **at least one tick earlier** than the
   binder-13-only baseline (peak deceleration strictly below the baseline's 3.7 m/s²).
3. The car **stops or creeps** (Speed < 0.5 m/s at some tick) while the ped is inside the lane
   (`-7.2 < pedY < 0`), and reaches full `maxSpeed` again within 4 ticks of the ped leaving the lane —
   yield, then no stall.
4. `Sim.ParityTests` 664/4 byte-identical; `Sim.Bench` hash `D96213B7BB4021A7`, par == single.

### CY-4 — Proximity cap (world-space, the hard backstop)
*Design ref:* §3.2(b). *Files:* same method as CY-3. *Depends:* CY-3

Exact rectangle-to-disc clearance in ego's world body frame (heading from
`LaneGeometry.PositionAtOffset`'s naviDegree), discs fully behind ego's rear bumper dropped.

**Success conditions**
1. A unit test on the clearance helper alone: a disc directly ahead, beside, diagonally off a corner, and
   overlapping, each against a hand-computed expected value (tolerance 1e-9); and a case with the lane
   rotated 90° that returns the same value as the axis-aligned case (proving it is world-space, not
   axis-aligned).
2. On the repro with the zone on: **no tick has clearance < 1.5 m while Speed > 2.0 m/s** (the baseline
   violates this at t=5 with 0.70 m @ 3.90 m/s).
3. Zone off ⇒ byte-identical to `main`.

---

## Stage 4 — Host wiring

### CY-5 — `LiveCitySim` pushes the yield zone
*Design ref:* §3.0. *Files:* `src/Sim.LiveCity/LiveCitySim.cs` (ctor near the
`SuppressHeldCrowdSwerve` opt-in line; `SetLcRealismZone`). *Depends:* CY-2, CY-4

Wire the yield zone to the camera-driven LC realism zone (`_lcZoneX/_lcZoneY/_lcZoneR`). Env opt-out
`LIVECITY_PEDYIELD=0`, mirroring `LIVECITY_HELDSWERVE`.

**Success conditions**
1. `LIVECITY_PEDYIELD=0` reproduces the pre-change demo behaviour exactly (used as the baseline arm of
   CY-6).
2. `SetLcRealismZone` moves the yield zone with the camera (asserted via a test that moves the zone and
   observes the engine's yield turning on/off for a car at a fixed position).
3. `Sim.LiveCity.Tests` green (run WITHOUT `--no-build`; not in `Traffic.sln`).

---

## Stage 5 — Proof

### CY-6 — Demo-scale close-fast-pass invariant test
*Design ref:* §3.2(b), success condition 2 of the session brief. *Files:* new
`tests/Sim.LiveCity.Tests/DemoPedYieldInvariantTests.cs` (modelled on `DemoCarOverlapInvariantTests`).
*Depends:* CY-5

Run the real `LiveCitySim` headless at demo ped density with a pinned config; every frame, for every car
inside the high-realism zone, compute the world clearance to every ORCA ped and record
`clearance < 1.5 m && Speed > 2.0 m/s` events.

**Success conditions**
1. The **baseline arm** (yield zone off) records **> 0** close-fast-pass events — proving the check is live,
   not vacuous.
2. The **fixed arm** (yield zone on) records **0**.
3. Deterministic: two runs of the fixed arm give identical counts.

### CY-7 — Extend `CrosswalkCrossingPedTests`
*Design ref:* §1, §3. *Files:* `tests/Sim.ParityTests/CrosswalkCrossingPedTests.cs`. *Depends:* CY-4

Add the yield assertions to the committed repro, keeping the three existing tests intact (Task A's contract
must not regress).

**Success conditions**
1. New test: zone ON ⇒ no close-fast-pass (clearance ≥ 1.5 m whenever Speed > 2.0 m/s), the car holds
   (Speed < 0.5 m/s) while the ped is in the lane, and it is back at `maxSpeed` within 4 ticks of the ped
   clearing.
2. New test: zone OFF ⇒ the close-fast-pass is still there (0.70 m @ 3.90 m/s, tolerance 0.05) — the
   characterisation of the defect stays committed and the new test is proven non-vacuous.
3. The three pre-existing tests still pass unchanged.

### CY-8 — No-new-gridlock / throughput
*Design ref:* §3.1 (the measured, not assumed, claim). *Files:* none (measurement). *Depends:* CY-5

**Success conditions**
1. `DenseFlow_OverAThousandSeconds_KeepsDischarging_NoGridlock` green.
2. `LiveCitySim.ArrivedTotal` at demo ped density with the yield ON is within **5%** of the OFF arm over an
   identical pinned run; the delta is reported in the tracker either way.

### CY-9 — Parity + bench final gate
*Files:* none. *Depends:* all
**Success conditions:** `dotnet test` 664 passed / 4 skipped; `Sim.Bench` hash `D96213B7BB4021A7` with
par == single; `Sim.LiveCity.Tests` green.
