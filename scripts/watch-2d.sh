#!/usr/bin/env bash
# Watch the engine run in the 2D raylib desktop viewer (src/Sim.Viewer), on a real SUMO scenario.
#
# A thin wrapper over the viewer's strong net/scenario CLI (docs/VIEWERS.md). Needs a desktop/GPU;
# Sim.Viewer is out of Traffic.sln, so `dotnet run` builds it on first use (pulling the raylib native
# package). The viewer is NOT a NuGet package — it builds from this repo.
#
# Usage:
#   scripts/watch-2d.sh                                  # a default committed scenario (real demand)
#   scripts/watch-2d.sh scenarios/11-priority-junction   # any committed scenario dir  (real demand)
#   scripts/watch-2d.sh --sumocfg path/to/your.sumocfg   # any self-describing .sumocfg (real demand)
#   scripts/watch-2d.sh --net path/to/your.net.xml       # a bare network as a sandbox (ambient traffic)
#   scripts/watch-2d.sh <scenarioDir> --seconds 30 ...   # extra viewer flags pass straight through
#
# In-window: drag = pan · wheel = zoom · click a road = drop an obstacle · 'd' = diagnostics.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEFAULT_SCENARIO="scenarios/11-priority-junction"

# If the first arg is already one of the explicit selectors (or there are none), pass everything
# through verbatim; otherwise treat the first token as a scenario directory (the common case).
if [[ $# -eq 0 ]]; then
  set -- --scenario "$DEFAULT_SCENARIO"
elif [[ "$1" == --* ]]; then
  :  # caller supplied --scenario/--sumocfg/--net (and maybe more) themselves
else
  scenario="$1"; shift
  set -- --scenario "$scenario" "$@"
fi

exec dotnet run -c Release --project "$ROOT/src/Sim.Viewer" -- --mode local "$@"
