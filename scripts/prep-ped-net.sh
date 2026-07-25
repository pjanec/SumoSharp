#!/usr/bin/env bash
#
# prep-ped-net.sh
# ---------------
# docs/LIVE-CITY-ARBITRARY-NET-DESIGN.md §8, -TASKS.md E1: offline preparation recipe for making an
# arbitrary bare SUMO road-net pedestrian-capable, so it can be loaded via
# `LiveCityConfig.ForDataset` in RouteGraph mode (SumoRouteGraphNav).
#
# This is DEV-SIDE TOOLING ONLY. It is never invoked by `dotnet test` and never runs in the offline
# test loop (CLAUDE.md: "the offline test loop must never invoke SUMO"). Run it by hand, on a
# network-enabled machine with SUMO installed, then commit the produced net.xml as a scenario input
# (exactly like scripts/regen-goldens.sh commits goldens -- the OUTPUT is committed, never
# regenerated at test time).
#
# WHAT IT DOES: runs `netconvert` with three guessing passes over the input net --
#   --sidewalks.guess              add sidewalk lanes to edges that lack one
#   --crossings.guess               add crossings at junctions where pedestrians can cross
#   --walkingareas.all-nonspecific  add walkingarea polygons at every junction needing one
# -- the exact recipe design §8 documents, matching what `netgenerate --sidewalks.guess
# --crossings.guess` does for a from-scratch synthetic net (see
# scenarios/_ped/roadnet_min/provenance.txt).
#
# USAGE:
#   scripts/prep-ped-net.sh <in.net.xml> <out.net.xml>
#
# EXAMPLE:
#   scripts/prep-ped-net.sh /path/to/bare-city.net.xml scenarios/_ped/my-city/net.xml
#
# REQUIRES: `netconvert` on PATH (part of a SUMO install, e.g. `apt-get install -y sumo` or the
# pinned SUMO_VERSION build -- see CLAUDE.md "Environment bootstrapping"). Never assumed present in
# the offline `dotnet test` loop.

set -euo pipefail

usage() {
    echo "usage: $(basename "$0") <in.net.xml> <out.net.xml>" >&2
    echo "" >&2
    echo "  Runs netconvert --sidewalks.guess --crossings.guess --walkingareas.all-nonspecific" >&2
    echo "  over <in.net.xml> and writes a pedestrian-capable net to <out.net.xml>." >&2
    echo "" >&2
    echo "  DEV-SIDE TOOLING ONLY -- never invoked by 'dotnet test'. Commit the output as a" >&2
    echo "  scenario input (net.xml) if it is meant to persist; the VM running this script is" >&2
    echo "  ephemeral (CLAUDE.md)." >&2
    exit 1
}

if [[ $# -ne 2 ]]; then
    usage
fi

in_net="$1"
out_net="$2"

if [[ -z "$in_net" || -z "$out_net" ]]; then
    usage
fi

if [[ ! -f "$in_net" ]]; then
    echo "error: input net '$in_net' does not exist" >&2
    exit 1
fi

if ! command -v netconvert >/dev/null 2>&1; then
    echo "error: 'netconvert' not found on PATH -- install SUMO first" \
         "(e.g. 'apt-get install -y sumo'; see CLAUDE.md \"Environment bootstrapping\")." >&2
    exit 1
fi

out_dir="$(dirname "$out_net")"
if [[ -n "$out_dir" && "$out_dir" != "." ]]; then
    mkdir -p "$out_dir"
fi

echo "prep-ped-net: netconvert --sumo-net-file '$in_net' --sidewalks.guess --crossings.guess --walkingareas.all-nonspecific -o '$out_net'"

netconvert \
    --sumo-net-file "$in_net" \
    --sidewalks.guess \
    --crossings.guess \
    --walkingareas.all-nonspecific \
    -o "$out_net"

echo "prep-ped-net: wrote '$out_net'"
