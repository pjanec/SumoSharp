# F3 — task breakdown: porting `MSVehicle::isLeader`

Design reference: `docs/F3-ISLEADER-PORT-DESIGN.md` (sections cited per task; **do not restate the
design here — read it**). Tracker: `docs/F3-ISLEADER-PORT-TRACKER.md`.

**Baseline confirmed at the start of this work** (re-confirm before claiming any task done):

| Surface | Baseline |
| --- | --- |
| `dotnet test tests/Sim.ParityTests -c Release` | **689 passed / 4 skipped / 0 failed** |
| `dotnet run --project src/Sim.Bench -c Release` | hash **`D96213B7BB4021A7`**, par == single |
| `dotnet test tests/Sim.LiveCity.Tests` (no `--no-build`) | **48 / 48** |
| `dotnet test tests/Sim.Pedestrians.Tests -c Release` | **272 / 272** |

**Standing rules for every task below.**
- Stage 1 is **parity-inert by construction** (new data is written but never read). Any golden change
  in stage 1 is a **bug in the task**, not a result to accept.
- Never judge a junction change on goldens alone (log §7 Lesson 1). The five gridlock diagnostics are
  the regression net: `WillPassSaturationDiagTests`, `DenseFlowDeadLaneDrainTests`,
  `RungHDp2g2CoordinatedLaneChangeTests`, `RblLeftTurnsDiagTests`, `LowDensityTeleportTests`.
- Never mix harnesses: `SumoShim.Run` and a direct `engine.Run()` give different baselines on the
  same scenario (log §7, §9.33). Teleport A/Bs go through `SumoShim.Run`.
- No `System.Random`. No `EntityIndex` in any tie-break.

---

## Stage 1 — data plumbing (parity-inert)

### T2.1 — `LinkIndexByInternalLane` and `EntryConnectionByLink`

**Design:** §2c (and §2a for why a cont link's `JunctionLink.Connection` is the *second* hop).
**Files:** `src/Sim.Ingest/NetworkModel.cs`, `src/Sim.Ingest/NetworkParser.cs`,
`tests/Sim.ParityTests/JunctionLinkLaneMapTests.cs` (new).

Add two parse-time lookups, both optional ctor params defaulting to null so nothing existing breaks:

1. `LinkIndexByInternalLane : string → (Junction Junction, int LinkIndex)` — every internal lane of a
   junction, **both** cont stages, to its junction link index. Build it by extending the existing
   `JunctionByInternalLane` back-walk (`NetworkParser.cs:228-267`), which already visits exactly
   these lanes; record `link.Index` alongside the junction it is already recording.
2. `EntryConnectionByLink : (string JunctionId, int LinkIndex) → Connection` — SUMO's
   `getCorrespondingEntryLink()` (`MSLink.cpp:1331-1339`). Resolve as the top-level `<connection>`
   whose `LinkIndex == i` **and** whose `Tl` is this junction's TL id, falling back to the connection
   whose `Via` is the stage-1 lane reached by the back-walk when the net has no TL. For a non-cont
   link this is the same connection `JunctionLink.Connection` already holds.

**Success conditions** (all in the new test file, offline, no SUMO):
1. On `scenarios/_repro/synthetic-junction2/grid.net.xml`, for junction `2336`:
   `LinkIndexByInternalLane[":2336_42_0"] == (2336, 18)` **and**
   `LinkIndexByInternalLane[":2336_18_0"] == (2336, 18)` — i.e. **both** cont stages resolve to link
   18, while `":2336_18_0"` is asserted **absent from `junction.IntLanes`**. This is the whole point
   of the map and must be asserted, not assumed.
2. `EntryConnectionByLink[("2336", 18)]` has `Tl == "2336"`, `LinkIndex == 18`, `State == "o"`, and
   `Via == ":2336_18_0"` — i.e. the **entry** hop, *not* the second hop. Assert in the same test that
   `junction.Links[18].Connection.Via == ":2336_42_0"` and that its `LinkIndex is null`, so the test
   demonstrates the two differ (a non-vacuous guard).
3. Every one of the ten cont links at `2336` (indices 5, 12, 17, 18, 19, 25, 31, 36, 37, 38, i.e.
   every `i` with `Requests[i].Cont`) has both stages present in `LinkIndexByInternalLane` mapping to
   `i`, and a resolvable `EntryConnectionByLink` entry carrying a non-null `LinkIndex`.
4. Across **every committed `*.net.xml`** in `scenarios/`: every lane id in every junction's
   `IntLanes` is present in `LinkIndexByInternalLane` with the matching index, and no entry maps to a
   junction that does not contain it. (Sweep test — cheap, and it is what catches a net shape the
   two-junction sample does not cover.)
5. **Parity:** `Sim.ParityTests` **689/4/0** and `Sim.Bench` hash **`D96213B7BB4021A7`** unchanged.
   Nothing reads the new maps yet, so any change here is a bug.

### T2.2 — the three per-vehicle timestamps, written but not read

**Design:** §2, §2b (the classification table and the worked cont example), §4 (why `long`).
**Files:** `src/Sim.Core/VehicleRuntime.cs`, `src/Sim.Core/Engine.cs`,
`tests/Sim.ParityTests/JunctionEntryTimeTests.cs` (new).

Add to `VehicleRuntime`, next to `WaitingTime`, three `long` fields initialised to `long.MaxValue`
(the `SUMOTime_MAX` sentinel): `JunctionEntryTime`, `JunctionEntryTimeNeverYield`,
`JunctionConflictEntryTime`. **`long` step indices, not `double` seconds** — §4 gives the reason
(exact-equality comparison); a reviewer will reject `double`.

Assign them at the lane-advance seam `Engine.cs:10127-10132` (documented there as *"the ONE site a
lane is fully left"*), per §2b's table, using the current step index. Reset all three to
`long.MaxValue` on exit and on every existing `LaneSeqIndex = 0` re-initialisation site
(insert / teleport / reroute — grep `v.LaneSeqIndex = 0`), so a recycled vehicle never inherits a
stale timestamp.

**Do not read these fields anywhere in this task.** That is what makes it parity-inert.

**Success conditions:**
1. A direct test driving `scenarios/_repro/synthetic-junction2` that captures, for a vehicle taking
   **non-cont** link 3, the three fields at each step and asserts: all three equal the entry step
   while it is on `:2336_3_0`, and all three are `long.MaxValue` before entry and after exit.
2. The same for a vehicle taking **cont** link 18, asserting the three-stage sequence from §2b's
   worked example: on `:2336_18_0` → `ET == ETN == t1` and `CET == long.MaxValue`; on `:2336_42_0` →
   `CET == t2 > t1` and `ET == ETN == t1`; after exit → all three `long.MaxValue`.
   **`CET == long.MaxValue` while in the bay is the load-bearing assertion** — it is what makes a car
   waiting in the bay yield to everything, and it is the one value that distinguishes a correct cont
   port from a plausible wrong one.
3. `Sim.ParityTests` **689/4/0** + the two new tests; `Sim.Bench` hash unchanged; LiveCity 48/48.
   Parity-inert by construction — any change is a bug.

---

## Stage 2 — the decision (flag-gated, default OFF)

### T2.3 — `IsLeader` and `ResponseFor` as directly testable helpers

**Design:** §3 (case selection), §3a (the four response attempts), §4 (tie-break).
**Files:** `src/Sim.Core/Engine.cs`, `tests/Sim.ParityTests/JunctionIsLeaderTests.cs` (new).

Port `MSVehicle::isLeader` (`MSVehicle.cpp:7343-7483`). Keep the tie-break chain in a **separately
callable** shape so it can be unit-tested without building a junction scenario — e.g. a static
`IsLeaderByEntryOrder(long egoET, long foeET, double egoSpeed, double foeSpeed, string egoId, string foeId)`
that `IsLeader` delegates to. Omissions per §7, each carrying the comment stated there.

**Success conditions:**
1. **Direct tie-break test** (§4), all three rungs, not via a scenario:
   - different entry times ⇒ the **later** entrant yields (`egoET > foeET` ⇒ true), both orders;
   - equal entry times, different speeds ⇒ the **slower** yields, both orders;
   - equal entry times and equal speeds ⇒ the **lexicographically smaller id** yields, both orders,
     and the result is the exact negation when the arguments are swapped (antisymmetry).
2. A test asserting the id comparison is **ordinal**: ids chosen so a culture-sensitive compare would
   disagree with a byte-wise one must follow the byte-wise answer. A test that would pass under
   `string.Compare` too is vacuous and does not satisfy this condition.
3. A test asserting **no committed net contains an indirect link**, so §7's omission of the
   indirect-left sub-case cannot silently begin to matter.
4. **Case-selection tests on the measured deadlock pair, per design §0a** — note this condition was
   corrected after measurement, so read §0a rather than reasoning from the matrix:
   - Assert junction `2336`'s TL **never shows links 3 and 18 non-red simultaneously** (0 of 12
     phases). This is why `attempt 1` is the only arm that ever runs for this pair.
   - Assert that with both entry links red, `ResponseFor` returns `response == response2 == true`, so
     the **mutual-conflict** branch is selected and **both** sides use `CET` — reached via attempt 1,
     *not* via the response matrix.
   - Assert the one-red cases pick the other two pairs (`ego.CET` vs `foe.ET` when only the foe's link
     is red; `ego.ET` vs `foe.CET` when only ego's is red).
   - **Assert antisymmetry directly:** for each of the three phase classes, evaluating `IsLeader`
     both ways round yields exactly one `true`. This is the property that makes the deadlock
     structurally unreachable, and it is the single most important assertion in this task.
5. **Attempt 1 must be implemented** — it is not stageable. §0a shows it is the only arm that executes
   for the confirmed deadlock. Include the `brakeGap` sub-branch with `:7386-7388`'s `-2 * minGap`
   arithmetic verbatim.
6. `Sim.ParityTests` green + the new tests; hash unchanged (nothing calls `IsLeader` yet).

### T2.4 — wire into arm 5 behind `JunctionIsLeaderGate` (default OFF)

**Design:** §5a, §6.
**Files:** `src/Sim.Core/Engine.cs`, `src/Sim.Sumo/SumoShim.cs` (env gate, mirroring
`SUMOSHARP_CONTTURNFIX`), `tests/Sim.ParityTests/JunctionIsLeaderTests.cs`.

`public bool JunctionIsLeaderGate { get; set; } = false;`

**The flag-off expression must remain character-for-character what is there today** (§5a) so
byte-identical-with-flag-off is a property of the code shape, not a measurement.

**Success conditions:**
1. A test asserting the default is `false` (mirroring `IgnoreJunctionBlockerTests.DefaultIsMinusOne`).
2. Flag **off**: `Sim.ParityTests` 689/4/0 (+new), hash `D96213B7BB4021A7`, LiveCity 48/48,
   Pedestrians 272/272 — all byte-identical.
3. Flag **on**: the full gate runs and its results are **reported**, not asserted, in this task.

---

## Stage 3 — measure, then decide

### T2.5 — measurement and the defaults decision

**Design:** §6.
**Files:** `docs/F3-SESSION-LOG.md` (append §9), `docs/NEED-arm5-mutual-junction-deadlock.md`
(resolve or update), `docs/NEED-yield-request-reset-unported.md` (new), plus the A/B test file.

Measure, with the flag ON, on **all four** surfaces, and record every number in the log:

1. **The deadlock, without the knob.** `synthetic-junction2` via `SumoShim.Run`, 2000 s,
   `IgnoreJunctionBlockerSeconds = -1` (SUMO's default), `ContTurnInsideJunctionGate = true`:
   teleports **≤ 2**, and vehicles **95 and 102 arrive** (real SUMO: 433 s / 497 s; the knob got
   647 s / 587 s). Verify arrival by diffing the `--tripinfo-output` arrived set, as §9.33 did.
2. All 661 goldens byte-identical, **or** every shift justified by a live-SUMO 1.20.0 diff
   (`sumo --version` must print 1.20.0).
3. `Sim.Bench` hash `D96213B7BB4021A7` and **par == single** (the determinism guard for §5c).
4. All **five** gridlock diagnostics green; LiveCity 48/48; Pedestrians 272/272.
5. **Re-measure the F3 overlap buckets** (log §3): total, worst penetration, max pairs/frame, and the
   `BOTH-INTERNAL-DIFFERENT-LANE` stopped/both-moving split. Expect the 12 both-moving events to
   drop. **Report this even if it worsens** — §6.3 notes the port may trade deadlock for overlap, and
   the log's standing lesson is that a teleport count alone is not evidence.

**Then, and only then**, put to the owner: whether `JunctionIsLeaderGate` and
`ContTurnInsideJunctionGate` go default-ON, and whether `IgnoreJunctionBlockerSeconds` stays at
SUMO's `-1`. Flipping a default changes outward-facing behaviour and is an owner decision, not a test
outcome.
