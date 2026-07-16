#!/usr/bin/env bash
#
# run-experiment.sh — sub-area traffic pipeline experiment (L1 + L2) driver.
#
# EXPLORATORY / AUTHORING ONLY. Uses real SUMO tools to author nets + demand and
# to crop a sub-area box. Nothing here is a `dotnet test` dependency. Generated
# nets/routes land in experiments/subarea/scratch/ (gitignored) — this script and
# the findings doc are the only committed artifacts.
#
# Prereqs (ephemeral, VM-volatile):
#   python3 -m pip install "eclipse-sumo==1.20.0"   # SUMO 1.20.0 tools + binaries
#   apt-get install -y dotnet-sdk-8.0               # to run the port (Sim.Run)
#
set -euo pipefail
REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

# SUMO on PATH (pip install location on this VM).
export SUMO_HOME="${SUMO_HOME:-/usr/local/lib/python3.11/dist-packages/sumo}"
export PATH="$SUMO_HOME/bin:$PATH"
export PYTHONPATH="$SUMO_HOME/tools:${PYTHONPATH:-}"

S="$REPO_ROOT/experiments/subarea/scratch"
mkdir -p "$S"
cd "$S"

echo "== 1. Synthetic networks =="
# The sub-area itself (a standalone ~3x3 km grid, traffic lights).
netgenerate --grid --grid.number=10 --grid.length=300 -j traffic_light -o synth_3x3.net.xml
# The larger macro map the box is later cut from.
netgenerate --grid --grid.number=30 --grid.length=300 -o synth_macro.net.xml

echo "== 2. L1: weighted-random trips on the standalone area (fringe-biased) =="
# NOTE (finding): a *closed* grid has NO fringe edges (every node has matched
# in/out degree), so --fringe-factor is a no-op and 100% of trips pop in/out on
# INTERNAL roads. L1-on-a-raw-grid CANNOT satisfy the no-cheating rule. The fringe
# only exists once you crop a box (step 3). Kept here to demonstrate that.
python3 "$SUMO_HOME/tools/randomTrips.py" -n synth_3x3.net.xml \
  -o l1.trips.xml -r l1.rou.xml --begin 0 --end 3600 --period 1.5 \
  --fringe-factor 10 --validate

echo "== 3. L2: crop a 3x3 km box out of the macro (this MAKES the fringe) =="
BBOX="2850,2850,5850,5850"            # centered 3000x3000 m window
netconvert -s synth_macro.net.xml --keep-edges.in-boundary "$BBOX" -o sub.net.xml

echo "== 4. L2 demand: route on the FULL macro, dump vehroutes WITH exit times =="
python3 "$SUMO_HOME/tools/randomTrips.py" -n synth_macro.net.xml \
  -o macro.trips.xml -r macro.rou.xml --begin 0 --end 3600 --period 0.8 --validate
cat > macro.sumocfg <<'EOF'
<configuration>
  <input><net-file value="synth_macro.net.xml"/><route-files value="macro.rou.xml"/></input>
  <time><begin value="0"/><end value="3600"/><step-length value="1"/></time>
  <processing><time-to-teleport value="-1"/><default.speeddev value="0"/></processing>
</configuration>
EOF
sumo -c macro.sumocfg --vehroute-output macro.vehroutes.xml \
     --vehroute-output.exit-times --no-step-log true

echo "== 5. cutRoutes: crop demand into the box, re-timing fringe departures =="
python3 "$SUMO_HOME/tools/route/cutRoutes.py" sub.net.xml macro.vehroutes.xml \
  --routes-output sub.rou.xml --orig-net synth_macro.net.xml

cat > sub.sumocfg <<'EOF'
<configuration>
  <input><net-file value="sub.net.xml"/><route-files value="sub.rou.xml"/></input>
  <time><begin value="0"/><end value="3600"/><step-length value="1"/></time>
  <processing><time-to-teleport value="-1"/><default.speeddev value="0"/></processing>
</configuration>
EOF

echo "== 6. Run the cropped box in SUMO (ground truth) =="
sumo -c sub.sumocfg --fcd-output sub.fcd.xml --tripinfo-output sub.tripinfo.xml --no-step-log true

echo "== 7. No-cheating audit: fraction departing/arriving OFF the fringe =="
python3 - <<'PY'
import sumolib, xml.etree.ElementTree as ET
net = sumolib.net.readNet('sub.net.xml')
fringe = {e.getID() for e in net.getEdges()
          if e.getFunction() != 'internal' and e.is_fringe()}
edge = lambda lane: lane.rsplit('_', 1)[0]
bad_dep = bad_arr = tot = 0
for tr in ET.parse('sub.tripinfo.xml').getroot():
    tot += 1
    if edge(tr.get('departLane'))  not in fringe: bad_dep += 1
    if edge(tr.get('arrivalLane')) not in fringe: bad_arr += 1
print(f"  completed trips        : {tot}")
print(f"  depart off-fringe (pop): {bad_dep} ({100*bad_dep/tot:.1f}%)  <- need internal SINKS")
print(f"  arrive off-fringe (pop): {bad_arr} ({100*bad_arr/tot:.1f}%)  <- need internal SINKS")
PY

echo "== 8. Feed the cropped box to the port (SumoSharp) =="
# GAP: SumoSharp's DemandParser rejects cutRoutes' SYMBOLIC depart attributes
# (departSpeed="max", departLane="best"). Strip them so the port can ingest.
mkdir -p subScenario
cp sub.net.xml sub.sumocfg subScenario/
sed -E 's/ departSpeed="[a-z]+"//g; s/ departLane="[a-z]+"//g; s/ departPos="[a-z]+"//g' \
    sub.rou.xml > subScenario/sub.rou.xml
cd "$REPO_ROOT"
dotnet run --project src/Sim.Run -c Release -- "$S/subScenario" \
    --fcd-out "$S/subScenario/engine.fcd.xml"

echo "== DONE. Compare $S/subScenario/engine.fcd.xml (port) vs $S/sub.fcd.xml (SUMO) =="
