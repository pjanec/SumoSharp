#!/usr/bin/env python3
"""Analyze an FCD trace from the junction-realism repro: gridlock onset, wedges, overlaps, causal order.

Consumes the SUMO FCD schema, so it runs unchanged on BOTH engines' output -- ours via
`Sim.Run --fcd-out` and SUMO's via `--fcd-output` -- which is the whole point: every number it
prints is directly comparable across the two.

COMMITTED ON PURPOSE (CLAUDE.md measurement discipline #8/#13): a probe that is run once and
deleted makes its own numbers unfalsifiable and silently poisons every later comparison, because
cross-instrument numbers are never comparable. Any A/B on a junction fix must be measured with THIS
script, on both arms.

THE OBB CONVENTIONS ARE NOT RE-DERIVED HERE, THEY ARE COPIED from src/Sim.Ingest/VehicleObb.cs, which
owns them and is guarded by VehicleObbConventionTests. Two independent bugs have come from
re-deriving them by hand (docs/NEED-obb-anchor-halflength.md):
  * the FCD pose is the FRONT BUMPER, not the box centre -- a box built as pose +/- L/2 sits a half
    length too far forward;
  * `angle` is naviDEGREE (0 = north, clockwise), so the unit tangent is (+sin, cos). The reflected
    (-sin, cos) agrees only at due north/south and is PERPENDICULAR to the truth at 45 deg -- i.e.
    wrong exactly on the curved internal junction lanes this script exists to measure.

Usage:
    scripts/analyze-junction-realism-fcd.py <fcd.xml> [--vtype-length 5.0] [--vtype-width 1.8]
    scripts/analyze-junction-realism-fcd.py <ours.xml> --compare <sumo.xml>
"""

from __future__ import annotations

import argparse
import collections
import math
import xml.etree.ElementTree as ET

MOVING = 0.1        # m/s at or below which a vehicle counts as stopped
GRAZE = 0.05        # m of penetration below which an overlap is numeric noise, not a body clash
WEDGE_STEPS = 20    # consecutive stopped-inside-junction steps that promote a stall to a "wedge"


class Obb:
    """src/Sim.Ingest/VehicleObb.cs, transcribed. See this module's docstring before editing."""

    def __init__(self, length: float, width: float):
        self.length, self.width = length, width

    @staticmethod
    def basis(angle_deg: float):
        th = math.radians(angle_deg)
        return (math.sin(th), math.cos(th)), (math.cos(th), -math.sin(th))

    def centre(self, x: float, y: float, angle_deg: float):
        f, _ = self.basis(angle_deg)
        return x - 0.5 * self.length * f[0], y - 0.5 * self.length * f[1]

    def penetration(self, a, b) -> float:
        """Separating-axis penetration between two FRONT-BUMPER poses (x, y, naviDeg). 0 = disjoint.

        The value is a MINIMUM over axes, so two identically-posed cars report exactly the WIDTH
        (1.8), not a meaningful depth -- do not read a saturated value as a penetration depth.
        """
        ac, bc = self.centre(*a), self.centre(*b)
        af, ar = self.basis(a[2])
        bf, br = self.basis(b[2])

        def half(f, r, ax):
            return (abs(f[0] * ax[0] + f[1] * ax[1]) * self.length / 2
                    + abs(r[0] * ax[0] + r[1] * ax[1]) * self.width / 2)

        worst = float("inf")
        for ax in (af, ar, bf, br):
            gap = abs((bc[0] - ac[0]) * ax[0] + (bc[1] - ac[1]) * ax[1])
            pen = half(af, ar, ax) + half(bf, br, ax) - gap
            if pen <= 0.0:
                return 0.0
            worst = min(worst, pen)
        return worst


def junction_of(lane: str) -> str | None:
    """':J01_5_0' -> 'J01'. A normal lane returns None."""
    return lane.split("_")[0][1:] if lane.startswith(":") else None


def analyze(path: str, obb: Obb) -> dict:
    last_moving, final, first_seen = {}, {}, {}
    first_wedge, first_overlap, first_overlap_pair = {}, {}, {}
    stranded_dwell = collections.Counter()
    run = collections.Counter()
    inside_samples = 0
    samples = 0
    t = None
    inside_now: list = []
    seen_inside: set = set()
    nsteps = 0

    for ev, el in ET.iterparse(path, events=("start", "end")):
        if ev == "start" and el.tag == "timestep":
            t = float(el.get("time"))
            nsteps += 1
            inside_now, seen_inside = [], set()
        elif ev == "end" and el.tag == "vehicle":
            samples += 1
            vid, lane = el.get("id"), el.get("lane")
            spd = float(el.get("speed"))
            x, y, ang = float(el.get("x")), float(el.get("y")), float(el.get("angle"))
            first_seen.setdefault(vid, t)
            if spd > MOVING:
                last_moving[vid] = t
            final[vid] = (t, lane, float(el.get("pos")), x, y, ang, spd)
            j = junction_of(lane)
            if j is not None:
                inside_now.append((j, vid, x, y, ang))
                if spd <= MOVING:
                    inside_samples += 1
                    stranded_dwell[vid] += 1
                    run[vid] += 1
                    seen_inside.add(vid)
                    if run[vid] >= WEDGE_STEPS and j not in first_wedge:
                        first_wedge[j] = (t - (WEDGE_STEPS - 1), vid)
                else:
                    run[vid] = 0
            el.clear()
        elif ev == "end" and el.tag == "timestep":
            for v in list(run):
                if v not in seen_inside:
                    run[v] = 0
            for i in range(len(inside_now)):
                for k in range(i + 1, len(inside_now)):
                    a, b = inside_now[i], inside_now[k]
                    if a[0] != b[0]:
                        continue
                    if obb.penetration((a[2], a[3], a[4]), (b[2], b[3], b[4])) > GRAZE:
                        if a[0] not in first_overlap:
                            first_overlap[a[0]] = t
                            first_overlap_pair[a[0]] = (a[1], b[1])
            el.clear()

    tend = t
    stuck = {v: d for v, d in final.items() if d[0] == tend and d[6] <= MOVING}
    return dict(path=path, nsteps=nsteps, tend=tend, samples=samples, final=final, stuck=stuck,
                last_moving=last_moving, first_seen=first_seen, inside_samples=inside_samples,
                stranded_dwell=stranded_dwell, first_wedge=first_wedge,
                first_overlap=first_overlap, first_overlap_pair=first_overlap_pair, obb=obb)


def report(r: dict) -> None:
    obb = r["obb"]
    print(f"\n#### {r['path'].split('/')[-1]}  ({r['nsteps']} steps, t_end={r['tend']:.0f})")
    print(f"  vehicles seen={len(r['final'])}   stopped & present at t_end={len(r['stuck'])}"
          f"   {'GRIDLOCK' if r['stuck'] else 'DRAINED'}")
    ins = r["inside_samples"]
    dwell = r["stranded_dwell"]
    print(f"  stopped-inside-junction samples={ins} ({100*ins/max(1,r['samples']):.2f}% of all samples);"
          f" vehicles ever stranded={len(dwell)}; LONGEST DWELL={max(dwell.values()) if dwell else 0} steps")

    print("  -- causal order per junction (a wedge is >= "
          f"{WEDGE_STEPS} consecutive stopped-inside steps) --")
    js = sorted(set(r["first_wedge"]) | set(r["first_overlap"]))
    if not js:
        print("     no wedge and no overlap at any junction")
    for j in js:
        w = r["first_wedge"].get(j)
        o = r["first_overlap"].get(j)
        ws = f"t={w[0]:.0f} ({w[1]})" if w else "never"
        os_ = f"t={o:.0f} ({'x'.join(r['first_overlap_pair'][j])})" if o else "never"
        order = "-" if not (w and o) else ("OVERLAP first" if o < w[0] else
                                           "WEDGE first" if w[0] < o else "same step")
        print(f"     {j:<6} wedge {ws:<30} overlap {os_:<44} {order}")

    items = list(r["stuck"].items())
    pairs = []
    for i in range(len(items)):
        vi, di = items[i]
        for k in range(i + 1, len(items)):
            vk, dk = items[k]
            if abs(di[3] - dk[3]) > 12 or abs(di[4] - dk[4]) > 12:
                continue
            p = obb.penetration((di[3], di[4], di[5]), (dk[3], dk[4], dk[5]))
            if p > GRAZE:
                pairs.append((p, vi, di[1], vk, dk[1]))
    pairs.sort(reverse=True)
    print(f"  -- OBB overlaps among stopped vehicles at t_end: {len(pairs)} pairs --")
    for p, vi, li, vk, lk in pairs[:8]:
        both = li.startswith(":") and lk.startswith(":")
        print(f"     {p:>6.3f} m  {vi:<18}[{li:<14}] x {vk:<18}[{lk:<14}]"
              f" {'BOTH-INTERNAL' if both else ''}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("fcd")
    ap.add_argument("--compare", help="a second FCD (e.g. the SUMO oracle) to report alongside")
    ap.add_argument("--vtype-length", type=float, default=5.0)
    ap.add_argument("--vtype-width", type=float, default=1.8)
    args = ap.parse_args()

    obb = Obb(args.vtype_length, args.vtype_width)
    report(analyze(args.fcd, obb))
    if args.compare:
        report(analyze(args.compare, obb))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
