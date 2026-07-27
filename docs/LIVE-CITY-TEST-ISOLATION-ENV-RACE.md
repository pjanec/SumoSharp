# Test trouble — `Sim.LiveCity.Tests` env-var isolation race

**Status:** interim mitigation applied (xunit parallelization disabled for the assembly); a proper per-test
env-var snapshot/restore fix is still **TODO** (see `docs/TASKS-TODO.md` → "Live-city test env-var isolation").

## Symptom

`Sim.LiveCity.Tests.LiveCitySimTests.DenseFlow_OverAThousandSeconds_KeepsDischarging_NoGridlock` is a
throughput/gridlock regression guard: it builds the demo (`LiveCityConfig.ForRepoRoot`) and asserts the coupled
sim keeps discharging (`finalArrivals >= 450`; healthy ≈ 700–736, a gridlock ≈ 361). It was observed **flaky**:
the SAME deterministic config produced wildly different arrival counts depending on *which other tests ran in the
same process*:

| Run context | arrivals |
|---|---|
| the test ALONE (`--filter`) | **718** |
| in the full assembly (parallel) — sometimes | **707** |
| in the full assembly (parallel) — other times | **431** (fails; `< 450`) |

707/718 are healthy; 431 is essentially the gridlock regime. Same source, same seed — so the variance is not the
sim; it is cross-test contamination.

## Root cause

1. Several `Sim.LiveCity.Tests` classes set **process-global** environment variables to drive `LiveCityConfig`:
   - `HeadOfQueueStallProbeTests.Probe` → `LIVECITY_CARS` **and** every gate in `AllLiveCityGateVars`, and does
     **not** reset them afterwards.
   - `LongHorizonGridlockDiagTests` → the gate vars (does reset to `null` at the end).
   - `ArbitraryNetStageATests` → `LIVECITY_CARS` (does reset to `null`).
2. `LiveCityConfig.ForRepoRoot` **reads** those same vars (`LIVECITY_CARS`, the gates, `LIVECITY_PEDS`, …) at
   construction time (`LiveCityConfig.cs` `WithEnvOverrides`).
3. xUnit runs test **collections in parallel by default**. So a setter class writing `LIVECITY_CARS`
   concurrently with the throughput test's `ForRepoRoot` call is a genuine data race on process-global state:
   the throughput test sometimes builds its `Engine` with another test's car count / gate flags → a different
   (often gridlocked) run. A non-resetting setter (`HeadOfQueueStallProbeTests`) also leaks to any test that
   runs after it, even sequentially.

This is a **pre-existing isolation defect introduced with PR #13** (the junction-correctness tests). On `main`
it does not surface because the demo's throughput margin there is high enough (~736) that even a contaminated
run stays ≥ 450. The ped-LOD-lifecycle branch's *demo-only* pedestrian realism (crosswalk wait + per-ped speed
variation) lowers that margin enough that the contaminated tail crosses 450 and the test fails — i.e. the
branch **exposed**, but did not cause, the race. The demo itself is healthy (707 arrivals at the shipped
`SpeedVariationFrac = 0.15`, run in isolation).

## Interim mitigation (applied on `claude/livecity-ped-lod-lifecycle-bylitj`)

`tests/Sim.LiveCity.Tests/TestParallelization.cs`:

```csharp
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
```

Process-global env vars are fundamentally incompatible with parallel test execution; disabling it makes the
suite deterministic. Verified green 3/3 full-suite runs (50/50) with the branch's ped realism at
`SpeedVariationFrac = 0.15`. This is a correctness fix, not a perf tweak, but it is the *blunt* version: it also
serialises the (independent, well-behaved) tests, costing wall-clock, and it does not stop the **sequential
leak** from a non-resetting setter (harmless today only because the current class order happens not to
contaminate).

## Proper fix (TODO)

Give every test that mutates process-global state a **snapshot/restore** guard so it always leaves the
environment exactly as it found it, then re-enable parallelization only if each such test is also confined to a
non-parallel collection (env vars are still global even with restore, so a *concurrent* reader can see a
half-set state). Concretely:

1. A small `IDisposable` helper (e.g. `EnvVarScope`) that snapshots the named vars on construction and restores
   their prior values (including "unset") on `Dispose`; every `LIVECITY_*`-setting test wraps its body in it.
   This kills the **leak**.
2. Put all `LIVECITY_*`-driven tests in a single **`[Collection]`** so xUnit never runs two of them (or one of
   them and the throughput test) concurrently. This kills the **race** while leaving unrelated collections
   parallel. Then `TestParallelization.cs`'s blanket disable can be removed.
3. Alternatively, make `LiveCityConfig` overridable without env vars (pass an explicit overrides record) so the
   tests stop touching process-global state at all — the cleanest long-term shape, but a wider change to the
   config surface and its call sites.

Owner: the junction/F3 (PR #13) test authors own these test files; this doc + the TODO are the hand-off. The
interim `DisableTestParallelization` can stay until the proper fix lands.

## Repro

```
# flaky (pre-mitigation): run the full assembly repeatedly, watch the throughput test's arrival count swing
dotnet test tests/Sim.LiveCity.Tests -c Release            # sometimes 49/50 (got 431), sometimes 50/50

# clean in isolation (no contaminating test in the process):
dotnet test tests/Sim.LiveCity.Tests -c Release \
  --filter "FullyQualifiedName~DenseFlow_OverAThousandSeconds"   # 707–718, always passes
```
