# NEED — `SumoShim` reads a PROCESS-GLOBAL env var for an engine flag; the race is fixed, the hazard is not

**Scope:** `src/Sim.Sumo/SumoShim.cs:250`.
**Found by:** the `isLeader` port (T2.3 verification), as an intermittent failure of
`LowDensityTeleportTests` — **one of the five gridlock diagnostics**.
**Severity:** the concrete race is **FIXED** (see below). What remains is a latent design hazard.

## What happened

```csharp
engine.ContTurnInsideJunctionGate = Environment.GetEnvironmentVariable("SUMOSHARP_CONTTURNFIX") == "1";
```

`ContTurnInsideJunctionGate` is not a SUMO option, so there is no CLI flag for it; the shim reads a
process-wide environment variable instead. `IgnoreJunctionBlockerTests` **sets** that variable around
its own `SumoShim.Run` calls in order to A/B the flag through the SUMO-compatible surface.

Under xUnit's default behaviour, separate test **collections** run in **parallel**, and a class with no
`[Collection]` attribute is its own collection. Six classes call `SumoShim.Run`, so five readers were
eligible to run concurrently with the one writer. An environment variable is process-global, so a
reader could simulate with the cont-turn gate **ON** when it meant OFF.

## It was observed, then reproduced deterministically

`LowDensityTeleportTests.SyntheticJunction2_TlPriorityVehiclesDoNotSpuriouslyTeleport` failed **1 of 3**
full-suite runs:

> `synthetic-junction2 fired 5 teleports (jam=0, yield=5); … should hold it at <= 2`

while passing every standalone run. The mechanism was then pinned without relying on chance:

```bash
# fails, with that exact message
SUMOSHARP_CONTTURNFIX=1 dotnet test tests/Sim.ParityTests -c Release \
    --filter "FullyQualifiedName~LowDensityTeleportTests"

# passes
SUMOSHARP_CONTTURNFIX= dotnet test tests/Sim.ParityTests -c Release \
    --filter "FullyQualifiedName~LowDensityTeleportTests"
```

**5 is exactly the cont-turn-gate-ON teleport count** for that scenario (`F3-SESSION-LOG.md` §9.17,
§9.33), which is what identified the cause rather than merely correlating with it. So this was
**not** engine non-determinism — the engine's own determinism guard (`Sim.Bench` `par == single`) was
green throughout.

## Why it mattered more than an ordinary flake

`LowDensityTeleportTests` and `DenseFlowDeadLaneDrainTests` are two of the **five gridlock diagnostics**
that are this repo's regression net for junction changes — the standing lesson being that goldens alone
are *not* sufficient evidence for a junction change (`F3-SESSION-LOG.md` §2, §7 Lesson 1). A diagnostic
that can go red for reasons unrelated to the change under test is worse than no diagnostic:

- a **false red** costs a session chasing a regression that does not exist;
- worse, it trains the reader to discount the diagnostic, which is exactly when a **real** failure
  gets waved through.

It is also the specific test that blocked `ContTurnInsideJunctionGate` from going default-ON (T1.10),
so its reliability is load-bearing for a pending owner decision.

## Fixed (the race)

`tests/Sim.ParityTests/SumoShimEnvCollection.cs` defines a collection that all six `SumoShim.Run`
classes now share, and xUnit runs a collection **sequentially**, so the mutation can no longer overlap
a reader. Full suite: **717 passed / 4 skipped / 0 failed**.

**Contract for new tests:** a class calling `SumoShim.Run` MUST carry
`[Collection(SumoShimEnvCollection.Name)]`. This is stated in the collection's own header and in each
annotated class.

## Not fixed (the hazard)

Serialization removes the *current* race, not the process-global read. It remains a hazard for any
future in-process concurrent consumer — a parallel test harness, a benchmark driving the shim, a
multi-scenario runner — and the failure mode is silent (a wrong engine configuration, not an error).

The clean fix is to stop reading global state: give the shim an explicit test/embedding seam for
non-SUMO engine flags (an optional configuration parameter or callback on `SumoShim.Run`) so the flag
is passed in rather than picked up from the environment. `LIVECITY_CONTTURNFIX` in `LiveCitySim.cs` is
the same pattern and should move with it.

## Success conditions, if picked up

- `SumoShim.Run` accepts non-SUMO engine flags explicitly; no `Environment.GetEnvironmentVariable`
  remains on a path that affects simulation behaviour.
- `IgnoreJunctionBlockerTests` passes the cont-turn flag without mutating process state, and the
  `[Collection]` serialization can then be **removed** — proving the coupling is gone rather than
  merely contained.
- Full suite green across **at least 5 consecutive** runs (the flake reproduced at roughly 1 in 3).
- All 661 goldens byte-identical; `Sim.Bench` hash `D96213B7BB4021A7`, par == single.
