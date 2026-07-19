#!/usr/bin/env bash
# GAP-1: publish the `sumosharp` drop-in binary as a warm, self-contained single-file exe.
#
# Why self-contained/single-file and NOT `dotnet run --project`: SumoData's calibration invokes the
# engine ~7 density probes + a serve + a verify PER BOX, so per-call `dotnet run` JIT/build startup
# would dominate wall-clock. A prebuilt exe (or `dotnet sumosharp.dll` against a prebuilt publish
# dir) keeps per-invocation cost to process start. Point SumoData's SUMO_BINARY env at the produced
# binary.
#
# Usage:
#   scripts/publish-sumosharp.sh [RID] [OUTDIR]
# Defaults: RID = linux-x64, OUTDIR = <repo>/artifacts/sumosharp/<RID>
# Then:   export SUMO_BINARY="<OUTDIR>/sumosharp"
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
RID="${1:-linux-x64}"
OUTDIR="${2:-$ROOT/artifacts/sumosharp/$RID}"

dotnet publish "$ROOT/src/Sim.Sumo/Sim.Sumo.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:InvariantGlobalization=true \
    -o "$OUTDIR"

echo
echo "published sumosharp -> $OUTDIR/sumosharp"
echo "point SumoData at it with:  export SUMO_BINARY=\"$OUTDIR/sumosharp\""
