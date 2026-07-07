# DESIGN.md — Architecture of record

This is the "why" behind the project. `CLAUDE.md` holds the rules, `TASKS.md` holds the
work queue, and both point here when a decision needs justification. Read this once fully;
re-read the relevant section when a task is ambiguous.

## What we are building, and what we are not

We are reimplementing SUMO's **microscopic mobility algorithms** in C# / .NET 8 using a
data-oriented ECS layout, with behavioral parity to SUMO as the correctness bar and better
performance (cache-friendly memory, multithreading, later SIMD) as the payoff. We reuse
SUMO's toolchain and file formats: networks come from `netconvert` as `.net.xml`, demand
from `.rou.xml`, runs from `.sumocfg`. We do not reimplement `netconvert`, routing import,
OSM parsing, emissions, persons, or the mesoscopic model. We consume SUMO's *output* of the
hard preprocessing and port only the per-step simulation core.

Two axes, opposite answers on each — this is the single most important framing:

- **Algorithms and their calculation order** are SUMO's 20 years of fine-tuning. Copy them
  faithfully, close to line-for-line, from the vendored source. This is the sensitive part.
- **Data structures** (deep `MSVehicle` inheritance, pointer-linked leaders, per-object
  dispatch) are C++/OOP artifacts and the exact thing we are porting away from. Rebuilding
  them as struct-of-arrays is the *point*, not a risky deviation.

The only behavioral deviations we permit are those that ECS parallelism *structurally
forces*: memory layout, and the *timing* of structural mutations (deferred to a command
buffer at step end). Neither touches the numbers.

## Phasing: lane-based first, laneless later

Phase 1 is **lane-based SUMO** (Krauss + LC2013, discrete lanes, ordered per-lane vehicle
lists). This is the bulk of the value and the validation surface. Phase 2 is the
**laneless/sublane** heterogeneous model (spatial hashing, multiple shadow-lane leaders,
continuous lateral movement, hexagonal footprints) for Indian/Egyptian-style traffic.

Phase 1 must not make decisions that block phase 2, but must not build any phase-2
machinery either. The seam analysis below is exactly how we get that: cheap insurance now,
expensive machinery deferred.

## The four seams

Lane-based and laneless share almost the entire backbone: the plan/execute double buffer,
the command buffer, XML ingestion, the parameter model, integration. The differences are
concentrated in four seams. For each we take the cheap phase-1 version and design so the
phase-2 version slots in without a rewrite.

### Seam 1 — neighbor discovery behind an interface

Lane-based gets its leader in O(1) from the lane's sorted vehicle list. Laneless needs a
fixed-radius near-neighbor query returning *multiple* leaders (shadow lanes). Do not scatter
`getLeader()` calls through the systems. Put neighbor discovery behind one query — "give me
the constraining neighbors for ego" — and have car-following consume a **collection** of
constraints, reducing to the minimum safe speed even when the collection has size 1.

The reason this is phase-1 work and not speculation: **junctions already need the
multi-constraint reducer.** A vehicle approaching an intersection takes the min of its
lane-leader safe speed, any conflicting foe/link-leader constraints, and a red-light stop
constraint. So "next speed = reduction over N constraints" is required for correct phase-1
intersections regardless. Shadow-lane leaders in phase 2 become just one more constraint
source feeding the same reducer. Build the reducer because intersections demand it; laneless
comes along for free.

Phase 1: the query walks the sorted lane list, returns one leader. Phase 2: swap in the
spatial hash, return several. Logic unchanged — only the data source swaps.

### Seam 2 — lane-relative coordinates with a lateral field from day one

SUMO's primary position representation is lane-relative: lane identity plus longitudinal
arc-length `pos` along the lane centerline. Global x/y is *derived* for output and rendering
by walking the lane's shape polyline. Keep it that way in both modes — lane-relative is the
source of truth, x/y is derived, never the reverse.

The sublane model adds `posLat`, a continuous lateral offset from centerline. So include a
`LatOffset` float in the transform now and always write 0 in lane mode. Cost: one always-zero
float. Benefit: no schema migration and no output-pipeline rewrite when lateral goes live.
Keep lateral kinematics (`LatSpeed`, `LatAccel`) out of `Kinematics` for now, but design that
struct so adding them is additive, not a reshape.

### Seam 3 — lane membership stays primary; the spatial hash is only ever additive

Laneless is not "throw away lanes, switch to a grid." Even in sublane SUMO every vehicle
still has a primary lane and an ordered position in it. So the per-lane sorted vehicle list
is permanent infrastructure, built now as the topology of record. The spatial hash in phase 2
is an *auxiliary* index layered on top for lateral queries — nothing about the lane list gets
discarded. There is no decision to make here beyond "build the lane list well now."

### Seam 4 — route lane-change results through a lateral intent

Discrete LC2013 changes lane as a structural chunk move; SL2015 does continuous lateral
drift. Bridge them: the lane-change *decision* writes a lateral target into `MoveIntent`, and
an integration step applies it. Phase 1: the target is a lane index and integration snaps to
it (matching LC2013's discrete semantics; the physical chunk move is deferred to the command
buffer at step end). Phase 2: the target is a continuous offset and integration drifts toward
it. Same intent→integration shape both times. LC2013 keeps its own decision logic verbatim
from source; we only standardize where its output lands.

### What phase 1 explicitly does NOT build

The spatial hash, hexagonal footprints, SL2015, continuous lateral integration, and SIMD.
All deferred cleanly by the seams above.

## The plan/execute contract (the load-bearing invariant)

SUMO's step is two-phase: `planMovements` computes every vehicle's next speed from the
**start-of-step** state of its neighbors, then `executeMove` applies them all. A follower does
**not** see its leader's updated position within the same step.

This matters more than any performance concern, and it is counterintuitive, so state it
plainly: **the intuitive "optimization" of letting a follower react to its leader's freshly
updated position is not more accurate — it is a different simulation that will not match
SUMO's FCD.** Our ECS double buffer is *faithful* to SUMO precisely because it reads
start-of-step state in the plan phase and writes at step end. Reproduce SUMO here with
discipline; do not "improve" it.

Concretely:

- **Plan phase** (parallel-safe): each vehicle reads the world (immutable during this phase)
  and writes only to its own `MoveIntent`. No shared-state writes. This holds even when we
  run single-threaded in early tasks — honoring it from the start is what turns
  multithreading into a *scheduling* change later rather than a rewrite.
- **Conflict resolution** (for competing lane changes): a deterministic pass arbitrates when
  two vehicles target the same gap. Tie-break by a stable key (e.g. lowest entity id) so
  results are reproducible regardless of thread order.
- **Execute phase**: apply intents, integrate position, then flush structural changes (lane
  swaps) through the command buffer sequentially at step end.

## Determinism ladder

Exact trajectory parity requires zero stochasticity, because reproducing SUMO's exact PRNG
stream and draw order is brutal and not worth it early. So phase-1 scenarios strip
randomness: `sigma=0`, fixed depart, `actionStepLength=1`, teleport off, Euler integration.
That yields exact parity we can assert tightly.

Only later do we introduce `sigma>0` and switch those scenarios to **statistical** parity —
comparing distributions (headway, flow, throughput) rather than per-vehicle position. Each
scenario declares which mode it is in via `parityMode` in its `tolerance.json`, so it is never
ambiguous. When randomness is eventually needed, use per-entity seeded RNG (hashed from entity
id), never a shared `System.Random` — both because a global instance contends under threading
and because it would make results depend on thread scheduling.

Also make the integration method (Euler vs ballistic) a config flag, not a baked-in choice:
laneless later favors ballistic, both are valid in lane mode, and mismatched integration is a
classic source of phantom parity failures.

## Golden-file parity, and why it is hermetic

SUMO is a volatile, network-installed C++ binary; the test loop must not depend on it. So we
split ground-truth generation from testing:

- **Golden generation (rare, network, human-triggered):** `regen-goldens.sh` installs the
  pinned SUMO, runs each scenario with fixed seed/step/Euler, and writes `golden.fcd.xml`
  (behavioral trajectory) plus `golden.state.xml` (fully-resolved parameters). These are
  **committed**. The committed goldens *are* the executable spec — SUMO's tuning frozen into
  trajectories.
- **Test loop (constant, offline):** `dotnet test` runs our engine on the same inputs and
  diffs against the committed goldens within tolerance. **SUMO is never needed here.** This is
  what makes AI-driven iteration fast and hermetic.

Because the VM is volatile, the split doubles as a persistence rule: everything that must
survive is committed (source, harness, scenario inputs, goldens, tolerances, vendored SUMO,
`.claude/`); everything ephemeral (SUMO binary, build output, NuGet cache) is regenerated and
never trusted by tests. Provenance (`provenance.txt`, stamped with `SUMO_VERSION`, command,
date, input hashes) makes staleness detectable: if inputs changed but goldens did not, the
hashes disagree.

## Layered comparison metric

Apply simple→complex to the *metric*, not just the scenarios. SUMO's FCD carries lane-relative
`lane` + `pos` alongside global `x, y, speed, angle` (and `acceleration` when requested).

- **Early rungs compare `(lane, pos, speed)` only.** This isolates the car-following math and
  integration from all geometry/shape interpolation. If longitudinal position matches, the
  Krauss + integration core is correct independent of coordinate derivation.
- **Later rungs add `x, y, angle`** once junction geometry and rendering matter.

Tolerance is per-attribute (max-abs and RMSE over the trajectory) plus a first-divergence
step, and is a first-class per-scenario config value — float-order drift is unavoidable, so a
single global constant would be wrong.

## Two kinds of ground truth (parameter extraction)

FCD answers "does my sim *move* like SUMO?" The `--save-state` dump answers "did I
*initialize* the vType defaults correctly?" They are complementary, and the second is a
debugging shortcut: a wrong passenger default shows up as trajectory drift 40 steps in, but the
state dump catches the init error *directly*. So every scenario commits both, and a
parameter-cross-check task (later in the ladder) diffs our resolved vType defaults against
`golden.state.xml` as a fast fail *before* trajectory tests run — separating init bugs from
algorithm bugs.

The extractable parameter layers, in decreasing ease: model defaults baked into the C++ model
classes (read straight from source — the papers sometimes differ from shipped values); the
per-`vClass` defaults table; and the implicit "what to do when a value is missing" derivation
logic (largely sidestepped for the network by consuming post-`netconvert` `.net.xml`, but must
be replicated for vehicle/demand defaults). Prefer the committed `golden.state.xml` as the
authoritative resolved values, since it reflects SUMO's own defaulting after all flags apply.

## Parallelization policy (and the junction caveat)

Car-following parallelizes cleanly under the double buffer: decisions read start-of-step state,
write to isolated `MoveIntent`, integrate — embarrassingly parallel across edges/lanes.

Junctions are the exception. SUMO's junction resolution can be **order-sensitive**: who goes
first when vehicles compete for an internal lane may depend on processing order. Naive
parallelism gives non-deterministic junction outcomes that diverge from SUMO not because the
model is wrong but because scheduling differs. So we parallelize the *decision gathering* but
serialize (or carefully partition) the *conflict resolution*, with a deterministic tie-break.

Decide explicitly at the junction rung which goal applies: "match SUMO trajectory-for-
trajectory" or "be deterministic and plausible." You probably cannot have both, and that is
fine — but choose deliberately and record it in the scenario's notes.

On SIMD: treat the "8–16 vehicles per clock" framing from the project's Gemini docs as
optimistic. Car-following is gather-heavy (each follower reads a different leader at a
non-contiguous index), which is exactly what resists clean vectorization. Real early wins come
from struct-of-arrays cache locality and multithreading; SIMD on the arithmetic is a later,
more marginal gain and must not shape early architecture. Any optimization must preserve
trajectory parity against the validated baseline or be reverted / gated behind a fast-mode flag.

## Trust calibration on the source documents

The three project docs (Gemini deep-research outputs) are a good directional map but not a
spec. They contain at least one concrete formula error — the Krauss "Taylor expansion" is
written with the gap term outside the divided expression; the exact form is
`v_safe = -b*tau + sqrt((b*tau)^2 + V^2 + 2*b*g)`. They also invert the double-buffering
rationale (framing SUMO-faithful two-phase planning as a *degradation* versus a sequential
follower-sees-updated-leader loop, which is backwards). Treat every formula and ordering claim
as needing verification against the vendored source before it enters the engine. The parameter-
extraction methods they describe (source parse, TraCI interrogation, `--save-state` dump,
defaults page) are all legitimate, and the quoted passenger defaults (accel 2.6, decel 4.5,
sigma 0.5, tau 1.0, minGap 2.5) are correct.

## Build order (mirrors TASKS.md)

Bootstrap the harness green with no engine and no SUMO (self-test) → vendor SUMO + rung-1
golden → ingest + engine skeleton → Krauss single-vehicle free-flow parity → two-vehicle
following → safe-stop math → insertion → platoon → LC2013 → junction → traffic light →
parameter cross-check pass. Each rung is one committed, green, checkout-and-continue state.
Sublane/laneless is a separate phase layered on the validated lane-based core, enabled by the
four seams above.
