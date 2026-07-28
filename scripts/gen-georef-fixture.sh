#!/usr/bin/env bash
#
# gen-georef-fixture.sh
# ---------------------
# docs/EXTERNAL-NET-VIEWER-DESIGN.md §6: generates the committed GEOREFERENCED 3-D test fixture
# `scenarios/_ped/georef_min/` -- a small synthetic stand-in for a SumoData `preprocess.py` cut
# sub-area (a Geneva box), which the SumoSharp repo cannot carry (a real cut is 100s of MB and is
# produced by a pipeline living in another repo).
#
# WHAT MAKES IT A STAND-IN (the four properties the external-net-loading work must handle, and that
# NO existing committed fixture has together):
#   1. GEOREFERENCED -- <location projParameter="+proj=utm +zone=32 ..."/> with a large negative
#      netOffset, so SUMO-local coordinates are the UTM32N absolute frame minus that offset. Exactly
#      the frame BIG converts back to UTM with `sumo - netOffset`.
#   2. 3-D -- every lane shape carries a real z (~370-400 m, Geneva's elevation band), so
#      Lane.ShapeZ is non-null and the ped-elevation work (design §4) has something to sample.
#   3. NOT NAMED net.xml -- the net is `scenario.net.xml` and there is a `scenario.sumocfg`, which is
#      precisely the naming a cut sub-area has and that `LiveCitySim` could not load before this work
#      (it hardcoded `<datasetDir>/net.xml`).
#   4. FAR FROM THE ORIGIN -- it is produced as a real CUT (`netconvert -s FULL
#      --keep-edges.in-boundary <bbox>`, no --offset.*), so like a real Geneva box it INHERITS the
#      full net's netOffset and its own local coordinates are ~90 km / ~78 km out. That magnitude is
#      the whole reason the Godot viewer needs a recenter (design §5, T2): a bare (float) cast of a
#      ~1e5 coordinate has ~1 cm ULP, which jitters once composed with camera/MultiMesh transforms.
#      A fixture sitting at 0..400 would silently pass a viewer that has no recenter at all.
#
# This is DEV-SIDE TOOLING ONLY, exactly like scripts/prep-ped-net.sh: it is never invoked by
# `dotnet test`, and its OUTPUT is committed (CLAUDE.md "the committed-vs-ephemeral split"). Re-run it
# only when the fixture must change or SUMO_VERSION is bumped; then commit the regenerated files.
#
# USAGE:
#   scripts/gen-georef-fixture.sh [outDir]        # default: scenarios/_ped/georef_min
#
# REQUIRES: `netconvert` + `duarouter` on PATH (a SUMO install matching SUMO_VERSION).

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
out_dir="${1:-$repo_root/scenarios/_ped/georef_min}"

for tool in netconvert duarouter; do
    command -v "$tool" >/dev/null 2>&1 || { echo "error: $tool not on PATH" >&2; exit 1; }
done

sumo_version="$(grep -E '^SUMO_VERSION=' "$repo_root/SUMO_VERSION" | cut -d= -f2)"
netconvert_version="$(netconvert --version | head -1)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# ---- plain-XML input, in GEO coordinates (lon/lat) so --proj.utm georeferences the result --------
# A 3x3 grid centred near Geneva (6.140 E, 46.200 N). Spacing is ~0.0025 deg lon / ~0.0018 deg lat,
# which is ~190 m each way at this latitude -- big enough for real edges/queues, small enough that the
# committed net stays a few hundred KB. Every node carries a DISTINCT elevation across a ~30 m band so
# the net is genuinely 3-D and a z sampled at one junction differs measurably from another's.
#
# Plus an ANCHOR stub far to the south-west (5.00 E, 45.50 N -- roughly Grenoble). It exists for ONE
# reason: netconvert normalizes the net so the SW-most point lands at the origin, so the anchor is
# what puts the grid's own local coordinates ~90 km east / ~78 km north, the magnitude a real cut
# sub-area of a country-sized net has. The anchor is then CROPPED AWAY below, exactly as SumoData's
# preprocess.py crops a box out of the Switzerland net -- and, exactly as there, the crop does NOT
# renormalize, so the box keeps the full net's netOffset and its far-from-origin coordinates.
python3 - "$work" <<'PY'
import sys, pathlib
work = pathlib.Path(sys.argv[1])

LON0, LAT0 = 6.140, 46.200
DLON, DLAT = 0.0025, 0.0018
ANCHOR = (5.00, 45.50)
# Elevation field: a gentle ramp + a bump, so no two grid nodes share a z.
def elev(i, j):
    return 370.0 + 4.0 * i + 7.0 * j + 1.5 * i * j

nodes = ['<nodes>']
for i in range(3):
    for j in range(3):
        nodes.append(
            f'  <node id="n{i}{j}" x="{LON0 + i * DLON:.6f}" y="{LAT0 + j * DLAT:.6f}" '
            f'z="{elev(i, j):.2f}" type="priority"/>'
        )
nodes.append(f'  <node id="anchor_a" x="{ANCHOR[0]:.6f}" y="{ANCHOR[1]:.6f}" z="200.00" type="priority"/>')
nodes.append(f'  <node id="anchor_b" x="{ANCHOR[0] + 0.002:.6f}" y="{ANCHOR[1]:.6f}" z="200.00" type="priority"/>')
nodes.append('</nodes>')
(work / 'plain.nod.xml').write_text('\n'.join(nodes) + '\n')

# Two lanes per direction so lane-change/multi-lane code paths are exercised, 50 km/h urban speed.
edges = ['<edges>']
for i in range(3):
    for j in range(3):
        if i + 1 < 3:
            edges.append(f'  <edge id="e_{i}{j}_{i+1}{j}" from="n{i}{j}" to="n{i+1}{j}" numLanes="2" speed="13.89"/>')
            edges.append(f'  <edge id="e_{i+1}{j}_{i}{j}" from="n{i+1}{j}" to="n{i}{j}" numLanes="2" speed="13.89"/>')
        if j + 1 < 3:
            edges.append(f'  <edge id="e_{i}{j}_{i}{j+1}" from="n{i}{j}" to="n{i}{j+1}" numLanes="2" speed="13.89"/>')
            edges.append(f'  <edge id="e_{i}{j+1}_{i}{j}" from="n{i}{j+1}" to="n{i}{j}" numLanes="2" speed="13.89"/>')
edges.append('  <edge id="e_anchor" from="anchor_a" to="anchor_b" numLanes="1" speed="13.89"/>')
edges.append('</edges>')
(work / 'plain.edg.xml').write_text('\n'.join(edges) + '\n')
PY

# ---- stage 1: the "full" net (grid + far SW anchor), UTM32N-projected -----------------------------
# --proj.plain-geo says "the plain XML x/y are lon/lat"; --proj.utm picks the UTM zone from them
# (zone 32 at 6.14 E) and writes the resulting <location netOffset/projParameter/> block. NO
# --offset.* / reprojection option is passed here or in the crop below, so the emitted netOffset is
# the real absolute georeference BIG relies on (design §2).
# The ped-guessing flags are exactly scripts/prep-ped-net.sh's recipe.
netconvert \
    --node-files "$work/plain.nod.xml" \
    --edge-files "$work/plain.edg.xml" \
    --proj.utm --proj.plain-geo \
    --sidewalks.guess --crossings.guess --walkingareas \
    --no-turnarounds true \
    --output-file "$work/full.net.xml"

# ---- stage 2: CUT the Geneva box out, the way preprocess.py does ----------------------------------
# `-s FULL --keep-edges.in-boundary <x0,y0,x1,y1>`, no --offset.* and no reprojection, so <location>
# passes through untouched: the cut INHERITS the full net's netOffset and keeps its own large local
# coordinates. The bbox is read from the full net (the grid's own extent, padded) rather than
# hardcoded, so it stays correct if the grid geometry above is ever changed.
bbox="$(python3 - "$work/full.net.xml" <<'PY'
import re, sys, pathlib
# Grid nodes are the ones named n<i><j>; the anchor pair is excluded by construction.
text = pathlib.Path(sys.argv[1]).read_text()
xs, ys = [], []
for m in re.finditer(r'<junction id="(n\d\d)"[^>]*?\sx="([-\d.]+)"\sy="([-\d.]+)"', text):
    xs.append(float(m.group(2))); ys.append(float(m.group(3)))
if not xs:
    sys.exit("no grid junctions found in the full net")
pad = 100.0
print(f"{min(xs)-pad:.2f},{min(ys)-pad:.2f},{max(xs)+pad:.2f},{max(ys)+pad:.2f}")
PY
)"

mkdir -p "$out_dir"
netconvert \
    -s "$work/full.net.xml" \
    --keep-edges.in-boundary "$bbox" \
    --output-file "$out_dir/scenario.net.xml"

# ---- demand: a handful of routed trips across the grid --------------------------------------------
# LiveCitySim only ever SCRAPES this file for its drivable-edge set (it generates its own procedural
# demand), so a small routed set is plenty; it exists so the fixture exercises the route-file path of
# `ForSumocfg` rather than the net-derived fallback.
cat > "$work/trips.xml" <<'XML'
<routes>
    <trip id="t0" depart="0.00"  from="e_00_10" to="e_21_22"/>
    <trip id="t1" depart="1.00"  from="e_22_12" to="e_10_00"/>
    <trip id="t2" depart="2.00"  from="e_01_02" to="e_20_21"/>
    <trip id="t3" depart="3.00"  from="e_20_10" to="e_02_01"/>
    <trip id="t4" depart="4.00"  from="e_11_21" to="e_01_00"/>
</routes>
XML

duarouter \
    --net-file "$out_dir/scenario.net.xml" \
    --route-files "$work/trips.xml" \
    --output-file "$out_dir/scenario.rou.xml" \
    --ignore-errors true \
    --no-warnings true

rm -f "$out_dir/scenario.rou.alt.xml"

# ---- the .sumocfg (RELATIVE paths, demo-city style) ------------------------------------------------
# `ForSumocfg` must handle BOTH this and the ABSOLUTE-path form preprocess.py emits; the absolute form
# is covered by a unit test that writes one to a temp dir (it cannot be committed portably).
cat > "$out_dir/scenario.sumocfg" <<'XML'
<?xml version="1.0" encoding="UTF-8"?>
<configuration>
    <input>
        <net-file value="scenario.net.xml"/>
        <route-files value="scenario.rou.xml"/>
    </input>
    <time><begin value="0"/><step-length value="1.0"/></time>
</configuration>
XML

# ---- provenance (CLAUDE.md: a committed input records which SUMO produced it) ----------------------
cat > "$out_dir/provenance.txt" <<EOF
georef_min -- synthetic GEOREFERENCED 3-D pedestrian-capable fixture
====================================================================

Generated by: scripts/gen-georef-fixture.sh
SUMO_VERSION (pinned): $sumo_version
netconvert:            $netconvert_version
duarouter:             $(duarouter --version | head -1)

WHY IT EXISTS
-------------
A stand-in for a SumoData preprocess.py cut sub-area (a Geneva box), which this repo cannot carry.
It is the only committed fixture that is simultaneously:
  * georeferenced (UTM32N <location projParameter=...>, large netOffset),
  * 3-D (every lane shape carries a z in the ~370-400 m Geneva band => Lane.ShapeZ non-null),
  * named scenario.net.xml with a companion scenario.sumocfg (a cut's naming, NOT net.xml).

RECIPE
------
3x3 grid of nodes at lon/lat around (6.140 E, 46.200 N), spacing ~190 m, each node at a distinct
elevation (370 + 4i + 7j + 1.5ij metres). Two lanes per direction, 13.89 m/s.

  netconvert --node-files plain.nod.xml --edge-files plain.edg.xml \\
             --proj.utm --proj.plain-geo \\
             --sidewalks.guess --crossings.guess --walkingareas \\
             --no-turnarounds true --output-file scenario.net.xml
  duarouter  --net-file scenario.net.xml --route-files trips.xml \\
             --output-file scenario.rou.xml --ignore-errors true

No --offset.* / reprojection option is passed, so <location> carries netconvert's own natural
netOffset -- the absolute UTM georeference the BIG/Spectacle side converts back with
"utm = sumo - netOffset" (docs/EXTERNAL-NET-VIEWER-DESIGN.md §2). Do not add offset
re-normalization here: consumers depend on that offset being the real one.

NOT a parity/golden scenario. It has no golden.fcd.xml and no tolerance.json; it is a LOADER and
VIEWER fixture only, consumed by tests/Sim.LiveCity.Tests.
EOF

echo "wrote fixture to $out_dir"
ls -la "$out_dir"
