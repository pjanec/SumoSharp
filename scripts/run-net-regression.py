#!/usr/bin/env python3
"""Cross-net no-regression battery (T6b of docs/JUNCTION-APPROACH-ARM-TASKS.md).

WHY THIS EXISTS. The junction-realism repro is a net PURPOSE-BUILT to gridlock, so "the repro got
better" says nothing about the two dozen committed nets that currently flow. The specific hazard the
approach arm carries is a NEW SYMMETRIC DEADLOCK -- "is anyone approaching my foe link?" is symmetric
across a 4-way, and this codebase has already shipped one gate that wedged four cars for 4890 steps for
exactly that reason. The owner's bar: a symmetric deadlock is as bad as the existing one, so fixing the
repro does not buy a regression anywhere else.

WHAT IT MEASURES, per net: arrived, still-running at the end, longest dwell inside a junction, and
junction-interior OBB overlap pairs. Those four are the ones a deadlock moves.

THE DRAIN WINDOW IS THE WHOLE TRICK. Vehicles still running when demand stops are not evidence of a
jam; vehicles still running long AFTER it stops are. `--drain-factor` extends each scenario's own step
count so the network gets a chance to clear. Nets whose demand runs to the very end cannot be judged
this way and are reported as INCONCLUSIVE rather than silently scored.

RUN THE BASELINE BEFORE TOUCHING THE ENGINE. A baseline captured after the change is not a baseline --
this workstream has already lost one that way (the threaded-tick "before" run, never captured because
threading was already the default when the session started).

Both arms MUST be run with this same script: cross-instrument number comparisons are invalid
(CLAUDE.md measurement discipline #8/#13).

NOT part of `dotnet test` -- it drives the Sim.Run CLI. Commit its report, not its FCD.

Usage:
    scripts/run-net-regression.py --out docs/reports/net-regression-baseline.txt
    scripts/run-net-regression.py --out /tmp/after.txt --only city-30,highway-dense
    scripts/run-net-regression.py --compare docs/reports/net-regression-baseline.txt --out /tmp/after.txt
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import re
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SIM_RUN = REPO / "src" / "Sim.Run" / "Sim.Run.csproj"

# The analyzer owns the OBB conventions (which it in turn copies from src/Sim.Ingest/VehicleObb.cs).
# Import it rather than re-deriving: hand-re-derivation of those conventions has produced two separate
# bugs in this repo (docs/NEED-obb-anchor-halflength.md).
_spec = importlib.util.spec_from_file_location(
    "jr_analyze", REPO / "scripts" / "analyze-junction-realism-fcd.py")
jr = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(jr)


def vtype_dims(scenario: Path) -> dict[str, tuple[float, float]]:
    """type id -> (length, width) from the scenario's own route files.

    Necessary, not cosmetic: these nets carry trucks and RAIL. Scoring a 100 m train's footprint as a
    5 m car would invent overlaps on rail-crossing-demo and miss them everywhere else. SUMO's own
    defaults are used for types that omit an attribute.
    """
    dims: dict[str, tuple[float, float]] = {}
    for rou in sorted(scenario.glob("*.rou.xml")):
        try:
            root = ET.parse(rou).getroot()
        except ET.ParseError:
            continue
        for vt in root.iter("vType"):
            tid = vt.get("id")
            if tid is None:
                continue
            dims[tid] = (float(vt.get("length", 5.0)), float(vt.get("width", 1.8)))
    return dims


def cfg_steps(scenario: Path) -> tuple[int | None, float]:
    """(steps, step_length) implied by the scenario's *.sumocfg, or (None, 1.0) if not derivable."""
    cfgs = sorted(scenario.glob("*.sumocfg"))
    if not cfgs:
        return None, 1.0
    root = ET.parse(cfgs[0]).getroot()

    def val(tag: str):
        el = root.find(f".//{tag}")
        return float(el.get("value")) if el is not None and el.get("value") else None

    begin, end, sl = val("begin") or 0.0, val("end"), val("step-length") or 1.0
    return (None if end is None else int(round((end - begin) / sl))), sl


def run_one(scenario: Path, max_steps: int, drain_factor: float, timeout: int) -> dict:
    name = scenario.name
    steps, _sl = cfg_steps(scenario)
    if steps is None:
        return dict(net=name, status="SKIP", note="no end time in sumocfg")

    # Extend past the scenario's own horizon so the network has a chance to CLEAR. Without this,
    # "still running at the end" measures demand, not deadlock.
    want = min(int(steps * drain_factor), max_steps)
    with tempfile.TemporaryDirectory() as td:
        fcd = Path(td) / "engine.fcd.xml"
        t0 = time.time()
        proc = subprocess.run(
            ["dotnet", "run", "--project", str(SIM_RUN), "-c", "Release", "--no-build", "--",
             str(scenario), "--parity", "--steps", str(want), "--fcd-out", str(fcd)],
            capture_output=True, text=True, timeout=timeout, cwd=str(REPO))
        wall = time.time() - t0
        if proc.returncode != 0 or not fcd.exists():
            return dict(net=name, status="FAIL", note=(proc.stderr or proc.stdout).strip()[-160:])

        dims = vtype_dims(scenario)
        default = jr.Obb(5.0, 1.8)
        # One Obb per distinct (length,width) so per-vehicle dimensions are honoured.
        obbs = {t: jr.Obb(l, w) for t, (l, w) in dims.items()}
        r = analyze_sized(str(fcd), obbs, default)

    drained = r["running_end"] == 0
    # A net whose demand runs to the very end cannot be judged on "did it clear" -- say so instead of
    # scoring it.
    inconclusive = want <= steps
    return dict(net=name, status="INCONCLUSIVE" if (inconclusive and not drained) else
                ("DRAINED" if drained else "STUCK"),
                steps=want, arrived=r["arrived"], running_end=r["running_end"],
                max_dwell=r["max_dwell"], max_dwell_terminal=r["max_dwell_terminal"],
                overlaps=r["overlaps"], wall=round(wall, 1))


def analyze_sized(path: str, obbs: dict, default) -> dict:
    """Like jr.analyze but honouring per-vehicle type dimensions (see vtype_dims)."""
    import collections
    last_seen, final, dwell, run = {}, {}, collections.Counter(), collections.Counter()
    inside_now, seen_inside = [], set()
    max_pairs = 0
    t = None
    for ev, el in ET.iterparse(path, events=("start", "end")):
        if ev == "start" and el.tag == "timestep":
            t = float(el.get("time"))
            inside_now, seen_inside = [], set()
        elif ev == "end" and el.tag == "vehicle":
            vid, lane, spd = el.get("id"), el.get("lane"), float(el.get("speed"))
            vt = el.get("type") or ""
            final[vid] = (t, lane, spd)
            last_seen[vid] = t
            if lane.startswith(":"):
                inside_now.append((lane.split("_")[0][1:], vid, float(el.get("x")),
                                   float(el.get("y")), float(el.get("angle")), vt))
                if spd <= jr.MOVING:
                    dwell[vid] += 1
                    run[vid] += 1
                    seen_inside.add(vid)
                else:
                    run[vid] = 0
            el.clear()
        elif ev == "end" and el.tag == "timestep":
            for v in list(run):
                if v not in seen_inside:
                    run[v] = 0
            pairs = 0
            for i in range(len(inside_now)):
                for k in range(i + 1, len(inside_now)):
                    a, b = inside_now[i], inside_now[k]
                    if a[0] != b[0]:
                        continue
                    obb = obbs.get(a[5], default)
                    if obb.penetration((a[2], a[3], a[4]), (b[2], b[3], b[4])) > jr.GRAZE:
                        pairs += 1
            max_pairs = max(max_pairs, pairs)
            el.clear()
    tend = t
    running_end = sum(1 for d in final.values() if d[0] == tend)
    # TWO dwell numbers, because ONE CANNOT TELL A CORRECT WAIT FROM A WEDGE -- and the change this
    # battery exists to judge (the internal-junction approach arm) deliberately HOLDS vehicles in their
    # cont bays, which are internal lanes. SUMO holds the same vehicle in the same bay for ten seconds
    # on the repro, so a rising `max_dwell` is the intended behaviour, not evidence of a deadlock.
    # Scoring the arm on `max_dwell` alone would therefore report a regression for doing its job --
    # exactly the "an occupancy metric is not a causation metric" error of CLAUDE.md lesson 15.
    #
    # `max_dwell_terminal` is the deadlock metric: the longest stopped-inside-junction run that is still
    # unbroken when the simulation ends, i.e. one that NEVER resolved. A bay wait ends; a wedge does not.
    terminal = {v: n for v, n in run.items() if n > 0 and v in seen_inside}
    return dict(arrived=len(final) - running_end, running_end=running_end,
                max_dwell=max(dwell.values()) if dwell else 0,
                max_dwell_terminal=max(terminal.values()) if terminal else 0,
                overlaps=max_pairs)


# maxDwell = longest stopped-inside-junction run (a correct bay wait counts here too).
# stuckDwell = longest such run STILL UNBROKEN AT THE END -- the one that never resolved. That is the
# deadlock number; maxDwell alone cannot distinguish waiting from wedged. See analyze_sized.
HEADER = (f"{'net':<28}{'status':<14}{'steps':>7}{'arrived':>9}{'running':>9}"
          f"{'maxDwell':>10}{'stuckDwell':>12}{'overlaps':>10}{'wall_s':>8}")


def fmt(r: dict) -> str:
    if r["status"] in ("SKIP", "FAIL"):
        return f"{r['net']:<28}{r['status']:<14}  {r.get('note','')}"
    return (f"{r['net']:<28}{r['status']:<14}{r['steps']:>7}{r['arrived']:>9}{r['running_end']:>9}"
            f"{r['max_dwell']:>10}{r['max_dwell_terminal']:>12}{r['overlaps']:>10}{r['wall']:>8}")


def parse_report(path: Path) -> dict[str, dict]:
    out = {}
    for line in path.read_text().splitlines():
        m = re.match(r"^(\S+)\s+(DRAINED|STUCK|INCONCLUSIVE)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)", line)
        if m:
            out[m.group(1)] = dict(status=m.group(2), steps=int(m.group(3)), arrived=int(m.group(4)),
                                   running_end=int(m.group(5)), max_dwell=int(m.group(6)),
                                   max_dwell_terminal=int(m.group(7)), overlaps=int(m.group(8)))
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True)
    ap.add_argument("--only", help="comma-separated net-name substrings")
    # city-15000 is excluded by default, and the reason is a real constraint rather than taste: at
    # 15k vehicles its FCD is multi-GB, and writable disk here is a fixed per-session allowance. It is
    # named in the report as EXCLUDED so its absence is visible instead of silent.
    ap.add_argument("--exclude", default="city-15000",
                    help="comma-separated net-name substrings to skip (default: city-15000)")
    ap.add_argument("--max-steps", type=int, default=1200)
    ap.add_argument("--drain-factor", type=float, default=2.0)
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--compare", help="a baseline report to diff this run against")
    args = ap.parse_args()

    scenarios = sorted([p for p in (REPO / "scenarios" / "_bench").iterdir() if p.is_dir()] +
                       [p for p in (REPO / "scenarios" / "_diag").iterdir() if p.is_dir()])
    if args.only:
        pats = [s.strip() for s in args.only.split(",")]
        scenarios = [s for s in scenarios if any(p in s.name for p in pats)]

    excluded = []
    if args.exclude:
        pats = [s.strip() for s in args.exclude.split(",") if s.strip()]
        excluded = [s.name for s in scenarios if any(p in s.name for p in pats)]
        scenarios = [s for s in scenarios if not any(p in s.name for p in pats)]

    rows, lines = [], [HEADER, "-" * len(HEADER)]
    for name in excluded:
        lines.append(f"{name:<28}{'EXCLUDED':<14}  by --exclude (see script header)")
    print(HEADER)
    for s in scenarios:
        try:
            r = run_one(s, args.max_steps, args.drain_factor, args.timeout)
        except subprocess.TimeoutExpired:
            r = dict(net=s.name, status="FAIL", note=f"timeout after {args.timeout}s")
        rows.append(r)
        line = fmt(r)
        print(line, flush=True)
        lines.append(line)

    Path(args.out).parent.mkdir(parents=True, exist_ok=True)
    Path(args.out).write_text("\n".join(lines) + "\n")
    print(f"\nwrote {args.out}")

    if args.compare:
        base = parse_report(Path(args.compare))
        print("\n=== REGRESSIONS vs baseline (arrived down / running up / dwell up / overlaps up) ===")
        bad = 0
        for r in rows:
            b = base.get(r["net"])
            if not b or r["status"] in ("SKIP", "FAIL"):
                continue
            deltas = []
            if r["arrived"] < b["arrived"]:
                deltas.append(f"arrived {b['arrived']}->{r['arrived']}")
            if r["running_end"] > b["running_end"]:
                deltas.append(f"running {b['running_end']}->{r['running_end']}")
            # maxDwell is reported but NOT a regression criterion: holding a vehicle in its cont bay
            # is the intended behaviour of the change under test, and SUMO does the same. Only an
            # UNRESOLVED dwell (still stopped inside a junction when the run ends) indicates a deadlock.
            if r["max_dwell_terminal"] > b["max_dwell_terminal"]:
                deltas.append(f"stuckDwell {b['max_dwell_terminal']}->{r['max_dwell_terminal']}")
            if r["overlaps"] > b["overlaps"]:
                deltas.append(f"overlaps {b['overlaps']}->{r['overlaps']}")
            if deltas:
                bad += 1
                print(f"  REGRESSED {r['net']:<28} {'; '.join(deltas)}")
        print(f"  {bad} net(s) regressed" if bad else "  none")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
