# F3 — junction car–car overlap: TRACKER

At-a-glance status for `docs/F3-JUNCTION-OVERLAP-TASKS.md` (task IDs) against
`docs/F3-JUNCTION-OVERLAP-DESIGN.md` (design sections).

**Legend:** `[x]` done & verified first-hand · `[~]` partially done, blocked · `[ ]` not started
· `[-]` deliberately not done (reason given)

---

## Stage 0 — baseline & instrumentation

- [x] **T0.1** Reproduce & record the baseline — parity **661/4/0**, bench **`D96213B7BB4021A7`**
      (par==single), demo worst **3.035 m** on `__veh134/__veh38` @ step **197**. All three confirmed
      first-hand, matching the handoff's severity figures exactly.
- [x] **T0.2** Lane-classify every overlap event — `F3JunctionOverlapDiagTests.cs`.
      **F3 is 8 of 61 events**, not 61.
- [x] **T0.3** Quantify the OBB anchor bug (A/B) — done; result was *not* what was hypothesised (see below).

## Stage 1 — the F3 fix

- [x] **T1.1** Split the foe-loop gate (`RespondsTo` = arbitration, `FoeWith` = occupancy) — implemented,
      arbitration arms verified still `respondsTo`-gated by reading the diff.
- [x] **T1.2** Port `getLeaderInfo`'s geometric skip guards (skip #1 + `pastTheCrossingPoint`) —
      implemented in `AdaptToJunctionLeader`, **gated behind the flag**. First landed unconditional and
      wrongly declared inert on golden-only evidence; it in fact moved the demo (61 → 94 overlap events).
      Gating restored the demo baseline exactly. See design §3e.
- [x] **T1.3** Verify the flag's effect — **DONE, and the result is NEGATIVE.** The gate makes the F3
      target bucket **worse**: 8 → **33** events, worst penetration 3.035 → **3.385 m**, stopped
      cars/frame ≈19.7 → ≈26.2. Braking without symmetry-breaking strands cars *inside* junctions, where
      they become new obstacles. **The flag must stay OFF until T1.4 lands.** See design §3d.
- [ ] **T1.4** *(NEW — the remaining blocker)* **Port SUMO's `isLeader()`**
      (`MSVehicle.cpp:7343-7483`): break mutual-conflict symmetry by junction **entry time**, tie-broken by
      speed then vehicle id. Requires **new per-vehicle junction entry-time state**. Without it, two cars in
      a mutual physical conflict both yield and saturated grids deadlock (measured: 290 stuck / 250 stuck /
      3-vs-2 teleports). **This is what stands between the flag and being default-on.**

## Stage 2 — parity gate

- [x] **T2.1** Full offline gate, flag OFF (default) — **all green:**
      `Sim.ParityTests` **661/4/0** · `Sim.Bench` **`D96213B7BB4021A7`** par==single ·
      `Sim.LiveCity.Tests` **46/46** · `Sim.Pedestrians.Tests` **272/272**
- [-] **T2.2** Golden-shift adjudication — **not needed: no golden shifted.** All 661 stayed
      byte-identical. (Also blocked in principle: apt ships SUMO **1.18.0** vs the **1.20.0** pin, and
      `pip install eclipse-sumo==1.20.0` failed here — so no valid SUMO diff was available anyway. Recorded
      because a future golden shift *will* need this resolved first.)
- [~] **T2.3** No-new-deadlock check — **flag OFF: verified no regression** (gate green).
      **flag ON: FAILS** — 3 gridlock diagnostics. This is precisely why the flag is off and T1.4 exists.

## Stage 3 — F4b and the residual causes

- [-] **T3.1** Flip the overlap invariant to ZERO — **NOT DONE, and should not be done as specified.**
      Verified in SUMO source that **`--collision.check-junctions` defaults to `false`** and internal lanes
      overlap **by construction**; zero OBB overlap is **stronger than SUMO parity**. See design §6b for the
      recommended replacement (1-D `gap >= 0` invariant + calibrated 2-D tripwire).
- [x] **T3.2** File the three residual causes — `docs/NEED-obb-anchor-halflength.md` (N1),
      `docs/NEED-colocated-vehicles.md` (N2), `docs/NEED-democity-overlapping-lane-geometry.md` (N3).
- [-] **T3.3** DR-render zero-overlap test — **DEFERRED with reason:** `RunLiveCityDrCheck` no longer
      exists, `Sim.LiveCity.Tests` doesn't reference `Sim.Viz`, neither is in `Traffic.sln`, and the
      assertion would be meaningless while N1 is unfixed (the DR path shares the same OBB math).

---

## Headline corrections to the handoff (all verified, all load-bearing)

1. **`--live-city-drcheck` / `--live-city-cartrace` DO NOT EXIST** on this branch despite being marked
   `[verified]`. Deleted; they survive only in commit `d9b209b`. The committed
   `DemoCarOverlapInvariantTests` is the repro instrument instead (better: offline, no SUMO).
2. **F3 is 8 of 61 overlap events (13%)**, not all of them. The other 53 are three unrelated causes (§7).
3. **The headline "3.035 m" is mostly an instrument artifact.** The sampled `(X,Y)` is the **front bumper**
   (`Engine.cs:2278` → `PositionAtOffset`, `Kinematics.Pos`), but `ObbOverlap` treats it as the box
   **centre** — every box is drawn shifted forward by `Length/2`. Anchored correctly, the famous
   `veh134/veh38` pair drops **3.035 m → 0.497 m**. (Correcting the anchor *raises* the total count
   61 → 97, because both cars shift: it changes *which* pairs overlap, it does not uniformly shrink them.)
4. **Handoff Pattern B is misdiagnosed.** For steps 51–57 `veh80` is on `e_d_6_5_d_5_5_2`, a **normal**
   lane, overlapping the garage stub — no internal lane involved. It is two *normal* lanes whose geometry
   overlaps (N3), unfixable by any junction admission gate.
5. **Success condition #1 ("0 overlaps") and F4b are unreachable by fixing F3** — and #1 is unreachable
   *in principle*, because SUMO itself does not guarantee it (§6b).
6. **The recurring `1.800 m` is the vehicle WIDTH** (L=5.0, W=1.8) — the min-penetration axis saturating at
   full lateral overlap, not a meaningful depth.
7. **A separate real engine bug found:** `__veh56`/`__veh84` sit at *identical* pos/speed/X/Y/angle for
   **9 consecutive steps** (also `__veh83`/`__veh121`, 1 step). Two perfectly superposed vehicles; nothing
   to do with junctions. → N2.
8. **The obvious fix is counterproductive on its own.** Widening the foe set to `FoeWith` — even with SUMO's
   correct narrow `inTheWay` predicate — makes both throughput AND the overlap it targets worse, because a
   yield that cannot resolve strands cars inside the junction. `isLeader()` entry-time ordering is
   load-bearing, not a later refinement.
9. **"All goldens byte-identical" does NOT mean parity-inert** in this repo. The demo and the gridlock
   diagnostics are not goldens and must be measured separately. This cost a wrong "inert" call mid-session
   (design §3e).

## Net state of this branch

**Shipped:** the analysis, the diagnostic instrument, the design/tasks/tracker trio, three NEED docs, and the
partial port **behind `Engine.JunctionPhysicalOccupancyGate`, default OFF**.
**Default behaviour is unchanged and fully verified:** `Sim.ParityTests` **661/4/0** ·
`Sim.Bench` **`D96213B7BB4021A7`** par==single · `Sim.LiveCity.Tests` **46/46** ·
`Sim.Pedestrians.Tests` **272/272** · demo overlap baseline reproduced exactly (61 events / 3.035 m).
**F3 is NOT fixed.** The one remaining blocker is **T1.4 (`isLeader()`)**.
