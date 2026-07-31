#!/usr/bin/env python3
"""Detect a stopped vehicle lane-changing INTO an already-occupied adjacent lane.

Background: docs/SUMOSHARP-ISSUE-stopped-lane-change-overlap.md. Symptom: at a red light with
two lanes queued side by side, a stopped car changes lane sideways into the neighbouring lane
where another car is already stopped, ending up physically overlapping it (impossible given both
vehicles' length). The prior investigation confirmed this in the authoritative engine state (not a
viewer/DR artifact) and left open whether honest SUMO does the same thing -- that is the question
this script exists to answer, on both engines' FCD output.

Consumes the SUMO FCD schema, so it runs unchanged on BOTH engines' output -- ours via
`Sim.Run --fcd-out` and SUMO's via `--fcd-output` -- exactly like scripts/analyze-junction-realism-fcd.py,
whose structure and argparse shape this script matches on purpose (a second engine consuming this
output should not have to learn a new convention).

THE OVERLAP TEST IS NOT THE OBVIOUS ONE. `pos` in FCD is the vehicle's FRONT-BUMPER arc position
along the lane, not its centre (docs/SUMOSHARP-ISSUE-stopped-lane-change-overlap.md SS2, confirmed
against Engine.cs: `leaderBackPos = leaderPos - leaderLength`, matching SUMO's own convention). So
for two vehicles on the SAME lane, with `ahead` = whichever has the larger `pos`:

    overlapBy = pos[behind] - (pos[ahead] - length[ahead])     # > ~0.10 m => real physical overlap

A centre-symmetric test `|pos_i - pos_j| < 0.5*(len_i+len_j)` is WRONG here: it fires constantly
whenever a long vehicle (e.g. a truck, length 12+) legally trails a short one at the SUMO-default
minGap, because their front-bumper positions are already less than half the summed length apart
while their bodies do not touch at all. Do not use it -- see SS2 of the doc above.

Usage:
    scripts/detect-stopped-lane-change.py <fcd.xml> [--vtypes-from SCENARIO_DIR] [--limit 5]
    scripts/detect-stopped-lane-change.py <ours.fcd.xml> --compare <sumo.fcd.xml> --vtypes-from scenarios/_diag/junction-realism-L2
"""

from __future__ import annotations

import argparse
import collections
import xml.etree.ElementTree as ET
from pathlib import Path

STOPPED = 0.1   # m/s at or below which a vehicle counts as "stopped" for this detector
OVERLAP = 0.10  # m of front-bumper-vs-back-bumper intrusion below which it's numeric noise, not a clash

DEFAULT_LENGTH = 5.0  # SUMO's own vType default, used only for types never declared in the scenario


def vtype_dims(scenario: Path | None) -> dict[str, float]:
    """type id -> length, from the scenario's own route files.

    Copied approach from scripts/run-net-regression.py's `vtype_dims()`: necessary, not cosmetic.
    These nets carry trucks and rail; assuming 5.0 m for everything would silently under-count
    overlap on a long vehicle and could also fabricate one where none exists. SUMO's own default
    (5.0 m) is used only for types that omit a `length` attribute or that never appear in a
    committed vType (e.g. an FCD taken in isolation with no --vtypes-from given).
    """
    dims: dict[str, float] = {}
    if scenario is None:
        return dims
    for rou in sorted(scenario.glob("*.rou.xml")):
        try:
            root = ET.parse(rou).getroot()
        except ET.ParseError:
            continue
        for vt in root.iter("vType"):
            tid = vt.get("id")
            if tid is None:
                continue
            dims[tid] = float(vt.get("length", DEFAULT_LENGTH))
    return dims


def lane_edge_and_index(lane: str) -> tuple[str, str] | None:
    """'EC_0' -> ('EC', '0'); ':J01_5_0' (internal junction lane) -> None.

    Only ordinary edge lanes have the "trailing index selects a parallel lane of the same edge"
    convention the detector needs; internal junction lanes (leading ':') are excluded because their
    id encodes a connection index, not a parallel-lane index, and their "adjacency" has no meaning
    for a sideways lane change.
    """
    if lane.startswith(":"):
        return None
    if "_" not in lane:
        return None
    edge, _, idx = lane.rpartition("_")
    if not idx.isdigit():
        return None
    return edge, idx


def detect(path: str, lengths: dict[str, float]) -> dict:
    """Scan one FCD file for stopped-vehicle sideways lane changes and score landing overlaps.

    Two passes are folded into one: per-timestep vehicle state is buffered so that, once a whole
    timestep is read, we know (a) which vehicles changed lane since last step and (b) who else is on
    the landing lane THIS step, so the overlap test uses the post-change snapshot, not a stale one.
    """
    prev_lane: dict[str, str] = {}   # vid -> lane at the previous timestep
    prev_speed: dict[str, float] = {}
    events: list[dict] = []
    total_changes = 0
    worst_overlap = 0.0

    t = None
    step_vehicles: list[tuple[str, str, float, float]] = []  # (vid, lane, pos, speed) this step

    def flush_step():
        nonlocal total_changes, worst_overlap
        by_lane: dict[str, list[tuple[str, float, float]]] = collections.defaultdict(list)
        for vid, lane, pos, speed in step_vehicles:
            by_lane[lane].append((vid, pos, speed))

        for vid, lane, pos, speed in step_vehicles:
            plane = prev_lane.get(vid)
            pspeed = prev_speed.get(vid, speed)
            if plane is not None and plane != lane and speed <= STOPPED and pspeed <= STOPPED:
                pe = lane_edge_and_index(plane)
                ce = lane_edge_and_index(lane)
                if pe is not None and ce is not None and pe[0] == ce[0]:
                    # same edge, different lane index => a genuine sideways lane change, not a
                    # succession onto the next edge (which also changes `lane` every step).
                    total_changes += 1
                    my_len = lengths.get(_veh_type.get(vid), DEFAULT_LENGTH)
                    overlap_with = None
                    overlap_by = 0.0
                    for ovid, opos, ospeed in by_lane[lane]:
                        if ovid == vid:
                            continue
                        o_len = lengths.get(_veh_type.get(ovid), DEFAULT_LENGTH)
                        # `ahead` = whichever of the pair has the larger front-bumper pos (SS2 formula).
                        if opos >= pos:
                            ob = pos - (opos - o_len)
                        else:
                            ob = opos - (pos - my_len)
                        if ob > OVERLAP and ob > overlap_by:
                            overlap_by = ob
                            overlap_with = ovid
                    events.append(dict(t=t, veh=vid, from_lane=plane, to_lane=lane, pos=pos,
                                        overlap_with=overlap_with, overlap_by=overlap_by))
                    if overlap_with is not None:
                        worst_overlap = max(worst_overlap, overlap_by)

        prev_lane.clear()
        prev_speed.clear()
        for vid, lane, pos, speed in step_vehicles:
            prev_lane[vid] = lane
            prev_speed[vid] = speed

    _veh_type: dict[str, str] = {}

    for ev, el in ET.iterparse(path, events=("start", "end")):
        if ev == "start" and el.tag == "timestep":
            if t is not None:
                flush_step()
            t = float(el.get("time"))
            step_vehicles = []
        elif ev == "end" and el.tag == "vehicle":
            vid = el.get("id")
            _veh_type[vid] = el.get("type", "")
            step_vehicles.append((vid, el.get("lane"), float(el.get("pos")), float(el.get("speed"))))
            el.clear()
        elif ev == "end" and el.tag == "timestep":
            el.clear()
    if t is not None:
        flush_step()

    landed_overlapping = [e for e in events if e["overlap_with"] is not None]
    return dict(path=path, events=events, total_changes=total_changes,
                landed_overlapping=landed_overlapping, worst_overlap=worst_overlap)


def report(r: dict, limit: int) -> None:
    print(f"\n#### {r['path'].split('/')[-1]}")
    print(f"  stopped sideways lane changes: {r['total_changes']}")
    print(f"  landed overlapping another vehicle: {len(r['landed_overlapping'])}")
    print(f"  worst overlap: {r['worst_overlap']:.3f} m")
    if not r["events"]:
        print("  (no stopped sideways lane changes found on this net -- nothing to report)")
        return
    shown = r["landed_overlapping"] or r["events"]
    label = "overlapping instances" if r["landed_overlapping"] else "instances (none overlapped)"
    print(f"  -- first {min(limit, len(shown))} {label} --")
    for e in shown[:limit]:
        if e["overlap_with"] is not None:
            print(f"     t={e['t']:>6.1f}  {e['veh']:<14} {e['from_lane']:<10} -> {e['to_lane']:<10}"
                  f"  overlapped {e['overlap_with']:<14} by {e['overlap_by']:.3f} m")
        else:
            print(f"     t={e['t']:>6.1f}  {e['veh']:<14} {e['from_lane']:<10} -> {e['to_lane']:<10}"
                  f"  (no overlap)")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("fcd")
    ap.add_argument("--compare", help="a second FCD (e.g. the SUMO oracle) to report alongside")
    ap.add_argument("--vtypes-from", type=Path, default=None,
                     help="scenario directory to resolve vType lengths from (its *.rou.xml files)")
    ap.add_argument("--limit", type=int, default=5, help="max instances to print per file")
    args = ap.parse_args()

    lengths = vtype_dims(args.vtypes_from)
    report(detect(args.fcd, lengths), args.limit)
    if args.compare:
        report(detect(args.compare, lengths), args.limit)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
