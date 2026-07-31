# TASKS — the approach arm of internal-junction admission

Work breakdown for `JUNCTION-APPROACH-ARM-DESIGN.md`. Each task names its design section (it does not
restate it), the files it touches, its dependencies, and — mandatory — **success conditions specific
enough to be checked by someone who did not write the task**.

A task is closed only when its success conditions are verified **first-hand** by the reviewer, not when
the implementor reports them. Where a test is the success condition, the reviewer must also confirm the
test is **non-vacuous** — that it FAILS with the change reverted or the gate off.

---

## T1 — parse `InternalLinkFoes`

**Design:** §2 (construction rule), §3 (mapping), §4.1 (the via-lane assertion), §10.2 (the bit trap).
**Files:** `src/Sim.Ingest/NetworkParser.cs`, `src/Sim.Ingest/NetworkModel.cs`.
**Depends on:** nothing.

Build, per internal junction, the set SUMO calls `myInternalLinkFoes`, stored as **via-lane handles**
(§4.1) keyed by `InternalJunction.Id`, alongside the existing `InternalLaneFoes`.

Rule, from `MSInternalJunction.cpp:96-110` — read it, do not work from this summary alone:
iterate `IncLanes` **from index 1** (the checker lane at index 0 is excluded); for each of that lane's
links, take the corresponding entry link's index; keep it iff that index is set in
`response[ownLinkIndex]`; when the link's via lane itself has a via lane, also add that follow-on
internal-junction link's via lane.

**Success conditions**
1. For `scenarios/_diag/junction-realism-L1`, internal junction `:J01_13_0` resolves an
   `InternalLinkFoes` set that **contains the via lane `:J01_10_0`**. Asserted as that specific lane —
   not "non-empty", which would pass under the reversed-bit-mask error of §10.2.
2. A test asserting the set is **empty when index 0 is NOT skipped** — i.e. the exclusion of the
   checker lane is exercised, not incidental.
3. Every link foe resolves to a **non-null** via lane across all committed scenarios (§4.1); if any
   does not, the task stops and reports rather than silently dropping it.
4. `NetworkParser` changes are parse-time only: no `Sim.Core` reader yet ⇒ all 661 goldens
   byte-identical, `Sim.Bench` hash unchanged. (This task is provably parity-inert; T4 is not.)

## T2 — expose the approach registry to the new arm

**Design:** §4.
**Files:** `src/Sim.Core/Engine.cs`.
**Depends on:** nothing (parallel with T1).

No behaviour change. Confirm `_foeCrossFirst/_foeCrossSecond` are readable at
`InternalJunctionAdmissionConstraint`'s call site with the same lifetime guarantees
`JunctionYieldConstraint` relies on, and add the accessor/shape the arm needs.

**Success conditions**
1. A test that `BuildFoeApproachIndex` registers a vehicle **still on its approach lane** against the
   internal lane ahead of it — specifically, on the repro at t=49, `f_cyc_cw2.1` is registered against
   `:J01_10_0` while its own lane is `in_W01_0`. This is the precise fact the whole arm rests on, so it
   is asserted directly rather than inferred.
2. Goldens byte-identical (no behavioural edit in this task).

## T3 — `ArrivalWindowBlocks`, with a total tie-break

**Design:** §5 (the branches kept and the four omissions), §6 (determinism), §10.1 (the deadlock risk).
**Files:** `src/Sim.Core/Engine.cs`.
**Depends on:** T2.

Port `blockedByFoe`'s reachable branches. **The tie-break is the load-bearing part, not the window
arithmetic** — CLAUDE.md lesson 11: a symmetric predicate cannot arbitrate a cycle, and this is exactly
how gate 3 produced a 4890-step wedge before its entry-order sub-gate.

Each of the four omissions in §5 is a **guarded** omission: assert the precondition (impatience==0, not
all-way-stop, no `jmIgnoreFoe*`, `lateral-resolution < 0`) rather than assuming it, so a future
scenario that violates one fails loudly instead of silently running an unfaithful predicate.

**Success conditions**
1. **Antisymmetry, asserted:** for any ego/foe pair, `ArrivalWindowBlocks(a,b)` and
   `ArrivalWindowBlocks(b,a)` are not both true. Property-checked over a constructed set that includes
   the exactly-equal-arrival-time case, which is the one that matters.
2. Ties resolve by `string.CompareOrdinal` on vehicle id — asserted by a test that would pass under an
   `EntityIndex` tie-break only by luck, i.e. one where id order and index order **disagree**.
3. Each guarded omission has a test proving the guard fires when its precondition is violated.

## T4 — wire the arm into `InternalJunctionAdmissionConstraint`

**Design:** §5, §6.
**Files:** `src/Sim.Core/Engine.cs`.
**Depends on:** T1, T2, T3.

Add the loop after the existing lane-foe loop, reached only when that loop did not already block. The
existing loop is **not** modified. Skip foes already on an internal lane — the lane-foe loop owns them,
and double-counting would make the two halves' contributions unattributable.

**Success conditions**
1. **§9.1 of the design, asserted directly:** on `junction-realism-L1`, `f_cyc_ccw2.0` never occupies
   `:J01_13_0` while `f_cyc_cw2.1` is on `:J01_10_0`, and is instead held on `:J01_5_0`.
2. **Non-vacuous:** that same test FAILS with the arm disabled. Stated as a required observation, not
   an expectation.
3. Junction-interior OBB overlaps = **0** on `junction-realism-L1` and `-L2`
   (`scripts/analyze-junction-realism-fcd.py`).

## T5 — binder diagnostics distinguish the two halves

**Design:** §6 (last bullet).
**Files:** `src/Sim.Core/Engine.cs`.
**Depends on:** T4.

Arm 14 now has two reasons to bind. Record which one fired.

**Success condition:** on the repro, the diagnostic attributes the hold at the seed step to the
**approach** half, and lane-foe holds elsewhere still attribute to the **lane** half. Without this, the
next investigator cannot tell the halves apart — the exact "stale binder diagnostics" failure that
distorted a previous session (`F3-SESSION-LOG.md` §7 lesson 2).

## T6 — the gate, and the honest answer on gridlock

**Design:** §7, §9.3.
**Depends on:** T4.

Re-run, with `scripts/analyze-junction-realism-fcd.py` on **both arms** (CLAUDE.md #8/#13 — never
compare across instruments):

* `JUNCTION-REALISM-TRACE-FINDINGS.md` §2 (arrived / running / halting, both lane counts) and §3
  (dwell, overlap counts, causal order);
* `dotnet test tests/Sim.ParityTests -c Release` → **775/4**, 661 goldens byte-identical;
* `Sim.Bench` hash `BF3794A4704BCD79`, par == single;
* `dotnet test tests/Sim.LiveCity.Tests` → **90/90** (⚠ NOT in `Traffic.sln` — build the csproj
  explicitly or stale code is measured);
* `dotnet test tests/Sim.Pedestrians.Tests` → **324/324**.

**Success conditions**
1. Every number above recorded in the tracker **whichever way it comes out**. A null result on the
   gridlock is a result, and §9.3 forbids quietly keeping the hypothesis alive if overlaps reach 0 and
   the network still deadlocks.
2. Any golden shift is SUMO-diffed per design §7.2 before being accepted, and the diff is committed.
3. The `-light` control is run too: if the fix only works at the stress demand, that is a finding.

## T7 — the remaining reported defects (NOT in this workstream's scope, tracked so they are not lost)

Not started, and deliberately **not** bundled — each needs its own trace before any design.

* **Lane change while stopped at a red, into an occupied lane** —
  `SUMOSHARP-ISSUE-stopped-lane-change-overlap.md` has an isolated repro and a deferred fix. Not yet
  looked for on the junction-realism net.
* **Pedestrian holding a car inside the junction** (owner's amplifier hypothesis). Cannot be tested via
  `Sim.Run` — there is no ped coupling on that path; it lives in `LiveCitySim`. Needs crossings +
  ped demand on the repro net, as a separate harness.
* **J10 wedges before it overlaps** (`JUNCTION-REALISM-TRACE-FINDINGS.md` §5.2) — the one junction that
  does not fit the story. Untraced.
