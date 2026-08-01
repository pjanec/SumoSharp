# GENEVA-ANALYSIS-RESUME — brief for the on-site session (real Geneva data)

**You are a fresh session on the physical machine that holds the real Geneva dataset.** You will
also receive a companion document from the 3D session describing the dataset layout and how it
launches the simulation — that document is authoritative for paths, launch commands, and the
viewer; THIS document is authoritative for engine state, instruments, method, and targets.

**Your goal, from the owner:** reproduce the remaining problematic situations on real Geneva
data and analyze their root causes — and/or compare this implementation against vanilla SUMO on
the same data to hunt the code differences behind different behaviour ("why SUMO clears the
junctions more easily and why its throughput is higher").

## 0. Where the engine stands (verify before believing)

Branch **`claude/sumosharp-traffic-bugs-g1y9hl`**, all pushed. Full `dotnet test -c Release`
from the repo root is the iron law: ParityTests 782/5/0 (goldens byte-identical), LiveCity.Tests
92/92, Pedestrians 324/324, Viewer.Motion 19, Host 6, DotRecast 2 — all green at head.
`Sim.Bench` hash `A134ED3716DDE7BC` (par==single). Trail: `JUNCTION-REALISM-SESSION-JOURNAL.md`
Entries 34–47 (every entry has BEFORE-predictions and AFTER-measurements — read 39–47 before
touching anything; the class decompositions there are measured, not guessed).

Recent structural fixes you must NOT re-derive: actual-lane link resolution (Entry 39, default);
corridor-follow jyArm 8 + mutual on-junction tie-break (Entry 40, gate-scoped); the C4-vii-a
cont-turn FRAME-BUG family swept across keepClear and six yield-arm sites (Entries 41–42,
default — this was the owner's "too-cautious" gridlock); congestion rerouting **DEFAULT-ON**
(Entry 47: period 60 s, probability 1.0, owner's decision).

## 1. Gates and instruments on the live-city surface (all process-global — set EVERY one explicitly in BOTH arms of any A/B)

| Var | Meaning |
| --- | --- |
| `LIVECITY_REROUTE` | **DEFAULT ON.** `0` = kill switch — REQUIRED in any arm meant to be reroute-free, and in every SUMO comparison unless you give SUMO `--device.rerouting.*` too |
| `LIVECITY_F3OCCUPANCY` | the junction physical-occupancy honesty gate (crossing/bay arms, jyArm 7/8). The owner tests with `1` |
| `LIVECITY_IGNOREBLOCKER` | patience seconds (auto-60 when the F3 gate is on; `-1` = SUMO parity) |
| `LIVECITY_URGENTFOLLOW` | `0` disables the Entry-31 urgent-strategic-follow arm — measured (3D session) as the dominant remaining mid-lane stall class (14 → 1 at matched windows) but it has its own battery wins; the trade is unresolved |
| `LIVECITY_WITNESS` | `1` = the diagnostic reporters (below) |
| `LIVECITY_TRACEVEH` | a def id (`__vehNNN`) → per-vehicle `[jy]`/`[bay]`/`[merge]`/`[keepclear]` stderr traces — the single most effective tool this workstream has |

Reporters on stderr every 20 s under `LIVECITY_WITNESS=1`:
`LIVECITY-REROUTES` (device liveness), `LIVECITY-HEADSTUCK` (stopped queue heads at non-red stop
lines with clear road — **now follows TWO blocker hops**: `head -> blocker ->> root`),
`LIVECITY-MIDLANE-STUCK` (stopped mid-lane with >25 m clear). Artifact classes (freeFlow,
sub-25 m stubs) are already excluded — a HEADSTUCK line on the current build is signal.
The def id in any line feeds `LIVECITY_TRACEVEH` directly.

Full gate table: `docs/ENV-GATES.md` (completeness is test-enforced).

## 2. The open problem classes, with the evidence in hand

1. **The unsignalled-junction standoff / internal-lane blocker chains.** 3D-session capture
   (rerouting off, 4000 cars): 7 chains where a queue head yields to a car stopped dead ON an
   internal lane; two pairs provably durable (identical positions across 20+ s); 3 of 5 roots
   were one hop deeper (hence the two-hop reporter); one chain is the PEDESTRIAN AMPLIFIER
   (car held inside a junction by peds — backlog item 4, first hard witness). Rerouting-off
   makes the class ~10× more frequent per sim-second — hunt with `LIVECITY_REROUTE=0`.
   START HERE: capture fresh two-hop chains, pick ONE durable pair, `LIVECITY_TRACEVEH` the
   ROOT vehicle, and name the constraint that pins it. The release guards that SHOULD fire
   (`!foe.WillPass`, `FoeKeepClearBlocked`, reservation distance, impatience ramp — Engine.cs
   arm-6 region, search `takesCrossingYield`) each have a comment explaining their intent;
   which one fails on this topology is exactly the open question.
2. **The crossing-streams interlock ring** (the owner's screenshot: two turning streams mutually
   blocked plus the lanes behind). `docs/DEADLOCK-RING-DESIGN.md` is signed off in principle
   ("sounds ok"): D1 (blocker-graph cycle detection + `LIVECITY-RING` witness, diagnostic-only)
   may be implemented when you need it — it is the right instrument for counting and aging these
   rings on Geneva. D2 (the gated break) needs D1 numbers first.
3. **The throughput gap vs SUMO** (owner: "why does SUMO clear junctions more easily / have
   higher throughput"). Known honest components of the gap: the F3 gate costs ~16% arrivals on
   the hour-horizon surface (2562 vs 3042) BECAUSE it refuses SUMO's junction interpenetration
   (`collision.check-junctions=false` — SUMO does not even detect it); SUMO's shipped defaults
   also teleport jams away (`time-to-teleport=300`). ANY comparison must use HONEST SUMO:
   `--time-to-teleport -1 --collision.action warn --collision.check-junctions true`
   (`docs/CONSTRAINT-high-realism-artefact-ladder.md` is binding: target SUMO's flow, never its
   method). Beyond those known components, unexplained per-junction clearance differences are
   real hunting ground — method in §3.

## 3. The SUMO-comparison playbook

- Check `sumo --version` — must match `SUMO_VERSION` (1.20.0); install if absent (this is
  allowed outside the offline test loop; the offline `dotnet test` must never invoke SUMO).
- The comparison harness exists: `scripts/run-density-diff.sh` + `Sim.DensityDiff` +
  `docs/DENSITY-DIFF-HARNESS-GUIDE/-TRACKER.md` — read the tracker's lessons first; several
  wrong conclusions were shipped from mislabeled comparisons. The demand-model rule is absolute:
  LiveCity closed-loop demand CANNOT measure capacity — use open-loop (`--inflow`) for any
  discharge/throughput claim, and give both sims the SAME demand (the demand-record sink /
  route-dump machinery exists — see the density-diff docs).
- The single highest-yield method this workstream found (5 reasoned interventions inert, then
  one trace found it in minutes): pick ONE junction that visibly clears differently, run BOTH
  sims on the same net+demand, and diff the FCD trajectories of ONE vehicle through it
  (`python3 scripts/classify-junction-overlaps.py` and `analyze-junction-realism-fcd.py` help;
  a raw side-by-side of one vehicle's (t, lane, pos, speed) through the junction is often
  enough). SUMO-side per-vehicle insight: `--fcd-output` + `--full-output` or TraCI.
- Vendored SUMO source at `/sumo/` (read-only) for reading what SUMO actually does once a
  behavioural difference is LOCALIZED — read after tracing, not before (the reasoned-hypothesis
  track record here is ~0-for-20).

## 4. Method discipline (each rule cost a real session)

- Journal BEFORE entries with falsifiable predictions before any change; AFTER entries with
  predictions-vs-measured. `docs/JUNCTION-REALISM-SESSION-JOURNAL.md` is the format.
- Trace first. One traced vehicle beats any amount of source reading or plausible reasoning.
- Label every measurement with its demand model (closed-loop vs open-loop) and its topology
  (box-grid numbers do NOT transfer: rerouting measured +36% on the box, +4–5% on Geneva-class).
- Both surfaces must accept a change (goldens AND the saturated demo); "goldens green" alone and
  "demo better" alone have each shipped a wrong conclusion.
- Commit instruments, never scratch probes. Run the FULL sln suite before pushing any
  default-behaviour change. Never introduce `System.Random`.
- Work from `git rev-parse --show-toplevel`. `demos/City3D` packs the engine as a NuGet:
  **clear `~/.nuget/packages/sumosharp.*` before repacking** or you will measure stale code.
- Ask the owner questions as plain chat text; design-first (design doc → owner review → code)
  for anything new. Deviations from SUMO must be argued against the artefact ladder.

## 5. Deliverables the owner expects from you

1. Per-class root-cause analyses of the reproduced Geneva situations (standoff chains, rings,
   any new class), each grounded in a trace, journaled with the evidence.
2. Where the cause is a code difference vs SUMO: the localized mechanism (file/arm/guard), the
   vendored-source reference, and a design-first fix proposal (no unreviewed behaviour changes).
3. A SUMO-vs-SumoSharp comparison on the Geneva cut with honest-SUMO flags and identical
   open-loop demand: throughput, junction-clearance times at the problem junctions, stall/ring
   counts — with every number labeled (demand model, gates, topology).
4. Updated resumption state (this doc's successor) so the next session starts warm.
