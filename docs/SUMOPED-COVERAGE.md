# SUMOPED — Parity coverage plan

**Status: PROPOSAL — awaiting owner sign-off.**

How we know the golden set actually covers SUMO's pedestrian behaviour, rather than merely containing
some pedestrians. Companion to `SUMOPED-REQUIREMENTS.md` (WHAT), `SUMOPED-DESIGN.md` (HOW),
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
   **A first pass exists: 149 branch rows**, plus sections listing the FCD-`HIDDEN` branches with the
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
  World `x/y` is therefore the *only* lateral witness, and stripe index must be back-derived by
  projecting `(x,y)` onto the lane centreline. `x`/`y` must be compared attributes at tight tolerance;
  they are not redundant with `pos`/`edge`.

`--person-summary-output` + `--statistic-output` are the discovery that makes large scenarios
affordable: **56 KB** buys a full-horizon, per-step, exactly-comparable witness of a 300 s saturated
run, including the jam counter.

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

Coverage is a product of axes, not a list of scenarios. Six axes, each with a value that fires branches
the others cannot:

| axis | values | why this axis exists |
| --- | --- | --- |
| **crossing width** (stripe count) | 1 · 6 (netconvert default) · 12 | `--default.crossing-width` is **independent of road lanes** — 4.00 m ⇒ 6 stripes on a 1-lane and a 2-lane road alike. A **1-stripe** crossing is the only way to reach the `sMax == 0` / `jamTimeNarrow` branch, which no realistic net produces but the port must still match. |
| **crossing length** (road lanes) | 1 lane (6.40 m) · 2 lanes (12.80 m) · 3 lanes | Long crossings let a vehicle arrive while peds are mid-crossing; short ones never do. |
| **control** | priority (uncontrolled) · TL · bare walkingarea (no marked crossing) | Three different vehicle-yield paths: `blockedAtDist` under right-of-way, under TL state, and `checkWalkingAreaFoe`'s 2-D test. |
| **ped demand** | single · counterflow pair · platoon · saturated · **jammed** | The jam family only fires above a density threshold. |
| **vehicle demand** | none · single · stream · saturated | "None" is essential: Tier A junction scenarios must have no vehicles so a divergence has exactly one cause. |
| **ped flow mix** | unidirectional · counterflow · **pass-by** (turns at the junction, does not cross) | Pass-by is the owner's R3d and needs peds who never enter a crossing. |

Every Tier A/B scenario states, in its `NOTES.md`, which axis value it pins and which branch IDs it
claims to fire. That claim is checked mechanically by §5.

### 4.1 Measured regime map — width drives the jam/collision regime
Same jam-level demand, 200 s, varying only crossing width:

| crossing width | stripes | jam events | veh↔ped collisions |
| --- | --- | --- | --- |
| 0.64 m | 1 | 144 | **42** |
| 4.00 m (default) | 6 | 175 (@300 s) | 33 (@300 s) |
| 8.00 m | 12 | 168 | **1** |

So width is not a cosmetic axis — it selects which failure modes the model exhibits at all.

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

Anything else that lands here needs owner sign-off, because an unwitnessed branch is a place the port
can be silently wrong.
