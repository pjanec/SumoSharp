# HIGH-DENSITY-P0-DESIGN.md — P0 plumbing (design + tasks)

Design + task-description (folded, per CLAUDE.md "small features may fold the first two together")
for the four P0 plumbing prerequisites. Reference for the WHAT/WHY: `docs/HIGH-DENSITY-FEATURES`
(`SUMOSHARP-HIGH-DENSITY-FEATURES.md` §2) and the verified findings in `docs/HIGH-DENSITY-PLAN.md`
§1. This doc is the HOW. Order: **P0-A → P0-C → P0-B → P0-D**.

## Invariants (all four tasks)
- **Additive only.** Existing `LoadScenario(net, rou, cfg)` and all 466 committed goldens must
  stay byte-identical green. New capability rides on new overloads / new optional config fields
  that default to today's behaviour.
- **Each task lands with its own parity scenario** (`scenarios/NN-name/`) whose golden is
  regenerated from vanilla SUMO 1.20.0 (`scripts/regen-goldens.sh`) and wired into
  `tests/Sim.ParityTests`, passing within `tolerance.json`.
- **RNG-insensitive parity** (owner Q1b): parity scenarios are deterministic; no PRNG-order port.

---

## P0-A — multi-file `.sumocfg <input>` (net-file / route-files / additional-files)

**Mechanism.** SUMO is driven by `sumo -c config.sumocfg`; the cfg's `<input>` names the inputs:
```xml
<input>
  <net-file value="net.net.xml"/>
  <route-files value="vtypes.rou.xml,demand.rou.xml"/>
  <additional-files value="extra.add.xml"/>
</input>
```
Paths are resolved **relative to the cfg's directory** (SUMO semantics). route-files and
additional-files are comma-lists (SUMO also allows spaces; accept both).

**Design.**
1. `ScenarioConfig` (src/Sim.Ingest/ScenarioConfig.cs): add optional fields
   `string? NetFile`, `IReadOnlyList<string> RouteFiles`, `IReadOnlyList<string> AdditionalFiles`
   (default null / empty — today's scenarios omit `<input>`, so behaviour is unchanged).
2. `ScenarioConfigParser`: read `root.Element("input")` and its `net-file`/`route-files`/
   `additional-files` `value=` attributes; split comma/space lists; trim.
3. `DemandParser`: add `Parse(IEnumerable<string> rouPaths)` that parses each file and **merges**
   into one `DemandModel` — union VTypes (later files override same-id? SUMO errors on dup vType
   id; match by erroring, or last-wins — pick SUMO's behaviour, verify), concat Routes, concat
   Vehicles. The existing single-path `Parse` delegates to the multi-file path with one element.
4. `Engine`: add SUMO-faithful `LoadScenario(string sumocfgPath)` overload that parses the cfg,
   resolves `<input>` paths against the cfg dir, and loads net + all route-files (+ additional-
   files; for P0-A the additional-file may be behaviourally idle, e.g. a defined-but-unused
   parkingArea — it only has to *load*). Keep the 3-arg overload.
5. `Sim.Run/Program.cs`: when the cfg has an `<input>` section, drive off it instead of the glob;
   otherwise keep the glob fallback (back-compat with existing scenario dirs).

**Files touched:** `ScenarioConfig.cs`, `ScenarioConfigParser.cs`, `DemandParser.cs`,
`Engine.cs` (LoadScenario overload), `Sim.Run/Program.cs`.

**Success conditions (P0-A):**
- New unit test: `ScenarioConfigParser` parses an `<input>` with a 2-file `route-files` and a
  1-file `additional-files` into the new fields (comma + space forms).
- New unit test: `DemandParser.Parse([vtypesFile, demandFile])` merges vTypes-from-one +
  vehicles-from-another into a `DemandModel` equivalent to the single-file version.
- New parity scenario `scenarios/41-multifile-cfg` (next free NN): a small free-flow run whose
  `config.sumocfg` splits the vType into `vtypes.rou.xml`, routes+vehicles into `demand.rou.xml`,
  and references an idle `extra.add.xml`. Golden from SUMO 1.20.0. `LoadScenario(cfgPath)`
  reproduces `golden.fcd.xml` within tolerance.
- `dotnet test` fully green (466 existing + new).

---

## P0-C — symbolic depart attributes (`departSpeed`, `departLane`, `departPos`)

**Mechanism (SUMO, MSVehicle/SUMOVehicleParameter).** Symbolic depart values:
- `departSpeed="max"` → min(vType maxSpeed·speedFactor, lane speed limit, **safe speed given the
  leader**) at insertion — i.e. as fast as safely possible. Also `"desired"` (=maxSpeed·factor,
  no leader clamp), `"speedLimit"`, `"avg"`, `"random"`, `"last"`.
- `departLane="best"` → the lane whose `getBestLanes` continuation (route-aware, occupancy-aware)
  is best; also `"free"` (emptiest), `"random"`, `"allowed"`, `"first"`, an index, `"departLane"`.
- `departPos="stop"` → the position of the vehicle's first `<stop>` (parkingArea/lane stop);
  also `"base"`, `"free"`, `"random"`, `"last"`, `"random_free"`, a number.

**Design.** DemandParser must stop crashing on symbolic strings and instead carry an enum + the
resolution happens at insertion (`Engine.InsertDepartingVehicles`/`TryInsertOnLane`), where leader
and lane occupancy are known.
1. `VehicleDef` / demand model: replace the raw `double DepartSpeed`/`int DepartLane`/`double
   DepartPos` with a discriminated form — a `DepartSpeedSpec`/`DepartLaneSpec`/`DepartPosSpec`
   (enum kind + optional numeric literal). Parser sets the kind; a literal number keeps today's
   behaviour.
2. Parser: replace the `double.Parse`-on-any-value with a symbolic-aware parse (number → literal;
   known keyword → enum; unknown → clear error).
3. Insertion (`Engine.cs`): resolve each spec at insertion time. **P0-C scope:** implement the
   values our pipeline sets on 100% of trips — `departSpeed="max"`, `departLane="best"`,
   `departPos="stop"` — plus the literal fallthrough. `getBestLanes` equivalent already exists
   (`RungC2iBestLanes` / the C2 best-lanes machinery — reuse it, do not reinvent); `departPos="stop"`
   reads the vehicle's first stop (parkingArea from the additional-file — couples to P0-A).
4. Gate any deviation: if a symbolic value can't be reproduced faithfully, it's an error, not a
   silent default (never manufacture a wrong number).

**Files touched:** `DemandParser.cs`, demand model types (`VehicleDef` + specs),
`Engine.cs` (insertion resolution), reuse best-lanes.

**Success conditions (P0-C):**
- Unit tests: parser maps `"max"/"best"/"stop"` to the right spec kinds; a number still parses as
  literal; an unknown keyword throws a clear error (not `FormatException` from `double.Parse`).
- Parity scenario `scenarios/42-symbolic-depart`: multi-vehicle insertion onto a busy lane with
  `departSpeed="max" departLane="best"` (and a `departPos="stop"` parked-origin car using a
  parkingArea additional-file). Golden from SUMO 1.20.0; SumoSharp matches insertion
  speed/lane/pos and subsequent trajectory within tolerance.
- `dotnet test` green.

---

## P0-B — `<vTypeDistribution>` resolution (RNG-insensitive parity)

**Mechanism.** `<vTypeDistribution id="civ_vehicle" vTypes="car:0.7 van:0.3">` (or child
`<vType .../>` with `probability=`). A `<vehicle type="civ_vehicle">` gets a member vType sampled
by probability. SUMO samples from its RNG per vehicle.

**Design (owner Q1b — RNG-insensitive).**
1. Parse `<vTypeDistribution>` (in route-files or additional-files) into a distribution registry:
   id → [(vTypeId, probability)], normalised.
2. Resolution: when a vehicle's `type=` names a distribution, assign a member.
   - **Parity gate scenario uses a single-member or `probability`-degenerate distribution**
     (or an assignment rule we can match deterministically) so the golden's type-per-vehicle is
     reproducible **without** cloning SUMO's PRNG stream — this is the RNG-insensitive gate.
   - **Sampling correctness** (multi-member weighted draw) is validated in a **separate
     statistical** test (`parityMode:"statistical"`): over many vehicles the assigned-type
     histogram matches the declared probabilities within tolerance — not vehicle-for-vehicle.
   - Seed via per-entity hashed RNG (never `System.Random`), per DESIGN.md.

**Files touched:** new `VTypeDistribution` parse in ingest, demand model, `Engine`/type resolution.

**Success conditions (P0-B):**
- Unit test: parse a `<vTypeDistribution>` with weights → normalised registry.
- Parity scenario `scenarios/43-vtypedist` with a degenerate/deterministic distribution: golden
  type assignment + trajectory reproduced exactly.
- Statistical test: 500+ vehicles over a weighted distribution → member-type frequencies within
  statistical tolerance of the declared weights.
- `dotnet test` green.

---

## P0-D — engine writers for `--summary-output` + `--statistic-output`

**Mechanism.** `--summary-output` writes per-step `<step time= running= halting= stopped=
meanSpeed= meanSpeedRelative= .../>`; `--statistic-output` writes a `<statistics>` doc including
`<teleports total= .../>`. These are SumoData's calibration signals.

**Design.**
1. Engine writer (new observer in `Sim.Run` / a `SummaryWriterObserver`, mirroring
   `FcdWriterObserver`) emitting all five per-step attributes. Aggregates largely exist; add
   `halting` (speed < 0.1 m/s threshold, SUMO's `haltingSpeedThreshold`), `stopped`,
   `meanSpeedRelative` (mean of v/vLimit).
2. `--statistic-output` writer emitting `<teleports total=…>` (0 until P1-F exists; wired now so
   P1-F just increments the counter).
3. **Harness parity side (bigger than a writer):** extend `Sim.Harness/SummaryOutputParser.cs` +
   `SummaryStepRecord.cs` to read `halting/stopped/meanSpeedRelative`; add a new
   `StatisticOutputParser` for `<teleports total>`. Add a comparator so the parity test can diff
   engine summary vs golden summary.
4. `Sim.Run` CLI flags `--summary-output PATH` / `--statistic-output PATH`.

**Files touched:** `Sim.Run/Program.cs`, new `SummaryWriterObserver`/`StatisticWriterObserver`
(likely in Sim.Core or Sim.Run export seam), `Sim.Harness/SummaryOutputParser.cs`,
`SummaryStepRecord.cs`, new `StatisticOutputParser.cs`, aggregate comparator.

**Success conditions (P0-D):**
- Unit tests: extended `SummaryOutputParser` reads all five attributes; `StatisticOutputParser`
  reads `<teleports total>`.
- Parity scenario (reuse `41`/`42` or a dedicated `44-summary-output`): engine
  `--summary-output`/`--statistic-output` are numerically within tolerance of golden's
  `summary.xml`/`statistic.xml` (teleports total = 0 pre-P1-F).
- `dotnet test` green.

---

## Tracker (P0)
- [x] P0-A multi-file cfg — parser + DemandParser merge + LoadScenario(cfg) + Sim.Run + scenario 41 ✅ (474 green)
- [ ] P0-C symbolic departs — specs + insertion resolution + scenario 42
- [ ] P0-B vTypeDistribution — parse + deterministic parity 43 + statistical sampling test
- [ ] P0-D summary/statistic writers + harness parsers + comparator + scenario 44
