# docs/ — index

243 files at this level plus `archive/`, so start here rather than with `ls`. This page is **entry points
per area**, not a catalogue: for each area it names the one or two docs to read first, and those docs point
onward to their own siblings. If you cannot find something, `grep -rl` for the mechanism name — the file
naming is consistent enough that it works.

The organising rules (and the measurement that produced them) are in
[`DOCS-HOUSEKEEPING-PLAN.md`](DOCS-HOUSEKEEPING-PLAN.md).

## Start here

| Doc | What it is |
| --- | --- |
| [`../CLAUDE.md`](../CLAUDE.md) | **Rules of the road.** Prime directives, the committed-vs-ephemeral split, the two test loops, and the measurement-discipline list. Read before changing behaviour. |
| [`DESIGN.md`](DESIGN.md) | **The architecture of record.** ECS layout, the plan/execute split, the command buffer, determinism. |
| [`TASKS-TODO.md`](TASKS-TODO.md) | **The live queue.** Open items only, with the current standing gate at the top. The single authority on what is in flight and who owns it. |
| [`TASKS-DONE.md`](TASKS-DONE.md) | The archive of completed work, with the full characterisation of each item. |
| [`ENV-GATES.md`](ENV-GATES.md) | **Every environment gate**, what it sets, and what an *unset* value means. Read before any A/B or benchmark: they are process-global, several are behavioural, and one breaks 14 goldens when set. Completeness enforced by a test. |
| [`../scenarios/README.md`](../scenarios/README.md) | **The test data.** What each scenario group is, which have committed SUMO goldens and which are behavioural-only, and which dataset to pick for a given need. |
| [`../README.md`](../README.md) | What the project is and how to run it. |

## Tutorials — driving the engine from your own code

A three-step ladder, each backed by a runnable sample that `Traffic.sln` compiles, so the snippets cannot
rot: [`TUTORIAL-VEHICLES.md`](TUTORIAL-VEHICLES.md) (load a net, spawn traffic, inject external agents) →
[`TUTORIAL-PEDESTRIANS.md`](TUTORIAL-PEDESTRIANS.md) (bake a navmesh, O/D demand, the two-level LOD) →
[`TUTORIAL-LIVE-CITY.md`](TUTORIAL-LIVE-CITY.md) (couple both, and measure the coupling causally).
[`TOOLS.md`](TOOLS.md) is the CLI-side companion: which of the 17 entry points to run, and the caveats.

## How to read a doc in here

**The triad convention.** `CLAUDE.md` mandates design-first, so most features have three files:
`X-DESIGN.md` (how it works — mechanisms, data structures, the parity argument), `X-TASKS.md` (the work
broken into stages with explicit success conditions), `X-TRACKER.md` (a checkable list of those task IDs).
Read the DESIGN for understanding, the TRACKER for status. There are 67 designs and 49 task/tracker files
because the workflow produces a set per feature — the fix for that is this index, not fewer files.

**Status banners.** A doc that is not self-evidently current carries one blockquote under its title:

- `CURRENT` — still the thing to read.
- `ARCHIVED` — historically valuable, not current guidance. Says what superseded it.
- `SUPERSEDED by X` — read X instead.
- `HISTORICAL TRAIL` — **contains claims later disproven.** The banner says which doc holds the correction.
  Do not act on these without reading the correction first.
- `NEVER IMPLEMENTED` — a considered-and-parked design. Legitimate to keep; do not go looking for the code.

**No banner** means the doc was reviewed on 2026-07-28 and found to still hold, or it is an append-only log
whose own text tracks its state.

**A doc can be wrong.** These are working records, not specifications, and several are load-bearing
*because* they record a failure. If a doc contradicts the source, the source wins — and please fix the doc
or add it to `TASKS-TODO.md`.

## Core engine and SUMO parity

| Entry point | Covers |
| --- | --- |
| [`DESIGN.md`](DESIGN.md) | The architecture. |
| [`PHASE2-SUBLANE.md`](PHASE2-SUBLANE.md) | The sublane / lateral model: what is exact and what is deferred. |
| [`RUNG9B.md`](RUNG9B.md), [`RUNGA2.md`](RUNGA2.md) | Cold-start decision records for unsignalized priority yielding and speed-gain overtaking. Good models of the format. |
| [`LANELESS-DIRECTION.md`](LANELESS-DIRECTION.md) + [`LANELESS-HANDOFF.md`](LANELESS-HANDOFF.md) | The laneless / ORCA direction and its current state. |
| [`RAIL-SUPPORT.md`](RAIL-SUPPORT.md) | Rail rungs R1–R6, all landed, plus the scoped deferrals. |
| [`UNIFIED-SOLVER.md`](UNIFIED-SOLVER.md) | A joint plan/execute solver — **proposed, measured, declined.** Kept for the measurement. |
| [`SPATIAL-OPT.md`](SPATIAL-OPT.md) | Cache-local parallelization of the plan phase; the probe is built, the segmented store is not. |

## Junctions — overlap, discharge, deadlock

The densest cluster in the repo and the source of most of `CLAUDE.md`'s measurement discipline.

| Entry point | Covers |
| --- | --- |
| [`F3-SESSION-LOG.md`](F3-SESSION-LOG.md) | **Read this first.** Append-only log for the junction-overlap and discharge work; §4 lists what the original brief got wrong, §6 the next action. Cited by `CLAUDE.md`. |
| [`F3-JUNCTION-OVERLAP-DESIGN.md`](F3-JUNCTION-OVERLAP-DESIGN.md) + `-TASKS`/`-TRACKER` | The admission-gate work. T1.6–T1.9 are genuinely open and blocked on each other. |
| [`DENSITY-DIFF-HARNESS-TRACKER.md`](DENSITY-DIFF-HARNESS-TRACKER.md) | The engine-vs-*honest*-SUMO harness; carries every measured table. Cited by `CLAUDE.md`. |
| [`CONSTRAINT-high-realism-artefact-ladder.md`](CONSTRAINT-high-realism-artefact-ladder.md) | **Binding.** What we may not copy from SUMO — target its flow, never its method. |
| [`NEED-junctionyield-impatience-saturation.md`](NEED-junctionyield-impatience-saturation.md) | Five reasoned interventions that were inert before one trace found the cause. Cited by `CLAUDE.md`. |
| [`ISSUE2-JUNCTION-KEEPCLEAR-DESIGN.md`](ISSUE2-JUNCTION-KEEPCLEAR-DESIGN.md), [`ISSUE2-JUNCTION-TELEPORT-DESIGN.md`](ISSUE2-JUNCTION-TELEPORT-DESIGN.md) | The "Issue 2" teleport family. |
| [`LANE-CHANGE-OVERLAP-SPEC.md`](LANE-CHANGE-OVERLAP-SPEC.md) + `-DESIGN`/`-STATUS`/`-TRACKER` | Lane-change-into-occupied. |
| [`F3-ISLEADER-PORT-DESIGN.md`](F3-ISLEADER-PORT-DESIGN.md) + `-TASKS`/`-TRACKER` | The `isLeader()` port. |

## High density and calibration

| Entry point | Covers |
| --- | --- |
| [`HIGH-DENSITY-HANDOFF.md`](HIGH-DENSITY-HANDOFF.md) | Entry point for the P2-G/P2-H/X1 cluster. |
| [`HIGH-DENSITY-PLAN.md`](HIGH-DENSITY-PLAN.md) | The tracker across all high-density stages. |
| [`HIGH-DENSITY-CALIBRATION-DESIGN.md`](HIGH-DENSITY-CALIBRATION-DESIGN.md) + `-TASKS`/`-TRACKER` | The SumoData-driven throughput-knee investigation. §2.3.x sections absorb several archived resume notes. |
| [`CALIBRATION-KNEE-INDEX.md`](CALIBRATION-KNEE-INDEX.md) | Index of the knee repros. ⚠ Its `arterial-tjunction` row records a conclusion that [`GETBESTLANES-RESUME.md`](GETBESTLANES-RESUME.md) later falsified — see `TASKS-TODO.md`. |
| [`SERVE-PATH-PLAN.md`](SERVE-PATH-PLAN.md) | The drop-in-`sumo`-binary effort, end to end, through to the authorized merge. |

## Pedestrians

| Entry point | Covers |
| --- | --- |
| [`PEDESTRIANS.md`](PEDESTRIANS.md) | **The front door.** What exists, where the code is, how to run it. |
| [`PEDESTRIAN-TRACKER.md`](PEDESTRIAN-TRACKER.md) | The authoritative done/parked map across every stage. |
| [`PEDESTRIAN-OVERVIEW.md`](PEDESTRIAN-OVERVIEW.md) / [`PEDESTRIAN-DESIGN.md`](PEDESTRIAN-DESIGN.md) | The WHAT and the HOW — layered agent, LOD axis, the navigation seams. |
| [`PEDESTRIAN-NAVMESH-CONTRACT.md`](PEDESTRIAN-NAVMESH-CONTRACT.md) | **Normative.** The `IPedNavigation` contract, including mandatory elevation. |
| [`PEDESTRIAN-SESSION-HANDOFF.md`](PEDESTRIAN-SESSION-HANDOFF.md) | State-of-the-subsystem handoff for a fresh session. |
| `PEDESTRIAN-P6-*`, `PEDESTRIAN-P8-*`, `PEDESTRIAN-R*` | Perf, navmesh connectivity, demand/density, and scenario-registry stages. The tracker says which landed. |
| [`PEDESTRIAN-LIVELINESS-DESIGN.md`](PEDESTRIAN-LIVELINESS-DESIGN.md), [`PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md`](PEDESTRIAN-WEAVE-PRODUCTION-DESIGN.md) | Believable motion: activity timelines, lateral weave. |
| [`PEDESTRIAN-DDS-TRANSPORT-DESIGN.md`](PEDESTRIAN-DDS-TRANSPORT-DESIGN.md) | The live DDS binding for the ped replication surface. |

## Live city — the coupled cars + pedestrians demo

The largest active cluster. Sessions coordinate through `TASKS-TODO.md`'s in-flight table.

| Entry point | Covers |
| --- | --- |
| [`LIVE-CITY-STATUS.md`](LIVE-CITY-STATUS.md) | Where the demo stands. |
| [`LIVE-CITY-HARNESS-GUIDE.md`](LIVE-CITY-HARNESS-GUIDE.md) | How to run it headless and what the flags do. |
| [`COORDINATION-livecity-realism-sessions.md`](COORDINATION-livecity-realism-sessions.md) | Session boundaries and no-touch lists. Read before editing a shared file. |
| [`LIVE-CITY-REALISM-COORDINATOR-HANDOFF.md`](LIVE-CITY-REALISM-COORDINATOR-HANDOFF.md) | Cross-session orientation and the owner-replay recipe. |
| [`LIVE-CITY-CAR-YIELDS-PED-DESIGN.md`](LIVE-CITY-CAR-YIELDS-PED-DESIGN.md) + `-TASKS`/`-TRACKER` | Cars yielding to pedestrians. Done; the TRACKER's "Still worth doing" is live. |
| [`LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md`](LIVE-CITY-PED-LOD-LIFECYCLE-DESIGN.md) + `-TASKS`/`-TRACKER` | Ped promote/demote. ⚠ §3.2 describes a fix that was designed and then dropped. |
| [`LIVE-CITY-ARBITRARY-NET-DESIGN.md`](LIVE-CITY-ARBITRARY-NET-DESIGN.md) | Loading a real road net instead of the synthetic box. |
| [`EXTERNAL-NET-VIEWER-DESIGN.md`](EXTERNAL-NET-VIEWER-DESIGN.md) + `-TASKS`/`-TRACKER` | External net loading in City3D, float recentring, live density dials, 3-D pedestrian elevation, the baked terrain field. |
| [`LIVE-CITY-THREADED-TICK-DESIGN.md`](LIVE-CITY-THREADED-TICK-DESIGN.md) | Running the engine tick off the render thread. **§8 is what actually landed** and corrects §5. |
| [`LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md`](LIVE-CITY-DEMO-INTEGRITY-FINDINGS.md) | F1–F4 from the replay review, with root causes. |
| `LIVE-CITY-15-*` | The Issue-15 family (gridlock, into-occupied, dead-lane drive-through, per-area LOD). |

## Viewers, rendering and dead reckoning

| Entry point | Covers |
| --- | --- |
| [`SUMOSHARP-VIEWER-DR-SMOOTHING.md`](SUMOSHARP-VIEWER-DR-SMOOTHING.md) | **Living doc**, and the best single read on viewer motion. §10/§11 track their own supersessions. |
| [`VIEWER-KINEMATIC-SMOOTHING-DESIGN.md`](VIEWER-KINEMATIC-SMOOTHING-DESIGN.md) + `-TASKS`/`-TRACKER` | The shared `KinematicReconstructor` (no-slip rear-axle drag). |
| [`SUMOSHARP-DEADRECKONING.md`](SUMOSHARP-DEADRECKONING.md) | The networked DR / production-render layer. |
| [`SUMOSHARP-DR-ERROR-PUBLISHING-DESIGN.md`](SUMOSHARP-DR-ERROR-PUBLISHING-DESIGN.md) | Publish-on-prediction-error instead of on an interval. |
| [`SUMOSHARP-NATIVE-VIEWER.md`](SUMOSHARP-NATIVE-VIEWER.md) | The raylib + DDS 10 k-vehicle viewer. |
| [`DEMO-CITY3D-DESIGN.md`](DEMO-CITY3D-DESIGN.md) + `-TASKS`/`-TRACKER` | The Godot 3-D city demo. |
| [`VIZ-UNIFICATION-DESIGN.md`](VIZ-UNIFICATION-DESIGN.md) + [`VIZ-UNIFICATION-STATUS.md`](VIZ-UNIFICATION-STATUS.md) | One `VizReplayBuilder` behind the whole HTML replay gallery. |
| [`EXTERNAL-AGENTS-VIZ.md`](EXTERNAL-AGENTS-VIZ.md) | **Public integration guide** for feeding external agents in as obstacles. |
| [`DEMOS.md`](DEMOS.md) | The demo gallery. |

## Performance

| Entry point | Covers |
| --- | --- |
| [`PERF-HANDOVER.md`](PERF-HANDOVER.md) | **Read first.** The on-target (16-core Windows) measurements, what shipped, what regressed, and an explicit check-before-repeating experiments log. |
| [`LIVE-CITY-PERF-SESSION-LOG.md`](LIVE-CITY-PERF-SESSION-LOG.md) | Append-only, one entry per attempt with before/after — **including the NULLs**. The coupled cars+peds target. |
| [`LIVE-CITY-PERF-DESIGN.md`](LIVE-CITY-PERF-DESIGN.md) + [`-TRACKER`](LIVE-CITY-PERF-TRACKER.md) | The measurement framework and the instrument it mandates. |
| [`PERF-ROADMAP.md`](PERF-ROADMAP.md) | Superseded by `PERF-HANDOVER.md`; one of its claims is falsified for the coupled host (see the banner). |
| [`BENCHMARK-INSTRUCTIONS.md`](BENCHMARK-INSTRUCTIONS.md), [`BENCHMARK_SPEC.md`](BENCHMARK_SPEC.md) | How to run the benches. |
| [`MEASURE-WRITE-RATE-RESULTS.md`](MEASURE-WRITE-RATE-RESULTS.md) | **The replication write rate**, measured: ~0.64 updates/car/s flat from 500 to 4000 cars, 125 KiB/s at 4000. Explains why the `laneChange` share is ~half the stream and yet only 0.7% is a real lane change. Instrument: `src/Sim.MeasureWriteRate`. |
| [`DOMAIN-DECOMP.md`](DOMAIN-DECOMP.md), [`PEDESTRIAN-P6-2-RESULTS.md`](PEDESTRIAN-P6-2-RESULTS.md) | Region decomposition, car side and ped side. The ped result missed its target — do not re-attempt phase 1. |

## Panic evacuation

[`PANIC-EVAC-OVERVIEW.md`](PANIC-EVAC-OVERVIEW.md) is the entry point; phases 1, 2, 3, 5 and 5-Tier-2 each
have a design + tasks + tracker, and [`PANIC-EVAC-PHASE4-DECISION.md`](PANIC-EVAC-PHASE4-DECISION.md)
records why phase 4 went the way it did. This cluster tracks its own implementation unusually well — the
designs match the code down to method names, and they are a good model to copy.

## Public API and packaging

| Entry point | Covers |
| --- | --- |
| [`SUMOSHARP-API.md`](SUMOSHARP-API.md) | The library API of record: handles, the obstacle store, the SoA read surface, the async runner. |
| [`PACKAGES.md`](PACKAGES.md) | What ships as a NuGet package. |
| [`SUMOSHARP-PACKAGING-DESIGN.md`](SUMOSHARP-PACKAGING-DESIGN.md) + `-TASKS`/`-TRACKER` | The à-la-carte packaging split and why natives are quarantined. |
| [`SUMOSHARP-SERVE-PATH-DROP-IN.md`](SUMOSHARP-SERVE-PATH-DROP-IN.md) | Replacing the `sumo` binary. Requirements; `SERVE-PATH-PLAN.md` is the verified how. |

## The IG bridge

[`IGBRIDGE-RESUME.md`](IGBRIDGE-RESUME.md) is the live working state. Around it:
[`IGBRIDGE-REQUIREMENTS.md`](IGBRIDGE-REQUIREMENTS.md), [`IGBRIDGE-DECISIONS.md`](IGBRIDGE-DECISIONS.md),
[`IGBRIDGE-DESIGN.md`](IGBRIDGE-DESIGN.md), `-TASKS`/`-TRACKER`,
[`IGBRIDGE-METHODOLOGY.md`](IGBRIDGE-METHODOLOGY.md),
[`IGBRIDGE-INTEGRATION-GUIDE.md`](IGBRIDGE-INTEGRATION-GUIDE.md),
[`IGBRIDGE-VERSIONS.md`](IGBRIDGE-VERSIONS.md),
[`IGBRIDGE-HTML-REPLAY-GUIDE.md`](IGBRIDGE-HTML-REPLAY-GUIDE.md).

## `NEED-*` — one mechanism gap per file

21 short notes, each naming a single missing or mis-ported SUMO mechanism, usually with a repro and a trace.
Named `NEED-<mechanism>.md`. They are the best-shaped documents in the repo: narrow, evidence-first, and
falsifiable. Several are referenced from `TASKS-TODO.md` as open work —
[`NEED-linkstatechar-cont-entry-link.md`](NEED-linkstatechar-cont-entry-link.md) and
[`NEED-stuck-reroute-blind-inside-junctions.md`](NEED-stuck-reroute-blind-inside-junctions.md) in
particular. Two record refuted candidate fixes; their banners say so.

## Cross-project coordination (SumoSharp ↔ SumoData)

This engine is consumed by a sibling project that supplies nets, demand and POI data. Those negotiations
happen as documents, and they accumulate in rounds — the **response** usually supersedes the **request**.

| Doc | Role |
| --- | --- |
| [`SUMOSHARP-LIVE-CITY-DECISIONS.md`](SUMOSHARP-LIVE-CITY-DECISIONS.md) | **The frozen contract** the live-city designs build against. Current. |
| [`SUMOSHARP-DEMO-CITY-REQUIREMENTS.md`](SUMOSHARP-DEMO-CITY-REQUIREMENTS.md), [`SUBAREA-DEMO-CITY-DESIGN.md`](SUBAREA-DEMO-CITY-DESIGN.md) | The demo-city build spec, from their side. |
| [`SUBAREA-FOR-PEDESTRIAN-SESSION.md`](SUBAREA-FOR-PEDESTRIAN-SESSION.md) | Their brief on the sub-area system and its compatibility requirements. |
| `COORDINATION-*.md` | Boundaries between concurrent workstreams. |

Some of these reference docs that live in *their* repo, not this one; those links will not resolve here.

## Subdirectories

| Path | What it is |
| --- | --- |
| [`archive/`](archive/) | 22 superseded session ephemera — resume notes, one-shot prompts, handoffs whose sessions finished. Each carries a banner saying what superseded it and why it was kept. Nothing here is current guidance; several record refuted hypotheses on purpose. |
| [`handoffs/`](handoffs/) | Briefs for sessions that are still live or whose checklist is still the recipe for re-running a test. Currently just the GPU visual-test sign-off. |
| [`reports/`](reports/) | Raw measurement output — sweeps, density diffs, SUMO-side XML. Data, not prose. |
| [`reference/`](reference/) | **Vendored, read-only.** Material imported from elsewhere as a porting blueprint. Not ours to rewrite, never built or tested. |
| [`weave-demo/`](weave-demo/) | Ours: the gallery weave demo page. |

## Conventions worth knowing before you write a doc here

- **Put the status in the header, and keep it there.** The most common rot in this tree is a "Status:" line
  that the same document contradicts 90 lines further down. Prefer the pattern
  `LIVE-CITY-THREADED-TICK-DESIGN.md` uses: an explicit "§8 — what actually landed" section that corrects
  the original sketch in place rather than silently leaving it wrong.
- **Pinned test counts and bench hashes go stale immediately.** They are scattered across dozens of docs at
  every historical value from 151 to 775. In a historical narrative that is fine — leave it. In an
  *instruction* ("this must stay 654/4") it is a trap. If you need a current number, `TASKS-TODO.md` has it;
  don't copy it somewhere it will rot.
- **Label a measurement with the demand model and the yardstick that produced it** (`CLAUDE.md`
  measurement-discipline #4 and #5). Numbers without that label have repeatedly had to be retracted.
- **Record the failures.** A doc describing something that did not work is worth more here than one
  describing something that did, because the success is also in the code and the tests, and the failure is
  nowhere else.
