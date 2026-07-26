# F3 — design: porting SUMO's `MSVehicle::isLeader` (junction entry-time ordering)

**Status:** design. Implementation is staged in `docs/F3-ISLEADER-PORT-TASKS.md`, tracked in
`docs/F3-ISLEADER-PORT-TRACKER.md`.
**Requirement / WHAT:** `docs/F3-SESSION-LOG.md` §6 (the owner chose this *faithful* fix over the
already-shipped pragmatic `--ignore-junction-blocker` knob).
**Source of record:** `sumo/src/microsim/MSVehicle.cpp:7343-7483` (`isLeader`), consumed at `:3429`,
timestamps assigned at `:4348-4368`, yield-request reset at `:3720-3731`.

This document is HOW. It does not restate the WHAT.

---

## 0. Why this port, and what it must not do

Two open items collapse into one piece of work (log §6):

- **The arm-5 mutual deadlock.** Two cars on crossing internal lanes of one junction car-follow *each
  other* through `JunctionYieldConstraint` arm 5 (`AdaptToJunctionLeader`), which by design has no
  right-of-way notion and no escape. Measured: veh 95 / 102 at speed **exactly 0.000 for 121 steps**,
  released only by the 120 s teleport.
- **The true-F3 residue.** The remaining `BOTH-INTERNAL-DIFFERENT-LANE` overlaps are **12 of 15
  both-moving** — simultaneous admission, which is what entry-time ordering resolves.

### 0a. Proof that this port resolves the measured deadlock

veh 95 sits on `:2336_42_0` = `intLanes[18]`, veh 102 on `:2336_3_0` = `intLanes[3]`, both at speed
exactly 0.000 for the 121 steps t=323…443.

**Which of `isLeader`'s four response attempts (§3a) actually runs was measured, not assumed** — and
the first answer was wrong, so it is worth stating carefully:

- The two links **do** mutually respond in the matrix (`response[18]` has bit 3 set **and**
  `response[3]` has bit 18 set). That was the original reasoning for this port.
- But junction `2336`'s traffic light **never shows both links non-red**: in all 12 phases of its
  90 s cycle at least one of link 3 / link 18 is red. So `attempt 1`
  (`entry->haveRed() || foeEntry->haveRed()`) fires in **121 of 121** deadlock steps and the response
  matrix is **never reached** for this pair.

Attempt 1's stuck-foe arm is `response = foeEntry->haveRed(); response2 = entry->haveRed();`
(`MSVehicle.cpp:7405-7407` — the `else`, taken here because both cars are stopped, so
`veh->getSpeed() > SUMO_const_haltingSpeed` is false). Over the 121 steps that yields:

| Phase class | Steps | `response` / `response2` | Branch taken | Pair compared |
| --- | --- | --- | --- | --- |
| both red | **75** | true / true | mutual conflict (`:7437-7440`) | `ego.CET` vs `foe.CET` |
| link 3 red only | 26 | true / false | neither adjustment | `ego.CET` vs `foe.ET` |
| link 18 red only | 20 | false / true | ego has right of way (`:7433-7436`) | `ego.ET` vs `foe.CET` |
| neither red | **0** | — | — | — |

In **every** class the two vehicles compare the *same two numbers* in opposite senses — veh 95
evaluates `egoET > foeET` while veh 102 evaluates exactly the transposed comparison — so the result is
**antisymmetric: precisely one of the pair yields, never both.** If the two values tie, the tie-break
chain (§4) still resolves it: speeds are equal (both 0.000), so it falls to the id, and
`CompareOrdinal("102", "95") < 0` — so veh 102 yields and veh 95 proceeds. **The symmetric state is
structurally unreachable.** That is the correctness argument for this port, and it is a proof about
the measured pair rather than a plausible-looking mechanism.

**Implementation consequence — attempt 1 is mandatory, not optional.** For the one confirmed deadlock
it is the *only* arm that ever executes. The matrix happens to also report "mutual" for this pair, so
a matrix-only port would break the deadlock too — but it would pick the wrong pair in the 46
one-red steps and so deviate from SUMO. Anyone tempted to stage attempt 1 out of T2.3 should read
this table first.

**Hard constraint.** Arm 5 today applies presence-only to a `RespondsTo` foe — a *parity-locked*
path (every committed golden traverses it). Introducing `isLeader` changes that path. Therefore the
decision is introduced behind a **default-OFF** flag (`JunctionIsLeaderGate`), exactly as
`ContTurnInsideJunctionGate` was, and goldens must stay byte-identical with the flag off **by
construction**, not by luck. See §6.

---

## 1. What SUMO actually computes

`isLeader(link, veh, gap)` answers one question: *must ego treat this foe as a leader (and brake for
it)?* It is consumed as a disjunction (`MSVehicle.cpp:3429`):

```cpp
} else if (isLeader(link, leader, (*it).vehAndGap.second) || (*it).inTheWay()) {
```

so ego brakes when **either** the foe has priority-by-entry-order **or** the foe physically occupies
the conflict point. Our `FoeIsInTheWay` already ports the second disjunct (`MSLink.cpp:1440-1443`).
This port supplies the first.

Structure, in SUMO's order:

1. **Not yet on the junction ⇒ every foe is a leader.** `if (!myLane->isInternal() ||
   myLane->getEdge().getToJunction() != link->getJunction()) return true;` — ego yields, stopping
   *outside*. This is the conservative default and it is what makes the whole mechanism safe.
2. **Foe only partially on the junction ⇒ leader.** The two trailing `else` arms (`:7474-7482`)
   return `true` when the foe is not on an internal lane whose edge starts at this junction.
3. **Otherwise, order by entry time**, with the pair `(egoET, foeET)` selected by one of three cases
   (§3), then the tie-break chain (§4).

`blueLight` (`:7353`) is skipped: we have no blue-light device, so the branch is structurally dead.

**Verified: the To/From asymmetry in clauses 1 and 2 is stylistic, not functional.** Clause 1 tests
ego with `getToJunction()` while clause 2 tests the foe with `getFromJunction()`, which looks like a
meaningful distinction for the two-stage cont case. It is not: for an **internal** edge, `NLHandler`
sets *both* endpoints to the same junction, derived from the lane id
(`NLHandler.cpp:431-445` → `SUMOXMLDefinitions::getJunctionIDFromInternalEdge`), so
`getFromJunction() == getToJunction() == J` for **both** stages of a cont turn. netconvert's separate
`type="internal"` junction object is never installed as any edge endpoint — it exists only to carry
foe bookkeeping (`NWWriter_SUMO.cpp:707-737`), and `MSLink::getJunction()` likewise resolves to the
real `J` for entry, internal-junction and exit links alike (`MSLink.cpp:215`).

Consequence for us: **one predicate, `IsInternalLaneOfJunction`, is correct for ego and foe both**,
and no special case is needed for a vehicle sitting on the second internal lane of a cont turn.

---

## 2. The three timestamps, and the two-stage (cont) structure

SUMO keeps three per-vehicle `SUMOTime`s, all initialised to `SUMOTime_MAX`
(`MSVehicle.cpp:1000-1002`) and assigned on lane entry (`:4348-4368`):

| Field | Set when | Meaning |
| --- | --- | --- |
| `myJunctionEntryTime` | `link->isEntryLink()` | when ego entered the junction; **relinquishable** |
| `myJunctionEntryTimeNeverYield` | `link->isEntryLink()` | same instant, but never relinquished |
| `myJunctionConflictEntryTime` | `link->isConflictEntryLink()` | when ego entered the *conflict area* |

with `if (link->isExitLink())` resetting all three to `SUMOTime_MAX`.

`isConflictEntryLink()` is `!myAmCont && (isEntryLink() || (internalLaneBefore && internalLane))`
(`MSLink.cpp:1293-1296`) — i.e. it fires on a plain entry link, or on an internal→internal hop, but
**never on a cont link**. That is the whole two-stage mechanism, and it maps exactly onto structure
we can read from the net.

### 2a. Derived from real net data (`scenarios/_repro/synthetic-junction2`, junction `2336`)

A **cont** link is a two-stage turn with an extra internal junction in the middle:

```
2417 ──entry(linkIndex=18, tl=2336, state='o')──▶ :2336_18_0   [stage 1: the waiting bay]
                                                      │
                                              internal junction :2336_42_0
                                                      │
             ──2nd hop (no tl, no linkIndex, state='m')──▶ :2336_42_0  [stage 2]  ──▶ -2337
```

Three facts follow, each verified against `grid.net.xml`:

1. **`intLanes[i]` for a cont link is the STAGE-2 lane.** `intLanes[18] == ":2336_42_0"`; the
   stage-1 lane `":2336_18_0"` is **absent from `intLanes`**. (Same shape as the already-committed
   `ContTurnInternalLaneOwnershipTests` finding.) Confirmed for all ten cont links at `2336`
   (indices 5, 12, 17, 18, 19, 25, 31, 36, 37, 38 → lanes `:2336_39_0 … :2336_48_0`).
2. **`JunctionLink.Connection` for a cont link is the SECOND hop**, because `NetworkParser` resolves
   it as `connections.FirstOrDefault(c => c.Via == intLanes[i])` (`NetworkParser.cs:341`). Its
   `From` is the stage-1 *internal* edge — which is exactly why the existing
   `JunctionByInternalLane` back-walk (`NetworkParser.cs:235-267`) already maps stage-1 lanes to
   their owning junction.
3. **The second hop carries no `tl`/`linkIndex`** (state `'m'`), while the entry connection carries
   `tl=2336, linkIndex=18, state='o'`. So a cont link's *right-of-way state* is only reachable via
   the entry connection. This is precisely SUMO's `getCorrespondingEntryLink()` (`MSLink.cpp:1332`).

### 2b. Link classification at our lane-advance seam

`Engine.cs:10127-10132` is documented as *"the ONE site a lane is fully left"* — the direct analogue
of SUMO's `enterLaneAtMove` + the timestamp block. With
`J(l) = NetworkModel.IsInternalLaneOfJunction`-style lookup (null when `l` is a normal lane), a hop
`old → new` classifies as:

| `J(old)` | `J(new)` | SUMO link kind | Action |
| --- | --- | --- | --- |
| null | `J` | entry link | `ET = ETN = now`; **and** conflict-entry iff `!Cont` (see below) |
| `J` | `J` | internal→internal (2nd stage) | `CET = now; ET = ETN` |
| `J` | null | exit link | `ET = ETN = CET = MAX` |
| null | null | not a junction hop | nothing |

**Two net-shape facts found while implementing this, both worth keeping:**

1. **A vehicle can cross two junction boundaries in one step, and the concrete reason is edge length.**
   `:2336_42_0`'s downstream edge `-2337` is **0.20 m** long, so a car clears it within a single step
   and stamps the *next* junction's entry time in the same step. SUMO does exactly the same — both
   `enterLaneAtMove` calls share one `getCurrentTimeStep()`. This is why the entry-time tests assert a
   whole-trace invariant (normal lane ⇒ all three `MAX`; internal lane ⇒ `ET`/`ETN` set) rather than
   "the sample after the junction lane is `MAX`", which is simply false here.
2. **An internal junction can carry a vestigial `intLanes` naming a lane owned by a *different* real
   junction** (observed at `:J_2_0` in scenario 41). So a sweep over raw `IntLanes` strings reports
   false violations; `junction.Links` is the correct scope, since `NetworkParser` only builds links for
   junctions that have a real right-of-way matrix. The committed sweep is scoped that way.

**One case is absent from the classification table on purpose:** a hop from junction `A`'s internal lane *directly*
to a **different** junction `B`'s internal lane. It is unreachable in a netconvert-produced net —
there is always a normal edge between two junctions, so an exit link is always traversed first — and
the measured trace confirms it (veh 95 goes `:2336_42_0` → `:444_0_0` inside one step, and ET is
restamped to that step, i.e. exit-then-entry both fired via the intervening normal lane). Were it ever
to occur, the current code would silently retain `A`'s timestamps. Flagged rather than handled, because
adding a branch for a structurally unreachable case would be untestable speculation; the whole-trace
invariant in `JunctionEntryTimeTests` (normal lane ⇒ all three `MAX`; internal lane ⇒ `ET`/`ETN` set)
is what would surface a net shape that breaks the assumption.

Cont-ness of the entry hop is read from `Junction.Requests[i].Cont` (`myAmCont`), resolved through a
new **`LinkIndexByInternalLane`** map that covers **both** stages (§2c). The structural shorthand
"`new` ∈ `intLanes` ⟺ not a cont entry" happens to be equivalent on this net, but we use the `Cont`
bit directly because that is what SUMO tests, and equivalence-by-coincidence is exactly the class of
shortcut that produced this session's earlier mis-ports.

Worked, for the deadlock pair:

- **veh 102, non-cont link 3.** `-2437 → :2336_3_0`: entry link, `Cont=0` ⇒ all three set to the
  entry step. On exit to `-2417`, all three back to MAX.
- **veh 95, cont link 18.** `2417 → :2336_18_0`: entry link, `Cont=1` ⇒ `ET = ETN = t_enter`, and
  `CET` **stays MAX** (it is in the bay, not the conflict area). Then
  `:2336_18_0 → :2336_42_0`: internal→internal ⇒ `CET = t_stage2`, and `ET` is restored to `ETN`
  (SUMO's *"renew yielded request"*, `:4361`) — so the car keeps its original seniority for the
  `!response` case while `CET` records when it actually entered the conflict area.

`CET == MAX` while in the bay is load-bearing: it makes `egoET > foeET` true against any foe, so a
car waiting in the bay yields to everything. That is the correct behaviour and it falls out of the
data rather than needing a special case.

### 2c. New network data required

Both additions are pure lookup tables built at parse time, read by nothing until §5 — so they are
parity-inert by construction.

- **`LinkIndexByInternalLane : string → (Junction, int)`** — every internal lane of a junction,
  *both* cont stages, to its junction link index. Built by extending the existing
  `JunctionByInternalLane` back-walk to also record the link index it is walking from.
- **`EntryConnectionByLink : (Junction, int) → Connection`** — SUMO's
  `getCorrespondingEntryLink()`. Resolved as the top-level connection at this junction whose
  `LinkIndex == i` (for a non-cont link this is the same connection `JunctionLink.Connection`
  already holds; for a cont link it is the entry hop). Needed for the state tests in §3.

> **Pre-existing defect noted, deliberately NOT fixed here.** `Engine.LinkStateChar`
> (`Engine.cs:12414`) reads `link.Connection`, so for a **cont** link it returns the second hop's
> static `'m'` instead of the live TL state — which means `ClassifyTeleportKind` labels every cont
> link's teleport `Yield` regardless of the actual phase. Out of scope (it only affects a diagnostic
> counter); filed as `NEED-linkstatechar-cont-entry-link.md` so it is not silently absorbed.

---

## 3. Selecting `(egoET, foeET)`

Given ego on an internal lane of junction `J` at link `e`, foe on an internal lane of `J` at link `f`:

```
egoET = ego.CET ; foeET = foe.ET          // MSVehicle.cpp:7359-7360 (the default pair)
```

then exactly one of three cases:

**(a) Same source lane** (`:7362-7368`). SUMO tests
`foeLane->getNormalPredecessorLane() == link->getInternalLaneBefore()->getNormalPredecessorLane()`
— i.e. both vehicles entered the junction from the *same* incoming lane, so they are in a queue, not
a conflict. Then `egoET = ego.ETN; foeET = foe.ETN` (the never-relinquished pair, so a car that has
yielded its request still keeps its queue order against a same-lane follower).

Our equivalent: the normal predecessor of a junction link is `EntryConnectionByLink[(J,i)]`'s
`(From, FromLane)`. Same source lane ⟺ those are equal for `e` and `f`.

**Validated on junction `2336`** (39 links): all 39 resolve an entry connection via
`(tl, linkIndex)` — including all ten cont links, so §2c's resolution strategy has no gaps here; the
39 links reduce to **11 distinct source lanes**, every one feeding 2–7 links, so case (a) is genuinely
reachable and is not a dead branch; and **no same-source pair responds to the other in the matrix**,
so case (a) and the response arms of §3a are disjoint — consistent with SUMO's intent that a shared
source lane is a queueing relationship, not a conflict. The deadlock pair is *not* same-source
(link 3 ← `-2437_1`, link 18 ← `2417_1`), which is why §0a's analysis goes through the response arms.

The nested `isExitLinkAfterInternalJunction() && …->isIndirect()` sub-case (`:7366-7367`) applies
only to **indirect** (bicycle-style two-stage) left turns. No committed scenario has one; it is
**omitted, with an explicit comment** and a guard test asserting no committed net contains an
indirect link, so the omission cannot silently start mattering.

**(b) Ego has right of way** (`!response`, `:7433-7436`): `foeET = foe.CET; egoET = ego.ET`.

**(c) Mutual conflict** (`response && response2`, `:7437-7440`): `foeET = foe.CET; egoET = ego.CET`.
**This is the measured deadlock case** — reached via attempt 1's both-red arm, not the matrix (§0a).

If `response && !response2` neither adjustment applies and the default pair stands.

### 3a. Determining `response` / `response2`

SUMO tries four things in order (`:7370-7418`), on the **corresponding entry links**:

1. **Either link red** — `entry->haveRed() || foeEntry->haveRed()`. Ensures a vehicle stuck on the
   intersection may exit. Contains a sub-branch (`:7381-7402`) that, for a moving oncoming foe with
   `gap < 0`, decides by whether the foe can still brake safely (`brakeGap`).
2. **Priorities differ** — `response = !entry->havePriority()`.
3. **Both yellow** — the faster vehicle keeps moving.
4. **Fallback** — the response matrix: `logic->getResponseFor(link).test(foeLink)`.

We have all four inputs: `LinkStateChar` on the *entry* connection gives the state char
(uppercase ⇒ priority, `'r'/'R'` ⇒ red, `'y'/'Y'` ⇒ yellow), and
`JunctionRequest.RespondsTo(foeLink)` is `getResponseFor(...).test(...)` verbatim (the bitstring is
already parsed with SUMO's rightmost-char-is-link-0 convention).

Attempt 1's `brakeGap` sub-branch needs the `gap` that `MSLink::getLeaderInfo` passes at `:3429`.
Arm 5 has the same quantities `FoeIsInTheWay` derives (`distToCrossing`, `leaderBackDist`), so the
gap is available; it is used **only** inside the red branch. Per Q12 the gap arriving at `isLeader` is
already reduced by ego's `minGap`
(`gap = distToCrossing - egoMinGap - leaderBackDist2 - foeCrossingWidth`, `MSLink.cpp:1647`), which is
why `:7386-7388` adds `-2 * minGap` back when re-deriving `foeGap` — reproduce that arithmetic
verbatim rather than "simplifying" it.

**Attempt 1 is not optional** (§0a): it is the only arm that executes for the one confirmed deadlock,
because that junction never shows its two conflicting links non-red simultaneously.

**Blast-radius measurement** (all 134 committed nets, so the parity risk is quantified rather than
guessed):

- mutual-response pairs are **2599 of 93961** conflicting link pairs (**2.8%**), confined to **12**
  nets — concentrated in `_bench` / `_repro` scenarios, i.e. the gridlock diagnostics.
- **26 of 134** nets contain cont links (8623 cont request rows), so §2b's two-stage timestamp logic
  is broadly load-bearing, not repro-specific.
- **0 of 134** nets contain an indirect link, confirming §7's omission.

Crucially, clause 1 means a vehicle **not yet on the junction always yields**, which is exactly what
arm 5 does today. So the behavioural delta is confined to **ego already inside the junction** — the
deadlock and the both-moving F3 overlaps, and nothing else.

---

### 3b. Deriving `gap` (needed only by attempt 1, but it changes outcomes)

`gap` reaches `isLeader` from `MSLink::getLeaderInfo`. Its full derivation (`MSLink.cpp:1376-1653`):

```cpp
distToCrossing    = dist - myConflicts[i].getLengthBehindCrossing(this);
foeDistToCrossing = foeLane->getLength() - myConflicts[i].getFoeLengthBehindCrossing(foeExitLink);
leaderBackDist    = foeDistToCrossing - leaderBack;                            // :1431
sameSource        = myInternalLaneBefore->getLogicalPredecessorLane() == foeLane->getLogicalPredecessorLane();
foeCrossingWidth  = (sameTarget || sameSource) ? 0 : myConflicts[i].getFoeConflictSize(foeExitLink);
contLane          = foeExitLink->getViaLaneOrLane()->getEdge().isInternal()
                    && !(isInternalJunctionLink() || isExitLinkAfterInternalJunction());   // :1384

if ((contLane && !sameSource && !ignoreIndirectBicycleTurn) || isOpposite)
    gap = -DBL_MAX;                                                            // :1623
else
    gap = distToCrossing - egoMinGap - leaderBackDist2 - foeCrossingWidth;     // :1647
```

Mapping to arm 5, where `sameTarget` is structurally false (a merge never carries a `JunctionConflict`
— see `FoeIsInTheWay`'s comment) so `leaderBackDist2 == leaderBackDist`:

| SUMO term | Ours |
| --- | --- |
| `distToCrossing` | already computed by `FoeIsInTheWay` |
| `leaderBackDist` | already computed by `FoeIsInTheWay` |
| `foeCrossingWidth` | `conflict.FoeConflictSize`, or **0 when `sameSource`** |
| `egoMinGap` | `ego.VType.MinGap` |

⚠ **Two traps here.**

1. **`sameSource` in `getLeaderInfo` uses `getLogicalPredecessorLane()`, but `isLeader`'s same-source
   test (§3a case (a)) uses `getNormalPredecessorLane()`.** These are *different* predicates
   (`MSLane.cpp:3077-3109`): logical is one hop back, normal recurses past every internal lane. Do not
   share one helper between the two sites just because both are called "same source".
2. **The `contLane` rule is not cosmetic.** With `gap = -DBL_MAX`, attempt 1's sub-branch computes a
   huge positive `foeGap`, so `foeGap < foeBrakeGap` is false and ego does **not** yield. Under the
   plain formula the same situation may give `gap > 0`, which fails the sub-branch's `gap < 0`
   precondition entirely and falls through to `response = foeRed; response2 = egoRed` — **a different
   answer.** And `contLane` is *live* for us: veh 95 sits on `:2336_42_0`, a cont continuation lane.

`isOpposite` and `ignoreIndirectBicycleTurn` are structurally false here (no opposite-direction driving
on these nets; no indirect links in any of the 134 committed nets, §3a).

## 4. The tie-break chain (determinism-critical)

Verbatim from `:7443-7472`:

```
if (egoET == foeET)
    if (egoSpeed == foeSpeed) return ego.Id <  foe.Id;   // lexicographic, ORDINAL
    else                      return ego.Speed < foe.Speed;
else                          return egoET > foeET;      // entered later ⇒ you yield
```

Two non-negotiables:

- **The id tie-break uses the vehicle ID STRING**, compared with `string.CompareOrdinal`. Never
  `EntityIndex` — CLAUDE.md requires order-independence, and SUMO's own tie-break is the id.
  `CompareOrdinal` (not `Compare`) because SUMO's `std::string::operator<` is byte-wise and
  culture-sensitive comparison would make results locale-dependent.
- **Entry times are compared for EXACT EQUALITY.** SUMO's `SUMOTime` is an integer (ms). Storing
  our timestamps as accumulated `double` seconds would make `egoET == foeET` fire or not fire on
  floating-point noise — a determinism bug of exactly the kind the parity bar exists to catch.
  **Therefore the fields are `long` step indices** (`Engine._elapsedSteps`, an `int` counter,
  `Engine.cs:303`), with `long.MaxValue` as the `SUMOTime_MAX` sentinel. Step index ordering is
  identical to time ordering under a fixed step length, and equality is exact.

---

## 5. Where it plugs in, and the yield-request reset

### 5a. Arm 5

`JunctionYieldConstraint`'s foe loop, at the existing gate (`Engine.cs:7117-7121`). Today:

```csharp
if (!respondsTo && (egoOnInternal || !FoeIsInTheWay(...))) continue;
```

With the flag on it becomes SUMO's `:3429` disjunction, applied to **both** the `RespondsTo` and the
`FoeWith`-only foe:

```csharp
if (!(IsLeader(v, junction, egoLink, foe, j, gap) || FoeIsInTheWay(...))) continue;
```

The flag-off path keeps the current expression **character-for-character**, so byte-identical
goldens with the flag off is a property of the code shape, not a measurement.

### 5b. The yield-request reset — investigated, and deliberately NOT ported

`MSVehicle.cpp:3720-3731` (in `processLinkApproaches`' no-request `else` branch) does this:

```cpp
// blocked on the junction. yield request so other vehicles may become junction leader
myJunctionEntryTime = SUMOTime_MAX;
myJunctionConflictEntryTime = SUMOTime_MAX;
```

resetting `ET` and `CET` but deliberately **not** `ETN` — which is the entire reason `ETN` exists as
a third field, and which the cont turn's "renew yielded request" line (`:4361`) then restores from.
Its effect is real: with `ET = CET = MAX` a foe evaluating ego sees `foeET = MAX`, so `egoET > MAX`
is false and the **foe stops yielding to ego**. It is a deadlock-breaking mechanism distinct from
the ordering.

**It is nevertheless out of scope here, on evidence.** Its firing condition is
`link == nullptr || !dpi.mySetRequest`, and `mySetRequest` (`MSVehicle.cpp:2732`) is
`(v > eps && !abortRequestAfterMinor) || leavingCurrentIntersection`. The
**`leavingCurrentIntersection` disjunct means a vehicle already inside a junction normally keeps its
request even at speed 0**; the reset therefore fires only when `checkRewindLinkLanes` has *cancelled*
the request because downstream space ran out (`MSVehicle.cpp:5221,5249-5253` — spillback).

Our `VehicleRuntime.WillPass` is **not** that predicate. It is `(planned vNext > eps)` and its own
doc comment states `leavingCurrentIntersection` is deliberately excluded
(`Engine.cs:5722-5724`). So `!WillPass` is true for **every stopped in-junction car**, not just a
spillback-blocked one. Wiring the reset to `!WillPass` would blank `ET`/`CET` for every stopped car
inside a junction and **destroy the ordering this port exists to establish** — a strictly worse
outcome than omitting it.

A faithful port needs a real `mySetRequest`, which needs `checkRewindLinkLanes`' spillback abort —
explicitly parked (log §6, "Parked, with reasons"). Two further facts make deferring it clearly
right rather than merely convenient:

- **It is not the mechanism the measured deadlock needs.** veh 95 / 102 are not spillback-blocked —
  they have a 2.99 m clear box gap and both stopped *short* of the crossing point (log §9.27). SUMO's
  reset would not fire for them; the *ordering* is what prevents their state.
- Bundling an approximation of `mySetRequest` would put a second, poorly-characterised behavioural
  change inside the same measurement — the exact error that made the first `--ignore-junction-blocker`
  A/B uninterpretable (log §9.33).

Recorded as `NEED-yield-request-reset-unported.md` so it is not lost, with the `!WillPass` trap
written down explicitly.

### 5c. Determinism / phase discipline

The timestamps are written in the **move** phase (at the lane-advance seam and the reset), each
vehicle writing only its own fields — safe under region-parallel `ExecuteMoves`, the same discipline
as `RouteDistanceTraveled`. They are read in the **plan** phase of a later step, from the frozen
start-of-step snapshot. So no plan-phase read ever observes a mid-step write, and parallel == serial
is preserved. `Sim.Bench`'s `par == single` check is the guard.

---

## 6. Parity argument

1. **Flag off ⇒ byte-identical by construction.** The new fields are written but never read; the
   gate expression is unchanged (§5a). The only unconditional additions are two parse-time lookup
   tables and three per-vehicle `long`s.
2. **Flag on ⇒ measured, not assumed.** Against all four surfaces, because the log's Lesson 1 is
   that goldens alone are insufficient: the 661 goldens, the **five gridlock diagnostics**, the
   live-city demo overlap buckets, and `Sim.Bench`'s hash + `par == single`.
3. **The one intended behavioural change is narrow and named:** a car already inside a junction may
   now be released past a foe it currently brakes for, when it entered the conflict area first.
   This trades a class of deadlock for a possible increase in junction *overlap* — SUMO accepts that
   trade, but per the log's standing lesson both must be measured, so the F3 buckets are re-measured
   and reported either way, not just the teleport count.
4. **The default stays off** until the owner decides, exactly as `ContTurnInsideJunctionGate` and
   `IgnoreJunctionBlockerSeconds` do. Flipping a default is an owner decision, not a test outcome.

## 7. Deliberate omissions (each with a guard)

| Omitted | Why | Guard |
| --- | --- | --- |
| `blueLight` priority (`:7353`) | no blue-light device exists in this engine | structurally unreachable |
| indirect-left sub-case (`:7366-7367`) | no committed net has an indirect link | test asserts no committed net does |
| `jmIgnoreJunctionFoeProb` (`:3430`) | already handled by the existing `IgnoresJunctionFoe` | unchanged |
| `gLateralResolution` sublane branch (`:3439-3448`) | sublane model is off by default | unchanged |
| yield-request reset (`:3720-3731`) | needs a faithful `mySetRequest`; our `WillPass` omits `leavingCurrentIntersection`, so wiring it would blank `ET`/`CET` for every stopped in-junction car — see §5b | `NEED-yield-request-reset-unported.md`; not required by the measured deadlock |

## 8. Verified source facts this design rests on

Each was read from the vendored 1.20.0 tree, not inferred from names. Recording them so a resumed
session need not re-derive them.

| Fact | Citation |
| --- | --- |
| `isEntryLink() = internalLane != null && internalLaneBefore == null` | `MSLink.cpp:1283-1290` |
| `isConflictEntryLink() = !myAmCont && (isEntryLink() \|\| (internalLaneBefore && internalLane))` | `MSLink.cpp:1292-1296` |
| `isExitLink() = internalLaneBefore != null && internalLane == null` | `MSLink.cpp:1298-1305` |
| entry / conflict-entry / exit are three independent `if`s; **entry and conflict-entry both fire** on a non-cont entry link; exit is exclusive of both | `MSVehicle.cpp:4354-4368` |
| `getCorrespondingEntryLink()` walks back while `laneBefore->isInternal()`; an entry link returns itself | `MSLink.cpp:1331-1339` |
| `getNormalPredecessorLane()` recurses through internal lanes to the first normal lane | `MSLane.cpp:3102-3109` |
| internal edge ⇒ `getFromJunction() == getToJunction() == J`, both cont stages | `NLHandler.cpp:431-445` |
| `SUMOTime` is `long long`, **milliseconds**; `SUMOTime_MAX = 2^63-1`; all three fields init to it | `SUMOTime.h:33-34`, `MSVehicle.cpp:1000-1002` |
| `gap` at `:3429` is already minGap-reduced: `gap = distToCrossing - egoMinGap - leaderBackDist2 - foeCrossingWidth` | `MSLink.cpp:1647,1663` |
| `mySetRequest` includes `leavingCurrentIntersection`, and is cancelled by `checkRewindLinkLanes` on spillback | `MSVehicle.cpp:2732`, `:5221,5249-5253` |
