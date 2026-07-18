# SumoSharp serve-path drop-in — achievements & handoff

**Branch:** `claude/sumosharp-drop-in-binary-vq7u9p` (NOT yet merged to `main` — try it here first).
**Scope:** the "last mile" from `docs/SUMOSHARP-SERVE-PATH-DROP-IN.md` — make SumoSharp a drop-in for
the vanilla `sumo` binary in the SumoData sub-area **serve / replay** path. Design/verification trail:
`docs/SERVE-PATH-PLAN.md`. All work is golden-verified against vanilla **SUMO 1.20.0** (`SUMO_VERSION`).

---

## 1. What landed (all three gaps + the audit)

### GAP-1 — a `sumo`-compatible CLI: the `sumosharp` binary
New project `src/Sim.Sumo` builds an executable named **`sumosharp`** (deliberately NOT `sumo`, so it
never shadows vanilla `netconvert`/`randomTrips`/`duarouter` on `PATH`). SumoData points its
`SUMO_BINARY` override at it. The core is a testable `SumoShim.Run(args, stdout, stderr) -> int`;
`Program.cs` is a one-line delegate.

**CLI contract** (vanilla spellings; both `--flag value` and `--flag=value` parse):

| Flag | Meaning |
|------|---------|
| `-c` / `--configuration <cfg>` | the `.sumocfg`; its `<input>` resolves net/route/additional relative to the cfg |
| `-b` / `--begin <t>` | sim begin time (optional; default = cfg `<begin>`) |
| `-e` / `--end <t>` | sim end **time** (optional; default = cfg `<end>`); run length = `round((end−begin)/step-length)` steps |
| `--fcd-output <path>` | SUMO-schema FCD |
| `--summary-output <path>` | per-step summary (`running/halting/stopped/meanSpeed/meanSpeedRelative`) |
| `--statistic-output <path>` | `<teleports total=… jam=…>` |
| `--tripinfo-output <path>` | SUMO-schema `<tripinfo>` per arrived vehicle (see GAP-2) |
| `--no-step-log [bool]` | accepted & ignored |
| *any other flag* | **tolerated** (warning to stderr, not an abort) |

Only the writers you ask for are produced (no flag → no file). Exit code `0` on success, `1` on a
usage/IO/parse error (with a message on stderr, never an unhandled exception).

### GAP-2 — `--tripinfo-output` with `arrivalLane`
The engine now captures a genuine per-vehicle arrival record at the single-threaded post-`Flush()` seam
(`Engine.CompletedTrips`), and `sumosharp --tripinfo-output` writes SUMO-schema `<tripinfo>` with
`id, depart, arrival, arrivalLane, arrivalPos, arrivalSpeed, duration, routeLength, waitingTime,
timeLoss`. `arrivalLane` is a real SUMO `<edge>_<laneIndex>` id — exactly what `audit_nocheat.py` keys
off. Trip-total `waitingTime`/`timeLoss` are new never-reset accumulators (distinct from the engine's
consecutive `WaitingTime`), matching `MSDevice_Tripinfo`/`MSVehicle::updateTimeLoss`. A parked-forever
sink vehicle never arrives, so it correctly does not appear in tripinfo (expected).

### GAP-3 — multi-occupant `parkingArea`
Up to `roadsideCapacity` vehicles resident at once, each in a **distinct off-lane slot**
(`LotPosition(i) = startPos + spaceDim·(i+1)`). A parked vehicle is lifted off the running lane
(lateral bay offset + **excluded from the leader search**, SUMO's `MSLane.cpp:2212 isParking()`), so a
following through-vehicle **passes it, not blocked**. `<stop parkingArea=… duration=…>` parks a moving
car; `departPos="stop"` inserts an already-parked car that pulls out into a gap. Park-and-stay sinks
(`duration≈100000`) stay resident. **All gated on `StopRuntime.IsParking`/`VehicleRuntime.IsParked`
(true only for `<stop parkingArea>`), so plain `<stop lane>` stops remain on-lane blocking and every
pre-GAP-3 golden is byte-identical.**

### The no-cheating audit
- `scripts/audit_nocheat.py` — the reference (needs `sumolib`; network/definitive tier). Rule: a
  vehicle may be **born** only on a **fringe** edge or a **park** edge (`departPos="stop"`), and **die**
  (`tripinfo arrivalLane`) only on a fringe or park edge; parkingArea id convention is `pa_<edgeId>`.
- A **C# offline port** (`tests/Sim.ParityTests/RungHDgap3NoCheatTests.cs`) runs the engine's own
  tripinfo + FCD and asserts 0 birth / 0 death / 0 FCD-first-appearance violations — no SUMO/sumolib,
  so the no-cheating rule is guarded on every fresh VM in `dotnet test`.

---

## 2. Scenarios & tests added (all golden from vanilla SUMO 1.20.0)

| Scenario | Proves | Test |
|----------|--------|------|
| `scenarios/66-tripinfo-arrivallane` | tripinfo `arrivalLane` + timing/timeLoss/waitingTime parity | `RungHDgap2TripinfoTests` |
| `scenarios/67-multi-parking` | N sharing one bay, follower passes at cruise, distinct off-lane slots | `RungHDgap3MultiParkingTests` |
| `scenarios/68-serve-nocheat` | synthetic served box (fringe→`pa_eB`→fringe) passes the no-cheating audit | `RungHDgap3NoCheatTests` |
| `scenarios/41-multifile-cfg` (reused) | the CLI shim drives the engine identically to golden | `RungHDgap1SumoCliTests` |

**Full suite (verified first-hand):** `Sim.ParityTests` **600 passed / 3 skipped** (3 skips are
pre-existing sublane/multilane cases), `Sim.Pedestrians` 72, `Nav.DotRecast` 2, `Sim.Host` 1.
Determinism tests (`RungD1`, `RungD8`) green. `git status scenarios/` shows only `66/67/68` new —
**every prior golden byte-identical**, the vehicle-parity determinism hashes unchanged.

---

## 3. How to build, publish, and use `sumosharp`

```bash
# from the repo root
dotnet build                       # or: dotnet build src/Sim.Sumo/Sim.Sumo.csproj
dotnet test                        # offline parity loop; no SUMO needed

# publish a warm, self-contained single-file exe (keeps per-invocation cost low --
# calibration invokes the engine ~7 probes + serve + verify per box):
scripts/publish-sumosharp.sh                 # default: linux-x64 -> artifacts/sumosharp/linux-x64/sumosharp
scripts/publish-sumosharp.sh linux-x64 /some/out/dir     # or pick RID + out dir

export SUMO_BINARY="/abs/path/to/artifacts/sumosharp/linux-x64/sumosharp"
```

Invocation is vanilla-shaped, e.g. the three serve/replay shapes:
```bash
sumosharp -c scenario.sumocfg --summary-output S.xml --statistic-output T.xml --end 3600 --no-step-log true
sumosharp -c scenario.sumocfg --tripinfo-output TI.xml --end 3600 --no-step-log true
sumosharp -c scenario.sumocfg --fcd-output F.xml --end 3600 --no-step-log true
```
Performance note: a self-contained exe cold-starts + runs a 120 s multi-file scenario in ~0.19 s, so
per-call startup is process-start only (NOT `dotnet run --project`, which pays JIT/build each call).

---

## 4. Known limitations / not-yet-covered (be honest)

- **No live Geneva-box run yet.** The real SumoData `scenario.sumocfg` is company-restricted, so the
  definitive end-to-end audit was run against a **synthetic** box (`68-serve-nocheat`), not a real
  produced scenario. `68` is faithful to the documented contract + `audit_nocheat.py`, but a real box
  is the true acceptance — see the Geneva-session prompt in §5.
- **parkingArea occupant→lot assignment is static (load-time).** Faithful to SUMO for the served
  shapes (park-and-stay sinks + `departPos="stop"` pull-out, no turnover). A scenario that **vacates**
  a lot and lets a **later** arrival re-use it within the same area would need SUMO's full dynamic
  reservation system — out of scope, and not used by the serve path. `<rerouter>`/`parkingAreaReroute`
  are likewise not implemented (not on the serve path).
- **Lateral parked position is functional, not byte-exact.** A parked car sits one lane-width off the
  travel lane (nonzero, off-lane, distinct per slot); `y` is not asserted against SUMO (the audit keys
  off edges/lanes, not `y`). Longitudinal `pos`/`lane`/`speed` ARE strict (0.001).
- **`departLane="free"/"random"`** still throw (only numeric + `"best"` parsed) — confirmed unused by
  the produced routes, so intentionally skipped.

---

## 5. Geneva-session prompt

See the copy-paste prompt for the SumoData / Geneva session in `docs/GENEVA-SESSION-TRY-PROMPT.md`.
It names this branch and walks that session through building `sumosharp`, pointing `SUMO_BINARY` at it,
running a real produced `scenario.sumocfg` through `preprocess.py --replay`, and running
`audit_nocheat.py` — the definitive acceptance we could not run here for lack of the restricted data.
