# CLAUDE.md — Operating rules for coding sessions

This repo reimplements SUMO's microscopic traffic-simulation algorithms in C# / .NET 8
(ECS, data-oriented, parallel-ready) with **behavioral parity to SUMO** as the
non-negotiable correctness bar. Performance matters, but a faster wrong answer is still
wrong. Read `docs/DESIGN.md` for the architecture of record; read `docs/TASKS.md` for the current
work queue. This file is the rules of the road.

## Interaction preferences

- **Ask questions as plain chat text, not interactive question widgets.** When you need a
  decision or clarification, write the options out in the message and let the user reply inline.
  Do not use the question/choice picker UI.

## Prime directives

1. **Work from the repo root.** Resolve it with `git rev-parse --show-toplevel`. Never
   hardcode an absolute VM path — the VM is volatile and its mount path is not stable.

2. **The VM is volatile. Only committed files persist.** If something must survive the
   session, it must be committed. Build artifacts, NuGet caches, and the SUMO install are
   all ephemeral and must never be relied upon by the test loop.

3. **Parity tolerance is the iron law.** No change — especially no performance
   optimization — may push any scenario's trajectory outside its committed
   `tolerance.json`. If a change moves a scenario out of tolerance, it is reverted or
   gated behind an explicit opt-in "fast mode" flag, never silently accepted.

4. **Follow SUMO on anything behavioral; deviate only where ECS parallelism structurally
   forces it.** Copy SUMO's algorithms and their calculation ordering faithfully. Rebuild
   only the memory layout and the *timing of structural mutations* (deferred to a command
   buffer). When in doubt, read the vendored source and match it.

## Ways of working: design-first, no ad-hoc development

**Design before code. No ad-hoc development unless the user explicitly asks for it.** When a new
feature or change is requested, do NOT jump to editing source. First produce, and get agreement on,
three committed documents (small features may fold the first two together, but the separation is the
default):

1. **A design document — HOW it will work.** Mechanisms, data structures, algorithms, the exact
   seams/APIs touched, the data flow, and the determinism/parity argument. This is where the thinking
   happens. Reference the requirements/spec doc for the WHAT; do not restate it.
2. **A task-description document — the work broken into stages and tasks.** Each task names its design
   reference (a section, not a copy), the files it touches, its dependencies, and — mandatory —
   **clear success conditions the implementor must fulfil** (specific test cases / assertions /
   measurable outcomes). References the design doc to avoid duplicating information.
3. **A task-tracker document — a plain checkable to-do list** referencing the task IDs from (2). This
   is the at-a-glance overview of what is done and what remains.

Only after these exist (and the user is happy with them) does implementation begin, task by task,
each closed only when its stated success conditions pass. This forces implementation to be thought
through up front and gives a clean overview of scope. Design docs live in `docs/`; keep them in sync
as understanding changes.

## Environment bootstrapping

The .NET 8 SDK itself is **not committed** — it is ephemeral, provisioned by the cloud
environment's setup script via `apt-get install -y dotnet-sdk-8.0`, and reinstalled from
scratch on every fresh VM. Microsoft's `dotnet-install.sh` endpoint
(`builds.dotnet.microsoft.com`) is blocked by the egress proxy policy; use the Ubuntu
archive mirror through `apt` instead. Never rely on the SDK being pre-committed or
pre-existing in the offline test loop — a fresh session gets it from the setup script, not
from the repo.

**This environment is NOT network-limited, and SUMO is available.** SUMO 1.20.0 (matching
`SUMO_VERSION`) is typically pre-installed at `/usr/local/bin/sumo` (with `netconvert` /
`duarouter` / `sumo-gui` alongside); check with `sumo --version`, and if it is ever missing you
may install it (e.g. `apt-get install -y sumo` or the pinned build) — the egress proxy does not
block it. This unlocks the **golden-regeneration** loop and, just as importantly, **direct
engine-vs-SUMO diagnosis** (run SUMO on the same net+demand, diff trajectories / tripinfo /
summary to localize a parity gap). This does NOT change the iron rule below: the **offline test
loop (`dotnet test`) still must never invoke SUMO** — it runs only against committed goldens, and
must pass on a fresh VM with no SUMO present. SUMO is for regenerating/authoring goldens and for
investigation, never a `dotnet test` dependency.

## The committed-vs-ephemeral split (memorize this)

**Committed (the project — survives VM death):**
- all C# source, the harness, the test projects
- scenario *inputs*: `*.net.xml`, `*.rou.xml`, `*.sumocfg`
- golden *outputs*: `golden.fcd.xml`, `golden.state.xml`, `provenance.txt`
- per-scenario `tolerance.json`
- vendored SUMO source at `/sumo/` (read-only reference)
- `SUMO_VERSION`, the `scripts/`, this file, `docs/DESIGN.md`, `docs/TASKS.md`, `.claude/`

**Ephemeral (regenerated, never trusted by tests):**
- the pip-installed SUMO binary
- `bin/`, `obj/`, `dotnet` build output
- the NuGet restore cache

## Two loops, kept strictly separate

**Offline test loop (constant, no network):**
```
dotnet test
```
This runs the engine against committed goldens. **SUMO is NOT needed here.** Never try to
install SUMO or reach the network inside this loop — it will stall.

**Golden regeneration (rare, network-enabled, ends in a commit):**
```
scripts/regen-goldens.sh      # installs SUMO fresh, regenerates FCD + state + provenance
git add ... && git commit      # goldens are committed, not computed at test time
```
Only run this when scenario inputs change or the SUMO version is bumped. Goldens are
**regenerated and committed**, never produced on the fly during testing.

## SUMO source and version

- `/sumo/` is the **read-only** algorithm reference. Port from it; never edit it.
- The pinned version lives in `SUMO_VERSION` (pip form, e.g. `1.20.0`). The matching git
  tag form is `v${SUMO_VERSION//./_}` (e.g. `v1_20_0`). Vendoring `/sumo/` at that tag is
  a manual, network-side step done by a human outside the offline loop:
  ```
  git clone https://github.com/eclipse-sumo/sumo.git
  cd sumo && git checkout v1_20_0 && rm -rf .git   # match SUMO_VERSION
  ```
- Source and goldens **must** come from the same version. `provenance.txt` records which
  version produced each golden; a mismatch against `SUMO_VERSION` means goldens are stale.

## Determinism (phase 1)

Parity is *exact* in phase 1 because randomness is stripped: `sigma=0`, fixed depart,
`actionStepLength=1`, teleport off, Euler integration. These are set in each scenario's
config, not overridden by scripts. Statistical parity (with `sigma>0`) comes much later
and is declared per scenario in `tolerance.json` via its parity mode. Never introduce a
`System.Random`; use per-entity seeded RNG so results are independent of thread order.

## Build / test commands

- Build: `dotnet build`
- Test: `dotnet test`
- A fresh clone into a blank VM must pass `dotnet test` **without** SUMO installed. If it
  doesn't, that's a bug in the harness, not a missing dependency.

## Subagents

Use the definitions in `.claude/agents/`. **Model routing (to conserve the expensive
model's budget): Opus orchestrates and owns judgment — planning and final-gate parity
review — while Sonnet does the volume work (routine porting, test-running).** Default to
delegating any large or mechanical task that does not require Opus-level reasoning to a
Sonnet subagent; keep Opus on decomposition, ambiguity resolution, and the accept/reject
gate. This is already wired in the agent definitions: `algorithm-porter` (sonnet) and
`harness-runner` (sonnet) do the porting and the `dotnet test` loop; `parity-reviewer`
(opus) is the final gate. Because a subagent starts from near-zero context, every
delegation must name: the exact `/sumo/` source file to read, the target C# file(s), the
scenario, the command to run, and the numeric done-condition. Nothing crosses the boundary
except the prompt.

### The orchestration loop (Opus orchestrates, Sonnet implements, Opus reviews hard)

The standing division of labour once the design-first docs exist:

1. **Opus orchestrates.** Opus has already detailed the tasks and their success conditions up front, so
   each task should be small and unambiguous enough for a cheaper implementor. Opus groups tasks into a
   **batch** and delegates it to a **Sonnet implementor** (a subagent) with a self-contained prompt: the
   task IDs, the design/section references, the exact files, and the precise success conditions.
2. **Sonnet implements** the batch and reports back.
3. **Opus reviews HARD — and does not believe the report.** Opus independently verifies that each task's
   success conditions are *actually* met: read the diff, read the tests to confirm they assert the real
   condition (not a vacuous or self-fulfilling check), and re-run `dotnet test` / the hash gate itself
   rather than trusting the implementor's summary. A task is accepted (its tracker box ticked) only when
   Opus has confirmed its success conditions first-hand. Anything not confirmed goes back as the next
   batch.
4. **Repeat** — Opus ticks the tracker and delegates the next batch.

Reserve Opus for orchestration, decomposition, ambiguity resolution, and this accept/reject gate;
delegate the implementation volume to Sonnet. The review is the load-bearing step — treat every "done"
as unverified until proven.

## Reporting a parity failure

When a scenario is out of tolerance, report: scenario name, first-divergence step,
per-attribute max-abs error and RMSE, and the suspected cause (init/vType vs
algorithm vs integration/ordering). Prefer diffing `golden.state.xml` first to rule out a
vType-default init bug before chasing the trajectory.
