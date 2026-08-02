#!/usr/bin/env python3
"""Render a SUMO person-FCD golden into a self-contained Sim.Viz HTML replay.

WHY THIS EXISTS
---------------
The SUMOPED port (docs/SUMOPED-*.md) is validated against committed SUMO goldens. Before any C#
exists, and afterwards as the ground-truth layer of the parity overlay (task SP-7.3), we need to
*look at* the oracle: what SUMO itself does with pedestrians at a crossing.

This script does not invent a renderer. It emits the EXACT payload schema of `src/Sim.Viz/Payload.cs`
and splices it into the real `src/Sim.Viz/template.{html,js}` via the same two markers
`VizHtml.Write` uses (`/*REPLAY_DATA*/`, `/*TEMPLATE_JS*/`). What you see is the repo's own player.

It is a *golden* renderer: input is `net.net.xml` + a SUMO `--fcd-output` file. It never runs the
engine and never runs SUMO. When the C# person model exists, `Sim.Viz` renders our side and this
stays as the oracle side.

USAGE
-----
  scripts/render-ped-fcd.py --net NET.xml --fcd FCD.xml --out OUT.html \
      [--name NAME] [--desc DESC] [--begin T] [--end T] [--stride N] \
      [--dir-split EDGE_SUBSTR] [--title TITLE]

  --dir-split  colour pedestrians by walking direction (cyan vs pink) instead of one colour, so a
               bidirectional counterflow reads at a glance. Direction is taken from the sign of the
               ped's own y- or x-displacement over its lifetime, whichever axis it moves more along.
"""

import argparse
import json
import math
import os
import sys
import xml.etree.ElementTree as ET


def r2(v):
    """Payload compaction rule from PayloadBuilder.R: round to 2 dp, away from zero."""
    return math.floor(v * 100 + 0.5) / 100 if v >= 0 else -(math.floor(-v * 100 + 0.5) / 100)


def flat(shape):
    out = []
    for x, y in shape:
        out.append(r2(x))
        out.append(r2(y))
    return out


def parse_shape(s):
    pts = []
    for tok in s.split():
        parts = tok.split(",")
        pts.append((float(parts[0]), float(parts[1])))
    return pts


def allows_road_vehicle(allow, disallow):
    """Mirror of Sim.Ingest/NetworkParser.cs:955 LaneAllowsRoadVehicle -- a lane is a sidewalk only
    when `allow` lists pedestrian (and nothing that can drive)."""
    if not allow:
        return True
    toks = [t for t in allow.split() if t]
    return any(t != "pedestrian" for t in toks)


def build_network(net_root):
    lanes, junctions, crossings = [], [], []
    lane_geom = {}   # laneId -> (shape, width, length)
    edge_of_lane = {}
    for edge in net_root.findall("edge"):
        fn = edge.get("function") or "normal"
        eid = edge.get("id")
        for i, lane in enumerate(edge.findall("lane")):
            lid = lane.get("id")
            shp = parse_shape(lane.get("shape", ""))
            if len(shp) < 2:
                continue
            w = float(lane.get("width", 3.2))
            lane_geom[lid] = (shp, w, float(lane.get("length", 0)))
            edge_of_lane[lid] = eid
            if fn == "crossing":
                # zebra footprint: the centreline offset by +-w/2
                crossings.append({
                    "id": eid,
                    "outline": flat(offset_polygon(shp, w / 2.0)),
                    "center": flat(shp),
                    "width": w,
                })
                continue
            if fn == "internal":
                continue  # internal car lanes: not drawn as bands (junction shape covers them)
            ped = not allows_road_vehicle(lane.get("allow"), lane.get("disallow"))
            if fn == "walkingarea":
                ped = True
            lanes.append({
                "id": lid, "edgeId": eid, "index": int(lane.get("index", i)),
                "width": w, "shape": flat(shp), "ped": ped,
            })
    for j in net_root.findall("junction"):
        if j.get("type") == "internal":
            continue
        s = j.get("shape")
        if not s:
            continue
        pts = parse_shape(s)
        if len(pts) >= 3:
            junctions.append({"id": j.get("id"), "shape": flat(pts)})
    return ({
        "lanes": lanes, "junctions": junctions, "tls": [], "signals": [],
        "crossings": crossings, "pedSignals": [],
    }, lane_geom)


def offset_polygon(center, half):
    """Closed polygon around a centreline at +-half width (good enough for a zebra footprint)."""
    left, right = [], []
    n = len(center)
    for i, (x, y) in enumerate(center):
        if i == 0:
            dx, dy = center[1][0] - x, center[1][1] - y
        elif i == n - 1:
            dx, dy = x - center[-2][0], y - center[-2][1]
        else:
            dx, dy = center[i + 1][0] - center[i - 1][0], center[i + 1][1] - center[i - 1][1]
        ln = math.hypot(dx, dy) or 1.0
        nx, ny = -dy / ln, dx / ln
        left.append((x + nx * half, y + ny * half))
        right.append((x - nx * half, y - ny * half))
    return left + list(reversed(right))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--net", required=True)
    ap.add_argument("--fcd", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--name", default=None)
    ap.add_argument("--desc", default="")
    ap.add_argument("--title", default=None)
    ap.add_argument("--begin", type=float, default=None)
    ap.add_argument("--end", type=float, default=None)
    ap.add_argument("--stride", type=int, default=1)
    ap.add_argument("--dir-split", action="store_true")
    ap.add_argument("--crop", default=None,
                    help="CX,CY,HALF -- camera box centred on (CX,CY) with the given half-extent, "
                         "instead of the whole network bbox. Use to zoom onto a junction so "
                         "pedestrian-scale avoidance is actually visible.")
    ap.add_argument("--crop-junction", default=None,
                    help="JUNCTION_ID,HALF -- same, but centred on a named junction's position.")
    ap.add_argument("--radius", type=float, default=0.25,
                    help="pedestrian disc radius in metres (default 0.25 ~ SUMO's 0.48 m ped width)")
    ap.add_argument("--template-dir", default=None)
    a = ap.parse_args()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    tdir = a.template_dir or os.path.join(repo, "src", "Sim.Viz")
    th, tj = os.path.join(tdir, "template.html"), os.path.join(tdir, "template.js")
    for p in (th, tj):
        if not os.path.exists(p):
            sys.exit(f"error: template not found: {p}")

    net_root = ET.parse(a.net).getroot()
    network, lane_geom = build_network(net_root)

    # ---- pass 1: fixed slots + per-ped dominant travel axis (for --dir-split) ----
    veh_slot, ped_slot = {}, {}
    ped_first, ped_last = {}, {}
    times = []
    for _, ts in ET.iterparse(a.fcd, events=("end",)):
        if ts.tag != "timestep":
            continue
        t = float(ts.get("time"))
        if (a.begin is not None and t < a.begin) or (a.end is not None and t > a.end):
            ts.clear()
            continue
        times.append(t)
        for v in ts.findall("vehicle"):
            veh_slot.setdefault(v.get("id"), len(veh_slot))
        for p in ts.findall("person"):
            pid = p.get("id")
            ped_slot.setdefault(pid, len(ped_slot))
            xy = (float(p.get("x")), float(p.get("y")))
            ped_first.setdefault(pid, xy)
            ped_last[pid] = xy
        ts.clear()
    if not times:
        sys.exit("error: no timesteps in range")
    times.sort()
    dt = (times[1] - times[0]) if len(times) > 1 else 1.0

    ped_kind = {}
    for pid, (x0, y0) in ped_first.items():
        x1, y1 = ped_last[pid]
        dx, dy = x1 - x0, y1 - y0
        if not a.dir_split:
            ped_kind[pid] = 2
        elif abs(dy) >= abs(dx):
            ped_kind[pid] = 0 if dy >= 0 else 1
        else:
            ped_kind[pid] = 0 if dx >= 0 else 1

    # ---- pass 2: frames ----
    keep = set(times[:: max(1, a.stride)])
    frames = []
    minx = miny = float("inf")
    maxx = maxy = float("-inf")
    for lane in network["lanes"]:
        s = lane["shape"]
        for i in range(0, len(s), 2):
            minx, maxx = min(minx, s[i]), max(maxx, s[i])
            miny, maxy = min(miny, s[i + 1]), max(maxy, s[i + 1])

    veh_len = veh_wid = 0.0
    for _, ts in ET.iterparse(a.fcd, events=("end",)):
        if ts.tag != "timestep":
            continue
        t = float(ts.get("time"))
        if t not in keep:
            ts.clear()
            continue
        V = [None] * len(veh_slot)
        D = [None] * len(ped_slot)
        for v in ts.findall("vehicle"):
            V[veh_slot[v.get("id")]] = [r2(float(v.get("x"))), r2(float(v.get("y"))),
                                        r2(float(v.get("angle")))]
            veh_len = veh_len or 5.0
            veh_wid = veh_wid or 1.8
        for p in ts.findall("person"):
            D[ped_slot[p.get("id")]] = [r2(float(p.get("x"))), r2(float(p.get("y"))),
                                        a.radius, ped_kind[p.get("id")]]
        frames.append({"v": V, "d": D})
        ts.clear()

    pad = 6.0
    view = [r2(minx - pad), r2(miny - pad), r2(maxx + pad), r2(maxy + pad)]
    if a.crop_junction:
        jid, half = a.crop_junction.rsplit(",", 1)
        half = float(half)
        found = None
        for j in net_root.findall("junction"):
            if j.get("id") == jid:
                found = (float(j.get("x")), float(j.get("y")))
        if found is None:
            sys.exit(f"error: junction '{jid}' not found in the net")
        cx, cy = found
        view = [r2(cx - half), r2(cy - half), r2(cx + half), r2(cy + half)]
    elif a.crop:
        cx, cy, half = (float(v) for v in a.crop.split(","))
        view = [r2(cx - half), r2(cy - half), r2(cx + half), r2(cy + half)]
    labels = None
    if a.dir_split:
        labels = ["pedestrian (stream A)", "pedestrian (stream B)", "pedestrian"]

    scene = {
        "name": a.name or os.path.basename(a.fcd),
        "desc": a.desc,
        "view": view,
        "network": network,
        "vdim": [veh_len, veh_wid],
        "dt": dt * max(1, a.stride),
        "frames": frames,
        "labels": labels,
        "incident": None,
        "boundary": None,
        "useDataHeading": False,
        "vehIds": [k for k, _ in sorted(veh_slot.items(), key=lambda kv: kv[1])] or None,
    }
    payload = {"scenes": [scene]}

    html = open(th).read() \
        .replace("__SCENARIO_NAME__", a.title or scene["name"]) \
        .replace("/*REPLAY_DATA*/", json.dumps(payload, separators=(",", ":"))) \
        .replace("/*TEMPLATE_JS*/", open(tj).read())
    with open(a.out, "w") as f:
        f.write(html)
    print(f"{a.out}: {len(frames)} frames, {len(ped_slot)} persons, {len(veh_slot)} vehicles, "
          f"{os.path.getsize(a.out)} bytes")


if __name__ == "__main__":
    main()
