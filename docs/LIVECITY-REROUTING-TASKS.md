# LIVECITY-REROUTING-TASKS — work breakdown (blocked on owner sign-off of the DESIGN doc)

Design reference: `LIVECITY-REROUTING-DESIGN.md` (do not restate it here; each task names its
section). Tracker: `LIVECITY-REROUTING-TRACKER.md`. All tasks are host-side; **no
`Sim.Core`/`Sim.Ingest` edits are in scope** — if a task seems to need one, stop and re-read
DESIGN §2.1 (the engine device is complete and golden-pinned).

## T1 — config pass-through + env gates (DESIGN §2.1)

Files: `src/Sim.LiveCity/LiveCityConfig.cs`, `src/Sim.LiveCity/LiveCitySim.cs`,
`docs/ENV-GATES.md`.

- Add `LiveCityConfig.RerouteProbability` (default 0.0) and `ReroutePeriodSeconds` (default
  0.0 = off); splice `device.rerouting.probability/period` into the config XML at
  `LiveCitySim.cs:378` ONLY when `ReroutePeriodSeconds > 0` (same conditional-splice pattern as
  `time-to-teleport`, InvariantCulture).
- Demo host env plumbing: `LIVECITY_REROUTE` (master, `1` = on), `LIVECITY_REROUTE_PERIOD`
  (default 60), `LIVECITY_REROUTE_PROB` (default 1.0), read where the other `LIVECITY_*` knobs
  are read.
- `docs/ENV-GATES.md` rows for all three.

**Success conditions:**
1. With all three vars unset: the spliced XML is byte-identical to today's (assert in a new
   LiveCity.Tests case that reads the built `ScenarioConfig`: `ReroutePeriod == 0`), and the
   400-car smoke witness output is byte-identical to the pre-change build.
2. With `LIVECITY_REROUTE=1`: the built `ScenarioConfig` shows `ReroutePeriod == 60`,
   `RerouteProbability == 1.0`; overrides respected (test with PERIOD=30, PROB=0.5).
3. `EnvGateDocumentationTests` green (it fails on any undocumented gate).
4. Full `dotnet test -c Release` green; no golden moves (engine untouched — a golden move is a
   stop-ship signal that scope was violated).

## T2 — determinism with the device ON (DESIGN §2.2)

Files: `tests/Sim.LiveCity.Tests/` (new test), no production code.

- New test: two runs of the same seeded LiveCity sim with `ReroutePeriodSeconds = 60`,
  `RerouteProbability = 1.0`, identical config → identical per-step car position streams
  (reuse the existing determinism-test harness pattern in that project).
- Same test asserts at least one periodic reroute actually occurred during the run (guard
  against a vacuously-deterministic never-fired device) — expose the count via the existing
  reroute bookkeeping (`RegisterPeriodicReroute` already tracks installs; surface a counter on
  the sim facade if none exists — facade-only change).

**Success conditions:**
1. The test is red if determinism breaks (verified once by intentionally perturbing the seed in
   a scratch run — do not commit the perturbation) and green at head.
2. Reroute count > 0 in the test scenario (a congested mini-net; author it in-test or reuse an
   existing fixture with a forced jam).
3. Full sln suite green.

## T3 — behavioural A/B on the demo surfaces (DESIGN §3)

Files: `src/Sim.Viewer/Program.cs` (witness line only), measurement — no engine code.

- Add a `LIVECITY-REROUTES: total=N` witness line to the smoke output (device installs per
  interval), so the A/B and the owner's runs can SEE the device working.
- Run the 400-car closed-loop smoke A/B: `LIVECITY_REROUTE=0` vs `=1`, EVERY other
  `LIVECITY_*`/`SUMOSHARP_*` gate set explicitly and identically in both arms (CLAUDE.md
  measurement discipline #10), 2400 frames.
- Run the hour-horizon surface (`LongHorizonGridlockDiagTests` scenario) once with the device
  ON as a scratch measurement; if healthy, add the gated test variant.

**Success conditions:**
1. OFF arm: byte-identical witness stream vs pre-change build (inertness re-proof at T3 scope).
2. ON arm: reroutes total > 0; arrivals ≥ OFF arm − 2% at t=1200; INTERNALSTUCK persistent
   heads not worse than OFF; no GRIDLOCK.
3. Hour-horizon ON: 0 stalls > 300 s (same bar as Entry 38's fix), else the failure is traced
   (one traced vehicle before any dial — the workstream's standing rule).
4. Numbers + demand-model label (closed-loop) recorded in the journal as an Entry with
   BEFORE-predictions written first.

## T4 — owner 3D validation handoff (DESIGN §2.4, §3.5)

- One-paragraph instruction for the owner: which env vars to set on the City3D viewer
  (`LIVECITY_REROUTE=1` + the two optional overrides + their existing gate set), what to look
  for (queued cars peeling off onto alternatives when a junction saturates), and the fallback
  (`LIVECITY_REROUTE=0`).
- After owner confirmation: the default-flip decision (opt-in → on-by-default) is taken WITH
  the owner, then executed as a one-line `LiveCityConfig` default change + ENV-GATES update.

**Success conditions:** owner reports the behaviour visible and not pathological on Geneva;
default-flip decision recorded in the tracker either way.
