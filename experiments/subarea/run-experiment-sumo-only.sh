#!/usr/bin/env bash
#
# run-experiment-sumo-only.sh — sub-area traffic pipeline (L1 demo + L2 + parking sinks),
# PURE SUMO, no C# port. Drop-in for the SumoData session. Self-contained: detects/installs
# SUMO, sets its own env (no external env.sh), and runs the whole synthetic pipeline end to end.
#
# EXPLORATORY / AUTHORING ONLY. Not a test dependency. Generated nets/routes go to a gitignored
# scratch dir. Companion to SUMODATA-HANDOFF.md (read that for the why); this is the runnable how.
#
# Prereq: python3 + pip. SUMO is installed here if missing (ephemeral, VM-volatile).
# Needs auto_parking.py next to this script (same dir).
#
set -euo pipefail

# ---- SUMO environment (self-contained; no external env.sh) --------------------------------
if ! command -v sumo >/dev/null 2>&1; then
  echo "==> SUMO not found; installing eclipse-sumo==1.20.0 (ephemeral) ..."
  python3 -m pip install "eclipse-sumo==1.20.0"
fi
export SUMO_HOME="${SUMO_HOME:-$(python3 -c 'import sumo,os;print(os.path.dirname(sumo.__file__))')}"
export PATH="$SUMO_HOME/bin:$PATH"
export PYTHONPATH="$SUMO_HOME/tools:${PYTHONPATH:-}"
sumo --version | head -1
python3 -c "import sumolib" || { echo "sumolib import failed"; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
S="${SCRATCH:-$SCRIPT_DIR/scratch}"
mkdir -p "$S"
cd "$S"

echo "== 1. Synthetic networks =="
netgenerate --grid --grid.number=10 --grid.length=300 -j traffic_light -o synth_3x3.net.xml
netgenerate --grid --grid.number=30 --grid.length=300 -o synth_macro.net.xml

echo "== 2. L1 demo (shows a CLOSED grid has NO fringe -> everything pops internally) =="
python3 "$SUMO_HOME/tools/randomTrips.py" -n synth_3x3.net.xml \
  -o l1.trips.xml -r l1.rou.xml --begin 0 --end 3600 --period 1.5 --fringe-factor 10 --validate
python3 - <<'PY'
import sumolib
net = sumolib.net.readNet('synth_3x3.net.xml')
fr = [e for e in net.getEdges() if e.getFunction()!='internal' and e.is_fringe()]
print(f"   L1 standalone grid fringe edges: {len(fr)} (0 => --fringe-factor is a no-op; L1 dead end)")
PY

echo "== 3. L2: crop a 3x3 km box out of the macro (this CREATES the fringe) =="
BBOX="2850,2850,5850,5850"
netconvert -s synth_macro.net.xml --keep-edges.in-boundary "$BBOX" -o sub.net.xml
python3 - <<'PY'
import sumolib
net = sumolib.net.readNet('sub.net.xml')
fr = [e for e in net.getEdges() if e.getFunction()!='internal' and e.is_fringe()]
print(f"   cropped sub-net fringe edges: {len(fr)} (>0 => the crop made the fringe)")
PY

echo "== 4. L2 demand: route on the FULL macro; vehroutes WITH exit-times + write-unfinished =="
# write-unfinished is MANDATORY (see handoff gotcha #1): without it ~14% of demand is silently
# dropped and every crop inherits the same shortfall.
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
     --vehroute-output.exit-times --vehroute-output.write-unfinished --no-step-log true

echo "== 5. cutRoutes: crop demand into the box, re-timing fringe departures =="
python3 "$SUMO_HOME/tools/route/cutRoutes.py" sub.net.xml macro.vehroutes.xml \
  --routes-output sub.rou.xml --orig-net synth_macro.net.xml

echo "== 6. Parking sinks: map internal origins/destinations to parkingAreas (no popping) =="
python3 "$SCRIPT_DIR/auto_parking.py" sub.net.xml sub.rou.xml sub_parking.add.xml sub_parking.rou.xml
cat > sub_parking.sumocfg <<'EOF'
<configuration>
  <input>
    <net-file value="sub.net.xml"/>
    <route-files value="sub_parking.rou.xml"/>
    <additional-files value="sub_parking.add.xml"/>
  </input>
  <time><begin value="0"/><end value="3600"/><step-length value="1"/></time>
  <processing><time-to-teleport value="-1"/><default.speeddev value="0"/></processing>
</configuration>
EOF

echo "== 7. Run the parking-enabled box in SUMO =="
sumo -c sub_parking.sumocfg --fcd-output sub_parking.fcd.xml \
     --tripinfo-output sub_parking.tripinfo.xml --no-step-log true

echo "== 8. No-cheating audit =="
python3 - <<'PY'
import sumolib, xml.etree.ElementTree as ET
net = sumolib.net.readNet('sub.net.xml')
fringe = {e.getID() for e in net.getEdges() if e.getFunction()!='internal' and e.is_fringe()}
edge = lambda l: l.rsplit('_',1)[0]
tot=badarr=0
for tr in ET.parse('sub_parking.tripinfo.xml').getroot():
    tot+=1
    if edge(tr.get('arrivalLane')) not in fringe: badarr+=1
first={}
for _,el in ET.iterparse('sub_parking.fcd.xml',events=('end',)):
    if el.tag=='timestep':
        for v in el:
            if v.get('lane') and v.get('id') not in first: first[v.get('id')]=edge(v.get('lane'))
        el.clear()
op={v.get('id') for v in ET.parse('sub_parking.rou.xml').getroot().findall('vehicle')
    if v.get('departPos')=='stop'}
badfirst=sum(1 for i,e0 in first.items() if i not in op and e0 not in fringe)
print(f"   completed (arrived-on-lane) trips : {tot}")
print(f"   arrived OFF-fringe (pop-out)      : {badarr}  <- must be 0")
print(f"   appeared on internal lane (pop-in): {badfirst}  <- must be 0 (parking origins excluded)")
print("   PASS" if badarr==0 and badfirst==0 else "   FAIL")
PY
echo "== DONE. Outputs in: $S =="
