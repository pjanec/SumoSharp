# SUMOPED — gap review of the design set

**Status: review findings. Read alongside the PROPOSAL doc set; nothing here is signed off.**

A deliberate pass over `SUMOPED-{REQUIREMENTS,DESIGN,PROCESS,TASKS,COVERAGE,BRANCH-INVENTORY,TRACKER}.md`
looking for gaps, contradictions and weakly specified spots, done **before** B0 so the corrections cost
a doc edit rather than a stage.

Every finding below was checked against the repo or the SUMO source, not inferred from the docs. Where a
suspected gap turned out not to be one, it is recorded in §4 — a check that came back clean is worth as
much as one that did not, and re-running it later is waste.

**Applied vs open.** The ROT fixes (§3) and the clear-cut parts of G1, G4, G5 and G12 are **already
folded into the doc set** — they were unambiguous corrections to documents that are all still PROPOSAL.
**G2, G3, G6, G8, G9, G10 and G11 remain open**; five of them change scope and are the owner's call. The
tracker carries a banner naming the open ones so they cannot be lost.

**Severity key.** **BLOCKING** = would produce wrong work if B0 started as written. **STRUCTURAL** = a
real specification hole; the port could proceed but the answer would be improvised at implementation
time by whoever hit it first. **ROT** = a stale number or name; individually trivial, but this repo's own
CLAUDE.md notes that every doc's copy of a gate number has rotted at least once.

---

## 1. BLOCKING — fix before B0

### G1 — The Tier C golden recipe throws away `angle`, which R2 calls load-bearing

`SUMOPED-DESIGN.md` Appendix B's generation command masks the FCD attribute set:

```
--fcd-output.attributes id,x,y,speed,pos,edge
```

`angle` is not in that list. But:

- **R2** names `angle` a compared attribute and says it is *"load-bearing, not cosmetic"* — SUMO encodes
  `mySpeedLat` into it (`MSPModel_Striping.cpp:2342-2349`) and person FCD carries **no other** lateral
  witness.
- **SP-2.1** gives `angle` a tight tolerance.
- **SP-2.1c** recovers `mySpeedLat` by *inverting* `angle`, and its own success condition pins the two
  recovered caps (0.6401 and 0.5556).
- **SP-2.1d**'s single-step replay needs `mySpeedLat` as an input field (`SUMOPED-PROCESS.md` §3.1).
- **`SUMOPED-DESIGN.md` §8.2 explicitly warns against this**: the `regen-goldens.sh` change is
  *"to ensure … person-FCD attributes are not masked out by an `--fcd-output.attributes` list."*

So the committed recipe contradicts the design's own instruction, and following it would produce Tier C
goldens on which the entire lateral-state ladder is unrunnable — silently, because a missing attribute
reads as "nothing to compare" rather than as a failure.

**Resolution:** add `angle` (and `slope`, which `PersonTrajectoryPoint` carries) to the attribute list, or
drop the mask entirely and pay the bytes. Re-measure the Tier C footprint afterwards — the 1.45 MB figure
in coverage §3 was measured *with* the mask, so it is now a lower bound. **Applied** to Appendix B; the
footprint number is flagged as needing re-measurement at SP-0.2b.

### G2 — `vClass="pedestrian"` throws in `Sim.Ingest` today, and no task fixes it

`VTypeDefaults.Resolve` (`src/Sim.Ingest/VTypeDefaults.cs:240-243`) looks the vClass up in
`RawDefaultsByVClass` and **throws** on a miss:

```
"VTypeDefaults.Resolve does not support vClass='{vClass}' (vType '{vType.Id}')."
```

There is no `pedestrian` entry. Every `_sumoped` scenario declares `<vType vClass="pedestrian">`, so the
first thing that resolves one throws. No task in `SUMOPED-TASKS.md` adds the entry, and no success
condition mentions ped vType defaults — even though `SUMOPED-DESIGN.md` §4.1(c) **denormalises
`width`/`length`/`minGap`/`vMax` into the hot arrays**, where a wrong default shifts every trajectory in
the port with no obvious cause.

Worse, the design talks itself out of the check that would catch it. §2.3 says:

> *"Unlike the vehicle harness there is no init cross-check; the vType cross-check role is taken by the
> `walk-straight-1` scenario instead."*

That is true of `--save-state` (which writes no person elements — verified) but **not** of the mechanism
this repo actually uses for vType cross-checks. `golden.vtype.json` is dumped by
`scripts/dump-scenario-vtypes.py` via libsumo/TraCI, and **SUMO persons reuse `MSVehicleType`**
(`SUMOPED-DESIGN.md` §10.5 establishes this) — so a person's resolved type is dumpable the same way.
The init cross-check is available for persons; the design gave it up unnecessarily.

This matters beyond tidiness: CLAUDE.md §Reporting a parity failure says to *"diff `golden.state.xml`
first to rule out a vType-default init bug before chasing the trajectory."* Without a person vType
cross-check, every ped divergence starts one rung higher than it needs to.

**Resolution (needs a task, recommend inserting as SP-1.0, before SP-1.1):** port `SVC_PEDESTRIAN`'s
`VClassDefaultValues` (`SUMOVTypeParameter.cpp`, plus the ped length 0.215 at `SUMOVehicleClass.cpp:547`)
into `RawDefaultsByVClass`; extend `dump-scenario-vtypes.py` to dump person types; commit
`golden.vtype.json` for every `_sumoped` scenario; let `ParameterCrossCheckTests` pick them up.
Expected values, already measured this session: `length 0.215  width 0.478  minGap 0.25
maxSpeed 10.44 (cap)  desiredMaxSpeed 1.3889  speedDev 0.1`. Correct §2.3 to stop claiming the
cross-check is unavailable.

### G3 — §6.1's phantom-leader seam is understated: our candidate type has no "no gap known" case

The seam is real and **live** — checked, because CLAUDE.md §Measurement discipline item 3 exists:
`AdaptToJunctionLeader` is called from the plan path at `Engine.cs:7827` and `:8011`, and
`JunctionLeaderCandidate` is constructed at `:7792/:7796/:7948/:7952`. (The nearby *"NOT WIRED IN"*
comment at `Engine.cs:10004` applies to `IsLeader`, a different method. Item-3 check passes.)

But the design describes the injection as *"a phantom `JunctionLeaderCandidate` (`Engine.cs:9988`) **with
a null vehicle**"*, and that type has **no vehicle field**:

```csharp
public readonly record struct JunctionLeaderCandidate(
    string LaneId, string Id, double Speed, long EntryTime, long EntryTimeNeverYield,
    long ConflictEntryTime, double MinGap = 0.0, double MaxAccel = 0.0,
    double MaxDecel = 0.0, double HeadwayTime = 0.0, double Length = 0.0);
```

Two consequences the design does not address:

1. **There is nothing to null.** A phantom is a synthetic `Id` — easier than SUMO's `nullptr`, fine.
2. **The consumption branch is the actual question.** SUMO pushes the ped in with `gap == -1`, which is a
   sentinel meaning *"no gap is known — brake to stop before `distToCrossing`"*, and it deliberately
   **bypasses** the arrival-time foe comparison. Our candidate instead carries `EntryTime`,
   `ConflictEntryTime`, `Length`, `MinGap`, `HeadwayTime` — the inputs to *car-following* adaptation
   against a real vehicle foe. A pedestrian has no meaningful value for any of them.

So "inject into the existing path" is not a drop-in, and SP-5.1's success condition (the
`13.89 → 11.11 → 6.61` profile) would be reachable by *tuning a synthetic `Length`/`EntryTime`* until the
numbers matched — the precise failure mode `SUMOPED-PROCESS.md` §6.1 forbids, and one that would pass its
own test.

**Resolution:** SP-5.1 gains a first success condition that is a *reading* task, not a coding one:
identify how SUMO's consumer branches on `gap == -1` (`MSVehicle::adaptToJunctionLeader` /
`adaptToLeaders`, from `MSLink::getLeaderInfo`'s ped block at `MSLink.cpp:1667-1688`), and establish
whether `AdaptToJunctionLeader` has an equivalent branch or needs one added. **If it needs one, that is
an edit to the live vehicle plan path** — which changes SP-5.1's risk profile from "additive, gated on
`Persons != null`" to "touches vehicle code", and makes the S-d full-gate re-run inside SP-5.1
non-negotiable rather than a formality.

### G4 — Eleven of the twenty-one committed scenarios appear in no task at all

Mechanically checked, every scenario name from `SUMOPED-REQUIREMENTS.md` §6 against `SUMOPED-TASKS.md`:

| scenario | mentions in TASKS.md |
| --- | --- |
| `counterflow-sidewalk-4m`, `counterflow-sidewalk-6m`, `counterflow-crossing`, `counterflow-crossing-jam` | **0** |
| `turning-vs-crossing-peds`, `ped-turners-through-bunch`, `ped-turners-gridlock` | **0** |
| `zebra-1v1-yields`, `xwalk-1v1-noprio`, `zebra-flow-balanced`, `zebra-flow-pedheavy` | **0** |

These eleven are exactly the ones added late in the design session, from the render pass — the coverage
holes that were found by *looking* (crossing counterflow, turning cars, the ped-priority zebra). They
reached `REQUIREMENTS.md` §6 and `COVERAGE.md` §4, and stopped there. The task list still discharges the
original ten-scenario set.

The sharpest instance: **R4b has a detailed acceptance condition and no task in `TASKS.md`.** The
*tracker* has an **SP-5.1b** row for it — a task ID that `TASKS.md` does not define. That is the two
documents having diverged, and the tracker being ahead.

**Resolution:** define SP-5.1b in `TASKS.md` (the tracker's row is the spec), and add the counterflow /
turning scenarios to the success conditions of SP-3.4 (sidewalk counterflow), SP-4.4 (crossing
counterflow, ped-turners) and SP-5.2 (turning-vs-crossing). Without this, eleven scenarios would be
authored in B1 and then never asserted against — goldens that exist and gate nothing.

---

## 2. STRUCTURAL — real holes, decide before the stage that hits them

### G5 — Stage 0 is not gate-neutral, and standing rule S-d does not cover it

S-d requires the full gate re-run *"after any task that touches `src/Sim.Core` or `src/Sim.Ingest`."*
Stage 0 touches neither — it commits data files. But four existing tests enumerate **every `*.net.xml`
under `scenarios/` recursively** and parse them all with the core `NetworkParser`:

```
JunctionLinkLaneMapTests   InternalJunctionFoeTests
JunctionIsLeaderTests      InternalLinkFoeTests
```

They assert structural invariants (every `intLanes` entry resolves in `LinkIndexByInternalLane` with a
matching index and junction; every internal-link foe resolves to a real lane handle; no committed net
contains an indirect connection). Committing ~30 `_sumoped` nets runs all of them through those
assertions **at SP-0.2**, before SP-1.1 teaches the parser anything about crossings.

Three of the four also assert `netFiles.Count >= 120` against a comment saying `~134`; the actual count
today is **141**, rising to ~171.

*Mitigating evidence, checked:* four committed nets already carry walkingareas and
`function="crossing"` edges — `scenarios/_ped/poc0-crossing-plaza`, `scenarios/_ped/evac-district`,
`scenarios/_ped/georef_min`, `scenarios/_bench/livecity-mega` — and `poc0-crossing-plaza`'s junction `c`
lists crossing internal lanes `:c_c0_0 … :c_c3_0` directly in `intLanes`. Those tests are green today. So
the risk is **low, not zero**, and the design's §3.1 inertness argument is actually stronger than it
claims.

**Resolution:** (a) extend S-d to *"after any task that adds a committed net"*; (b) add the full-gate
re-run to SP-0.2/SP-0.2b's success conditions; (c) name `scenarios/_ped/poc0-crossing-plaza` in design
§3.1 as the existing crossing-bearing net the core parser already survives — it is the natural regression
fixture for SP-1.1's additive parse and is currently cited nowhere in the doc set.

### G6 — Person demand parsing is specified for a shape none of the fixtures use

SP-3.5 covers `<person>` / `<walk edges=>`. But the committed fixtures are not that shape:

- **Appendix B (Tier C) is built entirely from `<personFlow>` with `<walk from= to=>`** — four
  personFlows, including the `ppass` pass-by flow that discharges R3d.
- Appendix A uses `<walk from= to=>` too.
- SP-0.2's own success conditions reference `departPos`/`arrivalPos` pinning and `departPosLat`.

And `<walk from/to>` **invokes SUMO's intermodal router at insertion** — a fact the design states exactly
once, in an aside at the end of Appendix A, and never resolves. Design §5.5 scopes only the
*junction-local* router and is explicit that this is *"much smaller than 'port the intermodal router'"*.

So there are three unbounded pieces with no owner: `<personFlow>` expansion (period/probability/number,
and the id-numbering scheme, which affects the **ordinal id tie-break** in §5.2 and therefore
determinism), `departPos`/`arrivalPos`/`departPosLat` semantics, and `<walk from/to>` route resolution.

**Resolution — recommend the narrow option:** require every committed `_sumoped` scenario to use
`<walk edges=>` with explicit `departPos`/`arrivalPos`, keeping the intermodal router permanently out of
scope (add it as **R-N8**). That means **rewriting Appendix A and Appendix B**, which are currently the
committed recipes — Appendix A already flags this for itself ("SP-0.2 should convert this"), Appendix B
does not. `<personFlow>` still needs porting (Tier C cannot be expressed without it) and needs its own
success condition pinning the generated ids, because they feed the determinism tie-break.

### G7 — Person insertion and departure are unspecified

Design §10.1 notes that `SpawnVehicle`'s queued-insertion shape transfers — *"a person also waits for its
depart time and for room on the sidewalk"* — and nothing anywhere says what "room on the sidewalk" means
or what happens when there is none. SUMO has real behaviour here (`MSTransportableControl`'s pending
queue, and the striping model's `myWaitingToEnter` insertion state, which the inventory already carries
as `WALK-OBSTRUCT-SELF-WAITING-EXEMPT` and `WALK-WAITINGTOENTER-CLEAR`).

Unspecified today: insertion when the departure stripe is occupied; `departPos` beyond the edge length;
`INIT-DIR-SINGLEEDGE` (`Striping.cpp:1604-1606` — initial direction from `departPos` vs `arrivalPos` on a
single-edge route, an inventory row with no scenario); and the retry cadence for a person that cannot be
inserted.

**Resolution:** a success condition on SP-3.5 covering insertion-blocked and single-edge-direction, plus
a Tier A scenario with `departPos > arrivalPos` on one edge (cheap — one ped, one edge, ~20 steps).

### G8 — The arrival / stopping-place family is in the inventory and in no task

Four inventory rows, all `DIRECT`-observable, none scoped:

| ID | what it does |
| --- | --- |
| `MIDOL-ARRIVAL-OBSTACLE` (`Striping.cpp:1260-1268`) | arrival obstacle placed `+minGap` past `arrivalPos` |
| `MIDOL-ARRIVAL-BLOCKED-STOPFULL` (`:1264-1266`) | stop is full ⇒ obstacle `−minGap` *before* it, ped stops short |
| `DISTTOLANEEND-FINAL-EDGE-MINGAP` (`:1869-1874`) | final-edge distance shrinks by `minGap` once waiting |
| `NEXTLANE-WA-ARRIVALPOS` (`:577-580`) | real `arrivalPos` vs edge-end as the walkingarea router's target |

Two of them need a `<busStop>` as the **walk destination**. R-N5 excludes *"ride/board stages"* — a walk
that ends at a stopping place is not a ride stage, so on the current wording these are **in scope by
default and unassigned**, which is the worst of both.

**Resolution — owner's call.** Either (a) add `<busStop>` as a walk destination to one Tier A scenario
and scope all four (cheap; `DISTTOLANEEND-FINAL-EDGE-MINGAP` fires on *every* ped's last edge regardless
of stops, so it is in scope no matter what), or (b) extend R-N5 to exclude stopping-place destinations
and move the three stop-dependent rows to coverage §8 as admitted holes. Note `DISTTOLANEEND-FINAL-EDGE-MINGAP`
cannot be deferred either way — it affects the exact stopping distance of every arriving pedestrian.

### G9 — The replication decision is a circular reference

Design §10.2 item 7 says of `Sim.Host/ReplicationPublisher`: *"It has **zero** occurrences of
'person'/'ped' — it is vehicle-only. **See §10.3**."* §10.3 decides where the *API* lives and never
mentions replication. SP-7.1 then says to add *"the replication decision from §10.3"* — a decision that
does not exist in either place.

**Resolution:** decide it explicitly. Recommend **out of scope for Phase 1** (add as **R-N9**): the ORCA
layer already has its own `PedReplicationPublisher`, Phase 1 has no host driving SUMO persons remotely,
and R7's acceptance is a local tutorial sample, not a replicated one. Then §10.2 item 7 becomes "not
Phase 1" and SP-7.1 loses a success condition it cannot discharge.

### G10 — R10's parallel-plan acceptance is discharged by nothing

R10 requires the person trajectory hash to *"match under `Engine.UseParallelPlan = true`."* But:

- SP-2.3's success condition is only *"hashing the same run twice gives the same value"* — single-threaded.
- Design §4.3 gate 2 explicitly **defers** par==single to when person-side parallelism is enabled:
  *"Until then the gate is that the hash is stable across two single-threaded runs (SP-2.3)."*

Those disagree, and the design's version misses why R10 is right: the person pass being single-threaded
does not make the run thread-count-independent, because **vehicles query persons from inside a possibly
parallel `PlanMovements`** (design §6.6.4 says exactly this). The race R10 is guarding against is on the
*read* side and is live in Phase 1.

SP-5.6(d) gestures at it (*"plus the par == single hash"*) without owning it.

**Resolution:** make it explicit and give it to SP-5.6 — on a person-bearing scenario with vehicles, both
the **vehicle** bench hash and the **person** trajectory hash must be identical with `UseParallelPlan`
on and off. Correct design §4.3 gate 2, which currently reads as though nothing parallel touches persons
in Phase 1.

### G11 — SP-2.1c cannot do walkingareas at Stage 2

SP-2.1c promises `(pos, posLat, stripe, speedLat)` *"from an FCD row + lane geometry"*. On a
**walkingarea** that is not available: `myRelX`/`myRelY` are measured along a `WalkingAreaPath` that the
model **computes** and the net file does not contain — design §10.5 says this in as many words
(*"`LaneArc` would additionally require publishing the `WalkingAreaPath` geometry, which the net file
does not contain — the model computes it"*). That geometry lands in SP-1.4 / SP-4.1.

Consequences, none of them fatal but all currently unsaid: the Stage-2 helper is complete only for normal
edges and crossings; SP-2.1d's replay cannot cover walkingarea steps until Stage 4; and the **S3 row of
the replayable-step-count table** — the stage-gate metric — has a denominator that silently excludes
them. Since curb waiting happens *on the walkingarea*, that is a large and interesting fraction of the
Tier B/C steps.

**Resolution:** state the dependency in SP-2.1c, and have the replay harness report **two** numbers per
stage — replayable steps and steps *excluded for missing geometry* — so the S3 → S4 jump is legible
rather than looking like a sudden win.

### G12 — R6's vehicle half has no success condition

R6 acceptance: *"one full red→green ped release **and one vehicle stop for a green ped phase**."*
SP-6.1's success condition covers only the ped release (*"`xwalk-tls-release` at exact parity … with the
ped's release step matching to the tick"*). The vehicle-side half — a vehicle held by a ped's green
phase — is asserted nowhere.

**Resolution:** add it to SP-6.1, and confirm `xwalk-tls-release` actually contains vehicle demand
conflicting with the ped phase; if it does not, the scenario is mis-authored under SP-0.4's own rule.

---

## 3. ROT — stale numbers and names

Each is one edit. They are listed because CLAUDE.md's orientation section records that every doc's copy
of a gate number in this repo has rotted at least once, and because two of these are *load-bearing for
scope*, not cosmetic.

| # | where | says | should say |
| --- | --- | --- | --- |
| R1 | `COVERAGE.md` §4 prose | "**Six** axes" | **eight** — the table below it has eight rows |
| R2 | `REQUIREMENTS.md` R12 | lists six axes | **eight** — missing **crossing priority** and **car movement**, the two found by the render pass. This one is load-bearing: SP-0.2 tells the author to satisfy "the six axes of coverage §4", so the two newest axes would go unbuilt |
| R3 | `REQUIREMENTS.md` R12 | "The **ten** scenarios above" | the table has **20 rows / 21 scenarios** |
| R4 | `TASKS.md` SP-1.4, SP-7.3 | "all **eight** scenarios" | the committed set, currently 21 |
| R5 | `TASKS.md` SP-7.4b, batch B0 | "out of **149**" | **148** everywhere else, and 148 is what the inventory header states |
| R6 | `TASKS.md` SP-1.1 | "all **91** committed scenarios" | 91 is the count of scenario *directories*; the repo-wide net tests enumerate **141** `*.net.xml` files. Say which |
| R7 | `REQUIREMENTS.md` R12 tier table | Tier C "**2–3**" | **4** — the tracker's own collision-baseline table already has rows for saturated, jam, **narrow** and **wide**, and the owner said 3 or 4 |
| R8 | `TASKS.md` + `TRACKER.md` | SP-5.6 listed **before** SP-5.5 | reorder, or renumber |
| R9 | `TRACKER.md` | defines **SP-5.1b** | `TASKS.md` has no such task (see G4) |
| R10 | `COVERAGE.md` §3 Tier C footprint | 1.45 MB FCD | measured **with** the attribute mask G1 removes; re-measure at SP-0.2b |

---

## 4. Checked and clean — do not re-investigate

Recorded so a later session does not spend the same time.

- **`AdaptToJunctionLeader` is a live consumer.** Called from the plan path at `Engine.cs:7827` and
  `:8011`. The *"NOT WIRED IN … parity-inert by construction"* comment at `Engine.cs:10004` belongs to
  `IsLeader`, a neighbouring method, and does **not** apply to the seam §6.1 hooks. CLAUDE.md
  §Measurement discipline item 3 satisfied — the mechanism has a live reader and a live caller of that
  reader. (What is *not* clean is the shape of the injection: G3.)
- **Crossing/walkingarea nets do not break the core parser.** Four committed nets already contain
  `function="crossing"` edges and walkingareas, and `scenarios/_ped/poc0-crossing-plaza` puts crossing
  internal lanes (`:c_c0_0 … :c_c3_0`) directly in a junction's `intLanes`. The four repo-wide invariant
  tests are green on them today. This retires most of G5's risk and strengthens design §3.1.
- **`ParameterCrossCheckTests` will not accidentally pick up `_sumoped`.** It enumerates
  `scenarios/*` at depth 1 and skips any directory without `golden.vtype.json`, so nested scenario
  directories are invisible to it — which is *also* why G2's cross-check needs the goldens to be placed
  where that test can see them, or the test extended.
- **`DemandParser` has zero person handling.** Expected — SP-3.5 owns it. Confirmed only so that "the
  parser silently ignores `<person>`" is on record as the *current* behaviour, matching
  `FcdParser.cs:24`'s silent drop of `<person>` rows.

---

## 5. Recommended order of correction

1. **Apply the ROT fixes** (§3) — mechanical, zero-risk, and two of them (R2, R7) change what B1 builds.
2. **G1** — one line in Appendix B; without it Tier C goldens are born unusable for the lateral ladder.
3. **G4 + G9 + G12** — task-list edits: define SP-5.1b, attach the eleven orphan scenarios to success
   conditions, decide replication out of scope, finish R6's acceptance.
4. **G2** — insert SP-1.0 (ped vType defaults + person `golden.vtype.json`) and correct design §2.3.
5. **G6, G8** — the two that need an owner decision on scope (intermodal router in or out; stopping-place
   arrivals in or out).
6. **G3, G5, G10, G11** — success-condition and standing-rule strengthening, none of which changes what
   gets built, all of which change what would be *accepted* as built.

G3 is the one to actually think about rather than edit: it is the only finding that could change a
stage's risk profile from "additive" to "touches the vehicle plan path".
