# URGENT-STRATEGIC-FOLLOW — task breakdown

Design reference: `URGENT-STRATEGIC-FOLLOW-DESIGN.md` (§ numbers below refer to it).
Checklist: `URGENT-STRATEGIC-FOLLOW-TRACKER.md`. Each task closes only when its success conditions pass.

## Stage 0 — done (the probe, committed with Entry 25)

- **T0.1 — probe constraint.** `UrgentStrategicLeaderFollowConstraint` (Engine.cs, binder 18), default
  OFF, `SUMOSHARP_URGENTFOLLOW` gate, ENV-GATES row, BinderLogObserver tag. *Done: builds; suite
  778/5/0 with flag off; goldens byte-identical in BOTH flag states; L2-light reproduces SUMO's move to
  two decimals; L2 collapse measured and recorded.*

## Stage 1 — diagnose the collapse (design §3; NO behavioural edits in this stage)

- **T1.1 — brake-without-change attribution.** Files: `src/Sim.Core/Engine.cs` (diagnostic counters or
  a `SUMOSHARP_TRACEVEH`-gated line only). For a flag-ON L2 run, measure per step-sample: of the
  vehicles bound by binder 18, how many have a change REQUEST this step that is (a) committed,
  (b) refused — and by WHICH veto (`slotContested` / `cutInDefer` / `overlapped` / `unsafeFollow` /
  `unsafeLead-still`). Success: a table over ≥ 300 steps of the saturated run attributing ≥ 95% of
  binder-18 samples, distinguishing H-A / H-B / H-C. The instrument is committed, not scratch.
- **T1.2 — publish the verdict.** Journal entry with the table and the surviving hypothesis; the design
  §3 updated from "hypotheses" to "finding". Success: the entry names the mechanism with numbers, or
  records the null and stops the workstream here.

## Stage 2 — the scoped fix (shape depends on T1.2; one task per surviving hypothesis)

- **T2.A (if H-A).** Make the veto set consistent with the coupling: a vehicle the coupling is braking
  must be allowed to complete the change the moment the SUMO-side gates (`IsTargetLaneSafe`) pass —
  the non-SUMO vetoes either yield to an urgent changer or gain the same urgency semantics SUMO's
  equivalents have. Files: `Engine.cs` (`TryStrategicLaneChange` veto block). Success: acceptance gates
  design §5, all rows.
- **T2.B (if H-B).** Align the "blocked" predicate with SUMO's `checkChange` leader arm (gap test at
  `plannedSpeed`, not current speed; secure-gap parameters verified against `MSLaneChanger.cpp:744-935`).
  Files: `Engine.cs` (the constraint's `IsTargetLaneSafe(lead-only)` call). Success: same gates.
- **T2.C (if H-C).** One-step-lag compensation (forecast the post-brake gap in the safety test, the same
  one-step-forecast pattern `neighNextGap` already uses). Success: same gates.

## Stage 3 — default flip + regression surface

- **T3.1 — measurements.** Flag-ON vs flag-OFF: goldens; L2 + L2-light + L1 vs their SUMO oracles;
  stopped-LC rate with denominator; 26-net battery vs `net-regression-keepclear-direction.txt`; the 4
  synthetic-junction2 behavioural tests. Success: every §5 acceptance row green, numbers in the journal.
- **T3.2 — flip `UrgentStrategicLeaderFollow` default ON** + ENV-GATES row update + RESUME update.
  Success: full suite green at the new default; battery clean; journal AFTER entry.
- **T3.3 — behavioural regression test.** A committed test pinning the L2-light behaviour (the
  left-turner changes lanes at speed, not at the stop line) so the mechanism cannot silently regress.
  Success: test fails when the flag is forced off, passes at the shipped default.
