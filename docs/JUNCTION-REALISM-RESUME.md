# JUNCTION-REALISM — RESUME (start here)

**Read this first, cold.** It is self-contained: you can pick the work up from this page alone.
Branch: **`claude/sumosharp-traffic-bugs-g1y9hl`**. Gate state at handoff:
**`dotnet test tests/Sim.ParityTests -c Release` = 776 passed / 5 skipped / 0 failed**, all 661 goldens
byte-identical.

---

## 1. What this workstream is

The owner reported four realism defects in the live-city demo: **gridlock**; **lateral lane changes
while standing at a red** (sometimes into an occupied lane); **two cars visually merged inside a
junction**; **two cars driving through each other** when one turns left and the other goes straight.

The approach was: build a **small synthetic net with a SUMO oracle**, reproduce each defect there, then
chase them one at a time with instruments rather than reasoning.

**Owner priorities, stated explicitly:** gridlock ≫ everything else — *"permanent gridlocks with no
automatic resolution are a show stopper"*. Normal-traffic junction overlaps and lateral lane changes in
high-realism zones come next. The evac pusher overlap (§5) is explicitly deprioritised. **Gridlock is
global, not zone-scoped** — an earlier suggestion to zone-scope it was wrong and is withdrawn.

## 2. Status of the four defects

| defect | status |
|---|---|
| Cars driving **through** each other at a junction | **FIXED, shipping default-ON.** Root cause: the omitted `myInternalLinkFoes` half of `MSLink::setRequestInformation`. Our trajectory is now byte-identical to SUMO across the traced hold-and-release. Overlap **events** on the repro: 12 751 → 313 (−97.5%) |
| Two cars **overlapping stopped** in a junction | **FIXED** — same mechanism |
| **Gridlock** | **FIXED on L2** (450 arrived / 0 running, identical to honest SUMO). **L1 root-caused but NOT fixed** — see §4, this is the next task |
| **Lateral lane change while stopped at red** | **MEASURED, not fixed.** We do it 2.5–3× as often as SUMO (83 vs 33 on L2; 53 vs 17 on L2-light). The *overlap* half does not reproduce on this net at all — 0 in both engines |

## 3. What shipped (engine changes, all on this branch)

| flag | default | what it does |
|---|---|---|
| `Engine.InternalJunctionApproachArm` | **ON** | The `myInternalLinkFoes` approach arm — holds a cont-bay vehicle against a foe *approaching* the conflict lane, not only one standing on it |
| `Engine.BayExitLaneKeepClear` | **ON** | `checkRewindLinkLanes`: don't release a vehicle from a cont bay when its own **exit lane** cannot accept it (needs `Length + MinGap` of room) |
| `Engine.BayExitLaneKeepClearExtra` | `-1` (= MinGap) | Threshold knob. Swept: the gridlock fix survives to 0.5 m, dies at 0.0. A **smaller** threshold does **not** recover throughput (city-mixed-1k 1001 → 985) |
| `Engine.JunctionPhysicalOccupancyGate` | OFF | **Do not pair it with the above.** Measured 4× counterproductive; the pair takes L2 from 320 arrived down to **61** |

Accepted cost, owner-approved: ~1% arrivals on two organic city nets (`city-mixed-1k` 1014 → 1001,
`city-organic` 509 → 499), in exchange for eliminating a total deadlock.

## 4. ⭐ THE NEXT TASK — `KeepClearConstraint` does not fire when its own condition holds

**This is the highest-value item and it has an exact repro.**

`KeepClearConstraint` (`Engine.cs:~7719`) already implements the box-block check — the downstream
available-space walk (`LaneBruttoVehLenSum` / `LaneSpaceTillLastStanding`) and a brake to the entry stop
line. **It applies to the wedged links** (its only scope gate is `request.Foes.Contains('1')`, and all
three wedged links have crossing foes). **And it never binds.**

Measured, `scenarios/_diag/junction-realism-L1`, vehicle `f_cyc_cw2.30` entering `:J01_10_0`:

| t | exit lane `h1_0` | ego |
|---|---|---|
| 405–407 | **9 vehicles, all stopped, nearest at pos 4.21** | on approach `in_W01_0`, accelerating |
| **408** | unchanged | **enters the junction** |
| 409 → end | unchanged | stopped at pos 3.61 **forever** |

Ego needs `Length + MinGap` = **7.5 m**; the space was **4.21 m**, unchanged for four steps before
entry. `keepClear` bound **zero times** for this vehicle over its whole life.

**Two testable candidates:**
1. `LaneSpaceTillLastStanding` never sets `foundStopped` → the `!foundStopped` early-out returns
   `+infinity` regardless of how little space there is;
2. `seenSpace` comes out too large (wrong end of the exit lane, or the internal lanes' brutto sum not
   subtracted as intended).

**How to start:** instrument the walk's intermediates at that ONE step (`seenSpace`, `foundStopped`,
each lane's contribution). Do not add a second box-block check — two overlapping guards make future
attribution ambiguous, and this session paid for that twice already.

⚠ **Blast radius.** `KeepClearConstraint` is **default-on, unconditional, parity-relevant** engine code
behind no gate. If it under-fires here it plausibly under-fires everywhere, which makes it a likely
contributor to the junction wedges still present on the `city-*` nets — a bigger prize than L1 alone.
It also means **goldens will probably move**: plan the SUMO diff up front, per CLAUDE.md.

**Success condition, fixed in advance:** L1 `arrived` moves materially above **112** (SUMO: 450). If it
does not, the mechanism is named correctly but is not the binding one — **publish that null**, as five
earlier hypotheses were.

## 5. Remaining backlog, in owner priority order

1. **§4 above.**
2. **Normal-traffic junction overlaps on the real nets.** `city-mixed-1k` still shows 10 peak
   overlapping pairs, `city-3000` 6, `city-organic` 5. Untraced. Likely the same box-block family.
3. **Lateral lane change while stopped.** Two separable halves:
   - **frequency** (reproduced): 83 vs SUMO's 33. The gap is in our lane-change **trigger while
     stationary**, not the manoeuvre. `Engine.LaneChangeMinSpeed` is **0** on the parity path, so
     nothing suppresses a change at zero speed — *hypothesis, not a finding; instrument it*.
     ⚠ Note the junction fixes made this **worse** (47 → 83): more held vehicles ⇒ more sideways slides.
   - **into an occupied lane** (NOT reproduced here): build the minimal repro
     `docs/SUMOSHARP-ISSUE-stopped-lane-change-overlap.md` §5 specifies.
4. **Pedestrian amplifier** (owner's hypothesis: a ped on the exit crossing holds a car inside the
   junction). **Cannot be tested through `Sim.Run` — it has no ped coupling at all**; needs a
   `LiveCitySim` harness plus crossings and ped demand on the repro net.
5. **Deprioritised:** `docs/NEED-evac-pusher-orca-pairwise-overlap.md` — a 0.463 m overlap between two
   evac ORCA pushers. Its test is `[Fact(Skip=…)]` with the threshold **UNCHANGED**. Do not "fix" it by
   relaxing the threshold; it measures a real overlap.
6. **Housekeeping:** `TASKS-TODO.md` states the parity law as 775/4; measured is **776/5**. Don't edit
   the literal — **single-source** it the way `EnvGateDocumentationTests` single-sources the env-gate
   table, so it cannot rot again.

## 6. Instruments (all committed — use these, do not re-derive)

| tool | what it answers |
|---|---|
| `scripts/gen-junction-realism-net.py` | regenerates the repro nets; header explains the demand calibration |
| `scripts/analyze-junction-realism-fcd.py` | onset, dwell, wedge/overlap causal order, OBB overlap pairs. Runs on **either** engine's FCD |
| `scripts/run-net-regression.py` | the 26-net battery. `--compare <baseline>` prints regressions |
| `scripts/detect-stopped-lane-change.py` | stopped sideways lane changes + whether they landed overlapping |
| `Sim.Run --binder-log PATH` | per-vehicle per-step CSV: `t,veh,lane,pos,speed,binder,binderName,jyArm,jyGreen,blocker` — **the single most useful instrument here** |
| `tests/…/EvacPusherOverlapDiagTests.cs` | always-passing report on evac pusher separation |

Baselines: `docs/reports/net-regression-{baseline,approach-arm,bay-exit-keepclear}.txt`.
**Always measure both arms with the same script** — cross-instrument numbers are invalid.

Binder legend: 0 none · 1 leaderFollow · 2 crossJxnLeader · 3 freeFlow · 4 successiveLane ·
5 deadLaneMerge · 6 stopLine · 7 redLight · 8 railSignal · 9 railCrossing · **10 junctionYield** ·
**11 keepClear** · 12 obstacle · 13 crowd · **14 internalJunctionAdmission** · 15 colocationSymmetryBreak ·
16 crowdYield · **17 internalJunctionApproachArm**.
`jyArm` (only meaningful when binder==10): 1 cycleHold · 2 cautiousApproach · **3 sameTargetMerge** ·
4 externalAgent · **5 onJunctionLeader** · 6 approachingCross.

## 7. Traps this session actually hit — read before trusting a number

1. **A diagnostic that never fires is evidence about the GUARD, not the hazard.** `keepClear` = 0 of 226
   was read as "not the mechanism". It was the opposite: the guard is broken. Cost: one wrong conclusion.
2. **A field that is constant across every sample is probably not wired.** `jyArm` read 0 for 100% of
   15 739 rows because a constructor argument was silently dropped. I argued myself out of the right
   instinct with a plausible code-reading.
3. **Scripted text replaces fail SILENTLY.** That dropped argument came from a `python replace` whose
   anchor no longer matched. **Assert every anchor.**
4. **Incidence ≠ duration.** "Overlap events +16%" (pair×step) looked like a regression; distinct
   overlapping pairs were 92 → 93, i.e. flat. And peak-simultaneous *understated* the same fix 20×.
   **Report both, never let one stand for the other.**
5. **A snapshot wait-for graph cannot see a cycle that a periodic constraint masks.** The first cycle
   looked like a tree rooted at a red light; the closing edge was invisible ~54% of samples.
6. **Check whether the guard already exists before building one.** Two of the most valuable moves this
   session were *not* writing code.

**Scoreboard, kept honestly: nine wrong hypotheses, five instrument/process defects.** Every real result
came from an instrument; the instruments needed fixing about as often as the engine did. Record
predictions *before* measuring — several would otherwise have been quietly revised afterwards.

## 8. Doc map

- `JUNCTION-REALISM-SESSION-JOURNAL.md` — append-only BEFORE/AFTER entries, 16 of them. **The full trail.**
- `JUNCTION-REALISM-TRACE-FINDINGS.md` — the measured characterisation (§5 lists what is *not* established).
- `JUNCTION-APPROACH-ARM-{DESIGN,TASKS,TRACKER}.md` — the design-first trio for the shipped approach arm.
- `NEED-evac-pusher-orca-pairwise-overlap.md` — the parked evac defect.
- `ENV-GATES.md` — every gate, including the four added here. Completeness is test-enforced.
