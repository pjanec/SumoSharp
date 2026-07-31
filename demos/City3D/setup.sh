#!/usr/bin/env bash
# City3D demo — one-shot PREPARE for a fresh checkout.
#
# Verifies prerequisites, fetches the Godot (.NET) editor, packs the local SumoSharp package feed, and
# builds the viewer — so a subsequent `./run-local.sh` launches instantly. Idempotent: re-running is
# cheap (Godot fetch and feed are skipped/refreshed as needed). Nothing here is committed to the repo;
# the Godot editor lives outside the tree (see fetch-godot.sh) and the feed is git-ignored.
#
# Usage:
#   demos/City3D/setup.sh            # prepare the local (co-hosted) demo
#   demos/City3D/setup.sh --remote   # also pack SumoSharp.Dds for the remote/DDS split
#
# After it prints READY:
#   demos/City3D/run-local.sh        # interactive window (or headless xvfb if no DISPLAY)
#   demos/City3D/run-remote.sh       # the 2-process remote/DDS demo (needs --remote here)
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
DEMO="$ROOT/demos/City3D"
REMOTE=""
[[ "${1:-}" == "--remote" ]] && REMOTE="--remote"

# ---- prerequisites -------------------------------------------------------------------------------
# .NET 8 SDK builds everything; curl+unzip fetch/extract the Godot editor; for a HEADLESS box (no
# DISPLAY) we also need Xvfb + a software-GL (mesa/llvmpipe) stack so Godot can open a GL context.
need() { command -v "$1" >/dev/null 2>&1; }
miss=()
need dotnet || miss+=("dotnet-sdk-8.0")
need curl   || miss+=("curl")
need unzip  || miss+=("unzip")
if [[ -z "${DISPLAY:-}" ]]; then
  need xvfb-run || miss+=("xvfb")
fi
if (( ${#miss[@]} )); then
  echo "ERROR: missing prerequisites: ${miss[*]}" >&2
  echo "Install them (Debian/Ubuntu):" >&2
  echo "  sudo apt-get update && sudo apt-get install -y ${miss[*]} libgl1 libglu1-mesa" >&2
  echo "(.NET 8 SDK: https://dotnet.microsoft.com/download or your distro's dotnet-sdk-8.0 package.)" >&2
  exit 1
fi

# ---- prepare -------------------------------------------------------------------------------------
echo "==> [1/3] fetching the Godot (.NET) editor  (~100 MB, ephemeral; skipped if already present)"
"$DEMO/fetch-godot.sh" >/dev/null

echo "==> [2/3] packing the local SumoSharp package feed${REMOTE:+ (with SumoSharp.Dds)}"
bash "$DEMO/build.sh" --pack-only $REMOTE >/dev/null

echo "==> [3/3] building the Godot viewer (Debug — what the editor loads at runtime)"
dotnet build "$DEMO/Viewer" -c Debug >/dev/null

echo ""
echo "READY. Watch it run with:"
echo "  demos/City3D/run-local.sh                          # default scenario, interactive/headless"
echo "  demos/City3D/run-local.sh --scenario=_bench/city-mixed-1k"
[[ -n "$REMOTE" ]] && echo "  demos/City3D/run-remote.sh                         # 2-process remote/DDS split"
