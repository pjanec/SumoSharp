# Sub-area traffic experiment — findings & port gap map

**Status: IN PROGRESS (checkpoint 1).** Exploratory session. Goal (A): a pipeline for
"user selects a ~3×3 km sub-area of a bigger network → believable SUMO traffic in that box,
fast, with NO visible cheating — cars appear/disappear only at the network FRINGE (roads cut
by the boundary) or at internal SINKS (parking)". Goal (B): map what our C# port (SumoSharp)
is MISSING to drive this workflow.

All SUMO usage here is **authoring/investigation only** (CLAUDE.md allows it). Nothing in this
experiment is a `dotnet test` dependency. Reproduce with `experiments/subarea/run-experiment.sh`;
generated nets/routes live in `experiments/subarea/scratch/` (gitignored).

## Environment (this VM)

| Tool | Status |
|---|---|
| SUMO 1.20.0 (`sumo`, `netgenerate`, `netconvert`, `duarouter`, `od2trips`) | pip `eclipse-sumo==1.20.0` → `$SUMO_HOME/bin` |
| `randomTrips.py`, `route/cutRoutes.py` | `$SUMO_HOME/tools/…` |
| .NET 8 SDK (to run the port) | `apt-get install -y dotnet-sdk-8.0` (8.0.129) |

Both are ephemeral (VM-volatile); neither is committed or required by the offline test loop.

## The approach ladder — what actually held up

### L1 "weighted random" — does NOT satisfy no-cheating on a raw grid

`netgenerate --grid` produces a **closed** network: every node has matched in/out degree
(corners 2/2, edges 3/3, interior 4/4), so **there are zero fringe edges**
(`sumolib … is_fringe() == 0/360`). `randomTrips --fringe-factor` is therefore a no-op, and
**100 %** of trips depart AND arrive on internal roads → cars pop in/out mid-network. Audited
via tripinfo: 2185/2185 trips off-fringe on both ends.

**Takeaway:** L1 on a standalone generated grid cannot meet the "fringe-only" rule, because a
standalone grid has no fringe. A fringe is *created by cutting a box out of a bigger network* —
which is exactly L2. So even the "simple" path routes through cropping.

### L2 "macro-crop" — the workable pipeline ✅

The exact commands (see `run-experiment.sh` for the full driver):

```bash
# 0. two synthetic nets: the macro map, and (for L1 only) a standalone box
netgenerate --grid --grid.number=30 --grid.length=300 -o synth_macro.net.xml   # 8700x8700 m

# 1. CROP the box — this is what creates the fringe (cut edges become dangling stubs)
netconvert -s synth_macro.net.xml --keep-edges.in-boundary 2850,2850,5850,5850 -o sub.net.xml
#   -> 440 edges, 80 of them is_fringe()==True   (were 0 before the cut)

# 2. demand on the FULL macro, routed, with per-edge EXIT TIMES (cutRoutes needs these)
python3 randomTrips.py -n synth_macro.net.xml -r macro.rou.xml --period 0.8 --end 3600 --validate
sumo -c macro.sumocfg --vehroute-output macro.vehroutes.xml --vehroute-output.exit-times

# 3. CUT demand into the box, re-timing departures to the moment each car reaches the fringe
python3 route/cutRoutes.py sub.net.xml macro.vehroutes.xml \
        --routes-output sub.rou.xml --orig-net synth_macro.net.xml
#   -> 3852 macro vehicles -> 1608 kept (those that pass through the box)
```

**No-cheating audit of the cropped box (SUMO run, from tripinfo vs. fringe set):**

| | off-fringe (visible pop) |
|---|---|
| departures | **26.9 %** (433/1608) |
| arrivals | **25.7 %** (413/1608) |

This decomposes the demand cleanly and is the key structural insight:

- **~73 % is through-traffic** — enters at a fringe stub, exits at a fringe stub. Already clean,
  zero cheating, for free. `cutRoutes` re-times its departure to *when it would have reached the
  boundary* in the macro run, so flow phasing is inherited from the road hierarchy.
- **~27 % has a genuine origin/destination INSIDE the box.** These are the trips that pop on
  internal roads. They are *not* a bug — they are the demand that must be absorbed by **internal
  sinks (parkingArea)** to look believable. This is precisely the design's "cars hide at home/
  mall garages" mechanism, and the audit quantifies how much of it you need (~1/4 of demand here).

**Density knob:** the macro `--period` (headway) and the box size set density independently of the
macro. A denser box than the full map = smaller box and/or shorter period; through-traffic scales
with the fraction of macro routes crossing the window.

### L3 "procedural OD" — not needed yet

Deferred (boss's simple-first bias): L2 already delivers realistic phasing + the fringe/sink split.
`od2trips` is present if we escalate.

## Port (SumoSharp) — gap map so far

Fed the cropped L2 box to `Sim.Run`. Presence parity is **exact**: after clearing the parser
blockers below, SumoSharp and SUMO agree on **1608 distinct vehicles, 3600 steps, peak 136
concurrent** — the macro-crop pipeline runs end-to-end through the port.

Gaps hit, in the order they blocked the run:

| # | Gap | Layer | Severity | Notes |
|---|---|---|---|---|
| G1 | **Symbolic depart attrs rejected** — `departSpeed="max"`, `departLane="best"` (also `departPos`) throw `FormatException` in `DemandParser.ParseNullableDouble/Int`. | Ingest (parser) | **Blocker** | `cutRoutes` emits these BY DEFAULT for every cut vehicle. SUMO supports symbolic values (`max/desired/speedLimit/last/avg/random`, lane `best/free/random/allowed`, pos `random/free/base/last`). Port only accepts numerics. Worked around by `sed`-stripping them. |
| G1b | **`departSpeed="max"` matters for believability, not just parsing.** | Core semantics | Medium | A fringe car is through-traffic already moving; entering at `max` speed vs. the port's default 0 is the difference between "flows in at the boundary" and "materializes stopped at the boundary". Fringe insertion speed is a believability lever, so this isn't a cosmetic default. |
| G2 | **No additional-file handling at all** — `<additional-files>` in the sumocfg is not read; `<parkingArea>`, `<rerouter>`, detectors, etc. are invisible. | Ingest (parser) | **Blocker for internal sinks** | `grep` confirms zero `additional` handling in `Sim.Ingest`. The whole "internal sink" half of the no-cheating rule (parking) has no ingestion path. |
| G3 | **`<stop>` support is lane-only.** Parser reads `lane/startPos/endPos/duration`; `parkingArea=`, `busStop=`, `triggered`, `until`, waypoint (`speed>0`) stops are explicitly out (`DemandParser.cs:78-80`). Engine mirror `StopRuntime` also only models lane stops. | Ingest + Core | **Blocker for parking sinks** | The route-end→parking mapping (`<stop parkingArea=… duration=…/>`) the design calls for cannot be parsed or honored today. |
| G4 | **No `<rerouter>` element.** Rerouting exists as an internal Dijkstra/`ReplaceRoute` code knob, but the XML `<rerouter>` (parkingAreaReroute / overflow-to-adjacent) is not ingested. | Ingest + Core | Medium | Auto-park / overflow-to-roadside needs this or an API equivalent. |
| — | **No native crop/cut seam.** Selecting a bbox → cropping the net → cutting+re-timing routes currently *must* shell out to `netconvert` + `cutRoutes`. The port has no equivalent authoring API. | (design) | — | To be assessed as a SumoSharp-native seam vs. staying a preprocessing step. |

Fringe-only through-traffic (the ~73 %) **already works through the port today** (modulo G1).
The ~27 % internal-O/D demand needs G2+G3 (parking ingestion + parkingArea stops) before the port
can render it without popping.

## Open questions for next steps

1. Demonstrate the **parking/internal-sink** mechanism in SUMO (define `<parkingArea>`, append
   `<stop parkingArea=…>` to internal-O/D routes, `<rerouter>` for overflow) and confirm no popping
   — then re-probe the port against it (expands G2/G3/G4 with concrete evidence).
2. Decide the L2 "good enough" bar for believability (through-only vs. through+parking).
3. (Later) run the same pipeline on the user's real manually-tuned Geneva net + scenarios.
