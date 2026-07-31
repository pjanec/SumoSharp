# ENV-GATES.md — every `LIVECITY_*` / `SUMOSHARP_*` / `CITY3D_*` environment gate

**This table is completeness-checked by a test.** `tests/Sim.ParityTests/EnvGateDocumentationTests.cs`
scans `src/` and `demos/` for every environment gate the code reads and fails if one is missing from this
file, or if this file lists one the code no longer reads. So the *inventory* cannot drift. The
*descriptions* are hand-written and can — if one contradicts the source, the source wins, and please fix
it here.

## Read this first: these are process-global, and that has cost real measurements

An environment variable is process-wide. Nothing in a gate's value records *who* set it, so **a value
inherited from your shell is indistinguishable from one you set deliberately.** That is not hypothetical
here: an inherited gate once produced a `392`-vs-`1295` "OFF" baseline (`Sim.DensityDiff/Program.cs:65`
carries the note), and it is why `CLAUDE.md` measurement-discipline #10 exists.

**The rule for any A/B: set every gate you care about EXPLICITLY, in BOTH arms.** Never leave one to the
ambient environment because you believe it is unset. `Sim.BenchLiveCity` prints the observed value of every
gate it knows about (`PrintEnvGates`) precisely so a run's log proves what it measured; the
`AllLiveCityGateVars` list in `tests/Sim.LiveCity.Tests/HeadOfQueueStallProbeTests.cs` is the
set-them-all-explicitly precedent to copy.

## The three-state trap, and why `EnvGate` exists

There are two forms in the codebase, and they differ **only when the variable is absent**:

| Form | Absent ⇒ | Safe when |
| --- | --- | --- |
| `EnvGate(name, engineDefault)` | **the engine's own default** | always — this is the correct form |
| `GetEnvironmentVariable(name) == "1"` | **`false`**, overriding the engine | only if the engine default is also `false` |

The second form is a *two-state override that silently forces OFF*. It was harmless while every junction
gate defaulted to `false`, and became a live bug the moment PR #13 (`604ad72`) flipped seven of them to
`true`. `LiveCitySim.cs:1588`'s `EnvGate` was written in that same PR to fix it, with a comment spelling
out the failure mode: the demo would have run with all seven gates disabled while the engine, the goldens
and every other host had them enabled, and "the demo still gridlocks" would have read as a failed fix
rather than a wiring mistake.

> ### ✅ `Sim.Sumo/SumoShim.cs` used the unsafe form — FIXED (journal Entry 19)
>
> The `sumosharp` drop-in binary (what the SumoData pipeline invokes via `SUMO_BINARY`) set three gates
> with `== "1"` while all three engine defaults are `true`, so **every invocation that did not set them ran
> with three junction gates OFF that the engine, the goldens and the LiveCity host all had ON**:
>
> | Gate | `Engine` default | drop-in binary, env unset — before → after |
> | --- | --- | --- |
> | `SUMOSHARP_CONTTURNFIX` → `ContTurnInsideJunctionGate` | `true` | **`false`** → `true` |
> | `SUMOSHARP_ISLEADERFIX` → `JunctionIsLeaderGate` | `true` | **`false`** → `true` |
> | `SUMOSHARP_INTERNALJUNCTIONFIX` → `InternalJunctionAdmissionGate` | `true` | **`false`** → `true` |
>
> All three now use `EnvGate(name, engineDefault)`. **This was not theoretical.** Two shim-driven parity
> tests were silently calibrated in the gates-off configuration, and `DenseFlowDeadLaneDrainTests` carried
> a "hard invariant" arrivals floor of 290 that **the shipped engine could not reach** (289 when pinned) —
> invisible precisely because the gates were quietly off.
>
> **Two guards now stand where none did.** `SumoShimUnsetGateFallbackTests` runs the shim with the
> variables ABSENT and asserts byte-identical output to running them explicitly at the engine defaults
> (with a vacuity guard proving the scenario discriminates them at all);
> `EnvGateDocumentationTests.GatesWhoseEngineDefaultIsTrue_AreNotReadWithTheTwoStateForm` fails the build
> on any reintroduction, naming the file and line. Shim-driven **tests** should still pin explicitly via
> `tests/Sim.ParityTests/JunctionGateEnv.cs` — pinning states intent and survives either shim behaviour.
>
> ⚠ Any SumoData-side measurement taken through `SUMO_BINARY` **before this fix** ran with three junction
> gates off, and is not comparable with one taken after.

## Classification, and what it means for you

- **behavioural** — changes trajectories. Subject to `CLAUDE.md` prime directive 3: may not push any
  scenario out of `tolerance.json`. Never flip one to make a number look better without running both the
  goldens and the open-loop discharge test.
- **refuted** — behavioural, measured, and measured *worse* or unfaithful. Kept as a gate so the
  measurement is reproducible. **Do not re-attempt without reading the linked evidence.**
- **perf** — proven bit-identical either way; exists only so a speedup can be A/B'd.
- **diagnostic** — writes logs or dumps; does not touch the simulation.
- **scenario** — sets up the run (counts, rate, timestep). Changes results trivially and on purpose.

## Junction / lane-change gates (behavioural)

The seven that PR #13 defaulted ON are marked ⑦. All read through `EnvGate`, so **absent = the engine
default**, and `=0` is the only way to turn one off.

| Gate | Sets | Unset ⇒ | Class |
| --- | --- | --- | --- |
| `LIVECITY_CONTTURNFIX` | `Engine.ContTurnInsideJunctionGate` | `true` ⑦ | behavioural |
| `LIVECITY_ISLEADERFIX` | `Engine.JunctionIsLeaderGate` | `true` ⑦ | behavioural |
| `LIVECITY_INTERNALJUNCTIONFIX` | `Engine.InternalJunctionAdmissionGate` | `true` ⑦ | behavioural |
| `LIVECITY_INTERNALJUNCTIONENTRYORDER` | `Engine.InternalJunctionAdmissionEntryOrder` — sub-gate of the line above, inert without it | `true` ⑦ | behavioural |
| `LIVECITY_COLOCATIONSYMMETRYBREAK` | `Engine.ColocationSymmetryBreak` — lets an already-overlapping same-lane pair separate | `true` ⑦ | behavioural |
| `LIVECITY_LANECHANGEARBITRATION` | `Engine.LaneChangeArrivalArbitration` — stops two cars taking one slot in one step | `true` ⑦ | behavioural |
| `LIVECITY_INSERTIONFOLLOWERGAP` | `Engine.InsertionFollowerGapCheck` — refuses a depart that buries the new car's rear in a queued follower. SUMO refuses these by default | `true` ⑦ | behavioural |
| `LIVECITY_F3OCCUPANCY` | `Engine.JunctionPhysicalOccupancyGate`. **Read with `== "1"`**, but the engine default is `false` too, so consistent | `false` | behavioural |
| `LIVECITY_KEEPCLEARHELD` | `Engine.KeepClearHeldPropagation` — G1 of the `checkRewindLinkLanes` port | `false` | **refuted** |
| `LIVECITY_MINORARRIVALSPEED` | `Engine.MinorApproachArrivalSpeed` | `false` | **refuted** |
| `LIVECITY_WRONGLANE` | `LiveCityConfig.WrongLaneRerouteAtApproach`. `"0"` off, **any other value on** | `false` (measured regression) | behavioural |
| `LIVECITY_DRIVETHROUGH` | `LiveCityConfig.DeadLaneDriveThrough` — experimental "never freeze, take any forward connection" | `false` | behavioural |
| `LIVECITY_COOP` | `Engine.CoordinatedLaneChange` **and** `CooperativeInformFollower` together, via `LiveCityConfig.CooperativeLaneChange` | `true` | behavioural |
| `LIVECITY_HELDSWERVE` | `Engine.SuppressHeldCrowdSwerve`. Read with `!= "0"`, so **on unless explicitly `0`** | `true` | behavioural |
| `LIVECITY_LCMIN` | `Engine.LaneChangeMinSpeed`, m/s. Keep ≤ ~2.0 for deadlock safety | `1.0` in `SceneGen`, `1.5` via `LiveCityConfig` | behavioural |
| `CITY3D_LCMIN` | the same knob, in the City3D viewer's own `SimSource` | `1.5` | behavioural |

### The two refuted ones, in full — read before touching either

**`LIVECITY_MINORARRIVALSPEED`** is the one to be most careful with. It ports SUMO's nonzero arrival-speed
target for minor links. Measured: **+67% throughput at 1.6 veh/s and it eliminated the collapse — and it
broke 14 goldens.** The goldens are SUMO's own output, so the change is unfaithful (`arrivalSpeed` is
arbitration metadata, not step speed). If this is set in your shell when you run the parity suite you get
**14 failures with no obvious cause**. It is kept default-OFF and labelled refuted because the +67%
localises where the missing capacity hides (`jyArm 2` under load), not because it is a candidate fix.

**`LIVECITY_KEEPCLEARHELD`** propagates junction blockage backward from a car that merely *cannot proceed*
rather than only one already halted — the gap its own NEED note ranks "highest impact". Measured **worse**:
trips 2938 → 2762. It makes admission *more* conservative, which is the opposite of widening a drain.
Evidence for both: `docs/TASKS-TODO.md` §"REFUTED — do not re-attempt" and
`docs/DENSITY-DIFF-HARNESS-TRACKER.md`.

## Pedestrian and yield gates

| Gate | Sets | Unset ⇒ | Class |
| --- | --- | --- | --- |
| `LIVECITY_PEDYIELD` | `"0"` turns the car→ped yield guard off (the A/B baseline arm) | on | behavioural |
| `LIVECITY_YIELD` | `LiveCityConfig.YieldEnabled` — crossing yield + ped signal compliance. `!= "0"` | `true` | behavioural |
| `LIVECITY_YIELDTIMEOUT` | `LiveCityConfig.JunctionYieldTimeoutSeconds` | `5.0` | behavioural |
| `LIVECITY_PEDPARALLELORCA` | parallel-plans the high-power ORCA crowd (A3). Bit-identical either way, asserted by `OrcaParallelStepTests` | engine default | perf |

## Scenario setup

| Gate | Sets | Unset ⇒ | Class |
| --- | --- | --- | --- |
| `LIVECITY_CARS` | concurrent car cap | `160` | scenario |
| `LIVECITY_PEDS` | concurrent ped cap; the spawn rate scales with it so it fills at the default's pace | `160` cap / `8.0`/s | scenario |
| `LIVECITY_HZ` | sim rate in Hz → `LiveCityConfig.Dt`. Any parsed value > 0 is accepted; the `--sim-hz` CLI flag does the `{1,2,5,10,20}` validation, this does not | `2` Hz (`Dt = 0.5`) | scenario |
| `LIVECITY_TELEPORT` | `LiveCityConfig.TimeToTeleportSeconds` | config default | behavioural |
| `LIVECITY_MERGEGAP` | `LiveCityConfig.MergeStoppedMinGap`, m | `5.0` | behavioural |
| `LIVECITY_MERGEDEFER` | `LiveCityConfig.MergeStoppedStrategicDeferDist`, m | `15.0` | behavioural |

⚠ **`LIVECITY_CARS` and `LIVECITY_PEDS` are closed-loop.** The host inserts only while
`live < CarTargetConcurrent`, so inflow is throttled by our own drain and the resident count cannot run
away. **A capacity or discharge claim measured this way is invalid** however careful the rest was — it once
reported "96% of SUMO" while an open-loop run climbed 258 → 2623 and never reached steady state. Use
`Sim.BenchLiveCity --inflow` (open-loop) for anything about capacity. `CLAUDE.md`
measurement-discipline #4.

## Diagnostics (no effect on the simulation)

| Gate | Does | Unset ⇒ |
| --- | --- | --- |
| `LIVECITY_DUMP` | `x,y` — dumps lane/pos/angle/speed for cars within 45 m, and flags any car whose lane is a pedestrian lane. Separates a sim bug from a render artifact | off |
| `LIVECITY_DUMPROUTES` | `<path>` — records every spawn as a SUMO `.rou.xml` so the exact procedural demand can be replayed through vanilla SUMO for an apples-to-apples comparison | off |
| `LIVECITY_WITNESS` | `1` — dumps engine-authoritative state for cars stuck **on green** with a clear gap ahead (`docs/LIVE-CITY-15-RESIDUAL-REPRO.md`) | off |
| `LIVECITY_LCLOG` | `1` — `Engine.DiagLaneChangeLog`, the issue-#15 float/swap analysis | off |
| `LIVECITY_SEQDESYNC` | `1` — `Engine.DiagSeqDesync`, issue-#15 prong 1 | off |

## The drop-in binary (`sumosharp`)

⚠ **All three are the unsafe two-state form — see the warning box above.** They are deliberately env vars
rather than `--flag`s because they are not SUMO options and must not appear in the parsed-args table.

| Gate | Sets | Unset ⇒ | Engine default |
| --- | --- | --- | --- |
| `SUMOSHARP_CONTTURNFIX` | `Engine.ContTurnInsideJunctionGate` | `false` | `true` ⚠ |
| `SUMOSHARP_ISLEADERFIX` | `Engine.JunctionIsLeaderGate` | `false` | `true` ⚠ |
| `SUMOSHARP_INTERNALJUNCTIONFIX` | `Engine.InternalJunctionAdmissionGate` | `false` | `true` ⚠ |

## `Sim.Run` (the scenario→FCD CLI)

| Gate | Sets | Unset ⇒ | Engine default |
| --- | --- | --- | --- |
| `SUMOSHARP_APPROACHARM` | `Engine.InternalJunctionApproachArm` | **engine default** | `true` |
| `SUMOSHARP_PHYSOCC` | `Engine.JunctionPhysicalOccupancyGate` | **engine default** | `false` |
| `SUMOSHARP_BAYEXITKEEPCLEAR` | `Engine.BayExitLaneKeepClear` | **engine default** | `true` |
| `EVAC_DIAG_STEPS` | `EvacPusherOverlapDiagTests` horizon (steps). Diagnostic-only; the test asserts nothing about separation | `300` | n/a |
| `SUMOSHARP_BAYEXITEXTRA` | `Engine.BayExitLaneKeepClearExtra` (metres of exit-lane room beyond ego length; numeric, not a gate) | **engine default** | `-1` = use MinGap |
| `SUMOSHARP_TRACEVEH` | `Engine.DiagTraceVehicleId` — a SUMO **vehicle id**, not a boolean. Makes the opted-in constraints dump their internal decision to **stderr** for that one vehicle: `KeepClearConstraint`'s downstream available-space walk (per-lane contribution, running `seenSpace`, `foundStopped`, verdict) and `SameTargetMergeConstraint`'s phase + foe. **Diagnostic only — changes no trajectory** | no trace | `null` |
| `SUMOSHARP_LCLOG` | `Engine.DiagLaneChangeLog` — histograms every COMMITTED lane change by [path][changer-speed bucket] (`overtake`/`speedGain`/`strategic`/`keepRight` × stopped/slow/moving) and prints it at the end of a `Sim.Run`. Answers "which path swaps a car that is standing still?". **Diagnostic only — parity-neutral** | **engine default** | `false` |
| `SUMOSHARP_URGENTFOLLOW` | `Engine.UrgentStrategicLeaderFollow` — the `informLeader` urgent-strategic leader-follow coupling (brake to slot in behind the target-lane leader instead of waiting for a gap two equal-speed vehicles cannot open). **Behavioural.** Default ON since journal Entry 30 (every `docs/URGENT-STRATEGIC-FOLLOW-DESIGN.md` §5 gate green); kept as the A/B/bisect switch. Read by `Sim.Run` AND the `sumosharp` drop-in, both in the safe `EnvGate` form | **engine default** | `true` |
| `SUMOSHARP_BINDERLOG` | a **file path**, not a boolean. Makes the `sumosharp` drop-in binary write `Sim.Harness.BinderLogObserver`'s per-vehicle per-step binder CSV. Env-driven rather than a `--flag` so the SUMO-compatible CLI contract is untouched. **Diagnostic only — changes no trajectory** | no log | n/a |

**Behavioural**, and the SAFE `EnvGate(name, engineDefault)` form — unset leaves the engine default
alone, so a plain `Sim.Run` invocation is the shipped behaviour. Contrast the three drop-in gates
above, which are the unsafe `== "1"` two-state form.

It exists so the arm's before/after can be measured through **one binary and one code path**: rebuilding
with a flipped default would make the two arms cross-instrument, which CLAUDE.md #8/#13 rules invalid.

*The deciding measurement* (ENV-GATES' own rule 3 — a gate whose deciding measurement is unnamed is a
gate nobody can retire): **the two are decided TOGETHER, by the paired experiment in
`docs/JUNCTION-REALISM-TRACE-FINDINGS.md` §8.** Measured alone, each looks like a failure — the approach
arm raises `city-organic` overlaps 255 → 296 by creating stationary vehicles nothing protects, and the
occupancy gate is recorded as "counterproductive three times" when tried alone. §8's reading is that
they are two halves of one mechanism. They are retired to unconditional only when the pair shows no
committed net regressing on arrived / still-running / `stuckDwell` / overlap pairs, goldens
byte-identical. Until then both stay flags so the 2×2 stays reproducible.

## Adding a gate

1. Read it with **`EnvGate(name, engineDefault)`**, not `== "1"`, unless you can show the engine default is
   `false` and will stay that way.
2. Add a row here. The test in `tests/Sim.ParityTests/EnvGateDocumentationTests.cs` fails until you do.
3. If it is behavioural, say so, and say what measurement decides it. A gate whose deciding measurement is
   unnamed is a gate nobody can retire.
4. Add it to `AllLiveCityGateVars` if an A/B needs to pin it, and to `Sim.BenchLiveCity`'s `PrintEnvGates`
   so a run's log records what it actually measured.
