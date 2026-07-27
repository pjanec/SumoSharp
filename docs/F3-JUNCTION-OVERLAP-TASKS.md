# F3 — junction car–car overlap: TASKS

Work breakdown for `docs/F3-JUNCTION-OVERLAP-DESIGN.md`. Each task names its design reference (a section,
not a copy), the files it touches, its dependencies, and **mandatory success conditions**.

Tracker: `docs/F3-JUNCTION-OVERLAP-TRACKER.md`.

---

## Stage 0 — baseline & instrumentation (no engine change)

### T0.1 — Reproduce and record the baseline
- **Design ref:** §0
- **Files:** none (measurement only)
- **Deps:** none
- **Success conditions:**
  - `dotnet test tests/Sim.ParityTests -c Release` = **661 passed / 4 skipped / 0 failed**
  - `dotnet run --project src/Sim.Bench -c Release` → hash **`D96213B7BB4021A7`**, `deterministic=True`, par==single
  - `DemoCarOverlapInvariantTests` reports worst **3.035 m**, pair `__veh134 / __veh38`, step **197**
- **Status:** DONE (all three confirmed first-hand)

### T0.2 — Lane-classify every overlap event
- **Design ref:** §1
- **Files:** `tests/Sim.LiveCity.Tests/F3JunctionOverlapDiagTests.cs` (new, always-passing instrument)
- **Deps:** T0.1
- **Success conditions:**
  - Totals reconcile to the committed baseline (61 events, worst 3.035 m, max 3 pairs/frame)
  - Every event assigned to exactly one of the 5 buckets; bucket counts sum to the total
  - Per-bucket and per-lane-pair worst penetration reported, with the F3 bucket isolated
- **Status:** DONE — F3 bucket (`BOTH-INTERNAL-DIFFERENT-LANE`) = **8 of 61**

### T0.3 — Quantify the OBB anchor bug (A/B)
- **Design ref:** §1b
- **Files:** `tests/Sim.LiveCity.Tests/F3JunctionOverlapDiagTests.cs`
- **Deps:** T0.2
- **Success conditions:**
  - Both variants (front-anchor vs centre-corrected) computed over **identical** trajectories (one sim, one
    `Step()` loop, two overlap computations)
  - Per-bucket delta reported (`frontEvents → centreEvents`, `frontWorst → centreWorst`)
  - Confirms/refutes: the 31 `ONE-INTERNAL-ONE-NORMAL` events are anchor artifacts
  - Reports whether the recurring `1.800 m` equals the vehicle **width** (min-penetration axis saturating)
  - Characterises the exactly-co-located-pair anomaly (count, persistence) → feeds N2
- **Status:** IN PROGRESS

---

## Stage 1 — the F3 fix (core, parity-critical)

### T1.1 — Split the foe-loop gate (`RespondsTo` vs `FoeWith`)
- **Design ref:** §2, §3
- **Files:** `src/Sim.Core/Engine.cs` (`JunctionYieldConstraint`, loop head `:6890-6895`)
- **Deps:** T0.3
- **Success conditions:**
  - Loop admits `j` when `respondsTo || physicalFoe`; `j == egoLink.Index` still skipped
  - **Arbitration arms stay `respondsTo`-gated:** approaching-foe yield (`:6981-7125`), sameTarget merge
    (`:6911-6924`), external-agent (`:6947-6957`) — verified by reading the diff, not by assertion alone
  - On-junction `AdaptToJunctionLeader` arm reachable for `physicalFoe && !respondsTo`
  - The new arm does **not** set `v.CrossingYieldTaken` (§4 `prePass` safety)
  - Cites `MSRightOfWayJunction.cpp:92-146`, `MSLink.cpp:1373`, `MSVehicle.cpp:3429` in comments

### T1.2 — Port `getLeaderInfo`'s geometric skip guards
- **Design ref:** §3b
- **Files:** `src/Sim.Core/Engine.cs` (`AdaptToJunctionLeader` `:7934`, or the arm's call site)
- **Deps:** T1.1 (same edit region; may land together)
- **Success conditions:**
  - **Ego-past-crossing** (`MSLink.cpp:1398` skip #1): when `distToCrossing + crossingWidth < 0` the arm
    returns `+∞`, **never** a negative-distance `StopSpeedFor`
  - **Foe-past-crossing** (`pastTheCrossingPoint`, `MSLink.cpp:1633`): returns `+∞`
  - A **direct unit/behavioural test** proves a car already past a conflict point is NOT braked — this is the
    anti-deadlock guard and must be asserted explicitly, not inferred from goldens
  - `sagitta` omission recorded in-comment as a deliberate deviation (no `myRadius` in `JunctionConflict`)

### T1.3 — Verify Pattern A is gone
- **Design ref:** §6.1
- **Files:** none (measurement)
- **Deps:** T1.1, T1.2
- **Success conditions:**
  - `BOTH-INTERNAL-DIFFERENT-LANE` events = **0**, measured **centre-corrected**
  - Specifically `__veh134`/`__veh38` at step 197 no longer overlap
  - The lane pairs `:d_5_4_1_0`×`:d_5_4_21_0`, `:d_4_2_7_1`×`:d_4_2_9_0`, `:d_3_4_10_0`×`:d_3_4_5_0`,
    `:d_5_4_12_0`×`:d_5_4_1_0`, `:d_4_2_1_1`×`:d_4_2_9_0`, `:d_5_3_10_0`×`:d_5_3_22_0` all clear

---

## Stage 2 — parity gate (the crux)

### T2.1 — Full offline gate
- **Design ref:** §5, §6
- **Files:** none
- **Deps:** T1.3
- **Success conditions:**
  - `dotnet test tests/Sim.ParityTests -c Release` = **661/4/0**, byte-identical goldens
  - `Sim.Bench` hash **`D96213B7BB4021A7`**, par==single
  - `dotnet test tests/Sim.LiveCity.Tests` green, run **WITHOUT** `--no-build`
  - `dotnet test tests/Sim.Pedestrians.Tests` green

### T2.2 — Golden-shift adjudication (only if T2.1 shows a shift)
- **Design ref:** §5
- **Files:** affected `scenarios/*/golden.*`, `provenance.txt`
- **Deps:** T2.1
- **Success conditions:**
  - **Prerequisite:** a genuine **SUMO 1.20.0** build (apt ships 1.18.0; pip install failed — see §5). A
    1.18.0 diff is **not** a valid anchor and must not be used.
  - For each shifted fixture: first-divergence step, per-attribute max-abs + RMSE, and a live-SUMO trajectory
    diff at that step
  - Regeneration **only** where the diff proves we moved **toward** SUMO; each decision recorded in the tracker
  - Any fixture where we moved **away** from SUMO ⇒ fix reworked, not accepted

### T2.3 — No-new-deadlock check
- **Design ref:** §6.5
- **Files:** none
- **Deps:** T1.3
- **Success conditions:**
  - `NEED-multilane-junction-passage` does not deepen: measure stop-line deadlock rate on a `-L 2` multilane
    grid before vs after; must not increase
  - `RungC4viFarRoutedFoeParityTests` (far-routed-foe over-yield) stays green
  - Demo throughput (`DenseFlow_…NoGridlock`) stays green; report vehicles-arrived before vs after

---

## Stage 3 — F4b and the residual causes

### T3.1 — Re-characterise the overlap invariant honestly
- **Design ref:** §7
- **Files:** `tests/Sim.LiveCity.Tests/DemoCarOverlapInvariantTests.cs`
- **Deps:** T2.1
- **Success conditions:**
  - The F3 bucket is asserted at **0**
  - Residual non-F3 buckets are asserted at their measured centre-corrected ceilings, each annotated with its
    cause (N1/N2/N3) and its follow-up doc
  - The test is **non-vacuous**: it still fails if F3 regresses
  - **Not** a blanket "assert ZERO" — §7 explains why the engine cannot yet deliver that

### T3.2 — File the three residual causes
- **Design ref:** §7
- **Files:** `docs/NEED-obb-anchor-halflength.md` (N1), `docs/NEED-colocated-vehicles.md` (N2),
  `docs/NEED-democity-overlapping-lane-geometry.md` (N3)
- **Deps:** T0.3
- **Success conditions:** each NEED states the evidence (lane pairs, steps, vehicle ids), the suspected
  mechanism, and why it is out of F3's scope

### T3.3 — DR-render zero-overlap test — **DEFERRED, with reason**
- **Design ref:** handoff §6.2
- **Deps:** T3.1
- **Success conditions:** N/A — deferred. `RunLiveCityDrCheck` no longer exists (§0), `Sim.LiveCity.Tests`
  does not reference `Sim.Viz`, and neither is in `Traffic.sln`. A DR-render assertion is meaningless while
  N1 (anchor) is unfixed, since the DR path shares the same OBB math. Revisit after N1.
