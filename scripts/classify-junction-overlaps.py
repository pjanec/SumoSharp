#!/usr/bin/env python3
"""Classify vehicle-overlap defects from an FCD trace into the two Geneva-report classes.

Background: docs/JUNCTION-FOE-LANE-DESIGN.md (owner report, July 31: (a) cars arriving at a jam
overlap the queue tail, stacking; (b) straight traffic passes through a car blocked mid-junction).
Journal Entry 35 measured both offline and this script is the committed instrument for that
measurement (CLAUDE.md measurement discipline #8: a probe run once and deleted makes its own
numbers unfalsifiable; any A/B on a junction-foe fix must be measured with THIS script, both arms).

Two independent classifiers, one FCD pass each:

1. JUNCTION-INTERIOR OVERLAP PAIR-STEPS -- every step where two vehicles inside the same junction
   OBB-overlap, classed by lane relation (sameLane / crossLane) x kinematics:
     stopXmove  -- one member <= 0.5 m/s, the other >= 3 m/s: the "pass-through a blocked car"
                   witness (owner report (b));
     bothMove   -- both > 0.5 m/s: converging movements overlapping mid-junction;
     bothSlow / stopXslow -- queue-creep contact inside the junction.
   OBB conventions are imported from analyze-junction-realism-fcd.py, which owns them (front-bumper
   anchor + naviDEGREE; two independent bugs have come from re-deriving these by hand).

2. NORMAL-LANE DEEP REAR-END OVERLAP ONSETS -- the first step of every episode where two vehicles
   on the same NORMAL lane overlap along-lane by > 1 m, with the PRIOR-step lane of both members,
   which classifies the onset cause:
     follower-exits-junction -- both members left the SAME junction in the SAME step from DIFFERENT
                                internal lanes and landed overlapped on the shared arrival lane:
                                the same-target merge race (owner report (a) -- in a jam, every such
                                double-landing stacks on the queue tail);
     follower/leader-lanechange, -inserted, same-lane-approach -- the other possible causes.

Entry 35 baselines (city-organic-L2, 1000 steps, this script): ours 145 crossLane|bothMove +
23 bothSlow + 17 stopXmove + 12 landing onsets (12/12 follower-exits-junction); honest SUMO
(--time-to-teleport -1 --collision.action warn --collision.check-junctions true): 4 bothMove,
0 everything else. city-mixed-1k: ours 10 deep landing onsets, 10/10 follower-exits-junction.

Consumes the SUMO FCD schema -- runs unchanged on both engines' output.
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def _load_obb():
    here = os.path.dirname(os.path.abspath(__file__))
    spec = importlib.util.spec_from_file_location(
        "ajr", os.path.join(here, "analyze-junction-realism-fcd.py"))
    ajr = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(ajr)
    return ajr


def classify(path: str, length: float, width: float, deep: float, examples: int = 3) -> None:
    ajr = _load_obb()
    obb = ajr.Obb(length, width)

    pair_classes: dict[str, int] = defaultdict(int)
    pair_examples: dict[str, list] = defaultdict(list)

    steps: dict[float, dict[str, tuple]] = {}

    for _, ts in ET.iterparse(path):
        if ts.tag != "timestep":
            continue
        t = float(ts.get("time"))
        snap = {}
        byj = defaultdict(list)
        for v in ts.findall("vehicle"):
            vid, lane = v.get("id"), v.get("lane")
            pos, spd = float(v.get("pos")), float(v.get("speed"))
            snap[vid] = (lane, pos)
            j = ajr.junction_of(lane)
            if j is not None:
                byj[j].append((vid, lane, float(v.get("x")), float(v.get("y")),
                               float(v.get("angle")), spd))
        steps[t] = snap
        for j, vs in byj.items():
            for i in range(len(vs)):
                for k in range(i + 1, len(vs)):
                    a, b = vs[i], vs[k]
                    if obb.penetration((a[2], a[3], a[4]), (b[2], b[3], b[4])) <= 0.0:
                        continue
                    slow, fast = min(a[5], b[5]), max(a[5], b[5])
                    kin = ("stopXmove" if (slow <= 0.5 and fast >= 3.0)
                           else "bothSlow" if fast < 3.0
                           else "bothMove" if slow > 0.5 else "stopXslow")
                    cls = ("sameLane" if a[1] == b[1] else "crossLane") + "|" + kin
                    pair_classes[cls] += 1
                    if len(pair_examples[cls]) < examples:
                        pair_examples[cls].append(
                            (t, j, a[0], a[1], round(a[5], 1), b[0], b[1], round(b[5], 1)))
        ts.clear()

    # Pass 2 (in-memory): normal-lane deep rear-end overlap onsets with prior-step cause.
    times = sorted(steps)
    active: set = set()
    onsets = []
    for ti in range(1, len(times)):
        t = times[ti]
        bylane = defaultdict(list)
        for vid, (lane, pos) in steps[t].items():
            if not lane.startswith(":"):
                bylane[lane].append((pos, vid))
        cur = set()
        for lane, vs in bylane.items():
            vs.sort()
            for i in range(1, len(vs)):
                back, front = vs[i - 1], vs[i]
                depth = back[0] - (front[0] - length)
                if depth > deep:
                    key = (back[1], front[1])
                    cur.add(key)
                    if key not in active:
                        prev = steps[times[ti - 1]]
                        fl = prev.get(back[1], ("ABSENT",))[0]
                        ll = prev.get(front[1], ("ABSENT",))[0]
                        if fl.startswith(":") and ll.startswith(":") \
                                and ajr.junction_of(fl) == ajr.junction_of(ll) and fl != ll:
                            cause = "follower-exits-junction(SAME-junction double-landing)"
                        elif fl.startswith(":"):
                            cause = "follower-exits-junction"
                        elif fl == "ABSENT":
                            cause = "follower-inserted"
                        elif fl != lane:
                            cause = "follower-lanechange"
                        elif ll == "ABSENT":
                            cause = "leader-inserted"
                        elif ll != lane and ll.startswith(":"):
                            cause = "leader-exits-junction"
                        elif ll != lane:
                            cause = "leader-lanechange"
                        else:
                            cause = "same-lane-approach"
                        onsets.append((t, lane, back[1], fl, front[1], ll, round(depth, 2), cause))
        active = cur

    print(f"#### {os.path.basename(path)}")
    print("  -- junction-interior overlapping pair-steps by class --")
    if not pair_classes:
        print("     none")
    for c, n in sorted(pair_classes.items(), key=lambda x: -x[1]):
        print(f"     {n:5d}  {c}")
        for e in pair_examples[c]:
            print(f"            t={e[0]:.1f} j={e[1]} {e[2]}@{e[3]}({e[4]} m/s) x {e[5]}@{e[6]}({e[7]} m/s)")
    print(f"  -- normal-lane deep (> {deep} m) rear-end overlap ONSETS: {len(onsets)} --")
    causes = defaultdict(int)
    for o in onsets:
        causes[o[7]] += 1
    for c, n in sorted(causes.items(), key=lambda x: -x[1]):
        print(f"     {n:5d}  {c}")
    for o in onsets[:10]:
        print(f"     t={o[0]:.1f} {o[1]} fol={o[2]}(prev {o[3]}) lead={o[4]}(prev {o[5]}) depth={o[6]} {o[7]}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("fcd", nargs="+")
    ap.add_argument("--vtype-length", type=float, default=5.0)
    ap.add_argument("--vtype-width", type=float, default=1.8)
    ap.add_argument("--deep", type=float, default=1.0,
                    help="along-lane overlap depth (m) that counts as a deep rear-end onset")
    ap.add_argument("--examples", type=int, default=3,
                    help="max example pair-steps printed per class")
    args = ap.parse_args()
    for f in args.fcd:
        classify(f, args.vtype_length, args.vtype_width, args.deep, args.examples)
    return 0


if __name__ == "__main__":
    sys.exit(main())
