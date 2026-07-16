# Sub-area traffic experiment — self-contained handoff (pure SUMO, SumoData session)

This document is a **complete, standalone brief** for a new Claude Code session whose only repo is
the company data repo (`BagiraSystems/SumoData`). It carries over everything learned in the earlier
`pjanec/SumoSharp` session so no context is lost. **You do not need the SumoSharp repo or the C# port
to do this work** — it is pure SUMO authoring/investigation. (The port gap-analysis that was a
secondary goal earlier is finished and lives on the SumoSharp branch `claude/sumo-subarea-pipeline-
gaps-erjqo3` in `docs/SUBAREA-TRAFFIC-EXPERIMENT.md`; ignore it here unless asked.)

---

## 0. Goal

A user selects **any ~3×3 km sub-area** of a large real road network (e.g. Switzerland, low-fidelity,
with detailed city insets). Produce **believable SUMO traffic inside that box, fast** (ideally instant,
≤10 min worst case), fully automated, with one **hard rule:**

> **No visible cheating.** Cars may appear/disappear only at the network **FRINGE** (roads cut by the
> box boundary) or at internal **SINKS** (parkingArea = garage / lot / roadside parking). No teleporting
> or popping on visible roads. Parking-lot *internal* navigation is out of scope — a parkingArea is just
> a start/stop/capacity node.

Secondary goals: **higher traffic density** inside the small box than the full map, without gridlocking
primary roads; and keep **preparation simple and time-from-selection-to-ready short**.

---

## 1. What was proven (executive summary)

1. **L2 "macro-crop" is the pipeline.** Crop the box out of a bigger net (`netconvert
   --keep-edges.in-boundary`), then cut+re-time the demand into it (`cutRoutes.py`). The **fringe is
   created by the crop** — cut edges become dangling entry/exit stubs. L1 ("weighted random on a
   standalone generated grid") is a dead end: a *closed* grid has **zero fringe**, so 100% of trips pop
   on internal roads.
2. **The no-cheating rule is fully achievable.** Through-traffic (~73% of cut demand) enters/exits at
   the fringe for free. The ~27% with a genuine origin/destination *inside* the box are mapped to
   **parkingArea sinks** (fully automated): destinations pull off the road into a lot and stay; origins
   start parked and pull out. Audited: **0 vehicles pop on a visible internal lane.**
3. **The macro scales with the BOX, not the terrain.** Empirically, in-box aggregate traffic matched
   the full-macro ground truth to within 1% with **no halo at all** (best case; caveat in §5). You never
   need to microsimulate the country.
4. **No external demographic data is required for a believable box.** Land-use-weighted `randomTrips`
   produced a clean home→work pattern (**8.1× destination concentration** in the commercial core) from
   per-edge weights alone — weights derivable automatically from OSM land-use polygons.

---

## 2. Environment bootstrap (copy-paste)

SUMO is **not** pre-installed on a fresh VM here; install it (network-side, ephemeral — fine):

```bash
python3 -m pip install "eclipse-sumo==1.20.0"
# SUMO lands under the pip package; find it and put it on PATH + PYTHONPATH:
export SUMO_HOME="$(python3 -c 'import sumo,os;print(os.path.dirname(sumo.__file__))')"
export PATH="$SUMO_HOME/bin:$PATH"
export PYTHONPATH="$SUMO_HOME/tools:${PYTHONPATH:-}"
# verify:
sumo --version                       # -> Eclipse SUMO 1.20.0
netgenerate --version; netconvert --version; duarouter --version
python3 -c "import sumolib; print('sumolib ok')"
ls "$SUMO_HOME/tools/randomTrips.py" "$SUMO_HOME/tools/route/cutRoutes.py"
ls "$SUMO_HOME/bin" | grep -E 'polyconvert|od2trips|marouter'   # land-use / OD tooling present
```

Pin SUMO **1.20.0** (matches the vendored SUMO source the company uses). Keep large generated nets/
routes OUT of git — use a gitignored scratch dir (e.g. `experiments/scratch/`). No .NET / no C# port
needed.

**Network note (from the prior env):** real OSM hosts (Overpass, Geofabrik, `api.openstreetmap.org`)
were **blocked by the egress proxy** (403 / refused). If this SumoData session also blocks them, the
real net must come from files already committed in the SumoData repo (see §7). Check egress early with a
tiny Overpass query; if it works, the OSM land-use leg (§6) becomes runnable here.

---

## 3. The pipeline — exact commands (L2 macro-crop + parking sinks)

Given an arbitrary full net + demand + a bbox `minX,minY,maxX,maxY`:

```bash
# (1) CROP the box (+ optional halo ring). This CREATES the fringe.
netconvert -s FULL.net.xml --keep-edges.in-boundary minX,minY,maxX,maxY -o sub.net.xml

# (2) If you have full-map demand as trips only, first ROUTE it on the full net and dump vehroutes
#     WITH exit times AND unfinished vehicles (see §8 gotcha #1 — the write-unfinished flag is
#     mandatory or you silently lose ~14% of demand):
sumo -c FULL.sumocfg --vehroute-output full.vehroutes.xml \
     --vehroute-output.exit-times --vehroute-output.write-unfinished --no-step-log true

# (3) CUT the demand into the box; departures are re-timed to when each car reaches the fringe:
python3 "$SUMO_HOME/tools/route/cutRoutes.py" sub.net.xml full.vehroutes.xml \
        --routes-output sub.rou.xml --orig-net FULL.net.xml

# (4) Map internal origins/destinations to parkingArea SINKS (the no-cheating layer) — see §9 script:
python3 auto_parking.py sub.net.xml sub.rou.xml sub_parking.add.xml sub_parking.rou.xml

# (5) Run it (teleport off, deterministic):
#     sumocfg references net=sub.net.xml, routes=sub_parking.rou.xml, additional=sub_parking.add.xml
sumo -c sub_parking.sumocfg --fcd-output out.fcd.xml --tripinfo-output out.tripinfo.xml \
     --no-step-log true
```

**Density knob:** independent of the macro — smaller box and/or shorter demand `--period` (headway)
raise in-box density. Through-traffic volume scales with the fraction of full-map routes crossing the box.

**Timing measured (synthetic 30×30 grid macro, 3852 veh):** per-box = crop 1.1 s + cutRoutes 2.0 s +
auto_parking 0.2 s ≈ **3.3 s**; the one-time full-map routing sim = ~19 s. Per-box cost is bounded by the
box+halo size, not the terrain — this is why it stays "instant" at country scale.

---

## 4. The no-cheating fringe definition & audit

"Fringe" = the same definition `randomTrips` uses: an edge is fringe if `sumolib` reports
`edge.is_fringe()` (its from-node has no predecessors or its to-node no successors — i.e. a dangling
stub created by the crop). Audit any run like this:

```python
import sumolib, xml.etree.ElementTree as ET
net = sumolib.net.readNet('sub.net.xml')
fringe = {e.getID() for e in net.getEdges()
          if e.getFunction() != 'internal' and e.is_fringe()}
edge = lambda lane: lane.rsplit('_', 1)[0]
# From tripinfo: every COMPLETED trip must arrive on a fringe edge (else it popped out on a lane).
# From FCD: no vehicle may FIRST APPEAR on a non-fringe, non-parking lane (else it popped in).
```

On the synthetic net with parking sinks: **1195 through-trips all arrived on fringe (0 off-fringe); 0
non-parking vehicles first appeared on an internal lane.** Every visible-road birth/death is at the
fringe; all internal births/deaths are off-road inside a parkingArea. Rule satisfied.

---

## 5. Country-scale design (box-anywhere, automated)

**Reframing:** the macro does not scale with the terrain. What a box needs from outside is only two
*boundary conditions*: (a) inflow rate + timing at each fringe edge, and (b) the through-turn
distribution. Neither needs a country-scale micro sim.

- **Precompute ONCE per country net (cheap, no micro sim):** attach per-edge demand *weights* to the
  net — a **source weight** (trip-origin propensity), a **sink weight** (destination propensity), and a
  **through weight** (road capacity/hierarchy).
- **Per box (instant):** crop box + thin halo, synthesize demand locally with `randomTrips
  --weights-prefix <landuse> --fringe-factor N`, route, then `auto_parking.py`. Cost bounded by box+halo.

**Demand-fidelity ladder — the "do we need demographic data?" answer (NO, it's optional):**

| Rung | Demand source | External data | Effort |
|---|---|---|---|
| **D1** (default) | land-use-weighted `randomTrips` (src=residential, sink=commercial/work, through=capacity) + `--fringe-factor` | **none** — from OSM land-use polygons in the map | autogenerated |
| **D2** | D1 + ASTRA counts set motorway/arterial fringe volumes; municipal population weights residential | real counts + population (Swiss open data) | light |
| **D3** | full OD matrix → `od2trips`/`marouter` | census / activity model | heavy |

**Halo (warm-up buffer):** micro-sim box + a thin halo, show only the inner box. Cold-injected fringe
traffic relaxes into realistic microstructure within a few hundred metres, so the halo is a small
fraction of a multi-km box. Rule of thumb: local/collector ⇒ ~300–800 m; urban arterial ⇒ ~0.5–1.5 km;
a motorway's long-range *volume* is injected as a boundary inflow from counts rather than captured by
growing the halo. **Empirical (synthetic best case): h\* = 0** — in-box mean speed, veh-distance, and
distinct-vehicle count were within 1% of the full-macro ground truth with no halo at all, flat out to a
2400 m halo. **Caveat:** homogeneous grid + uniform demand + mild congestion + aggregate metrics; a real
net with directional bottlenecks / congestion propagating inward, or a need for entry platooning
realism, will want a non-zero halo. **Measure the real halo number on the actual net** (see §10).

**Automated end-to-end (real net):**
```
ONCE per country net (no micro sim):
  OSM  --netconvert-->  full.net.xml   (low-fi country + hi-res city insets via typemaps)
  OSM  --polyconvert-->  land-use polygons  --nearest-edge assign-->  per-edge src/dst weights (D1)
  [optional D2] ASTRA counts + population -> calibrate through/src weights
PER BOX (seconds):
  crop(box+halo) -> randomTrips(--weights-prefix,--fringe-factor) -> duarouter -> auto_parking -> run
```

---

## 6. Empirical results carried over (both independently verified)

**Land-use weighting (validates D1).** On the synthetic macro, commercial core = 2.87% of edges.
Weighted demand landed **22.7%** of trip destinations in the core vs **2.8%** under uniform (= the area
baseline) — an **8.1× concentration**; commercial origins suppressed 3.25% → 0.44%. Uniform tracked the
area baseline exactly (no hidden bias). ⇒ believable home→work structure from per-edge weights alone,
zero external data. On a real net the weights come from OSM land-use polygons via `polyconvert` +
nearest-edge assignment (`sumolib.net.getNeighboringEdges()`), accumulating polygon area per land-use
class per edge → the `<edge id value>` rows in `W.src.xml`/`W.dst.xml`. randomTrips knobs that matter:
`--weights-prefix`, `--fringe-factor` (through-traffic), `--vclass`, `--period`/`--insertion-rate`.

**Halo convergence (validates "macro scales with box").** h\* = 0 on the synthetic best case (see §5).

---

## 7. Gotchas (each cost real debugging time — heed them)

1. **`--vehroute-output.write-unfinished` is MANDATORY when dumping vehroutes to cut from.** Without it,
   SUMO silently drops vehicles still en route at sim end (14.4% = 648/4500 in our run). Every crop then
   inherits an identical demand shortfall that *looks like* spatial non-convergence but is a recording
   artifact. Always pair it with `--vehroute-output.exit-times`.
2. **The fringe only exists after a crop.** A standalone `netgenerate` grid (or any closed net) has zero
   fringe edges → `--fringe-factor` is a no-op and everything pops internally. Always crop first.
3. **Symbolic depart attrs** — `cutRoutes` emits `departSpeed="max"`, `departLane="best"` on every cut
   vehicle (and origin-parking needs `departPos="stop"`). These are correct SUMO and required for
   believability (a fringe car is already moving → enter at `max`, not from rest). Pure SUMO handles them
   natively; only the C# port choked on them (not your concern here).
4. **Parked-vehicle representation.** A `parkingArea` stop pulls the car off the running lane (it is not
   a lane leader/obstacle while parked). For viz, check whether your FCD includes parked vehicles
   (`--fcd-output` options) if you want to *show* cars sitting in lots vs. just having them vanish
   off-road. Set park duration >> sim end for a permanent sink; use a `<rerouter>` with
   `parkingAreaReroute` for overflow / finite-dwell turnover (a realism refinement, not required).

---

## 8. What to do in THIS (SumoData) session

The SumoData repo presumably contains the **real** assets (Geneva / Switzerland net(s), demand/routes,
possibly land-use or counts). First steps:

1. **Inventory the repo.** Find the real `*.net.xml`, `*.rou.xml`/trips, any `*.poly.xml` land-use,
   count/OD data, and any bbox definitions. Report what exists (sizes, coverage, resolution — country
   low-fi vs city insets). Ask (plain text) which sub-area(s) to target if not obvious.
2. **Run the L2 + parking pipeline on the real data** (§3) for one selected box; audit no-cheating (§4);
   eyeball believability (density, no gridlock on primaries, flow phasing).
3. **Do the legs that were BLOCKED before** (they needed real data / OSM egress):
   - **Real halo number:** repeat the §5 convergence test on the real net (heterogeneous, real
     bottlenecks) to get the halo depth that actually matters — the synthetic h\*=0 is a best case.
   - **Real land-use D1:** if OSM land-use polygons or a `polyconvert` step are available, build per-edge
     weights and generate demand without any full-country routing; compare believability to the cut-from-
     macro demand.
   - **Timing at real scale:** measure per-box crop+cut+parking time on the real (large) net; confirm
     ≤10 min worst case.
4. **Keep it simple-first** (boss's bias): prove L2 + parking believability on the real box before
   escalating to D2/D3 (counts/OD). Capture everything in a findings doc in the SumoData repo.

Work on a **feature branch**, never main. Ask questions as **plain chat text** (no interactive widgets).
Keep large generated files gitignored.

---

## 9. Carry-over scripts

Copy the whole `experiments/subarea/` directory from the SumoSharp branch
`claude/sumo-subarea-pipeline-gaps-erjqo3` (on `pjanec/SumoSharp`) into the SumoData repo, OR recreate
from the appendix below. Key files:
- `run-experiment.sh` — the synthetic L1+L2 driver (adapt paths; drop its final C#-port step).
- `auto_parking.py` — the parking-sink generator (full source in §10; the one essential novel artifact).
- `landuse/{landuse_zones.py,gen_weights.py,measure.py}` — synthetic land-use weighting + A/B measure.
- `halo/{compute_inner_edges.py,fcd_distinct.py}` — halo-convergence tooling.
- `RESULTS-landuse.md`, `RESULTS-halo.md` — the verified experiment reports.

---

## 10. Appendix — `auto_parking.py` (full source, self-contained)

```python
#!/usr/bin/env python3
"""
auto_parking.py — turn a cut sub-area route file into a NO-POPPING one by mapping
every internal origin/destination to a parkingArea sink, fully automatically.
Usage: auto_parking.py <sub.net.xml> <sub.rou.xml> <out.add.xml> <out.rou.xml>
"""
import sys, collections, xml.etree.ElementTree as ET
import sumolib

net_f, rou_f, add_out, rou_out = sys.argv[1:5]
net = sumolib.net.readNet(net_f)
fringe = {e.getID() for e in net.getEdges()
          if e.getFunction() != 'internal' and e.is_fringe()}

PARK_FOREVER = 100000   # >> sim end: destination cars park and stay (the sink)
PULLOUT      = 5        # origin cars sit briefly then merge into traffic

tree = ET.parse(rou_f); root = tree.getroot()
vehicles = root.findall('vehicle')

demand = collections.Counter(); plan = []
for v in vehicles:
    edges = v.find('route').get('edges').split()
    first, last = edges[0], edges[-1]
    o = first if first not in fringe else None
    d = last  if last  not in fringe else None
    if o: demand[o] += 1
    if d: demand[d] += 1
    plan.append((v, o, d))

add = ET.Element('additional')
for edge_id, cap in sorted(demand.items()):
    L = net.getEdge(edge_id).getLane(0).getLength()
    ET.SubElement(add, 'parkingArea', {
        'id': f'pa_{edge_id}', 'lane': f'{edge_id}_0',
        'startPos': '2.00', 'endPos': f'{max(4.0, L - 2.0):.2f}',
        'roadsideCapacity': str(max(1, cap))})
ET.ElementTree(add).write(add_out, encoding='UTF-8', xml_declaration=True)

for v, o, d in plan:
    if 'arrival' in v.attrib: del v.attrib['arrival']   # cutRoutes metadata, not input
    route = v.find('route'); ridx = list(v).index(route)
    if o:
        v.set('departPos', 'stop')                       # inserted already parked (off-road)
        v.insert(ridx + 1, ET.Element('stop',
                 {'parkingArea': f'pa_{o}', 'duration': str(PULLOUT)}))
    if d:
        ET.SubElement(v, 'stop',
                      {'parkingArea': f'pa_{d}', 'duration': str(PARK_FOREVER)})
tree.write(rou_out, encoding='UTF-8', xml_declaration=True)
print(f"parkingAreas: {len(demand)}  |  vehicles touched: "
      f"{sum(1 for _,o,d in plan if o or d)}/{len(vehicles)}")
```

**Land-use weight recipe (synthetic version; replace the zone test with OSM polygon→edge assignment
for the real net):** classify each edge by the mean of its shape coordinates into RESIDENTIAL vs
COMMERCIAL; write two `randomTrips` weight files in edgedata format —
`<edgedata><interval begin="0" end="T"><edge id=EID value=W/>…` — with `W.src.xml` = residential 1.0 /
commercial 0.1 and `W.dst.xml` = commercial 1.0 / residential 0.1; then
`randomTrips.py -n NET --weights-prefix W --fringe-factor N -r out.rou.xml --period P --validate`.

---

## 11. Where the finished port gap-analysis lives (for later, not this session)

The earlier session also probed whether the C# port (SumoSharp) could consume this workflow. Findings
(recorded in `docs/SUBAREA-TRAFFIC-EXPERIMENT.md` on branch `claude/sumo-subarea-pipeline-gaps-erjqo3`):
the port runs cropped through-traffic with exact presence parity, but lacks (G1) symbolic depart attrs,
(G2) additional-file/`<parkingArea>` parsing, (G3) parkingArea stops, (G3b) off-lane parked semantics,
(G4) `<rerouter>`. Not relevant to the pure-SUMO experiments here; listed only so the link isn't lost.
```
