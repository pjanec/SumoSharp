# JUNCTION-FOE-LANE — tasks

Design: `JUNCTION-FOE-LANE-DESIGN.md` (each task names its section; do not restate it here).
Tracker: `JUNCTION-FOE-LANE-TRACKER.md`. Measurements referenced: journal Entry 35.
**Blocked on owner sign-off of the design.**

## Stage 0 — verify the source rule (no engine code)

### F0.1 — the foes-vs-response question (design §2 MUST-VERIFY)
Read `MSLink::setRequestInformation` end to end and answer, with line numbers: (a) is `myFoeLanes`
built from the geometric `foes` set or the `response` yield matrix; (b) how same-target lanes
enter it (`CONFLICT_DUMMY_MERGE`); (c) the exact `ConflictInfo` arithmetic
(`lengthBehindCrossing`, `getLengthsBeforeCrossing`); (d) which side brakes in a mutual crossing
(getLeaderInfo's use of `isLeader`/`inTheWay`). **Success**: a design-doc §2 amendment with the
four answers, each cited, plus one TraCI probe on a two-movement junction confirming (a)
empirically (a prioritized straight's behavior toward a foe stalled in the conflict zone).

## Stage 1 — ingest geometry (parity-inert: no reader)

### F1.1 — conflict geometry per (link, foe internal lane)
Files: `src/Sim.Ingest/*` (parser/network model), tests in `tests/Sim.ParityTests`.
Per design §3.1. **Success**: (1) unit tests on a hand-built 4-way asserting crossing pairs get a
conflict interval and same-target pairs get the merge point, values checked against
manually-computed shape intersections; (2) the all-nets sweep (F3 T2.1's corpus floors pattern)
parses every committed net with non-zero conflict pairs on ≥ the measured floor of RoW junctions,
no throw; (3) `dotnet test` green, `Sim.Bench` hash unchanged (no reader exists).

## Stage 2 — the constraint (gated, default OFF)

### F2.1 — the foe-lane occupancy arm
Files: `src/Sim.Core/Engine.cs` (+ `SumoShim` EnvGate, `docs/ENV-GATES.md`, gate test list).
Per design §3.2-3.3. **Success**: (1) `SUMOSHARP_FOELANE` off ⇒ all four surfaces byte-identical
(goldens, bench hash, LiveCity, Pedestrians); (2) ON ⇒ the Entry 35 traced episode dies — veh 122
brakes before `:123_1_1`'s conflict zone while veh 15 occupies `:123_3_0` (a pinned-witness test
in the InternalJunctionAdmissionTests style, with a vacuity guard); (3) a committed `[foelane]`
trace line (SUMOSHARP_TRACEVEH-gated) showing lane/foe/conflict-distance/verdict.

### F2.2 — same-target merge half
Same files. **Success**: (1) ON ⇒ city-organic-L2 deep rear-end onsets 12 → ≤ 2 and every
remaining onset's cause is NOT `follower-exits-junction(SAME-junction double-landing)`;
(2) city-mixed-1k 10 → ≤ 2 likewise; (3) no stuckDwell anywhere in the battery.

## Stage 3 — measure, decide, flip

### F3.1 — the full gate ladder (design §4), both gate states
**Success**: every §4 gate green with numbers recorded in the journal (BEFORE predictions first —
the Entry 34 discipline), determinism 4+1 hashes both states, battery vs
`net-regression-entry34-stays.txt`.

### F3.2 — default flip + docs
**Success**: default ON only if F3.1 is fully green; ENV-GATES row, tracker ticks, resume-doc
backlog item 0 closed with the owner's Geneva re-check noted as the final verification.
