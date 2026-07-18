#!/usr/bin/env python3
"""
build.py -- deterministic generator for the SYNTHETIC SUMO parity scenario.

Reproduces two vanilla-SUMO-1.20.0 vs SumoSharp engine-behaviour divergences on a
GEOMETRY-FREE synthetic net (no real road-network data), so it can be shared as the
golden repro with the SumoSharp team.

Issue 1 (park-and-stay residency): several vehicles carry
    <stop parkingArea="PA" duration="100000"/>
a duration far past sim end. In vanilla they stay PARKED and resident for the whole
run (never "arrive", never in tripinfo, keep the `running` count high). SumoSharp
removes them at parking arrival -> they complete (appear in tripinfo) and drop out of
`running`.

Issue 2 (excess deadlock/jam-teleport, best-effort): a grid of UNSIGNALIZED priority
junctions with heavy, turn-crossing demand, time-to-teleport=120 and device.rerouting
-- an attempt to provoke SumoSharp junction deadlock while vanilla keeps flowing.

Everything is deterministic: fixed RNG seed, list-form subprocess (shell=False),
sys.executable for python tools, relative paths in the generated cfg. Re-runnable.

Usage:  python3 build.py [--out DIR] [--grid-number N] [--grid-length L] [--lanes K]
                         [--junction-type T] [--seed S] [--end N]
                         [--through M] [--park-stay P] [--depart-parked D]
                         [--through-period SEC]
"""
import argparse
import os
import random
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
# vType files copied verbatim from the real produced fixture (they carry NO geometry).
PORTABLE = os.path.abspath(os.path.join(
    HERE, "..", "..", "scratch", "fixture", "portable"))
VTYPE_FILES = ["vType.config.xml", "vTypeDist.config.xml", "vType_pedestrians.xml"]


def find_sumo_home():
    home = os.environ.get("SUMO_HOME")
    if home and os.path.isdir(os.path.join(home, "tools")):
        return home
    exe = shutil.which("sumo") or shutil.which("netgenerate")
    if exe:
        # <prefix>/bin/sumo -> <prefix>
        cand = os.path.dirname(os.path.dirname(os.path.realpath(exe)))
        if os.path.isdir(os.path.join(cand, "tools")):
            return cand
    for cand in ("/usr/local/lib/python3.11/dist-packages/sumo",
                 "/usr/share/sumo", "/usr/local/share/sumo"):
        if os.path.isdir(os.path.join(cand, "tools")):
            return cand
    raise SystemExit("Could not locate SUMO_HOME (needs <home>/tools for sumolib).")


SUMO_HOME = find_sumo_home()
sys.path.insert(0, os.path.join(SUMO_HOME, "tools"))
import sumolib  # noqa: E402


def run(cmd):
    """List-form subprocess, shell=False, cross-platform. Fail loud."""
    print("  $", " ".join(cmd))
    subprocess.run(cmd, check=True)


def netgenerate_bin():
    return shutil.which("netgenerate") or os.path.join(SUMO_HOME, "bin", "netgenerate")


def generate_net(out_dir, args):
    net_path = os.path.join(out_dir, "grid.net.xml")
    cmd = [
        netgenerate_bin(),
        "--grid",
        "--grid.number", str(args.grid_number),
        "--grid.length", str(args.grid_length),
        "--default-junction-type", args.junction_type,
        "--grid.attach-length", str(args.attach_length),
        "--turn-lanes", "0",
        "--no-turnarounds", "false",
        "--tls.discard-simple", "true",
        "--seed", str(args.seed),
        "--output-file", net_path,
    ]
    if args.lanes:
        cmd += ["--default.lanenumber", str(args.lanes)]
    run(cmd)
    return net_path


def classify_edges(net):
    """Split real (non-internal) edges into entry / exit / interior sets.

    The grid is built with --grid.attach-length, so every peripheral node gets a
    dead-end stub. sumolib is_fringe() is True on those stub edges (and the audit
    uses exactly is_fringe() for its fringe set, so we must agree). A stub-end node
    has a single neighbour (degree 2 in half-edges). Entry edges leave a stub-end
    (vehicle drives INTO the net); exit edges enter a stub-end (vehicle leaves).
    """
    def stub_end(node):
        return (len(node.getIncoming()) + len(node.getOutgoing())) <= 2

    real = [e for e in net.getEdges()
            if e.getFunction() != "internal" and e.allows("passenger")]
    entry, exit_, interior = [], [], []
    for e in real:
        if e.is_fringe():
            if stub_end(e.getFromNode()):
                entry.append(e)
            if stub_end(e.getToNode()):
                exit_.append(e)
        else:
            interior.append(e)
    return entry, exit_, interior


def route_between(net, a, b):
    path, _ = net.getShortestPath(a, b, vClass="passenger")
    if not path:
        return None
    return [e.getID() for e in path]


def pick_parking_edges(interior, rng, n):
    """Interior edges long enough to hold a parkingArea, deterministic order."""
    cands = sorted((e for e in interior if e.getLength() >= 20.0),
                   key=lambda e: e.getID())
    rng.shuffle(cands)
    return cands[:n]


def build(args):
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)
    rng = random.Random(args.seed)

    print("[1/5] netgenerate ...")
    net_path = generate_net(out_dir, args)
    net = sumolib.net.readNet(net_path)

    entry, exit_, interior = classify_edges(net)
    print(f"      entry={len(entry)} exit={len(exit_)} interior={len(interior)}")
    if len(entry) < 2 or len(exit_) < 2:
        raise SystemExit("Not enough fringe edges; increase --grid-number.")

    # -- parkingAreas (multi-occupant sinks + single-space origin lots) ----------
    print("[2/5] parkingArea .add.xml ...")
    n_sink_pa = max(3, args.park_stay // args.sink_capacity + 1)
    n_origin_pa = max(3, args.depart_parked)
    park_edges = pick_parking_edges(interior, rng, n_sink_pa + n_origin_pa)
    if len(park_edges) < n_sink_pa + n_origin_pa:
        raise SystemExit("Not enough interior edges for parkingAreas.")
    sink_pa_edges = park_edges[:n_sink_pa]
    origin_pa_edges = park_edges[n_sink_pa:]

    add_root = ET.Element("additional")
    pa_id = {}          # edge-id -> parkingArea id
    pa_capacity = {}
    for e in sink_pa_edges:
        pid = f"pa_{e.getID()}"
        pa_id[e.getID()] = pid
        cap = args.sink_capacity          # multi-occupant sink (roadsideCapacity>1)
        pa_capacity[pid] = cap
        L = e.getLength()
        # The sink parkingArea sits on LANE 0 of a MULTI-LANE edge. This is what makes
        # ss's park-and-stay-residency bug bite (Issue 1): ss's StopLineConstraint only
        # brakes for a stop when stop.LaneId == the vehicle's current lane, and ss does
        # NOT perform the strategic lane-change onto the parking lane that vanilla does.
        # So a park-and-stay car that departs on lane 1 (departLane="best") never brakes
        # for the lane-0 stop, drives off the END of its final (parking) edge, and is
        # removed as "arrived" -> it appears in tripinfo. Vanilla changes to lane 0,
        # parks, and stays resident (never in tripinfo). Bay length is moderate; it is
        # the LANE mismatch, not the bay, that drives the divergence.
        end = min(L - 2.0, args.sink_bay)
        ET.SubElement(add_root, "parkingArea", {
            "id": pid, "lane": f"{e.getID()}_0",
            "startPos": "2.00", "endPos": f"{end:.2f}",
            "roadsideCapacity": str(cap), "friendlyPos": "true"})
    for e in origin_pa_edges:
        pid = f"pa_{e.getID()}"
        pa_id[e.getID()] = pid
        pa_capacity[pid] = 2
        L = e.getLength()
        ET.SubElement(add_root, "parkingArea", {
            "id": pid, "lane": f"{e.getID()}_0",
            "startPos": "2.00", "endPos": f"{min(L - 2.0, 16.0):.2f}",
            "roadsideCapacity": "2", "friendlyPos": "true"})
    add_path = os.path.join(out_dir, "scenario.add.xml")
    ET.ElementTree(add_root).write(add_path, encoding="UTF-8", xml_declaration=True)

    # -- demand ------------------------------------------------------------------
    # Build three pools of vehicle SPECS, then INTERLEAVE them onto one dense
    # departure schedule so park-and-stay / depart-parked cars experience the same
    # junction congestion as through traffic and actually get rerouted
    # (device.rerouting.probability=1.0). Long, grid-crossing routes are what make
    # rerouting meaningful -- that is the path that triggers the SumoSharp
    # park-and-stay-residency and junction-deadlock divergences.
    print("[3/5] demand .rou.xml ...")
    MIN_LEN = max(4, args.grid_number)      # require long, junction-crossing routes

    through_specs, parkstay_specs, departparked_specs = [], [], []

    attempts = 0
    while len(through_specs) < args.through and attempts < args.through * 60:
        attempts += 1
        a, b = rng.choice(entry), rng.choice(exit_)
        if a.getID() == b.getID():
            continue
        edges = route_between(net, a, b)
        if not edges or len(edges) < MIN_LEN:
            continue
        through_specs.append(dict(edges=edges, lane="best", speed="max",
                                  pos=None, stops=[]))

    sink_cycle = 0
    attempts = 0
    while len(parkstay_specs) < args.park_stay and attempts < args.park_stay * 120:
        attempts += 1
        a = rng.choice(entry)
        sink_e = sink_pa_edges[sink_cycle % len(sink_pa_edges)]
        edges = route_between(net, a, sink_e)
        if not edges or len(edges) < MIN_LEN:
            continue
        pid = pa_id[sink_e.getID()]
        parkstay_specs.append(dict(edges=edges, lane="best", speed="max",
                                   pos=None, stops=[(pid, 100000)]))
        sink_cycle += 1

    origin_cycle = 0
    attempts = 0
    while len(departparked_specs) < args.depart_parked and attempts < args.depart_parked * 120:
        attempts += 1
        origin_e = origin_pa_edges[origin_cycle % len(origin_pa_edges)]
        b = rng.choice(exit_)
        if origin_e.getID() == b.getID():
            continue
        edges = route_between(net, origin_e, b)
        if not edges or len(edges) < MIN_LEN:
            continue
        pid = pa_id[origin_e.getID()]
        departparked_specs.append(dict(edges=edges, lane="0", speed="max",
                                       pos="stop", stops=[(pid, 5)]))
        origin_cycle += 1

    made = {"through": len(through_specs), "park_stay": len(parkstay_specs),
            "depart_parked": len(departparked_specs)}

    # interleave: round-robin the three pools onto one schedule (deterministic).
    pools = [list(through_specs), list(parkstay_specs), list(departparked_specs)]
    weights = [max(1, len(p)) for p in pools]
    order = []
    idx = [0, 0, 0]
    remaining = sum(len(p) for p in pools)
    while remaining:
        for k in range(3):
            # emit roughly proportional to pool size each round
            take = max(1, round(weights[k] / max(weights)))
            for _ in range(take):
                if idx[k] < len(pools[k]):
                    order.append(pools[k][idx[k]])
                    idx[k] += 1
                    remaining -= 1

    routes = ET.Element("routes")
    depart_t = 0.0
    for vid, spec in enumerate(order):
        v = ET.SubElement(routes, "vehicle", {
            "id": str(vid), "depart": f"{depart_t:.2f}",
            "departLane": spec["lane"], "departSpeed": spec["speed"]})
        if spec["pos"]:
            v.set("departPos", spec["pos"])
        ET.SubElement(v, "route", {"edges": " ".join(spec["edges"])})
        for pa, dur in spec["stops"]:
            ET.SubElement(v, "stop", {"parkingArea": pa, "duration": str(dur)})
        depart_t += args.through_period
    vid = len(order)

    rou_path = os.path.join(out_dir, "scenario.rou.xml")
    ET.indent(routes, space="  ")
    ET.ElementTree(routes).write(rou_path, encoding="UTF-8", xml_declaration=True)
    print(f"      vehicles: {made}  total={vid}")

    # -- self-audit: births/deaths must be fringe or parking (no-cheating) -------
    fringe_ids = {e.getID() for e in entry} | {e.getID() for e in exit_}
    park_ids = set(pa_id.keys())
    for v in routes.findall("vehicle"):
        edges = v.find("route").get("edges").split()
        origin_park = v.get("departPos") == "stop"
        first_ok = edges[0] in park_ids if origin_park else edges[0] in fringe_ids
        stops = v.findall("stop")
        dest_park = bool(stops) and stops[-1].get("parkingArea") == f"pa_{edges[-1]}"
        last_ok = edges[-1] in park_ids if dest_park else edges[-1] in fringe_ids
        if not (first_ok and last_ok):
            raise SystemExit(f"self-audit: vehicle {v.get('id')} would cheat "
                             f"(first={edges[0]} last={edges[-1]})")
    print("      self-audit OK (all births/deaths at fringe or parking)")

    # -- vType files (copied verbatim) -------------------------------------------
    print("[4/5] copy vType files ...")
    for f in VTYPE_FILES:
        src = os.path.join(PORTABLE, f)
        shutil.copyfile(src, os.path.join(out_dir, f))

    # -- sumocfg (processing/routing block mirrors the real scenario.sumocfg) ----
    print("[5/5] scenario.sumocfg ...")
    cfg = ET.Element("configuration")
    inp = ET.SubElement(cfg, "input")
    ET.SubElement(inp, "net-file", {"value": "grid.net.xml"})
    ET.SubElement(inp, "route-files", {
        "value": "vType.config.xml,vType_pedestrians.xml,vTypeDist.config.xml,scenario.rou.xml"})
    ET.SubElement(inp, "additional-files", {"value": "scenario.add.xml"})
    tm = ET.SubElement(cfg, "time")
    ET.SubElement(tm, "begin", {"value": "0"})
    ET.SubElement(tm, "step-length", {"value": "1.0"})
    proc = ET.SubElement(cfg, "processing")
    ET.SubElement(proc, "time-to-teleport", {"value": "120"})
    ET.SubElement(proc, "ignore-route-errors", {"value": "true"})
    ET.SubElement(proc, "collision.action", {"value": "none"})
    rt = ET.SubElement(cfg, "routing")
    ET.SubElement(rt, "routing-algorithm", {"value": "astar"})
    ET.SubElement(rt, "device.rerouting.probability", {"value": "1.0"})
    ET.SubElement(rt, "device.rerouting.period", {"value": "30"})
    ET.SubElement(rt, "device.rerouting.adaptation-steps", {"value": "18"})
    rep = ET.SubElement(cfg, "report")
    ET.SubElement(rep, "no-step-log", {"value": "true"})
    ET.indent(cfg, space="  ")
    cfg_path = os.path.join(out_dir, "scenario.sumocfg")
    ET.ElementTree(cfg).write(cfg_path, encoding="utf-8", xml_declaration=True)

    print(f"\nDONE. Scenario in {out_dir}")
    print(f"  net={net_path}")
    print(f"  vehicles total={vid}  (through={made['through']} "
          f"park_stay={made['park_stay']} depart_parked={made['depart_parked']})")
    return out_dir


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--out", default=HERE)
    p.add_argument("--grid-number", type=int, default=8)
    p.add_argument("--grid-length", type=float, default=120.0)
    p.add_argument("--attach-length", type=float, default=100.0)
    p.add_argument("--lanes", type=int, default=2)
    p.add_argument("--junction-type", default="priority")
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--end", type=int, default=1000)  # informational; used by run cmds
    p.add_argument("--through", type=int, default=1200)
    p.add_argument("--park-stay", type=int, default=120)
    p.add_argument("--depart-parked", type=int, default=40)
    p.add_argument("--sink-capacity", type=int, default=3)
    p.add_argument("--sink-bay", type=float, default=16.0)  # bay on LANE 0
    p.add_argument("--through-period", type=float, default=0.3)
    build(p.parse_args())


if __name__ == "__main__":
    main()
