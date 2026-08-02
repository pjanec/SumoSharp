# SUMOPED — Parity coverage plan

**Status: PROPOSAL — awaiting owner sign-off.**

How we know the golden set actually covers SUMO's pedestrian behaviour, rather than merely containing
some pedestrians. The mechanism being covered is explained in `SUMOPED-ALGORITHM.md`, whose §4.5 lists
the knobs this set does **not** witness. Companion to `SUMOPED-REQUIREMENTS.md` (WHAT), `SUMOPED-DESIGN.md` (HOW),
`SUMOPED-TASKS.md`, `SUMOPED-TRACKER.md`.

The oracle is **vanilla SUMO 1.20.0 only** — no hand-authored expectations, no reference implementation
other than SUMO itself. Every number below was measured first-hand this session (§2).

---

## 1. The coverage claim, and how it is made falsifiable

"We have enough goldens" is not assertable. It is made checkable by three independent mechanisms, none
of which is sufficient alone:

1. **A branch inventory** (`SUMOPED-BRANCH-INVENTORY.md`, task SP-0.0) — every behavioural branch in
   `MSPModel_Striping` + the ped-relevant parts of `MSLink`/`MSLane`, each with a stable ID, its C++
   predicate, and whether it is observable in FCD. Derived from the source, not from imagination.
   **A first pass exists: 148 branch rows**, plus sections listing the FCD-`HIDDEN` branches with the
   cheapest oracle signal for each, the branches that need saturation, and the branches that need
   multi-lane roads or wide crossings. It needs review, not authoring.
2. **A branch→scenario matrix** (§4) — every inventory ID names at least one scenario that fires it and
   the oracle signal that witnesses it. An ID with no witnessing scenario is an admitted coverage hole,
   listed as such, not quietly absent.
3. **A coverage-witness counter in the port** (§5) — each ported branch increments a named counter; a
   test asserts the `_sumoped` suite hits every counter, and that each scenario fires the branches it
   claims. This runs in the offline loop with no SUMO.

**These prove different things and the distinction matters.** (2) and (3) prove a branch is
*exercised*; only the golden comparison proves it is *correct*. A hit counter with a passing golden is
coverage; a hit counter alone is theatre. The tasks state both.

---

## 2. What vanilla SUMO actually gives us — seven person-bearing outputs

I originally scoped this on person FCD alone. That was too narrow. Measured on a 300 s saturated
2-lane signalized junction (4 car flows, 4 personFlows, 460 persons loaded):

| output | person content | size @300 s | role |
| --- | --- | --- | --- |
| `--fcd-output` | `<person id x y angle speed pos edge slope/>` per step | 5.26 MB (3.76 MB attr-trimmed) | **primary exact trajectory** |
| `--netstate-dump` | `<edge id><person id pos angle stage/></edge>` per step | 3.57 MB | per-**edge membership** + `stage` |
| `--person-summary-output` | per-step `loaded inserted walking waitingForRide riding stopping jammed ended arrived teleports` | **54 KB** | per-step time series, incl. **`jammed`** |
| `--personinfo-output` | `<personinfo>` + per-stage `<walk>` | 113 KB | per-person aggregate |
| `--statistic-output` | `<persons loaded running jammed/>`, `<personTeleports/>`, `<pedestrianStatistics number routeLength duration timeLoss/>` | **2.4 KB** | whole-run aggregate |
| `--collision-output` | `<collision time type lane pos collider victim colliderType victimType colliderSpeed victimSpeed/>` | varies | **the vehicle↔ped oracle** |
| stderr warnings | `Person 'X' is jammed on edge 'Y', time=Z` · `Vehicle 'V' collision with person 'P', lane=..., time=...` | small | per-**event** witness for FCD-hidden branches |

`--tripinfo-output` also emits `<personinfo>`, redundantly with `--personinfo-output`.

Three facts that shape the plan:

- **`intermodal-collision.action` defaults to `warn`** (`MSFrame.cpp:382`), so ped/vehicle collision
  detection is on by default. An empty `collision-output` is a real negative result, not an unarmed check.
- **`--save-state` writes zero persons** — no init cross-check for persons, unlike vehicles.
- **`posLat` is not emitted for persons even when explicitly requested** in `--fcd-output.attributes`.
  Lateral state must be back-derived — but it *is* fully derivable, see §2.1.

`--person-summary-output` + `--statistic-output` are the discovery that makes large scenarios
affordable: **56 KB** buys a full-horizon, per-step, exactly-comparable witness of a 300 s saturated
run, including the jam counter.

### 2.1 The FCD row is very nearly a complete `PState` observation

`posLat` is absent, which initially looked like a serious observability gap. It is not.
`PState::getAngle` (`MSPModel_Striping.cpp:2342-2349`) returns

```cpp
angle = shape.rotationAtOffset(geomX) + (myDir == BACKWARD ? M_PI : 0);
angle += (myDir == BACKWARD ? +1 : -1) * atan2(mySpeedLat, MAX2(mySpeed, NUMERICAL_EPS));
```

so the FCD `angle` attribute **directly encodes `mySpeedLat`**. Verified by inverting it —
`|mySpeedLat| = speed * tan(angle - laneBearing)` — over 1805 crossing samples in the saturated run:

```
max derived |mySpeedLat| = 0.6401 m/s   <- exactly the stripeWidth (0.64) clamp on maxYSpeed
strong second mode       = 0.5556 m/s   <- exactly vMax * LATERAL_SPEED_FACTOR (1.3889 x 0.4)
```

Both of the model's theoretical lateral-speed caps land on the nose, which validates the inversion.

**What a single FCD row therefore yields:**

| `PState` field | recovered from |
| --- | --- |
| `myRelX` | `pos` (direct) |
| `myRelY` | project `(x, y)` onto the lane centreline |
| `mySpeed` | `speed` (direct) |
| `mySpeedLat` | `angle`, inverted as above — **verified** |
| `myLane` / edge | `edge` (direct, internal ids included) |
| `myDir` | sign of `pos` progression / the `angle` branch |
| `myWaitingTime` | largely derivable from consecutive `speed < 0.1` steps |
| `myAmJammed` | **not in FCD** — but `--person-summary-output`'s `jammed` column and the stderr warning |
| `myWaitingToEnter`, `myNLI`, `myWalkingAreaPath` | **not observable** |

Consequences for the plan: **`angle` is a first-class compared attribute at tight tolerance** (it is
the lateral-velocity witness, not a cosmetic heading), and every branch the inventory marks `LATERAL`
should be read as *observable*, not weakly observable. Only five `PState` fields are genuinely
unobserved, and the most behaviourally significant of them has its own counter.

---

## 3. The three-tier ladder

Two hard constraints set the shape. First, a saturated golden diverges catastrophically on the first
tiny mismatch, so debugging one tells you nothing — you must never debug at Tier C what a Tier A
scenario would have caught. Second, the repo's committed FCD goldens total **5.1 MB** today, largest
single **1.26 MB**; a naive 300 s saturated person FCD is 5.26 MB and would double that alone.

Exactness is *not* the thing that degrades with tier — determinism holds at saturation (§3.4). What
degrades is how much of the run we can afford to store.

### Tier A — micro. Exact FCD, full horizon.
1–4 peds, ≤80 steps, ~10–50 KB each. **One mechanism per scenario**, so a failure localizes to one
branch. Roughly 20 scenarios. This is where nearly all debugging happens.

### Tier B — meso. Exact FCD, full horizon.
10–40 peds plus vehicles, 120–200 steps, ~150–400 KB each. Crowd formation, curb accumulation, abreast
crossing, vehicle coupling. Roughly 8 scenarios. Measured Tier-B candidate: 25 peds / 120 s /
1793 person rows = **281 KB** FCD + 21 KB person-summary.

### Tier C — macro. Windowed exact FCD + full-horizon aggregates.
300+ persons, saturated multi-lane junction, 300 s. 2–3 scenarios. Committed shape, measured:

```
--device.fcd.begin 200, trimmed attributes   1.45 MB   (verified: FCD starts at t=200.000)
--person-summary-output   full horizon         54 KB
--personinfo-output       full horizon        113 KB
--statistic-output + --collision-output        ~3 KB
                                       TOTAL  ~1.6 MB   -- one existing large vehicle golden
```

The run still starts at t=0; only the window is committed and compared. Saturation is reached by
t≈80 (steady state ~110–140 concurrently walking), so a window at 200–300 is well inside it.

### 3.4 Exactness holds at saturation — measured, not assumed
The 300 s saturated run produced **10,068 vehicle rows + 30,549 person rows**, and two independent
runs gave **byte-identical FCD bodies** (the only diff was the config echo naming a different output
file). Exact parity is a legitimate bar at Tier C; the reason to window is storage, not chaos.

---

## 4. The scenario matrix — the axes that must be varied

Coverage is a product of axes, not a list of scenarios. **Eight** axes, each with a value that fires
branches the others cannot. (Six of them were identified from the source; **crossing priority** and
**car movement** were found later, by *rendering* the goldens and looking — §4.3 and §4.5. That is worth
remembering about how the last two got here.)

| axis | values | why this axis exists |
| --- | --- | --- |
| **crossing width** (stripe count) | 1 · 6 (netconvert default) · 12 | `--default.crossing-width` is **independent of road lanes** — 4.00 m ⇒ 6 stripes on a 1-lane and a 2-lane road alike. A **1-stripe** crossing is the only way to reach the `sMax == 0` / `jamTimeNarrow` branch, which no realistic net produces but the port must still match. |
| **crossing length** (road lanes) | 1 lane (6.40 m) · 2 lanes (12.80 m) · 3 lanes | Long crossings let a vehicle arrive while peds are mid-crossing; short ones never do. |
| **control** | priority (uncontrolled) · TL · bare walkingarea (no marked crossing) | Three different vehicle-yield paths: `blockedAtDist` under right-of-way, under TL state, and `checkWalkingAreaFoe`'s 2-D test. |
| **crossing priority** | `priority="false"` (ped yields) · `priority="true"` (**zebra — car yields**) | ⚠ Not a nuance — it inverts who gives way, and `--crossings.guess` only ever produces the first at an uncontrolled node. See §4.5. |
| **ped demand** | single · counterflow pair · platoon · saturated · **jammed** | The jam family only fires above a density threshold. |
| **vehicle demand** | none · single · stream · saturated | "None" is essential: Tier A junction scenarios must have no vehicles so a divergence has exactly one cause. |
| **ped flow mix** | unidirectional · counterflow **on a sidewalk** · counterflow **on a crossing** · **pass-by** (turns at the junction, does not cross) | Three separate cases, see §4.2 — crossing counterflow does **not** happen by default and must be forced. Pass-by is the owner's R3d and needs peds who never enter a crossing. |
| **car movement** | straight · **right turn** · **left turn** | A turning car yields to peds on the crossing over its **EXIT** edge, holding *on the internal lane inside the junction* rather than at the stop line. Straight-through flows never exercise this. See §4.3. |

Every Tier A/B scenario states, in its `NOTES.md`, which axis value it pins and which branch IDs it
claims to fire. That claim is checked mechanically by §5.

### 4.2 ⚠ Crossing counterflow does NOT occur by default — a coverage hole found by rendering

Measured on a dense uncontrolled 4-arm junction with peds crossing both ways on every arm, counting
each crossing's traversals by the sign of `pos` progression:

```
:c_c0:  0 peds +pos,  56 -pos          steps with BOTH directions on the same crossing: NONE
:c_c1:  5 peds +pos,  31 -pos
:c_c3:  0 peds +pos,   5 -pos          (:c_c2 never used at all)
```

**Every crossing is unidirectional.** The junction-local ped router (design §5.5) sends opposing
streams around the junction the same way, so they use *different* crossings. A scenario set built by
"add lots of pedestrians going both ways" would therefore never exercise counterflow-on-a-crossing —
the `mergeObstacles` oncoming path, `ONCOMING_CONFLICT_PENALTY`, the reserved-oncoming band on a
junction lane, and `jamTimeCrossing` under head-on pressure would all sit untested while looking
thoroughly covered.

Forcing it needs peds routed across **one arm** in both directions (both sidewalks of the same road,
depart/arrival positions set near the junction so the router cannot go around the far node instead).
Measured with that fix, moderate rate: `:c_c0` 36 peds +pos / 36 −pos, **43 steps with both directions
simultaneously**, busiest 50 peds on the crossing at once (24 vs 26 opposing), all arrive, zero jams,
zero collisions. At 2.5× the rate it deadlocks: first arrival only at t≈230, jam count climbing to 57
as squeeze-through breaks the standoff — still zero vehicle-ped collisions.

Sidewalk counterflow, by contrast, works with the obvious demand and self-organises cleanly: on a 4 m
(6-stripe) sidewalk with 75 peds each way, the two streams separate into lanes at **y = −6.72** and
**y = −3.52** and hold that 3.2 m separation for the whole run; the same result holds at 214 peds
concurrent on a 6 m (9-stripe) sidewalk.

### 4.2b Ped turners threading the waiting bunch (R3d, the sharpest form)

A pedestrian that **turns at the corner and stays on the sidewalk** (no crossing) must pass through the
walkingarea where other peds are queued waiting for a gap. Corner-turn routes with **zero crossings**
exist at a 4-arm junction — `nc → :c_w0 → cw`, `ec → :c_w1 → cn`, `wc → :c_w3 → cs` — and are the
cleanest witness for R3d.

Measured, moderate density (car flow gaps present):

```
TURNERS : 1428 ped-steps on walkingareas,  411 stopped (29%)
crossers:  511 ped-steps on walkingareas,  152 stopped (30%)
turner cells on :c_w0 also used by a waiter:  2%
turner cells on :c_w1 also used by a waiter:  4%
```
Turners are no more delayed than the crossers and occupy almost entirely *different ground* — the model
routes them **around** the cluster. At 2.4× the car flow the same scenario degrades into corner
gridlock: turners stopped 76% of the time and sharing 77% of their ground on `:c_w3` with waiters.
The degradation is continuous with density, not a switch.

⚠ **Metric warning, learned the hard way here.** Conditioning "turner stopped %" on *steps where ≥3
peds are already stopped on that walkingarea* reports 79–95% stopped and looks like total failure —
because the condition selects the congested moments. The unconditioned figure is 29%. Any
"is it flowing" metric for this behaviour must be unconditioned, or it measures its own selector.

### 4.3 Turning cars blocked by peds on the EXIT crossing

A car turning left or right commits into the junction and must then yield to pedestrians on the
crossing over the edge it is *leaving by* — so it holds **on the internal lane, inside the junction
box**, not at the stop line. Straight-through demand never produces this.

Measured on an uncontrolled 2-lane junction with every car flow turning (three arms, both directions)
and peds crossing all four arms both ways, counting vehicle-steps where a stopped car on an internal
lane had peds on its exit crossing:

```
RIGHT turns blocked:  80 vehicle-steps
LEFT  turns blocked:  57 vehicle-steps
e.g. eRIGHT.15 (ec->cn, right turn) held on internal lane :c_4_0 from t=84 to t=89, 9 peds on :c_c0
```

### 4.5 ⚠ Crossing priority — `--crossings.guess` never produces a ped-priority zebra

`NBNode.cpp:2788` / `:2831`: a guessed crossing is created with
`addCrossing(candidates, UNSPECIFIED_WIDTH, isTLControlled())` — its **priority is
`isTLControlled()`**. So at an *uncontrolled* node, `--crossings.guess` always yields
`priority="false"`: the walkingarea→crossing link is state `m` (minor) and **the pedestrian gives way
to traffic**. Every scenario built with `--crossings.guess` at a priority node therefore shows peds
waiting for a gap, and the zebra / ped-right-of-way case is *silently absent*.

Getting it requires declaring crossings explicitly in the connections file:
```xml
<connections>
  <crossing node="c" edges="nc cn" priority="true"/>
</connections>
```
which flips the link to state `M` (major). Verified at the net level on both variants.

**The behavioural A/B, one car and one pedestrian, identical except for that boolean:**

```
priority="false"  (guessed)                    priority="true"  (declared)
 t=3  ped :c_w1 0.08  1.39   car 13.89          t=3  ped :c_w1 0.08  1.39   car 13.89
 t=4  ped :c_w1 0.00  0.07   car 13.89          t=4  ped :c_c1 11.49 1.39   car 13.89
 t=5  ped :c_w1 0.00  0.00   car 10.78          t=5  ped :c_c1 10.10 1.39   car 10.78
 t=6  ped :c_w1 0.00  0.00   car  6.28          t=6  ped :c_c1  8.71 1.39   car  6.28
 t=7  ped :c_w1 0.00  0.00   car  8.88 <-GOES   t=9  ped :c_c1  4.54 1.39   car  2.15
 t=10 ped :c_c1 11.41 1.39   car 13.89          t=10 ped :c_c1  3.15 1.39   car  0.00 <-STOPS
                                                t=12 ped :c_c1  0.38 1.39   car  0.00
     PED WAITS, CAR PROCEEDS                    t=13 ped :c_w2  2.23 1.39   car  2.60 <-resumes
                                                     CAR STOPS, PED NEVER BREAKS STRIDE
```

At flow density the inversion is total (same demand, same net geometry, only the flag differs):

| | cars fully stopped for a ped | peds stopped on the curb | throughput |
| --- | --- | --- | --- |
| `priority="false"` | 67 veh-steps, 18 vehicles | **91%** of walkingarea steps | all 109 vehicles clear; 54 of 65 peds still waiting |
| `priority="true"` (balanced) | 5783 veh-steps, **68 vehicles** | **0%** (0 of 245) | 109 vehicles clear, 17 queued; 4 collisions |
| `priority="true"` (ped-heavy) | 8611 veh-steps, 68 vehicles | 0% (2 of 438) | only 69 inserted, **45 still queued** — cars starved |

Both regimes must be in the golden set. `blockedAtDist`'s second clause
(`leaderFrontDist <= oncomingGap && ped.myWaitingTime < TIME2STEPS(2.0)`, `jmCrossingGap` default
**10 m**, `MSLink.cpp:66`) is what makes a car brake for an *approaching* ped — and the 2 s standing
rule is what releases it again in the `priority="false"` regime. Only the priority regime exercises
the sustained full-stop path.

### 4.1 Measured regime map — width drives the jam/collision regime
Same jam-level demand, 200 s, varying only crossing width:

| crossing width | stripes | jam events | veh↔ped collisions |
| --- | --- | --- | --- |
| 0.64 m | 1 | 144 | **42** |
| 4.00 m (default) | 6 | 175 (@300 s) | 33 (@300 s) |
| 8.00 m | 12 | 168 | **1** |

So width is not a cosmetic axis — it selects which failure modes the model exhibits at all.

---

## 4.4 Renders of the oracle

`scripts/render-ped-fcd.py` turns any `(net.net.xml, fcd.xml)` pair into a self-contained HTML replay
by emitting the **exact** payload schema of `src/Sim.Viz/Payload.cs` and splicing it into the real
`src/Sim.Viz/template.{html,js}` through the same two markers `VizHtml.Write` uses. It is not a second
renderer — it is the repo's own player, fed from a golden. `--dir-split` colours pedestrians by
travel direction so counterflow reads at a glance; `--crop`/`--crop-junction` only set the initial
camera (the player has zoom/pan).

When the C# person model exists this becomes the **ground-truth layer** of the parity overlay
(task SP-7.3): SumoSharp's peds drawn live, SUMO's drawn as rings, same frame.

---

## 5. Coverage witnesses in the port

`StripingParams`-adjacent, a `BranchCounters` struct with one counter per inventory ID, incremented at
the branch site, compiled in always (they are increments on a preallocated array; the person model is
already gated off by default, so there is no cost on the vehicle path).

Two tests:
- **`AllBranchesCoveredTest`** — running the whole `_sumoped` suite hits every counter ≥ 1, or names the
  misses. A miss is either a missing scenario or an admitted hole in §6.
- **`PerScenarioClaimTest`** — each scenario's `NOTES.md` branch-ID claim list matches the counters it
  actually fires. This catches a scenario that silently stops exercising what it was authored for.

On the SUMO side we cannot instrument, so the oracle-side witnesses are the proxies in §2: the
`jammed` counters, the stderr warning strings, `<collision>` records, and assertions derived from FCD
geometry (stripe occupancy, abreast entry, no-stall).

---

## 6. Vehicle↔pedestrian collisions: the honest position

**Correction to `SUMOPED-REQUIREMENTS.md` R5 as first drafted.** R5 said "no vehicle body may overlap a
pedestrian's footprint at any step". That is **not true of the oracle**, so it cannot be a parity
requirement. Measured, at jam density (personFlow `period="0.5"` ×2, car `period="2"` ×2, 300 s):

```
<persons loaded="1200" running="365" jammed="175"/>      175 of 370 walking are JAMMED
80 <collision> records, 29 distinct (collider,victim) pairs, all type="crossing"
colliderSpeed: min 0.00, max 2.60 -- only 1 of 80 above 0.1 m/s
```

The cause is the model's own squeeze-through: once `myWaitingTime > jamTimeCrossing` (10 s),
`myAmJammed = true` and `xSpeed = vMax/4` **ignoring the usual collision gating**
(`MSPModel_Striping.cpp:2200-2215`), and jammed peds stop being obstacles for others. This is SUMO
choosing deadlock-avoidance over physical plausibility — the same class as CLAUDE.md §Measurement
discipline item 11, except it is the *model*, not a default flag.

Note the nuance: essentially every collision is a **stopped** car (`colliderSpeed 0.00`) enveloped by a
jammed crowd, not a car driving through a pedestrian. Exactly one of 80 had the car moving above
0.1 m/s.

**R5 therefore becomes three separate things, in order:**

- **R5a (Phase 1, parity).** Reproduce SUMO's collision set **exactly**: the committed
  `golden.collisions.xml` must match on `(time, type, lane, pos, collider, victim, colliderSpeed,
  victimSpeed)`. This is a *stronger* test than "we never collide" — it pins the jam-squeeze behaviour
  to the tick.
- **R5b (Phase 1, measurement).** Collision count, distinct-pair count, and max `colliderSpeed` are
  committed per scenario in the tracker as first-class metrics. This is the **baseline** the later
  improvement is measured against; without it, "we made it better" is unfalsifiable.
- **R5c (later, own design).** Reduce collisions below SUMO's. This is a deliberate *deviation* from
  parity and is therefore governed by `docs/CONSTRAINT-high-realism-artefact-ladder.md` — target SUMO's
  flow, never its method. It must be gated behind an explicit opt-in so the parity goldens stay
  reachable, exactly like every other fast-mode/realism flag in the engine. **Not Phase 1.**

Tier A and Tier B scenarios do produce **zero** collisions, verified — so "cars do not hit pedestrians"
remains a hard invariant at realistic density, and only degrades in the pathological jam regime.

---

## 7. The owner's crowd behaviours — measured in the oracle before being required of us

Saturated 2-lane TL junction, `dawdling=0`, crossing `:c_c1` (4.00 m = 6 stripes, 12.80 m long),
lateral offset back-derived from `x/y` by projection onto the crossing centreline:

```
MAX distinct stripes simultaneously occupied : 6 of 6   at t=100  (32 peds on the crossing)
MAX peds simultaneously STOPPED on :c_w1     : 25       at t=133
speeds on the crossing at t=100              : min 0.000  median 1.198  max 1.389
```

- **R3a** (accumulation, no overlap) — 25 peds stopped on one walkingarea. Real and measurable.
- **R3b** (abreast, not single-file) — the horde occupies **all 6 stripes**. Real and measurable.
- **R3c** — and a genuine correction to `SUMOPED-REQUIREMENTS.md` R11: **speed heterogeneity appears
  with `dawdling=0` and `speedDev=0`** (min 0.000 / median 1.198 / max 1.389). It emerges from the
  interaction dynamics — peds slowing for each other — not only from RNG. So the "members move at
  different speeds" look is **substantially on the exact-parity side**, not wholly deferred to the
  production regime as R11 implied. R11's two-regime split still stands for the *additional* spread
  that `dawdling`/`speedDev` provide, but the core of the effect is golden-checkable.

These are computed by the same helper on both arms (§5), and asserted against the **oracle** first
(task SP-0.4): a scenario whose golden does not show the behaviour is mis-authored and goes back for
re-authoring, not worked around.

---

## 8. Admitted coverage holes

Filled in as the branch inventory (SP-0.0) is matched against the matrix. A branch listed here is one we
consciously choose not to witness in Phase 1, with a reason. Candidates known now:

- Branches reachable only via TraCI/`moveToXY` (Requirement R-N5).
- `--no-internal-links` straight-line junction-distance fallback (R-N6).
- `MSLCM_SL2015` sublane ped checks (R-N7).
- Stop/`arrivalPos`-full "blocked" arrival obstacle — needs a `<stop>`-anchored walk, which is
  personTrip-adjacent scope.
- **`--pedestrian.striping.reserve-oncoming`** (normal lanes; default 0.0) — measured inert in every
  configuration tried (symmetric and asymmetric counterflow, 2/4/6 m sidewalks, with and without
  dawdling). The rule codifies segregation that already emerges, so nothing observable changes.
  `SUMOPED-ALGORITHM.md` §4.3 has the mechanism. Port it, but do not expect a golden to witness it.
- Knobs needing a purpose-built scenario before they can be witnessed at all:
  `jamtime.narrow` (1-stripe lane), `jmDriveAfterRedTime` on either vType (TL scenario with a red
  phase), `legacy-departposlat`, `walkingarea-detail=2`. `SUMOPED-ALGORITHM.md` §4.5.

Anything else that lands here needs owner sign-off, because an unwitnessed branch is a place the port
can be silently wrong.
