#!/usr/bin/env bash
# City3D demo — the two-process REMOTE (DDS) round-trip (task T2.2b).
#
# docs/DEMO-CITY3D-DESIGN.md "Data path -> Remote mode": a separate `src/Sim.Host.App --transport dds`
# process steps a scenario and publishes geometry+frames+TL over CycloneDDS; the Viewer, built with
# -p:City3DRemote=true, subscribes (DdsSubscriber) and runs the SAME reconstruction/render code local mode
# uses (only the IReplicationSource + ILaneShapeSource differ -- see Main.cs's RenderFrame).
#
# Steps: (a) pack the local feed with --remote (adds SumoSharp.Replication.Dds); (b) build the Viewer with
# DDS support; (c) launch Sim.Host.App in the background, publishing scenarios/09-traffic-light over DDS;
# (d) run the Viewer headless with --transport=dds (Xvfb + software GL only if --shot is requested -- a
# plain --headless dummy-renderer run is enough to prove the data path); (e) grep the viewer's own log for
# "received geometry" + a non-zero vehicle count, kill the host, report PASS/FAIL.
#
# HONESTY NOTE (docs/DEMO-CITY3D-DESIGN.md "What is verified where"): CycloneDDS cross-process discovery
# uses UDP multicast even on loopback, which may or may not be permitted in a given sandboxed container.
# This script does not fake success -- if the viewer never logs the two success markers within the
# wall-clock timeout below, it reports FAIL and tells you to check for a multicast/network limitation
# rather than a code bug (see the design doc's fallback verification path: an in-process
# publisher<->DdsSubscriber check, e.g. src/Sim.Viewer's own LoopbackSelfTest pattern).
#
# WALL-CLOCK, not frame-count: Sim.Host.App paces itself in REAL time (--hz step interval + a ~500ms
# post-geometry discovery settle), which a headless Godot dummy-render loop (which otherwise burns through
# hundreds of frames in a fraction of a real second -- see run-smoke.sh's own comment) knows nothing about.
# Main.cs's own frame-count auto-quit is LOCAL-ONLY for exactly this reason (see _Process); the dds
# transport here is bounded by an external `timeout` instead, so the process has actually-elapsed real
# seconds for DDS discovery + publishing to happen in.
#
# Usage:
#   demos/City3D/run-remote.sh                                  # default scenario, no screenshot
#   demos/City3D/run-remote.sh --shot=/tmp/city3d-remote.png     # also capture a screenshot (Xvfb + software GL)
#   demos/City3D/run-remote.sh --host-seconds=60 --viewer-seconds=20
#
# Requires: .NET 8 SDK on PATH; network access for fetch-godot.sh (ephemeral Godot binary) and the
# CycloneDDS.NET NuGet restore (also ephemeral); Godot 4 (.NET/mono).
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
DEMO="$ROOT/demos/City3D"
VIEWER="$DEMO/Viewer"
HOST_PROJECT="$ROOT/src/Sim.Host.App"
SCENARIO_REL="scenarios/09-traffic-light"

HOST_SECONDS=40
VIEWER_SECONDS=20
SHOT=""
for arg in "$@"; do
  case "$arg" in
    --shot=*) SHOT="${arg#--shot=}" ;;
    --host-seconds=*) HOST_SECONDS="${arg#--host-seconds=}" ;;
    --viewer-seconds=*) VIEWER_SECONDS="${arg#--viewer-seconds=}" ;;
    *) echo "unknown arg: $arg" >&2; exit 2 ;;
  esac
done

echo "==> [1/5] packing the local NuGet feed (--remote: adds SumoSharp.Replication.Dds)"
bash "$DEMO/build.sh" --remote --pack-only

echo "==> [2/5] building the Viewer with DDS support (-p:City3DRemote=true)"
dotnet build "$VIEWER" -c Debug -p:City3DRemote=true

echo "==> [3/5] building Sim.Host.App"
dotnet build "$HOST_PROJECT" -c Release

echo "==> [4/5] resolving the Godot 4 (.NET/mono) engine binary"
GODOT_BIN="$("$DEMO/fetch-godot.sh" | tail -1)"
echo "    godot binary: $GODOT_BIN"

HOST_LOG="$(mktemp)"
VIEWER_LOG="$(mktemp)"
HOST_PID=""

cleanup() {
  if [[ -n "$HOST_PID" ]] && kill -0 "$HOST_PID" 2>/dev/null; then
    kill "$HOST_PID" 2>/dev/null || true
    wait "$HOST_PID" 2>/dev/null || true
  fi
  rm -f "$HOST_LOG" "$VIEWER_LOG"
}
trap cleanup EXIT

echo "==> starting Sim.Host.App in the background: scenario=$SCENARIO_REL transport=dds seconds=$HOST_SECONDS"
dotnet run --no-build --project "$HOST_PROJECT" -c Release -- \
  --scenario "$ROOT/$SCENARIO_REL" --transport dds --seconds "$HOST_SECONDS" \
  > "$HOST_LOG" 2>&1 &
HOST_PID=$!

# Give the host a moment to actually start (process spawn + DDS participant init) before the subscriber
# comes up -- not load-bearing for correctness (DDS discovery is async either way), just avoids racing the
# subscriber's own participant construction against the host process not existing yet.
sleep 1

echo "==> [5/5] running the Viewer with --transport=dds for up to ${VIEWER_SECONDS}s of real wall-clock time"
GODOT_ARGS=(--path "$VIEWER")
USER_ARGS=(--transport=dds)
if [[ -n "$SHOT" ]]; then
  USER_ARGS+=(--shot="$SHOT" --shot-delay=3.0)
fi

set +e
if [[ -n "$SHOT" ]]; then
  timeout "${VIEWER_SECONDS}s" env LIBGL_ALWAYS_SOFTWARE=1 GALLIUM_DRIVER=llvmpipe \
    xvfb-run -a -s "-screen 0 1600x900x24" \
    "$GODOT_BIN" "${GODOT_ARGS[@]}" --rendering-driver opengl3 --resolution 1600x900 \
    -- "${USER_ARGS[@]}" > "$VIEWER_LOG" 2>&1
else
  timeout "${VIEWER_SECONDS}s" "$GODOT_BIN" --headless "${GODOT_ARGS[@]}" -- "${USER_ARGS[@]}" \
    > "$VIEWER_LOG" 2>&1
fi
VIEWER_STATUS=$?
set -e

echo "----- Sim.Host.App log (tail) -----"
tail -20 "$HOST_LOG" || true
echo "----- Viewer log (tail) -----"
tail -40 "$VIEWER_LOG" || true

# `timeout` exits 124 when it had to kill the process -- expected here (the dds transport has no internal
# frame-count auto-quit, see Main.cs's _Process; the external timeout above IS its quit mechanism), not a
# failure on its own. Any OTHER non-zero status is a real Godot-side failure.
if [[ $VIEWER_STATUS -ne 0 && $VIEWER_STATUS -ne 124 ]]; then
  echo "FAIL: godot exited with unexpected status $VIEWER_STATUS"
  exit 1
fi

FAIL=0
if ! grep -q 'transport=dds active' "$VIEWER_LOG"; then
  echo "FAIL: viewer never reported --transport=dds active"
  FAIL=1
fi

if ! grep -qE 'Main: --transport=dds received geometry \([1-9][0-9]* lane' "$VIEWER_LOG"; then
  echo "FAIL: viewer never logged received geometry"
  FAIL=1
fi

if ! grep -qE 'Main: frame=[0-9]+ simTime=.* vehicles=[1-9]' "$VIEWER_LOG"; then
  echo "FAIL: viewer never logged a non-zero vehicle count"
  FAIL=1
fi

if [[ $FAIL -ne 0 ]]; then
  echo "FAIL: remote (DDS) round-trip did not complete within ${VIEWER_SECONDS}s -- see logs above."
  echo "      CycloneDDS cross-process discovery needs UDP multicast, even on loopback; some sandboxed"
  echo "      containers disable it. If that's the symptom here, this is a documented environment"
  echo "      limitation (docs/DEMO-CITY3D-DESIGN.md 'What is verified where'), not a code defect --"
  echo "      verify the remote data path another way (e.g. an in-process publisher<->DdsSubscriber check)"
  echo "      instead of trusting this script's exit code alone."
  exit 1
fi

echo "PASS: remote (DDS) round-trip OK -- the Viewer (separate process) received geometry and rendered vehicles published by Sim.Host.App (another separate process)."
