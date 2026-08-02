# Running the Geneva live-city tests HEADLESS — the harness the 3D-test session uses

**Audience:** the engine/junction-realism session, working on `claude/sumosharp-traffic-bugs-g1y9hl`
with no GPU and no display. Written by the Windows/3D-test session that has been running the Geneva
re-checks, so that the same measurements can be taken without a viewer window.

Everything below was **executed and verified** on 2026-08-01 at branch `72d7ac9`, on the real Geneva
cut. Where something does *not* work, that is stated as a measured fact, not a guess.

---

## 0. Read this first — the blocker

**There is currently no headless driver that runs LiveCitySim on an external net (Geneva) *and* emits
the witness instruments.** That is why Geneva reports have not been reproducible on your side. The
four drivers split the two capabilities cleanly:

| driver | external net (`--sumocfg`) | `LIVECITY_WITNESS` lines | headless |
| --- | --- | --- | --- |
| `src/Sim.BenchLiveCity` | ✅ `--sumocfg` / `--dataset` | ❌ **none, ever** | ✅ |
| `src/Sim.Viewer --mode live-city --smoke` | ❌ **silently ignored** | ✅ rich | ✅ |
| `tests/Sim.LiveCity.Tests` (hour-horizon) | ❌ bundled scenario | ✅ | ✅ |
| Godot `demos/City3D` viewer | ✅ | ✅ | ❌ needs GPU + window |

Two measured proofs, so you do not have to take this on faith:

- `Sim.BenchLiveCity` with `LIVECITY_F3OCCUPANCY=1 LIVECITY_WITNESS=1 LIVECITY_REROUTE=1`, 800 steps,
  2000 cars: `grep -c "LIVECITY-"` → **0**. Not one witness line. The same env on the xunit
  hour-horizon test → **73** lines. `ReportMidLaneStuck()` *is* called from `LiveCitySim.Step()`
  (line ~1401) and there is no early `return` before it, so the cause is further in — I did not
  isolate it, and it is your instrument.
- `Sim.Viewer --mode live-city --smoke --sumocfg <geneva>` ran, printed a full instrument set, and the
  lane ids in it were `e_d_3_4_d_3_3_3` — **the demo grid**. `RunLiveCitySmoke(int steps, string?
  recordPath, int simHz)` (Program.cs:1304) takes no net argument at all; both it and `RunLiveCity`
  call `LiveCityConfig.ForRepoRoot(repoRoot)` unconditionally.

### The fix that unblocks you (small, and the seam already exists)

`LiveCityConfig.ForSumocfg(path)` already exists — `Sim.BenchLiveCity/Program.cs:214` uses it for its
own `--sumocfg`. So thread the existing parsed `--sumocfg` value into the smoke path and pick the
factory:

```csharp
// src/Sim.Viewer/Program.cs, the mode == "live-city" branch (~line 314)
? RunLiveCitySmoke(Math.Max(frames, 120), recordPath, resolvedSimHz, sumocfgPath)
// and inside RunLiveCitySmoke / RunLiveCity:
var cfg = sumocfgPath is not null
    ? LiveCityConfig.ForSumocfg(sumocfgPath)
    : LiveCityConfig.ForRepoRoot(DemoCatalog.RepoRoot());
```

That single change gives you the **witness-rich instrument set on the real Geneva net, headless** —
which is exactly the combination every Geneva round has been missing. Alternatively, make
`Sim.BenchLiveCity` emit the witness; the smoke route is smaller and gives more instruments.

---

## 1. The data — what "Geneva" is, and how to get it

**The Geneva dataset is access-restricted and local to the owner's machine. Nothing in this repo may
reference it by path or environment variable.** If you do not have it: **ask the owner. Do not go
looking for it, and do not substitute another dataset** — a substituted net invalidates every Geneva
comparison.

What the runs actually use is not the full dataset but a **cut**, and the cut recipe is the part worth
knowing:

- **Source:** a single large SUMO net for Switzerland (~280 km, ~175 465 lanes). Owner-supplied path;
  referred to below as `<SOURCE_NET>`.
- **Cut:** central Geneva, ~11 km across, produced once with `netconvert`:

  ```bash
  netconvert --sumo-net-file "<SOURCE_NET>" \
             --keep-edges.in-boundary "-112500,-141500,-103500,-132500" \
             -o "<CUT_DIR>/geneva_city.net.xml"
  ```

- **Config:** a one-line `.sumocfg` naming only the net (no route file — LiveCity generates demand):

  ```xml
  <configuration><input><net-file value="<CUT_DIR>/geneva_city.net.xml"/></input></configuration>
  ```

- **Resulting scale, so you can tell you have the right cut:** **28 276 lanes**, 4 695 of them
  sidewalk/concrete, real elevation **z 370–468 m**. If your net has ~175 k lanes you took the whole
  country; Geneva then becomes sub-pixel and looks like an empty scene.

Below, `<CFG>` means that `geneva_city.sumocfg`.

**Why the cut matters for interpreting results.** It is a real, organically-shaped city net: long
arteries, left-turn bays, unsignalised priority junctions, and `cont` turns. The demo grid
(`LiveCityConfig.ForRepoRoot`) has none of that topology, which is why several defects the owner sees
on Geneva have never reproduced on the box scene.

---

## 2. Build — four traps, all of which have cost real time

```bash
dotnet build -c Release                       # the solution
dotnet build -c Release src/Sim.Viewer        # NOT in Traffic.sln -- build explicitly
dotnet build -c Release src/Sim.Run           # NOT in Traffic.sln
dotnet build -c Release src/Sim.Sumo          # NOT in Traffic.sln
dotnet build -c Release src/Sim.BenchLiveCity # NOT in Traffic.sln
```

1. **`src/Sim.Viewer` is NOT in `Traffic.sln`.** Measured today: after a clean solution build, its
   `bin/Release/net8.0/Sim.Core.dll` was still dated **07-24** — eight days stale, predating this
   entire branch — and `dotnet run --no-build` happily ran it. My first Geneva smoke attempt measured
   *old engine code* because of this. **Always build it explicitly, and check the dll timestamp**:
   `ls -la src/Sim.Viewer/bin/Release/net8.0/Sim.Core.dll`.
2. **`tests/Sim.LiveCity.Tests` IS in `Traffic.sln`** (since `f4f39a4`) — plain `dotnet test -c
   Release` runs it. It is the only hour-horizon surface. Run the **full** suite, not just
   `tests/Sim.ParityTests`.
3. **`demos/City3D/CityLib.Tests` is NOT in the sln** — build/test that csproj explicitly.
4. **The NuGet trap does not apply to you.** Clearing `~/.nuget/packages/sumosharp.*` matters only for
   the *Godot* demo, which consumes the engine as a packed `0.1.0` package. Every driver in this
   document builds the engine from source by project reference.

---

## 3. What to run, headless — in priority order

### 3.1 The full gate (always, before pushing any default-behaviour change)

```bash
dotnet build -c Release && dotnet test -c Release
dotnet run  -c Release --project src/Sim.Bench
```

Expected at `72d7ac9`, all verified by me today:

| gate | expected |
| --- | --- |
| `Sim.ParityTests` | **782 passed / 5 skipped / 0 failed**, all 661 goldens byte-identical |
| `Sim.LiveCity.Tests` | **92 / 92** (90 + the two rerouting tests) |
| `Sim.Pedestrians.Tests` | 324 / 324 |
| `Sim.Host.Tests` / `Sim.Viewer.Motion.Tests` | 6 / 6 · 19 / 19 |
| `Sim.Bench` | hash **`A134ED3716DDE7BC`**, `deterministic=True`, **hashA == hashPar** |
| `demos/City3D/CityLib.Tests` (explicit) | 186 / 4 skipped |

⚠ The bench hash moved from `BF3794A4704BCD79` at **Entry 34 (`05653f4`)** — bisected, and now
re-pinned in the docs. If you see the old value quoted anywhere else, that doc has rotted.

### 3.2 The hour-horizon LiveCity test — the surface that has caught everything

This is the highest-value headless measurement you have. It is a **60 sim-minute** closed-loop run of
the real coupled host, it reports arrivals and long-stall counts, and **it emits the witness lines**.
It is what caught the Entry-34b latent merge deadlock (129 hour-long stalls) that the goldens and the
parity suite were both green through.

```bash
# arrivals + long stalls, with the diag output (xunit hides stdout on PASS -- you need -l detailed):
LIVECITY_F3OCCUPANCY=1 \
dotnet test -c Release --no-build tests/Sim.LiveCity.Tests \
  --filter "LongHorizon_GridlockAndInterpenetration_OffVsOn" \
  -l "console;verbosity=detailed" 2>&1 \
| grep -E "CONFIG:|ArrivedTotal \(|stopped runs > 300"

# witness lines from the same run:
LIVECITY_F3OCCUPANCY=1 LIVECITY_WITNESS=1 \
dotnet test -c Release --no-build tests/Sim.LiveCity.Tests \
  --filter "LongHorizon_GridlockAndInterpenetration_OffVsOn" \
  -l "console;verbosity=detailed" 2>&1 | grep "LIVECITY-"
```

It prints two arms of its own (`CONFIG: OFF` / `CONFIG: ON` = the nine `LIVECITY_*` junction gates
forced off/on). Note the ON arm is the shipped engine default configuration, so **that is the arm that
corresponds to what the owner sees.** `LIVECITY_F3OCCUPANCY` is *not* in the test's gate list, so it
is honoured from the environment — which is what makes it A/B-able.

Measured reference values (mine, at `72d7ac9`), for the gates-ON arm:

| env | arrivals (OFF arm) | arrivals (ON arm) | stalls > 300 steps |
| --- | --- | --- | --- |
| `F3OCCUPANCY=0 REROUTE=0` | 2936 | 2436 | 0 |
| `F3OCCUPANCY=1 REROUTE=0` | 2936 | 2436 | 0 |
| `F3OCCUPANCY=1 REROUTE=1` | 3042 | 2562 | 0 |

### 3.3 `Sim.BenchLiveCity` — the ONLY driver that runs Geneva headless today

Use it for throughput, drain, fill and per-step cost on the real net. **It emits no witness lines**, so
it cannot answer "which constraint holds this car".

```bash
dotnet build -c Release src/Sim.BenchLiveCity
LIVECITY_F3OCCUPANCY=1 LIVECITY_REROUTE=1 \
dotnet run -c Release --project src/Sim.BenchLiveCity --no-build -- \
  --sumocfg "<CFG>" --cars 4000 --peds 8000 \
  --fill-steps 600 --steps 2400 --warmup 20 --quiet
```

- `--fill-steps N` prefills before measuring; **without it you measure a fill-rate-limited run, not
  the density you asked for.** Check the `FILL-FAILED` line and the achieved `cars=`/`peds=`.
- Useful flags: `--profile` (per-phase breakdown), `--hi-res-radius R` (the ORCA pocket),
  `--car-spawn-per-step`, `--ped-spawn-rate`, `--csv`, `--repeats`.
- Reference at 4 000 cars / 8 000 peds on the Geneva cut: `mean_ms ≈ 90`, `rtf ≈ 5.5`,
  `arrived = 2612` over 2 400 steps, `over3xp50 = 0`.

⚠ **Two gaps in this tool that will silently mislead you.** It prints
`LIVECITY_F3OCCUPANCY = 1 (NOT in the curated list above -- update Sim.BenchLiveCity/Program.cs)`, and
the same for `LIVECITY_REROUTE`. Per CLAUDE.md §Measurement-discipline #10 (gates are process-global,
set every one explicitly in both arms), a gate outside that curated list is not recorded with the
measurement. My first rerouting A/B ran through this tool and returned **exactly 2612 arrivals in both
arms** — I initially read that as "rerouting does nothing on Geneva", and it was the gate never
reaching the path. **Add both gates to the curated list before trusting any bench A/B.**

### 3.4 `Sim.Viewer --mode live-city --smoke` — witness-rich, but demo grid ONLY

```bash
dotnet build -c Release src/Sim.Viewer            # mandatory -- see trap 1
LIVECITY_CARS=1500 LIVECITY_PEDS=1500 LIVECITY_F3OCCUPANCY=1 LIVECITY_WITNESS=1 \
dotnet run -c Release --project src/Sim.Viewer --no-build -- \
  --mode live-city --smoke --frames 900
```

No window, no raylib draw calls, safe with no display at all. `--frames N` is the step count (floored
at 120); at `simHz 2` that is `N/2` sim-seconds. Note `--mode live-city` is valid even though the
error message only lists `local|loopback|publish|remote`.

**Do not pass `--sumocfg` here and believe it** — it is accepted by the parser and then ignored (§0).

This is the richest instrument set available, and it is the same "smoke 400 / 800-car smoke" surface
your own entries cite:

```
LIVECITY-GRIDLOCK:  t(s) liveCars stoppedFrac meanSpd(m/s) aggMove(m) arrivals peds
LIVECITY-WITNESS:   stuck= minorGreenYield= majorGreenSTUCK= red= behindLeader/exit=
                    strandedDeadEnd= renderedGreen= tlRenderLie= onInternal= stuckInternal=
LIVECITY-BINDER(majorGreenSTUCK): junctionYield=3 || JYarm: adaptToJxnLeader=2(prio0) ...
LIVECITY-STRANDREASON(cumulative): reResolveOK= rerouteOK=
LIVECITY-STUCKCLEAR: clearStuck= overlaps= byBinder: ... | JYfoe: moving= slow= stopped= none=
LIVECITY-HEADSTUCK / LIVECITY-MIDLANE-STUCK / LIVECITY-REROUTES   (LiveCitySim, LIVECITY_WITNESS=1)
```

### 3.5 `Sim.Run` — single-vehicle tracing, no ped coupling

For per-vehicle constraint traces and FCD on committed scenarios. Remember it has **no pedestrian
coupling at all**, so nothing involving the `crowd` / `crowdYield` binders can be reproduced here.

```bash
dotnet build -c Release src/Sim.Core src/Sim.Run
dotnet run -c Release --project src/Sim.Run --no-build -- \
  scenarios/_bench/city-organic-L2 --steps 1000 --fcd-out /tmp/on.fcd.xml --binder-log /tmp/b.csv
SUMOSHARP_TRACEVEH=<vehId> ...   # per-vehicle constraint trace to stderr
```

---

## 4. Environment gates — the name trap that has bitten twice

**The same engine flag has different names depending on the driver.** `LiveCitySim` reads
`LIVECITY_*`; `Sim.Run` and the `sumosharp` drop-in read `SUMOSHARP_*`. Setting the wrong one gives a
silent gate-OFF run that looks like a gate-ON run.

| engine flag | LiveCity / viewer / bench / smoke | `Sim.Run` + `SumoShim` |
| --- | --- | --- |
| `JunctionPhysicalOccupancyGate` | **`LIVECITY_F3OCCUPANCY=1`** | `SUMOSHARP_PHYSOCCUPANCY=1` |
| `IgnoreJunctionBlockerSeconds` | `LIVECITY_IGNOREBLOCKER=<s>` (auto **60** when F3 is ON) | — |
| rerouting device | `LIVECITY_REROUTE=1`, `_PERIOD=<s>`, `_PROB=<0..1>` | — |
| witness instruments | `LIVECITY_WITNESS=1` | — |
| per-vehicle trace | `LIVECITY_TRACEVEH=__vehN` | `SUMOSHARP_TRACEVEH=<id>` |
| `UrgentStrategicLeaderFollow` | ❌ **no gate exists** | `SUMOSHARP_URGENTFOLLOW` |

**The last row is an open gap and it matters.** There is no way to A/B the urgent-strategic
leader-follow arm on the live-city surface. I had to flip the engine default locally to attribute the
Geneva mid-lane stall class to it — 12 of 14 mid-lane stalls bound on that arm with it on, 0 with it
off. Adding `EnvGate("LIVECITY_URGENTFOLLOW", …)` next to the others in `LiveCitySim` would make that
reproducible for you.

The full gate table is `docs/ENV-GATES.md`, and its completeness is test-enforced.

---

## 5. Reading the instruments — and two known artifacts

`LIVECITY-HEADSTUCK` / `LIVECITY-MIDLANE-STUCK` fire every 20 sim-seconds, capped at 12 lines each:

```
LIVECITY-HEADSTUCK: t=500 __veh201 gen_road_2533_0@163,1/169 bind=junctionYield/corridorFollow
                    tl=- mouth=inf foeSpd=0,0 blockerEnt=3785
                 -> __veh3785 :33346_4_0@1,0 v=0,0 bind=leaderFollow/none
```

- `bind=` is the binding constraint; the part after `/` is the junction-yield arm and is **only
  meaningful when `bind=junctionYield`** — ignore it otherwise.
- `blockerEnt=-1` means no blocker was recorded. For `urgentStrategicFollow` that is unconditional
  (the constraint sets `blockerIdx = -1`), so it carries no information there.
- A blocker on a `:`-prefixed lane at `v=0,0` is the standoff signature: a queue head yielding to a
  car frozen inside a junction.
- **Same head + same blocker at two consecutive timestamps with unchanged positions** is the proof
  that a standoff is durable rather than transient. That is the thing worth grepping for.

**Two artifacts to filter before reading any histogram.** In a 280-line Geneva sample, `freeFlow`
(102) and `deadLaneMerge` (85) — **187 of 280 lines** — were all at `pos ≈ laneLength` on very short
connector lanes (`8,2/8`, `0,8/1`, `32,7/33`). `freeFlow` means *nothing is binding*, which cannot be
a stuck head. Only **18 of 280** lines named a blocker at all. The reporter needs a minimum-lane-length
or binder filter; until then, treat the blocker-naming subset as the signal and the rest as noise.

---

## 6. Measurement discipline specific to this net

1. **Label the demand model.** LiveCity inserts only while `live < CarTargetConcurrent` — closed-loop,
   so inflow is throttled by our own drain and the population cannot run away. Capacity claims need
   open-loop demand.
2. **The high-realism ORCA pocket follows the camera in the Godot viewer.** Ped promotion, and hence
   the `crowd` binder, differs between runs with different camera positions, so run-to-run *counts*
   from the 3D viewer are not strictly comparable. Binder *identity* is unaffected. Headless drivers
   use a fixed pocket (`--hi-res-radius` / `--hi-res-centre`) and do not have this problem.
3. **Both surfaces must accept a change.** Goldens are 2–5 vehicles over ~40 steps and cannot contain
   a saturated junction; Geneva and `_bench/*` saturate but have no SUMO reference. Green goldens are
   not sufficient — the Entry-34b regression shipped through green goldens *and* a green parity suite.
4. **Rerouting masks wedges.** The same stuck-head class was 160 lines by t=2138 with
   `LIVECITY_REROUTE=1`, and 280 lines by t=580 with it off — roughly an order of magnitude more per
   unit sim-time. **Hunt wedges with rerouting OFF.**
5. Rerouting's benefit on this net is **+3.6% / +5.2% arrivals** (§3.2), not the +36% measured on the
   800-car box grid. Do not quote the box figure as the Geneva expectation.

---

## 7. If you only run three things

```bash
# 1. the gate -- catches default-behaviour regressions the goldens cannot
dotnet build -c Release && dotnet test -c Release && dotnet run -c Release --project src/Sim.Bench

# 2. the hour horizon -- arrivals, long stalls, witness lines, both gate arms
LIVECITY_F3OCCUPANCY=1 LIVECITY_WITNESS=1 dotnet test -c Release --no-build \
  tests/Sim.LiveCity.Tests --filter "LongHorizon_GridlockAndInterpenetration_OffVsOn" \
  -l "console;verbosity=detailed" 2>&1 | grep -E "CONFIG:|ArrivedTotal \(|stopped runs|LIVECITY-"

# 3. Geneva itself -- throughput/drain on the real topology (no witness, see §0)
dotnet build -c Release src/Sim.BenchLiveCity && LIVECITY_F3OCCUPANCY=1 \
dotnet run -c Release --project src/Sim.BenchLiveCity --no-build -- \
  --sumocfg "<CFG>" --cars 4000 --peds 8000 --fill-steps 600 --steps 2400 --warmup 20 --quiet
```

And if you want the Geneva witness lines that every recent round has depended on the 3D session to
collect, do the §0 fix first — it is the difference between one round-trip per finding and none.
