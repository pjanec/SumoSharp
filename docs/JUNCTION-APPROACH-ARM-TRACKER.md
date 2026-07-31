# TRACKER — the approach arm of internal-junction admission

At-a-glance state. Task detail is in `JUNCTION-APPROACH-ARM-TASKS.md`; the reasoning is in
`JUNCTION-APPROACH-ARM-DESIGN.md`. A box is ticked only when the reviewer has verified that task's
success conditions **first-hand**, including that its test is non-vacuous.

## Stage 0 — reproduce (DONE)

- [x] **Repro net + demand, calibrated against honest SUMO** — `scripts/gen-junction-realism-net.py`,
      `scenarios/_diag/junction-realism-L{1,2}{,-light}`. Demand scale 2 = the discriminating point
      (SUMO congests hard, mean trip 261 s vs ~90 s free-flow, but always drains); scale 1 gridlocks
      SUMO itself and is unusable as an oracle.
- [x] **Analyzer** — `scripts/analyze-junction-realism-fcd.py`. Runs on either engine's FCD.
- [x] **Defect reproduced and localised** — `docs/JUNCTION-REALISM-TRACE-FINDINGS.md`. Engine gridlocks
      permanently (225/311 vehicles at 0.000 m/s) where honest SUMO drains 450/450, in `--parity` mode,
      at both lane counts.
- [x] **Mechanism named from a per-vehicle SUMO diff, not from source-reading** — the omitted
      `myInternalLinkFoes` half of `MSLink::setRequestInformation`.

## Stage 1 — design (DONE, awaiting sign-off)

- [x] Design / tasks / tracker trio written.
- [ ] **Owner sign-off on the design.** ← *blocking; nothing below starts before this*

## Stage 2 — implement

- [ ] **T1** parse `InternalLinkFoes` (parse-time only; provably parity-inert)
- [ ] **T2** expose the approach registry to the arm (no behaviour change)
- [ ] **T3** `ArrivalWindowBlocks` with a **total** tie-break ← *the deadlock risk lives here*
- [ ] **T4** wire the arm in
- [ ] **T5** binder diagnostics distinguish lane-half from approach-half

## Stage 3 — gate

- [x] **T6b (baseline half)** — `scripts/run-net-regression.py`, baseline in
      `docs/reports/net-regression-baseline.txt`. Captured **before** any engine edit.

  **Two findings from the baseline that were not expected and change the framing:**

  1. **Junction-interior overlaps are NOT confined to the synthetic repro — they are on the committed
     benchmark city nets today.** Peak simultaneous overlapping pairs: `city-mixed-1k` **9**,
     `city-3000` **6**, `city-organic-L2` 4, `willpass-saturation` 4, `city-organic` 3, `city-30` 1,
     `city-300` 1. Several of those nets also fail to drain (`city-mixed-1k` 223 still running with a
     **81-step** junction dwell; `city-organic` 62; `city-organic-L2` 12). So this defect has been
     shipping on the real nets, not just on a net built to provoke it.
  2. ⚠ **Overlap does NOT imply gridlock.** `junction-realism-L{1,2}-light` **fully drain** (228/228,
     0 running) while still producing **3 and 2** overlapping pairs and dwells of 21 and 4 steps. At
     light demand we interpenetrate and recover; only at stress demand does it become permanent.
     This does not refute the §5 hypothesis — interpenetration may still be what converts a recoverable
     queue into a permanent wedge — but it does kill the simple form of it. "Fix the overlap and the
     gridlock goes away" is now explicitly **unsupported by the data we have**, and the `-light`
     control is the row that says so.
- [ ] **T6** re-run both surfaces and record the numbers **whichever way they come out**
- [ ] **T6b (after half)** no committed net regresses on arrived / still-running / junction dwell /
      overlap pairs. Owner's bar: *a symmetric deadlock is as bad as the existing one* — fixing the
      repro does not buy a regression anywhere else.

| measurement | before | after | source |
|---|---|---|---|
| L1 arrived / running @ t=1800 | 60 / 225 | — | findings §2 |
| L2 arrived / running @ t=1800 | 139 / 311 | — | findings §2 |
| SUMO-honest reference (both) | 450 / 0 | *(unchanged)* | findings §2 |
| longest dwell inside a junction | 1660 steps | — | findings §3 |
| junction-interior overlap pairs @ t_end | 4 | — | findings §3 |
| first junction-interior overlap | J01 t=50 | — | findings §3 |
| parity | 775/4, 661 byte-identical | — | `Sim.ParityTests` |
| bench hash | `BF3794A4704BCD79` | — | `Sim.Bench` |
| LiveCity | 90/90 | — | ⚠ not in `Traffic.sln` |
| Pedestrians | 324/324 | — | |

**The gridlock question is open and must be answered honestly.** Overlap precedes wedge at 3 of 4
junctions, which is precedence, not causation. If overlaps reach 0 and the network still gridlocks, that
is a real result and the gridlock gets its own trace — design §9.3 forbids keeping the hypothesis alive
through a null.

## Out of scope here — tracked so they are not lost (T7)

- [ ] Lane change while stopped at a red, into an occupied lane —
      `SUMOSHARP-ISSUE-stopped-lane-change-overlap.md` (isolated, fix deferred, never actioned)
- [ ] Pedestrian holding a car inside a junction (owner's amplifier hypothesis) — needs crossings + ped
      demand on the repro net, and a `LiveCitySim` harness: `Sim.Run` has no ped coupling at all
- [ ] J10 wedges *before* it overlaps — the one junction that does not fit the story, untraced
