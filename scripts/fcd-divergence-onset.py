#!/usr/bin/env python3
"""Report the FIRST step at which two FCD files disagree, and how far apart they then drift.

WHY THIS EXISTS. Every cross-engine claim about a per-vehicle mechanism is only valid while the two
engines are still in the SAME state. On `junction-realism-L2` ours and SUMO's trajectories diverge
almost immediately, which quietly invalidated two side-by-side traces in
docs/JUNCTION-REALISM-SESSION-JOURNAL.md Entries 21-22 -- "SUMO's vehicle X at t=57" was somewhere else
entirely. A LOCKSTEP WINDOW is therefore a prerequisite for asking "what would SUMO do HERE?", and this
script measures it.

Use it to pick a scenario, or to bound how much of a trace you are allowed to believe.

    scripts/fcd-divergence-onset.py ours.fcd.xml sumo.fcd.xml [--pos-tol 0.01] [--limit 5]

Reports, per step: the number of vehicles present in one file but not the other, and the max abs
position delta over the vehicles present in both. The FIRST step where either exceeds tolerance is the
end of the lockstep window -- after that, per-vehicle cross-engine comparison is not evidence.

NOT part of `dotnet test`; a committed CLI instrument (CLAUDE.md #8: a probe that is deleted makes its
own numbers unfalsifiable).
"""
import argparse
import xml.etree.ElementTree as ET


def read(path):
    """path -> {time: {vehId: (lane, pos)}}, streamed so a 14 MB FCD does not land in memory twice."""
    out = {}
    t = None
    for ev, el in ET.iterparse(path, events=("start", "end")):
        if ev == "start" and el.tag == "timestep":
            t = float(el.get("time"))
            out[t] = {}
        elif ev == "end" and el.tag == "vehicle":
            out[t][el.get("id")] = (el.get("lane"), float(el.get("pos")))
            el.clear()
        elif ev == "end" and el.tag == "timestep":
            el.clear()
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("ours")
    ap.add_argument("sumo")
    ap.add_argument("--pos-tol", type=float, default=0.01,
                    help="metres of front-bumper difference tolerated before a step counts as diverged")
    ap.add_argument("--limit", type=int, default=5, help="how many diverging vehicles to name")
    args = ap.parse_args()

    a, b = read(args.ours), read(args.sumo)
    steps = sorted(set(a) & set(b))
    if not steps:
        print("no common timesteps -- are these the same scenario/horizon?")
        return 2

    first_bad = None
    for t in steps:
        va, vb = a[t], b[t]
        only_a, only_b = set(va) - set(vb), set(vb) - set(va)
        both = set(va) & set(vb)
        worst, worst_id = 0.0, None
        lane_mismatch = []
        for vid in both:
            if va[vid][0] != vb[vid][0]:
                lane_mismatch.append(vid)
            d = abs(va[vid][1] - vb[vid][1])
            if d > worst:
                worst, worst_id = d, vid
        bad = only_a or only_b or lane_mismatch or worst > args.pos_tol
        if bad and first_bad is None:
            first_bad = t
            print(f"LOCKSTEP WINDOW: t={steps[0]:.0f} .. t={t:.0f}  ({int(t - steps[0])} steps)")
            print(f"  first divergence at t={t:.0f}:")
            print(f"    vehicles only in ours: {sorted(only_a)[:args.limit]}")
            print(f"    vehicles only in sumo: {sorted(only_b)[:args.limit]}")
            print(f"    lane mismatches: {sorted(lane_mismatch)[:args.limit]}")
            print(f"    worst pos delta: {worst:.4f} m on {worst_id}")
            break

    if first_bad is None:
        print(f"NO DIVERGENCE over {len(steps)} common steps "
              f"(t={steps[0]:.0f}..{steps[-1]:.0f}) within pos-tol={args.pos_tol} m.")
        return 0

    # How bad does it get? A window that ends at t=3 is useless; one that ends at t=300 may still be
    # enough to contain the artefact under study.
    tail = steps[-1]
    va, vb = a[tail], b[tail]
    both = set(va) & set(vb)
    worst = max((abs(va[v][1] - vb[v][1]) for v in both), default=0.0)
    print(f"  by the last common step t={tail:.0f}: {len(both)} shared vehicles, "
          f"worst pos delta {worst:.1f} m")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
