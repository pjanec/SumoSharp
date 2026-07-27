#!/usr/bin/env bash
# A1 (docs/DENSITY-DIFF-HARNESS-TASKS.md) -- run SUMO on one net+demand in the THREE configurations of
# docs/DENSITY-DIFF-HARNESS-DESIGN.md §1.
#
# WHY THREE AND NOT TWO. SUMO's SHIPPED DEFAULTS ARE THE CHEATING: time-to-teleport=300 (teleports a car
# stuck 5 min), collision.action=teleport (resolves a collision BY teleporting), and
# collision.check-junctions=false (junction interpenetration is not even DETECTED). So "match SUMO" is the
# wrong target -- it would import exactly the artefacts docs/CONSTRAINT-high-realism-artefact-ladder.md
# forbids, including rung 4, which the owner has ruled out unconditionally in high-realism areas.
#
#   S-default  SUMO as shipped.        Upper bound, NOT a target.
#   S-honest   cheats disabled.        THE TARGET -- SUMO playing by our high-realism rules.
#
#   (S-default - S-honest) = SUMO's CHEAT DIVIDEND  -> never chase this.
#   (S-honest  - ours)     = the REAL work list.
#
# ⚠ THIS SCRIPT IS NOT PART OF THE OFFLINE TEST LOOP. It invokes SUMO. Per CLAUDE.md `dotnet test` must pass
# on a fresh VM with no SUMO present, so nothing in tests/ may call this -- committed report files only, the
# same discipline as golden regeneration.
set -euo pipefail

usage() {
    cat >&2 <<'USAGE'
usage: run-density-diff.sh --net NET.xml --routes ROUTES.rou.xml --out DIR [--steps N] [--add ADD.xml]

  --net     SUMO network
  --routes  route file (from the B1 demand recorder, so both engines see the same cars)
  --out     output directory (created; one subdir per column)
  --steps   simulation steps (default 7200) -- with --step-length 0.5 that is one simulated hour
  --add     optional additional-files (e.g. the A2 detector file, pedestrian demand)
USAGE
    exit 2
}

NET=""; ROUTES=""; OUT=""; STEPS=7200; ADD=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --net) NET="${2:-}"; shift 2 ;;
        --routes) ROUTES="${2:-}"; shift 2 ;;
        --out) OUT="${2:-}"; shift 2 ;;
        --steps) STEPS="${2:-}"; shift 2 ;;
        --add) ADD="${2:-}"; shift 2 ;;
        -h|--help) usage ;;
        *) echo "unknown argument: $1" >&2; usage ;;
    esac
done
[[ -n "$NET" && -n "$ROUTES" && -n "$OUT" ]] || usage

# SC4: fail LOUDLY when SUMO is absent. A silent skip that still exits 0 would report success for a
# measurement that never ran -- the failure mode this whole workstream keeps being bitten by.
if ! command -v sumo >/dev/null 2>&1; then
    echo "FATAL: sumo not on PATH. This script is the network-enabled side of the workflow;" >&2
    echo "       it is deliberately NOT part of 'dotnet test'. Install SUMO ${SUMO_VERSION:-1.20.0} first." >&2
    exit 1
fi
for f in "$NET" "$ROUTES"; do
    [[ -f "$f" ]] || { echo "FATAL: no such file: $f" >&2; exit 1; }
done

SUMO_HAVE=$(sumo --version 2>&1 | head -1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1 || true)
# Resolve the repo root from THIS SCRIPT's location, not the caller's cwd -- the harness is routinely run
# from a scratch directory, where `git rev-parse` fails.
REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# SUMO_VERSION is a COMMENTED file, so it must be parsed, not slurped -- a bare `tr -d` pulls the entire
# comment block in and the version check then always "mismatches" with unreadable output.
SUMO_WANT=$(grep -oE '^[[:space:]]*(SUMO_VERSION=)?[0-9]+\.[0-9]+\.[0-9]+[[:space:]]*$' "$REPO_ROOT/SUMO_VERSION" 2>/dev/null \
            | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1 || true)
if [[ -z "$SUMO_WANT" ]]; then
    SUMO_WANT=$(grep -oE 'SUMO_VERSION=[0-9]+\.[0-9]+\.[0-9]+' "$REPO_ROOT/SUMO_VERSION" 2>/dev/null \
                | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1 || true)
fi
if [[ -n "$SUMO_WANT" && "$SUMO_HAVE" != "$SUMO_WANT" ]]; then
    echo "WARNING: sumo $SUMO_HAVE does not match SUMO_VERSION $SUMO_WANT -- results are not comparable" >&2
    echo "         to committed goldens or to earlier reports in this workstream." >&2
fi

mkdir -p "$OUT"
STEP_LENGTH=0.5
END=$(python3 -c "print($STEPS * $STEP_LENGTH)")

# The SHARED options -- everything that is NOT a cheat control. Both columns get exactly this, which is what
# makes SC2 (the two configs differ in only the four cheat flags) hold by construction rather than by
# inspection.
#
# Phase-1 determinism per CLAUDE.md: sigma comes from the vTypes in the route file, and --step-method.ballistic
# is deliberately NOT passed (we use Euler). Nothing here randomises.
write_cfg() {
    local cfg="$1" column="$2"
    {
        echo '<configuration>'
        echo '  <input>'
        echo "    <net-file value=\"$(realpath "$NET")\"/>"
        echo "    <route-files value=\"$(realpath "$ROUTES")\"/>"
        [[ -n "$ADD" ]] && echo "    <additional-files value=\"$(realpath "$ADD")\"/>"
        echo '  </input>'
        echo '  <time>'
        echo '    <begin value="0"/>'
        echo "    <end value=\"$END\"/>"
        echo "    <step-length value=\"$STEP_LENGTH\"/>"
        echo '  </time>'
        echo '  <processing>'
        echo '    <default.action-step-length value="0"/>'
        echo '    <lateral-resolution value="0"/>'
        # The four CHEAT CONTROLS -- the ONLY elements that differ between the two columns (SC2).
        if [[ "$column" == "s-honest" ]]; then
            echo '    <time-to-teleport value="-1"/>'
            echo '    <time-to-teleport.highways value="-1"/>'
            # `warn` not `none`: warn still records to collision-output WITHOUT teleporting, so SUMO's
            # collisions are COUNTED rather than hidden. `none` would suppress the record we need.
            echo '    <collision.action value="warn"/>'
            echo '    <collision.check-junctions value="true"/>'
        fi
        echo '  </processing>'
        echo '  <report>'
        echo '    <no-step-log value="true"/>'
        echo '    <duration-log.statistics value="true"/>'
        echo '  </report>'
        echo '</configuration>'
    } > "$cfg"
}

run_column() {
    local column="$1"
    local dir="$OUT/$column"
    mkdir -p "$dir"
    local cfg="$dir/run.sumocfg"
    write_cfg "$cfg" "$column"

    echo "=== $column ==="
    # Outputs are given on the command line rather than in the cfg so the two cfg files stay diffable down
    # to the four cheat elements (SC2) -- output paths necessarily differ per column.
    sumo -c "$cfg" \
        --tripinfo-output "$dir/tripinfo.xml" \
        --summary-output "$dir/summary.xml" \
        --statistic-output "$dir/statistic.xml" \
        --collision-output "$dir/collisions.xml" \
        > "$dir/stdout.log" 2> "$dir/stderr.log" || {
            echo "FATAL: sumo failed for column $column; see $dir/stderr.log" >&2
            tail -20 "$dir/stderr.log" >&2
            exit 1
        }

    local teleports collisions arrived
    teleports=$(grep -oE 'teleports="[0-9]+"' "$dir/statistic.xml" 2>/dev/null | head -1 | grep -oE '[0-9]+' || echo "0")
    collisions=$(grep -oE 'collisions="[0-9]+"' "$dir/statistic.xml" 2>/dev/null | head -1 | grep -oE '[0-9]+' || echo "0")
    arrived=$(grep -oE 'inserted="[0-9]+"' "$dir/statistic.xml" 2>/dev/null | head -1 | grep -oE '[0-9]+' || echo "0")
    echo "    inserted=$arrived teleports=$teleports collisions=$collisions"
    echo "$column inserted=$arrived teleports=$teleports collisions=$collisions" >> "$OUT/summary.txt"
}

: > "$OUT/summary.txt"
run_column "s-default"
run_column "s-honest"

# SC2, asserted rather than trusted: the two configs must differ in EXACTLY the four cheat elements. If they
# differ anywhere else the cheat-dividend decomposition is invalid, because the margin between the columns
# would no longer be attributable to the cheats alone.
DIFF_LINES=$(diff <(grep -oE '<[a-z.-]+ value' "$OUT/s-default/run.sumocfg") \
                  <(grep -oE '<[a-z.-]+ value' "$OUT/s-honest/run.sumocfg") | grep -cE '^[<>]' || true)
echo "cfg element diff count: $DIFF_LINES (expected 4)" | tee -a "$OUT/summary.txt"
if [[ "$DIFF_LINES" != "4" ]]; then
    echo "FATAL: the two columns differ in $DIFF_LINES config elements, not 4. The cheat-dividend" >&2
    echo "       decomposition is only valid if the ONLY difference is the four cheat controls." >&2
    exit 1
fi

echo
echo "wrote $OUT -- see summary.txt"
