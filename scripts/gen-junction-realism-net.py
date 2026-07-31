#!/usr/bin/env python3
"""Generate the junction-realism repro scenarios (Stage 0 of the junction-realism workstream).

WHY THIS NET EXISTS. The four reported realism defects -- gridlock, lane-change-at-red into an
occupied lane, two cars overlapping stopped inside a junction, and two cars driving THROUGH each
other at a junction -- are all observed on the live-city demo, which has no SUMO reference and is far
too large to reason about. This builds the smallest network that can express ALL FOUR, and does so as
plain SUMO inputs so that every measurement taken on it has a SUMO oracle (run the same net+routes
through honest SUMO and diff).

WHAT THE GEOMETRY IS FOR, ARM BY ARM -- each feature is here to force ONE defect:

  * 2x2 grid of junctions, SHORT links between them (default 80 m centre-to-centre, ~60 m of usable
    lane => ~8 jammed cars).  A queue on an internal link therefore spills back INTO the upstream
    junction within a few vehicles.  This is the box-block precondition; without it a junction never
    has to decide whether to enter a space it cannot clear.
  * The four internal links form a CYCLE (J00->J10->J11->J01->J00).  A circular wait needs a cycle;
    a corridor or a single junction structurally cannot gridlock.  Routes below deliberately load it.
  * Every junction is a 4-way with two external stub arms, so left-turn demand can be injected and
    drained without interfering with the cycle.
  * Traffic lights (default netconvert program) give the "standing at a red light" state that the
    lane-change defect needs, and their PERMISSIVE left phase ('g') is exactly the state in which a
    left-turner and the opposing straight are both admitted -- the pass-through defect.
  * --lanes 2 puts two cars side by side at the stop line (the lane-change-into-occupied geometry).
    --lanes 1 is generated too, as the confounder-free control: docs/NEED-multilane-junction-passage.md
    records a SEPARATE multilane over-yield deadlock, so a gridlock seen only at L2 may be that NEED
    rather than the defect under investigation.  Always read the two variants together.

DETERMINISM.  sigma=0, fixed depart times/lanes/speeds, teleport off, Euler integration -- the phase-1
parity settings from CLAUDE.md.  netconvert is deterministic given identical inputs, so re-running this
script reproduces net.net.xml byte-for-byte (asserted by provenance.txt's sha256).

THIS IS A [net] STEP: it shells out to netconvert and is therefore NETWORK/SUMO-side.  It is NOT part
of `dotnet test` -- the offline loop consumes only the committed outputs, per CLAUDE.md.

Usage:
    scripts/gen-junction-realism-net.py [--out-root scenarios/_diag] [--spacing 80] [--stub 200]
"""

from __future__ import annotations

import argparse
import hashlib
import shutil
import subprocess
import sys
from pathlib import Path

# --- grid layout ---------------------------------------------------------------------------------
# Junction (col, row) -> id.  Row 0 is south, row 1 is north; col 0 is west, col 1 is east.
GRID = [(0, 0), (1, 0), (0, 1), (1, 1)]


def jid(col: int, row: int) -> str:
    return f"J{col}{row}"


def nodes_xml(spacing: float, stub: float) -> str:
    """Four traffic-light junctions in a square, each with two external stub arms.

    Stub arms are placed so that each grid junction has EXACTLY four arms (two internal, two
    external) -- a 4-way is the only geometry that produces the left-turn-vs-opposing-straight
    conflict this net is built to expose.  Corner junctions get their two external arms on the two
    sides that face away from the grid.
    """
    lines = ['<?xml version="1.0" encoding="UTF-8"?>', "<nodes>"]
    for col, row in GRID:
        x, y = col * spacing, row * spacing
        lines.append(f'    <node id="{jid(col, row)}" x="{x}" y="{y}" type="traffic_light"/>')
    # External stubs: 'priority' dead-ends, far enough out that insertion/arrival never interacts
    # with the junction being measured.
    for col, row in GRID:
        x, y = col * spacing, row * spacing
        # West/east stub on the outward side.
        if col == 0:
            lines.append(f'    <node id="W{col}{row}" x="{x - stub}" y="{y}" type="priority"/>')
        else:
            lines.append(f'    <node id="E{col}{row}" x="{x + stub}" y="{y}" type="priority"/>')
        # South/north stub on the outward side.
        if row == 0:
            lines.append(f'    <node id="S{col}{row}" x="{x}" y="{y - stub}" type="priority"/>')
        else:
            lines.append(f'    <node id="N{col}{row}" x="{x}" y="{y + stub}" type="priority"/>')
    lines.append("</nodes>")
    return "\n".join(lines) + "\n"


def _edge(eid: str, frm: str, to: str, lanes: int, speed: float) -> str:
    return f'    <edge id="{eid}" from="{frm}" to="{to}" numLanes="{lanes}" speed="{speed}"/>'


def edges_xml(lanes: int, speed: float) -> str:
    """Bidirectional edges: the four internal cycle links, plus one in/out pair per stub."""
    lines = ['<?xml version="1.0" encoding="UTF-8"?>', "<edges>"]

    # Internal links -- the cycle.  Named <axis><index>[r] where r = the reverse direction.
    lines.append("    <!-- internal grid links: these four form the gridlock cycle -->")
    for row in (0, 1):
        a, b = jid(0, row), jid(1, row)
        lines.append(_edge(f"h{row}", a, b, lanes, speed))
        lines.append(_edge(f"h{row}r", b, a, lanes, speed))
    for col in (0, 1):
        a, b = jid(col, 0), jid(col, 1)
        lines.append(_edge(f"v{col}", a, b, lanes, speed))
        lines.append(_edge(f"v{col}r", b, a, lanes, speed))

    lines.append("    <!-- external stub arms: demand sources and sinks -->")
    for col, row in GRID:
        j = jid(col, row)
        ew = f"W{col}{row}" if col == 0 else f"E{col}{row}"
        ns = f"S{col}{row}" if row == 0 else f"N{col}{row}"
        for stub_node in (ew, ns):
            lines.append(_edge(f"in_{stub_node}", stub_node, j, lanes, speed))
            lines.append(_edge(f"out_{stub_node}", j, stub_node, lanes, speed))
    lines.append("</edges>")
    return "\n".join(lines) + "\n"


# --- demand --------------------------------------------------------------------------------------
# Each route names the defect it is here to provoke.  Keep this mapping explicit: a route set whose
# purpose is undocumented cannot be shrunk safely when the repro is later minimised.
#
# 'stub' ids follow the node naming above: W00/S00 at J00, E10/S10 at J10, W01/N01 at J01, N11/E11 at J11.
ROUTES: list[tuple[str, str, str]] = [
    # (id, purpose, space-separated edge list)
    # -- CYCLE LOADERS: traverse three junctions and use the internal links, so a queue on one
    #    internal link blocks the junction feeding it.  This is the gridlock demand.
    ("cyc_ccw", "gridlock: counter-clockwise around the internal cycle",
     "in_W00 h0 v1 h1r out_W01"),
    ("cyc_cw", "gridlock: clockwise around the internal cycle",
     "in_S00 v0 h1 v1r out_S10"),
    ("cyc_ccw2", "gridlock: second counter-clockwise entry, offset by one junction",
     "in_S10 v1 h1r v0r out_S00"),
    ("cyc_cw2", "gridlock: second clockwise entry, offset by one junction",
     "in_W01 h1 v1r h0r out_W00"),

    # -- OPPOSING PAIR at J00: an unprotected LEFT turn and the STRAIGHT it must yield to, arriving
    #    together.  This is the pass-through-each-other geometry.
    ("left_W00", "pass-through: westbound entry turning LEFT (north) across opposing traffic at J00",
     "in_W00 v0 out_N01"),
    ("thru_E10", "pass-through: the opposing STRAIGHT the left-turner must yield to at J00",
     "in_E10 h0r out_W00"),

    # -- OPPOSING PAIR at J11: same conflict, mirrored, so the defect is not an artefact of one
    #    junction's generated TL program or connection order.
    ("left_E11", "pass-through: eastbound entry turning LEFT (south) across opposing traffic at J11",
     "in_E11 v1r out_S10"),
    ("thru_W01", "pass-through: the opposing STRAIGHT at J11",
     "in_W01 h1 out_E11"),

    # -- STRAIGHT THROUGH-TRAFFIC: fills both lanes of an approach so that a car forced to change
    #    lane at the stop line finds the target lane already OCCUPIED (the lane-change defect).
    ("fill_S00", "lane-change-at-red: fills the northbound approach to J00",
     "in_S00 v0 out_N01"),
    ("fill_N11", "lane-change-at-red: fills the southbound approach to J11",
     "in_N11 v1r out_S10"),
]

# Insertion schedule.  `period` is per-route seconds-between-departures; `begin` staggers the routes
# so the opposing pairs actually MEET at the junction rather than passing at different times.
# Deterministic: SUMO's <flow period=...> inserts at exact multiples, no RNG.
FLOWS: list[tuple[str, float, float]] = [
    # (routeId, begin, period)
    ("cyc_ccw", 0.0, 6.0),
    ("cyc_cw", 1.0, 6.0),
    ("cyc_ccw2", 2.0, 6.0),
    ("cyc_cw2", 3.0, 6.0),
    ("left_W00", 0.0, 12.0),
    ("thru_E10", 0.0, 12.0),
    ("left_E11", 6.0, 12.0),
    ("thru_W01", 6.0, 12.0),
    ("fill_S00", 0.0, 4.0),
    ("fill_N11", 2.0, 4.0),
]


def routes_xml(end: float, demand_scale: float) -> str:
    lines = ['<?xml version="1.0" encoding="UTF-8"?>', "<routes>"]
    # sigma=0 and a fixed speedFactor: phase-1 determinism (CLAUDE.md).  No driver imperfection, so a
    # divergence from SUMO is a model difference, never noise.
    lines.append(
        '    <vType id="car" accel="2.6" decel="4.5" sigma="0" length="5.0" width="1.8"'
        ' minGap="2.5" maxSpeed="13.89" speedFactor="1.0" speedDev="0"/>'
    )
    lines.append("")
    for rid, purpose, edges in ROUTES:
        lines.append(f"    <!-- {purpose} -->")
        lines.append(f'    <route id="{rid}" edges="{edges}"/>')
    lines.append("")
    # SUMO SILENTLY DROPS a <flow> whose begin precedes the previous one ("Route file should be
    # sorted by departure time, ignoring ...") -- a WARNING, not an error, and the run still exits 0
    # with a plausible-looking result.  The first version of this file lost 4 of its 10 flows that
    # way.  Sort here so the schedule that is written is the schedule that runs.
    for rid, begin, period in sorted(FLOWS, key=lambda f: (f[1], f[0])):
        # departLane="free" would introduce a placement decision that differs between engines and
        # muddies the lane-change measurement; pin it. departSpeed="max" matches the goldens' style.
        lines.append(
            f'    <flow id="f_{rid}" route="{rid}" type="car" begin="{begin}" end="{end}"'
            f' period="{period * demand_scale:g}" departLane="0" departSpeed="max"/>'
        )
    lines.append("</routes>")
    return "\n".join(lines) + "\n"


def sumocfg_xml(end: float, step_length: float) -> str:
    """Phase-1 determinism + HONEST collision settings.

    The three <processing> entries are the point of this file: SUMO's shipped defaults
    (time-to-teleport=300, collision.action=teleport, collision.check-junctions=false) would HIDE
    exactly the defects being measured -- a wedged pair would teleport out after 5 minutes and a
    junction interpenetration would not even be detected.  See CLAUDE.md measurement discipline #11.
    """
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<configuration>
    <input>
        <net-file value="net.net.xml"/>
        <route-files value="rou.rou.xml"/>
    </input>
    <time>
        <begin value="0"/>
        <end value="{end}"/>
        <step-length value="{step_length}"/>
    </time>
    <processing>
        <!-- honest SUMO: never conceal a wedge or an interpenetration (CLAUDE.md #11) -->
        <time-to-teleport value="-1"/>
        <collision.action value="warn"/>
        <collision.check-junctions value="true"/>
        <!-- phase-1 parity settings -->
        <step-method.ballistic value="false"/>
        <default.action-step-length value="{step_length}"/>
        <lateral-resolution value="-1"/>
    </processing>
    <report>
        <no-step-log value="true"/>
    </report>
</configuration>
"""


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out-root", default="scenarios/_diag", help="parent directory for the generated scenarios")
    ap.add_argument("--spacing", type=float, default=80.0, help="junction centre-to-centre spacing (m)")
    ap.add_argument("--stub", type=float, default=200.0, help="external stub arm length (m)")
    ap.add_argument("--speed", type=float, default=13.89, help="edge speed limit (m/s)")
    # TWO horizons, deliberately different.  Demand stops at --flow-end; the simulation runs on to
    # --end with no further insertions.  That DRAIN WINDOW is what makes "is it gridlocked?" a
    # decidable question: a merely-busy network empties once inflow stops, a gridlocked one does not.
    # Judging saturation from vehicles still running at the end of the demand window instead reports
    # SATURATED for any healthy network with cars in flight -- which is what the first sweep did.
    ap.add_argument("--flow-end", type=float, default=600.0, help="last insertion time (s)")
    ap.add_argument("--end", type=float, default=1800.0, help="simulation end time (s), incl. drain")
    ap.add_argument("--step-length", type=float, default=1.0, help="simulation step length (s)")
    # Demand must sit BELOW honest SUMO's saturation point or the oracle is worthless: if SUMO also
    # gridlocks, "we gridlock" is not evidence of a defect.  Larger scale = longer period = LESS
    # demand.  The committed default is chosen by the sweep in --sweep mode; see provenance.txt.
    ap.add_argument("--demand-scale", type=float, default=1.0,
                    help="multiplier on every flow period (>1 = less demand)")
    ap.add_argument("--suffix", default="", help="append to the scenario dir name (for sweeps)")
    ap.add_argument("--lanes", type=int, nargs="+", default=[1, 2], help="lane-count variants to emit")
    args = ap.parse_args()

    if shutil.which("netconvert") is None:
        print("FATAL: netconvert not on PATH. This is the SUMO-side generation step; install SUMO first.",
              file=sys.stderr)
        return 1

    # Resolve the repo root from THIS SCRIPT's location, not the caller's cwd: the generator is
    # routinely run from a scratch directory during a demand sweep, where `git rev-parse` exits 128.
    # (Same reasoning, and the same past failure, as scripts/run-density-diff.sh.)
    repo_root = Path(__file__).resolve().parent.parent
    out_root = repo_root / args.out_root

    for lanes in args.lanes:
        out = out_root / f"junction-realism-L{lanes}{args.suffix}"
        out.mkdir(parents=True, exist_ok=True)
        (out / "n.nod.xml").write_text(nodes_xml(args.spacing, args.stub))
        (out / "e.edg.xml").write_text(edges_xml(lanes, args.speed))
        (out / "rou.rou.xml").write_text(routes_xml(args.flow_end, args.demand_scale))
        (out / "config.sumocfg").write_text(sumocfg_xml(args.end, args.step_length))

        cmd = [
            "netconvert",
            "--node-files", str(out / "n.nod.xml"),
            "--edge-files", str(out / "e.edg.xml"),
            "--output-file", str(out / "net.net.xml"),
            # Deterministic, and no geometry simplification that would merge the four junctions we
            # are specifically trying to keep distinct.
            "--no-turnarounds", "true",
            "--tls.guess", "false",
            "--junctions.corner-detail", "0",
            "--offset.disable-normalization", "true",
        ]
        subprocess.run(cmd, check=True)

        net_sha = hashlib.sha256((out / "net.net.xml").read_bytes()).hexdigest()
        sumo_ver = subprocess.check_output(["sumo", "--version"], text=True).splitlines()[0]
        (out / "provenance.txt").write_text(
            "Generated by scripts/gen-junction-realism-net.py -- DO NOT hand-edit.\n"
            f"{sumo_ver}\n"
            f"lanes={lanes} spacing={args.spacing} stub={args.stub} speed={args.speed}\n"
            f"demand-scale={args.demand_scale}\n"
            f"flow-end={args.flow_end} end={args.end} step-length={args.step_length}\n"
            f"net.net.xml sha256={net_sha}\n"
        )
        print(f"wrote {out}  (net sha256 {net_sha[:16]})")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
