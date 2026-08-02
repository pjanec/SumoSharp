# SUMOPED — What we are porting: the striping algorithm and its knobs

**Status: PROPOSAL companion — reference material, not a plan.**

`SUMOPED-DESIGN.md` says *how* we will port. `SUMOPED-BRANCH-INVENTORY.md` says *what the branches
are*. This document says **what the model actually does and what each tuning knob is worth**, so the
port is written by someone who understands the mechanism rather than transcribing it.

Two halves:
- **§1–3** the algorithm, explained — the shape of the model and why each piece produces the behaviour
  in the goldens.
- **§4** the knobs, **measured** — every tunable SUMO 1.20.0 exposes, its verified default, and the
  behavioural delta from actually sweeping it against the oracle (`scripts/sumoped-knob-sweep.py`).

Everything here is against `/sumo` at tag `v1_20_0`.

---

## 1. The shape of the model in one page

**It is a lane model, not a crowd model.** A pedestrian's authoritative state is
`(lane, myRelX, myRelY, myDir)` — longitudinal offset along a lane, plus a lateral offset measured
from the lane's **left** edge, discretised into **stripes** of `stripeWidth = 0.64 m`. World `x/y` is
derived only for output. There is no continuous 2-D collision resolution anywhere.

Each step, for each pedestrian:

1. Everything that could matter — other peds, vehicles, the lane end, a closed link, the arrival
   position — is folded into **one `Obstacle` per stripe** (`Obstacle[numStripes]`).
2. `walk()` scores every stripe with a **utility**, picks the best, and moves **at most one stripe**
   laterally toward it.
3. Longitudinal speed is whatever the distance to the chosen stripe's obstacle allows.

That is the whole model. The realism comes entirely from *what gets folded into the obstacle array*
and *the order of the utility adjustments*.

### 1.1 Key quantities

| symbol | meaning | default |
| --- | --- | --- |
| `stripeWidth` | lateral discretisation | **0.64 m** |
| `numStripes(lane)` | `max(1, floor(laneWidth / stripeWidth))` | 4.00 m crossing ⇒ **6** |
| `myRelY` | lateral offset, **0 = leftmost stripe edge**, increasing right | — |
| `stripe(relY)` | `floor(relY/stripeWidth + 0.5)`, clamped | — |
| `otherStripe(relY)` | the second stripe a ped straddles when wider than `SQUEEZE·width` allows | — |
| `myDir` | `FORWARD (+1)` / `BACKWARD (-1)` along the lane | — |
| `myWaitingTime` | consecutive time at `speed < 0.1`; reset on any real motion | — |
| `myAmJammed` | squeeze-through latch (see §3.3) | false |

Pedestrian vType defaults, from `SUMOVTypeParameter.cpp:80-91` and `SUMOVehicleClass.cpp:547`:

```
length 0.215   width 0.478   minGap 0.25   height 1.719   mass 70
maxSpeed 10.44 m/s  (37.58 km/h -- Usain Bolt; a CAP, not the walking speed)
desiredMaxSpeed 1.3889 m/s  (DEFAULT_PEDESTRIAN_SPEED = 5/3.6 -- the actual walking speed)
speedFactor deviation 0.1
```

Note `width 0.478 < stripeWidth 0.64`, so a default ped occupies **one** stripe. Raise width past the
squeeze threshold and it occupies two — which is why `width` is one of the most destructive knobs
(§4.2).

---

## 2. The per-step algorithm

### 2.1 Scheduling — peds move first, on last step's vehicle state

`MovePedestrians` is a **begin-of-timestep event** (`MSPModel_Striping.cpp:172-175`), so within one
step of `MSNet::simulationStep` (`MSNet.cpp:763-796`):

```
myBeginOfTimestepEvents->execute()   <- pedestrians move HERE
planMovements()                      <- vehicles plan, seeing THIS step's ped positions
executeMovements()
changeLanes()
```

Pedestrians therefore move on **previous-step** vehicle state, and SUMO makes that explicit by
querying `link->opened(currentTime - DELTA_T, ...)` (`:1242`, with a comment saying exactly why).
Vehicles then plan against **already-updated** pedestrians. This asymmetry is observable and is the
single most important ordering fact in the port (`SUMOPED-DESIGN.md` §5.1).

Within the step: `moveInDirection(FORWARD)` over **all** lanes, then `moveInDirection(BACKWARD)` over
all lanes — direction-then-direction, not lane-then-lane. Lanes iterate in numerical-id order; peds
within a lane are re-sorted by `dir * myRelX` descending with a **tie-break on person id string**,
which is the only thing preventing nondeterminism when two peds share an exact `myRelX`.

### 2.2 `walk()` — the utility fold

The ordered adjustments (`MSPModel_Striping.cpp:2022+`). They are **non-commutative**; getting the
order right *is* getting the behaviour right.

| # | adjustment | value | what it buys |
| --- | --- | --- | --- |
| a | overlapping stripe ⇒ penalty, **propagated to every stripe beyond it away from `current`** | `-300000` | you cannot sidestep *through* someone — this is the no-overlap accumulation |
| b | oncoming-reserved stripes (§4.1) | `-20000` (half if it is `current`) | keep-right / counterflow segregation |
| c | stripe one step further from an oncoming obstacle's approach side | `-0.5` | evasion bias |
| d | `expectedDist = min(vMax·LOOKAHEAD, distance[i] + obs[i].speed·myDir·lookAhead)`; add it, or `-1000 + distance` if negative | up to **+5.56 m** | **the dominant term** — a free stripe is worth ~5.6 m of utility |
| e | edge stripe in the walking direction, if oncoming traffic present at all | `-1000` | don't hug the far edge into oncoming |
| f | lateral displacement cost, **only if `distance[current] > 0` and `myWaitingTime == 0`** | `-1` per stripe | discourages gratuitous weaving — but **never applied to an already-stalled ped**, so a blocked ped can always escape sideways |
| g | shared road + walking BACKWARD | keep-right bias | rule of the road with no dedicated sidewalk |

Then `chosen = argmax(utility)` subject to `utility ≥ 0.5·(-20000)`; `next` moves **one stripe** toward
`chosen`; `xSpeed` is clamped by the distance to the obstacles on `{current, other, next}`.

**Why the horde crosses abreast rather than single-file**: (f) costs only **1 m** per stripe, while
(d) pays up to **5.56 m** for a free one. Taking a free adjacent stripe outbids queueing by ~5×. That
ratio *is* the look, and it is entirely deterministic — no RNG in the path.

### 2.3 The speed and lateral update

```
xSpeed  = clamp(xDist - NUMERICAL_EPS, 0, vMax)
          MIN_STARTUP_DIST (0.4 m) guard: a stopped ped refuses a tiny step unless the limiting
          obstacle is topological (lane end / closed link), not a moving body
xSpeed -= dawdle                      # dawdle = min(xSpeed, rand() * vMax * dawdling)   <- the ONLY
                                      #   per-step RNG draw in the whole model
maxYSpeed = min( max(vMax*0.4, vMax - xSpeed), stripeWidth )      # 0.5556 and 0.64 m/s in practice
```

Both lateral caps are recoverable from the FCD `angle` attribute and were measured landing exactly on
`0.5556` and `0.6401` (`SUMOPED-COVERAGE.md` §2.1) — a useful cross-check that a port's lateral
solver is right.

---

## 3. The three mechanisms that produce the interesting behaviour

### 3.1 Vehicles yield to pedestrians — `blockedAtDist`

SUMO gives pedestrians **no bespoke braking rule**. `MSLink::getLeaderInfo` (`MSLink.cpp:1667-1688`)
pushes a blocking ped into the *same* `LinkLeaders` vector used for vehicle-vehicle junction
conflicts, as `{vehicle: nullptr, gap: -1, distToCrossing: distToPeds}`. Everything downstream —
car-following adaptation, lane-change gap acceptance, zippering — brakes for it exactly as for a
stopped vehicle.

The decision itself (`MSPModel_Striping.cpp:223`):

```cpp
leaderBackDist >= -vehWidth
  && ( leaderFrontDist < 0                                        // already at/past the vehicle's edge
       || (leaderFrontDist <= oncomingGap                         // jmCrossingGap, default 10 m
           && ped.myWaitingTime < TIME2STEPS(2.0)) )              // ...and not standing >= 2 s
```

**The 2-second clause is load-bearing.** A ped that has stood still for 2 s stops blocking the
vehicle — SUMO assumes a stationary pedestrian has yielded. That is what breaks the mutual deadlock
in the goldens, and getting it wrong produces a plausible-looking gridlock.

### 3.2 Who yields is a *network* property, not a model property

`NBNode.cpp:2788`: a guessed crossing is created with `priority = isTLControlled()`. At an
uncontrolled node `--crossings.guess` therefore always produces `priority="false"` — link state `m`,
**the pedestrian gives way**. Declaring `<crossing ... priority="true"/>` flips it to `M` and the
*car* gives way. Measured A/B in `SUMOPED-COVERAGE.md` §4.5: identical car and ped, one boolean, and
the outcome inverts completely (peds stopped on the curb 91% → 0%; cars fully stopping 18 → 68).

### 3.3 The jam / squeeze-through latch

When `xSpeed == 0`, SUMO picks a threshold and compares it to `myWaitingTime`:

| context | threshold | default |
| --- | --- | --- |
| on a crossing (or a walkingarea blocked by a long-waiting vehicle) | `jamTimeCrossing` | **10 s** |
| single-stripe lane (`sMax == 0`) facing an oncoming obstacle | `jamTimeNarrow` | **1 s** |
| anywhere else | `jamTime` | **300 s** |

Past the threshold, `myAmJammed = true` and **`xSpeed = vMax/4`, ignoring the usual collision
gating**; a jammed ped also stops being an obstacle for others. It un-jams once room reopens.

This is SUMO choosing deadlock-avoidance over physical plausibility, and it is the direct cause of the
vehicle↔pedestrian collisions in the jam-regime goldens (`SUMOPED-COVERAGE.md` §6). It is also, per
§4.1 below, **the single most powerful knob in the model**.

---

## 4. The knobs, measured

Method: `scripts/sumoped-knob-sweep.py`. For each knob, run vanilla SUMO across a range of values on a
fixed scenario and diff the aggregate + lateral metrics against a baseline.

⚠ **Two methodology traps, both hit while producing this table.**

1. **The baseline must pin the RNG.** `dawdling` defaults to **0.2** and draws from SUMO's single
   *process-global* stream once per moving ped per step. Changing *any* option shifts the draw
   sequence, so on an unpinned baseline small deltas are noise. The first sweep run this way reported
   things like `jammed 26→25` and `collisions 0→1` that vanished or reversed once pinned. Every number
   below is from a run with `--pedestrian.striping.dawdling 0` and ped `speedDev="0"`. (`dawdling=0`
   showing "NO CHANGE" against that baseline is the self-check that the pinning worked.)
2. **Aggregate counters are blind to lateral behaviour.** On a free-flowing sidewalk, `jammed`,
   `collisions` and `running` cannot see stripe usage at all, so every lateral knob read as inert. The
   sweep grew a `--lat-edge` mode computing distinct lateral bands, peak simultaneous bands, and
   counterflow stream separation from the FCD. Only then did `stripe-width` and `dawdling` show up as
   the lateral levers they are.

### 4.1 Global / striping options

Baseline A = uncontrolled dense junction, 330 persons, 98 vehicles, 150 s
(`jammed 24`, `collisions 8`, `persons.running 240`, `vehicles.running 38`).
Baseline B = 6 m counterflow sidewalk, 376 persons, 200 s
(`lat.bands 2`, `peak 2`, `separation 5.12 m`).

| option | default | measured effect | verdict |
| --- | --- | --- | --- |
| `pedestrian.striping.jamtime` | **300 s** | `→10`: **jammed 24 → 381 (+1488%)**, persons.running 240→179; `→60`: jammed 91 (+279%); `→-1` (off): inert at this density | **the biggest lever in the model** |
| `pedestrian.striping.jamtime.crossing` | **10 s** | `→2`: jammed +71%, collisions +75%; `→60`: jammed −71%, **vehicles.running 38→25**; `→-1`: **jammed 0, collisions 0, vehicles.running 38→10** | huge — and the direct collision source |
| `pedestrian.striping.stripe-width` | **0.64 m** | A: `→1.00` jammed +50%, collisions +88%; `→0.55` collisions 8→**0**. B: shifts stream separation 5.12 → 5.6 / 4.8 | high; changes the discretisation itself |
| `pedestrian.striping.dawdling` | **0.2** | B: `→0.5` **bands 2→69, peak 2→17, separation 5.12→3.81**; `→1.0` bands 2→210, peak 33, separation 2.14 | **this is what makes a sidewalk crowd look organic** — see §4.4 |
| `pedestrian.striping.walkingarea-detail` | **4** | `→8` jammed +25%; `→16` jammed +38%; `→2` inert | moderate; pure geometry, but it moves outcomes |
| `pedestrian.striping.mingap-to-vehicle` | **0.25 m** | `→0` collisions +50%; `→2.5` collisions 8→**0**, vehicles.running 38→51 | moderate, and a real safety lever |
| `pedestrian.striping.reserve-oncoming.junctions` | **0.34** | `→0`: jammed +17%, vehicles.running 38→48; `→0.6`/`1.0` inert (saturates at the `.max` cap) | moderate |
| `pedestrian.striping.reserve-oncoming.max` | **1.28 m** | `→0.64`: jammed −21%, collisions +62% | moderate — it is the *binding* cap, see below |
| `pedestrian.striping.reserve-oncoming` | **0.0** | **inert in every configuration tried** (symmetric and asymmetric counterflow, 2 m / 4 m / 6 m sidewalks, with and without dawdling) | see §4.3 |
| `pedestrian.striping.jamtime.narrow` | **1 s** | inert on both baselines | needs a **1-stripe** lane to fire at all |
| `pedestrian.striping.legacy-departposlat` | **false** | inert | only affects insertion posLat |
| `--step-length` | **1 s** | A: `→0.5` jammed +33%; `→0.2` collisions +50% | changes everything; goldens must pin it |

`getReserved(stripes, factor) = min(floor(stripes·factor), floor(RESERVE_FOR_ONCOMING_MAX / stripeWidth))`
— with the defaults the second term is `floor(1.28/0.64) = **2**`, so **at most 2 stripes are ever
reserved** regardless of factor. That is why `reserve-oncoming.junctions` at 0.6 and 1.0 measured
identical to 0.34: they all clamp to 2.

### 4.2 Pedestrian vType attributes

| attribute | default | measured effect (baseline A) | verdict |
| --- | --- | --- | --- |
| `width` | **0.478 m** | `→1.00`: jammed +112%, **collisions 8 → 122 (+1425%)**; `→0.70`: collisions −62% | **the most destructive knob.** Past `stripeWidth·SQUEEZE` a ped straddles two stripes and SUMO itself warns about vehicle collisions at construction |
| `minGap` | **0.25 m** | `→0`: jammed −21%, collisions 8→**0**; `→0.75`: jammed +83% | high |
| `length` | **0.215 m** | `→1.00`: collisions +188% | high (it feeds `blockedAtDist`'s `leaderBackDist`) |
| `impatience` | **0.0** | `→1.0`: jammed +38%, persons.running −3% | moderate — feeds `link->opened` |
| `desiredMaxSpeed` | **1.3889 m/s** | `→2.0`: jammed +21%; `→0.9`: persons.running +6% | moderate; this is the walking speed |
| `speedFactor` | 1 (dev **0.1**) | `→0.7`: persons.running +4%; `→1.4`: −2%. B: `→0.7` running +102%, `→1.4` −67% | moderate |
| `speedDev` | **0.1** | `→0.3`: small; `→0` inert (already pinned in the baseline) | low, but it is the per-person heterogeneity source |
| `jmDriveAfterRedTime` | **-1** | inert on A and B | **needs a TL scenario** — `ignoreRed` only exists on a red link |

### 4.3 Why `reserve-oncoming` is inert, and what that means

Not a bug, and worth understanding before porting it. The reserve rule penalises stripes
`[0, reserved)` for FORWARD walkers and the mirror band for BACKWARD ones. But peds are *inserted* on
the side the rule would push them to (`myRelY = stripeWidth·(numStripes-1) - myRelY` for FORWARD), and
with `dawdling=0` they hold a single stripe per direction unless actually blocked. So the rule
**codifies the segregation that already emerges** and changes nothing.

Confirmed inert across: symmetric counterflow (2 m / 4 m / 6 m sidewalks), asymmetric counterflow
(24:1 rate ratio), and with `dawdling=0.5` spreading peds over 17 simultaneous lateral bands. The
option is accepted and echoed by SUMO in every run, so this is not a typo.

**Port consequence:** implement it (it is three lines and it *will* bind eventually), but do not expect
a golden to witness it. It belongs in `SUMOPED-COVERAGE.md` §8 as an admitted hole with this reason,
or needs a purpose-built scenario where one stream is genuinely forced to spill.

### 4.4 The finding that changes an earlier claim

`SUMOPED-REQUIREMENTS.md` R11 argued the "members move at different speeds" look is *substantially
deterministic*, because a crossing horde shows min 0.000 / median 1.198 / max 1.389 m/s with
`dawdling=0` and `speedDev=0`. That holds — **for the crossing**, where peds slow for each other.

It does **not** hold on a sidewalk. Measured on the 6 m counterflow at `dawdling=0`, the two streams
collapse to exactly **2 lateral bands** held 5.12 m apart for the entire run — two perfect single-file
queues, which is not what a real pavement looks like. Turning dawdling up breaks that open:

```
dawdling 0    ->  2 bands total,   2 peak,  separation 5.12 m
dawdling 0.5  -> 69 bands total,  17 peak,  separation 3.81 m
dawdling 1.0  -> 210 bands total, 33 peak,  separation 2.14 m
```

So on sidewalks **`dawdling` is the organic-look knob**, and it is RNG-fed. The two-regime split
(R11) is therefore more load-bearing than R11 implies: the parity regime proves the mechanism, but the
production regime is what makes a sidewalk crowd not look like a queue. R11's wording should be read
with this correction.

### 4.5 Knobs the current scenario set does not witness

Directly actionable for `SUMOPED-COVERAGE.md` §8 — each is inert on both baselines and needs a
purpose-built scenario:

| knob | what it needs |
| --- | --- |
| `jamtime.narrow` | a **1-stripe** lane or crossing (`--default.crossing-width 0.64`) |
| `jmDriveAfterRedTime` (both vTypes) | a **TL** scenario with a red phase and an arriving ped/vehicle |
| `legacy-departposlat` | a scenario setting `departPosLat` explicitly |
| `reserve-oncoming` (normal lanes) | see §4.3 — may be unreachable; admit as a hole |
| `walkingarea-detail=2` | a walkingarea with enough curvature for the Bezier detail to matter |

---

## 5. What this means for the port

1. **Port the jam family carefully and test it directly.** `jamtime`/`jamtime.crossing` dominate every
   aggregate outcome; a subtly wrong threshold will look fine on a Tier A golden and be catastrophic
   at Tier C.
2. **`blockedAtDist`'s 2 s clause and `jmCrossingGap` are the whole vehicle-yield story.** Both are
   single numbers with outsized effect (`jmCrossingGap=0` ⇒ vehicles.running 38 → 22).
3. **Crossing priority is a network property.** The port must read `<crossing priority>` and map it to
   link state, or half the behavioural space is unreachable.
4. **Keep the constants in one table with `.cpp:line` anchors** (`StripingParams`, task SP-3.1). This
   document is the rationale for why that table is worth auditing line by line.
5. **Expect some knobs to be untestable by golden.** That is fine if it is *recorded*; it is a defect
   only if it is discovered later by accident.
