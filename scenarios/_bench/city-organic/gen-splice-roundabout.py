#!/usr/bin/env python3
"""Splice a scenario-32-style single-lane priority roundabout into an organic net's plain XML.

Finds a bidirectional edge pair (X->Y and Y->X) in the interior of the network with a
reasonable length, removes those two edges, and replaces them with a small 4-node ring
(radius 20m, same priority scheme as scenarios/32-roundabout and 33-roundabout-solo:
ring edges priority=10, spoke edges priority=1) plus explicit <connection> entries cloning
that proven topology (2 entries + 2 exits on separate ring nodes, ring continuity all the
way around). This exactly mirrors the already-parity-anchored roundabout construction so the
engine's junction arrival-time right-of-way logic (C4-iii) sees a topology it has already been
exercised against.
"""
import math
import re
import sys
import xml.etree.ElementTree as ET

WORK = sys.argv[1] if len(sys.argv) > 1 else "."

nod_path = f"{WORK}/organic.nod.xml"
edg_path = f"{WORK}/organic.edg.xml"

nod_tree = ET.parse(nod_path)
nod_root = nod_tree.getroot()
edg_tree = ET.parse(edg_path)
edg_root = edg_tree.getroot()

ns = ""  # no namespace prefix needed for tag lookups (xsi is only an attribute ns)

nodes = {}
for n in nod_root.findall("node"):
    nodes[n.get("id")] = (float(n.get("x")), float(n.get("y")), n.get("type"))

edges = {}
edge_elems = {}
for e in edg_root.findall("edge"):
    edges[e.get("id")] = (e.get("from"), e.get("to"))
    edge_elems[e.get("id")] = e

# index edges by (from,to)
by_pair = {}
for eid, (f, t) in edges.items():
    by_pair[(f, t)] = eid

candidates = []
for eid, (f, t) in edges.items():
    rev = by_pair.get((t, f))
    if rev is None:
        continue
    if f not in nodes or t not in nodes:
        continue
    xf, yf, tf = nodes[f]
    xt, yt, tt = nodes[t]
    if tf == "dead_end" or tt == "dead_end":
        continue
    length = math.hypot(xt - xf, yt - yf)
    if 90.0 <= length <= 220.0:
        candidates.append((length, f, t, eid, rev))

candidates.sort(key=lambda c: -c[0])  # prefer longer segments (more room for the ring)
if not candidates:
    print("NO CANDIDATE FOUND", file=sys.stderr)
    sys.exit(1)

length, X, Y, eXY, eYX = candidates[0]
print(f"chosen segment: {X} -> {Y} (edges {eXY}/{eYX}), length={length:.1f}m")

xf, yf, _ = nodes[X]
xt, yt, _ = nodes[Y]
mx, my = (xf + xt) / 2.0, (yf + yt) / 2.0
# unit vector from X to Y
dx, dy = (xt - xf), (yt - yf)
dl = math.hypot(dx, dy)
ux, uy = dx / dl, dy / dl
# perpendicular
px, py = -uy, ux

R = 20.0
# Ring nodes at cardinal-like positions relative to the X-Y axis:
#  RS ("south", faces X)  -- BOTH entry from X and exit to X (X-side of the ring)
#  RN ("north", faces Y)  -- BOTH entry from Y and exit to Y (Y-side of the ring)
#  RE, RW                 -- pure ring pass-through vertices (no spokes; just give the
#                            ring its round shape), matching scenarios/32-roundabout's
#                            radius-20 single-lane priority-ring pattern.
rs = (mx - ux * R, my - uy * R)
rn = (mx + ux * R, my + uy * R)
re_ = (mx + px * R, my + py * R)
rw = (mx - px * R, my - py * R)

prefix = "rbX"
RS, RE, RN, RW = f"{prefix}_S", f"{prefix}_E", f"{prefix}_N", f"{prefix}_W"

# Remove the original edge pair
del edge_elems[eXY]
del edge_elems[eYX]
for e in list(edg_root.findall("edge")):
    if e.get("id") in (eXY, eYX):
        edg_root.remove(e)

# Add ring nodes to nod.xml
for nid, (x, y) in [(RS, rs), (RE, re_), (RN, rn), (RW, rw)]:
    el = ET.SubElement(nod_root, "node")
    el.set("id", nid)
    el.set("x", f"{x:.2f}")
    el.set("y", f"{y:.2f}")
    el.set("type", "priority")

# Ring edges (priority=10, one-way loop S->E->N->W->S), single lane, 8.33 m/s (30 km/h)
ring_edges = [
    (f"{prefix}_ring_SE", RS, RE),
    (f"{prefix}_ring_EN", RE, RN),
    (f"{prefix}_ring_NW", RN, RW),
    (f"{prefix}_ring_WS", RW, RS),
]
for eid, f, t in ring_edges:
    el = ET.SubElement(edg_root, "edge")
    el.set("id", eid)
    el.set("from", f)
    el.set("to", t)
    el.set("priority", "10")
    el.set("numLanes", "1")
    el.set("speed", "8.33")

# Spoke edges (priority=1): X's entry+exit both at RS (X-side node), Y's entry+exit
# both at RN (Y-side node) -- geometrically correct (spokes stay on their own side of the
# ring instead of crossing through it).
spoke_x_in = f"{prefix}_{X}_in"
spoke_x_out = f"{prefix}_{X}_out"
spoke_y_in = f"{prefix}_{Y}_in"
spoke_y_out = f"{prefix}_{Y}_out"
spokes = [
    (spoke_x_in, X, RS),
    (spoke_x_out, RS, X),
    (spoke_y_in, Y, RN),
    (spoke_y_out, RN, Y),
]
for eid, f, t in spokes:
    el = ET.SubElement(edg_root, "edge")
    el.set("id", eid)
    el.set("from", f)
    el.set("to", t)
    el.set("priority", "1")
    el.set("numLanes", "1")
    el.set("speed", "13.89")

nod_tree.write(f"{WORK}/nodes_final.nod.xml", encoding="UTF-8", xml_declaration=True)
edg_tree.write(f"{WORK}/edges_final.edg.xml", encoding="UTF-8", xml_declaration=True)

ring_SE, ring_EN, ring_NW, ring_WS = (e[0] for e in ring_edges)

# explicit connections, same priority-ring idea as scenarios/32-roundabout but generalized to
# a real bidirectional through-pair: RS/RN each carry BOTH an entry (merge onto the ring --
# yields to circulating ring traffic per the priority=10 vs priority=1 edge weighting, same as
# the reference) and an exit (diverge off the ring, no yield needed). RE/RW are pure ring
# vertices with only the single "continue circulating" connection.
con_xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<connections xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/connections_file.xsd">
    <!-- Ring continuity (all the way around) -->
    <connection from="{ring_SE}" to="{ring_EN}" fromLane="0" toLane="0"/>
    <connection from="{ring_EN}" to="{ring_NW}" fromLane="0" toLane="0"/>
    <connection from="{ring_NW}" to="{ring_WS}" fromLane="0" toLane="0"/>
    <connection from="{ring_WS}" to="{ring_SE}" fromLane="0" toLane="0"/>
    <!-- {X}-side node RS: entry merges onto the ring (yields to ring_WS, priority=10 > 1);
         circulating ring_WS traffic can also exit back to {X} -->
    <connection from="{spoke_x_in}" to="{ring_SE}" fromLane="0" toLane="0"/>
    <connection from="{ring_WS}" to="{spoke_x_out}" fromLane="0" toLane="0"/>
    <!-- {Y}-side node RN: entry merges onto the ring (yields to ring_EN); circulating
         ring_EN traffic can also exit to {Y} -->
    <connection from="{spoke_y_in}" to="{ring_NW}" fromLane="0" toLane="0"/>
    <connection from="{ring_EN}" to="{spoke_y_out}" fromLane="0" toLane="0"/>
</connections>
"""
with open(f"{WORK}/connections_final.con.xml", "w") as f:
    f.write(con_xml)

print(f"X={X} Y={Y}")
print(f"spliced: {spoke_x_in}, {spoke_y_in}, {spoke_y_out}, {spoke_x_out}")
print("wrote nodes_final.nod.xml, edges_final.edg.xml, connections_final.con.xml")
