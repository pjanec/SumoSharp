#!/usr/bin/env python3
"""Measure the behavioural sensitivity of every SUMO pedestrian tuning knob.

WHY THIS EXISTS
---------------
`docs/SUMOPED-BRANCH-INVENTORY.md` says what the striping model's branches ARE.
This says what its KNOBS are WORTH: for each tunable, run vanilla SUMO across a range of values on a
fixed scenario and report the behavioural delta. That tells us three things the source alone cannot:

  * which knobs must be ported exactly (they move behaviour a lot),
  * which are inert in our scenario set (and so are untested by it -- a coverage hole),
  * which form the tuning surface for later realism work (SUMOPED-REQUIREMENTS R5c).

Output feeds `docs/SUMOPED-ALGORITHM.md` SS4.

Never run inside `dotnet test`. Needs the real SUMO binary.

USAGE
-----
  scripts/sumoped-knob-sweep.py --net NET --rou ROU --end 150 [--json OUT.json] [--only SUBSTR]
"""

import argparse
import copy
import json
import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

# Every ped-relevant tunable SUMO 1.20.0 exposes, with its verified default.
# (opt, default, values-to-sweep). `None` value = "use the default" (the baseline run).
STRIPING_OPTS = [
    ("--pedestrian.striping.stripe-width",              "0.64",  ["0.40", "0.55", "0.80", "1.00"]),
    ("--pedestrian.striping.dawdling",                  "0.2",   ["0", "0.5", "1.0"]),
    ("--pedestrian.striping.mingap-to-vehicle",         "0.25",  ["0", "1.0", "2.5"]),
    ("--pedestrian.striping.jamtime",                   "300",   ["10", "60", "-1"]),
    ("--pedestrian.striping.jamtime.crossing",          "10",    ["2", "60", "-1"]),
    ("--pedestrian.striping.jamtime.narrow",            "1",     ["10", "-1"]),
    ("--pedestrian.striping.reserve-oncoming",          "0",     ["0.2", "0.34", "0.5"]),
    ("--pedestrian.striping.reserve-oncoming.junctions", "0.34", ["0", "0.6", "1.0"]),
    ("--pedestrian.striping.reserve-oncoming.max",      "1.28",  ["0.64", "3.0"]),
    ("--pedestrian.striping.walkingarea-detail",        "4",     ["2", "8", "16"]),
    ("--pedestrian.striping.legacy-departposlat",       "false", ["true"]),
    ("--step-length",                                   "1",     ["0.5", "0.2"]),
]

# vType attributes on the PEDESTRIAN type (patched into the routes file).
PED_VTYPE_ATTRS = [
    ("width",     "0.478", ["0.30", "0.70", "1.00"]),
    ("length",    "0.215", ["0.50", "1.00"]),
    ("minGap",    "0.25",  ["0", "0.75"]),
    ("speedDev",  "0.1",   ["0", "0.3"]),
    ("speedFactor", "1",   ["0.7", "1.4"]),
    ("desiredMaxSpeed", "1.3889", ["0.9", "2.0"]),
    ("jmDriveAfterRedTime", "-1", ["5"]),
    ("impatience", "0.0",  ["1.0"]),
]

# vType attributes on the CAR type that govern how it yields to pedestrians.
CAR_VTYPE_ATTRS = [
    ("jmCrossingGap", "10", ["0", "3", "30"]),
    ("jmDriveAfterRedTime", "-1", ["5"]),
    ("impatience", "0.0", ["1.0"]),
]


def patch_vtype(rou_in, rou_out, vclass, attr, value):
    """Set attr=value on the vType whose vClass matches, writing a copy."""
    tree = ET.parse(rou_in)
    root = tree.getroot()
    hit = False
    for vt in root.findall("vType"):
        if vt.get("vClass") == vclass:
            vt.set(attr, value)
            hit = True
    if not hit:
        return False
    tree.write(rou_out)
    return True


def metrics(stat_path, coll_path):
    m = {}
    try:
        r = ET.parse(stat_path).getroot()
    except Exception:
        return {"ERROR": 1}
    for tag, keys in [
        ("vehicles", ["loaded", "inserted", "running", "waiting"]),
        ("persons", ["loaded", "running", "jammed"]),
        ("safety", ["collisions", "emergencyStops"]),
        ("personTeleports", ["total"]),
        ("vehicleTripStatistics", ["count", "speed", "duration", "waitingTime", "timeLoss"]),
        ("pedestrianStatistics", ["number", "routeLength", "duration", "timeLoss"]),
    ]:
        el = r.find(tag)
        if el is None:
            continue
        for k in keys:
            if el.get(k) is not None:
                m[f"{tag}.{k}"] = float(el.get(k))
    try:
        m["collisions"] = float(len(ET.parse(coll_path).getroot().findall("collision")))
    except Exception:
        m["collisions"] = 0.0
    return m


def lateral_metrics(fcd_path, edge):
    """Lateral/spatial metrics the aggregate statistics are BLIND to.

    The <statistic-output> counters (jammed, collisions, running) cannot see stripe usage at all, so
    on a free-flowing sidewalk every lateral knob reads as inert when it is not. These come from the
    person FCD instead: distinct lateral bands occupied, peak simultaneous bands, and -- for a
    counterflow -- the separation the two streams settle at.
    """
    try:
        tree = ET.parse(fcd_path)
    except Exception:
        return {}
    first, last, ys = {}, {}, set()
    per_step = []
    for ts in tree.getroot():
        cur = []
        for p in ts.findall("person"):
            if edge and p.get("edge") != edge:
                continue
            pid, y = p.get("id"), round(float(p.get("y")), 2)
            first.setdefault(pid, float(p.get("pos")))
            last[pid] = float(p.get("pos"))
            ys.add(y)
            cur.append((pid, y))
        if cur:
            per_step.append(cur)
    if not per_step:
        return {}
    fwd = {pid for pid in first if last[pid] > first[pid]}
    peak = max(len({y for _, y in step}) for step in per_step)
    seps = []
    for step in per_step:
        a = [y for pid, y in step if pid in fwd]
        b = [y for pid, y in step if pid not in fwd]
        if a and b:
            seps.append(abs(sum(a) / len(a) - sum(b) / len(b)))
    m = {"lat.bands_total": float(len(ys)), "lat.bands_peak": float(peak)}
    if seps:
        m["lat.stream_separation"] = round(sum(seps) / len(seps), 3)
    return m


def run(net, rou, end, extra_opts, workdir, tag, fcd_edge=None):
    stat = os.path.join(workdir, f"st_{tag}.xml")
    coll = os.path.join(workdir, f"co_{tag}.xml")
    cmd = ["sumo", "-n", net, "-r", rou, "--begin", "0", "--end", str(end),
           "--pedestrian.model", "striping",
           "--time-to-teleport", "-1", "--collision.action", "warn",
           "--collision.check-junctions", "true",
           "--statistic-output", stat, "--collision-output", coll,
           "--no-step-log", "true"] + extra_opts
    fcd = os.path.join(workdir, f"fcd_{tag}.xml")
    if fcd_edge:
        cmd += ["--fcd-output", fcd, "--fcd-output.attributes", "id,x,y,speed,pos,edge", "--precision", "3"]
    p = subprocess.run(cmd, capture_output=True, text=True)
    if p.returncode != 0:
        return {"ERROR": 1, "stderr": p.stderr[:200]}
    m = metrics(stat, coll)
    if fcd_edge:
        m.update(lateral_metrics(fcd, fcd_edge))
        try:
            os.remove(fcd)
        except OSError:
            pass
    return m


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--net", required=True)
    ap.add_argument("--rou", required=True)
    ap.add_argument("--end", type=float, default=150)
    ap.add_argument("--json", default=None)
    ap.add_argument("--only", default=None, help="only sweep knobs whose name contains this")
    ap.add_argument("--lat-edge", default=None,
                    help="edge id to compute lateral metrics on (bands used, peak bands, counterflow "
                         "stream separation). REQUIRED for any lateral knob to be visible at all -- "
                         "the aggregate statistics cannot see stripe usage.")
    ap.add_argument("--pin-rng", action="store_true", default=True,
                    help="pin --pedestrian.striping.dawdling 0 on the baseline AND every run except "
                         "the dawdling sweep itself. WITHOUT THIS THE TABLE IS NOISE: dawdling "
                         "defaults to 0.2 and draws from SUMO's single global RNG stream, so "
                         "changing ANY option shifts the draw sequence and small deltas are not "
                         "attributable to the knob. The ped vType must also carry speedDev=\"0\".")
    ap.add_argument("--no-pin-rng", dest="pin_rng", action="store_false")
    a = ap.parse_args()

    wd = tempfile.mkdtemp(prefix="knobsweep_")
    pin = ["--pedestrian.striping.dawdling", "0"] if a.pin_rng else []
    if a.pin_rng:
        devs = [vt.get("speedDev") for vt in ET.parse(a.rou).getroot().findall("vType")
                if vt.get("vClass") == "pedestrian"]
        if any(d not in ("0", "0.0", None) or d is None for d in devs):
            print(f"WARNING: ped vType speedDev={devs} -- pin-rng needs speedDev=\"0\" or deltas "
                  f"include a speedFactor draw shift", file=sys.stderr)
    base = run(a.net, a.rou, a.end, pin, wd, "base", a.lat_edge)
    if "ERROR" in base:
        sys.exit(f"baseline run failed: {base}")
    print(f"BASELINE ({'dawdling PINNED to 0' if a.pin_rng else 'SUMO defaults'}): {json.dumps(base, sort_keys=True)}\n")

    results = {"_baseline": base, "_scenario": {"net": a.net, "rou": a.rou, "end": a.end}}

    def record(kind, name, default, value, m):
        key = f"{name}={value}"
        results.setdefault(kind, {})[key] = m
        if "ERROR" in m:
            print(f"  {key:<56} FAILED {m.get('stderr','')[:60]}")
            return
        deltas = []
        for k in ("persons.jammed", "collisions", "pedestrianStatistics.timeLoss",
                  "pedestrianStatistics.duration", "pedestrianStatistics.routeLength",
                  "persons.running", "vehicleTripStatistics.timeLoss", "vehicles.running",
                  "lat.bands_total", "lat.bands_peak", "lat.stream_separation"):
            b, v = base.get(k), m.get(k)
            if b is None or v is None:
                continue
            if abs(v - b) < 1e-9:
                continue
            pct = (v - b) / b * 100 if abs(b) > 1e-9 else float("inf")
            deltas.append(f"{k.split('.')[-1]} {b:g}->{v:g}" + (f" ({pct:+.0f}%)" if abs(b) > 1e-9 else ""))
        print(f"  {key:<56} {'; '.join(deltas) if deltas else 'NO CHANGE (inert on this scenario)'}")

    print("=== striping / global options ===")
    for opt, dflt, vals in STRIPING_OPTS:
        if a.only and a.only not in opt:
            continue
        for v in vals:
            # never pin dawdling on top of a dawdling sweep
            extra = ([] if "dawdling" in opt else pin) + [opt, v]
            record("options", opt, dflt, v, run(a.net, a.rou, a.end, extra, wd, f"o{abs(hash(opt+v))}", a.lat_edge))

    for label, vclass, attrs in [("pedestrian vType", "pedestrian", PED_VTYPE_ATTRS),
                                 ("car vType", "passenger", CAR_VTYPE_ATTRS)]:
        print(f"\n=== {label} attributes ===")
        for attr, dflt, vals in attrs:
            if a.only and a.only not in attr:
                continue
            for v in vals:
                rp = os.path.join(wd, f"r_{vclass}_{attr}_{v}.rou.xml")
                if not patch_vtype(a.rou, rp, vclass, attr, v):
                    print(f"  {attr}={v}: no vType with vClass={vclass} in {a.rou}")
                    continue
                record(f"vtype.{vclass}", attr, dflt, v,
                       run(a.net, rp, a.end, pin, wd, f"v{abs(hash(attr+v+vclass))}", a.lat_edge))

    if a.json:
        json.dump(results, open(a.json, "w"), indent=1, sort_keys=True)
        print(f"\nwrote {a.json}")


if __name__ == "__main__":
    main()
