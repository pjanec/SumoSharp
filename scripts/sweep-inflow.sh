#!/usr/bin/env bash
# A3/SC4 (docs/DENSITY-DIFF-HARNESS-TASKS.md): sweep OPEN-LOOP inflow to find, for each engine, the highest
# rate at which it still reaches steady state. That rate IS "max sustainable density" -- the number the
# high-density calibration workstream cannot currently obtain for us, because at every inflow it tries our
# resident count runs away.
#
# For each inflow: run OUR engine open-loop (recording the demand), then replay that SAME demand into SUMO's
# two columns, then classify all three as STEADY or RUNAWAY on the same rule (mean resident over the last
# quarter vs the quarter before it, 5% tolerance).
#
# ⚠ NOT part of the offline test loop -- it invokes SUMO (design §5).
set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
OUT="${1:-}"
shift || true
INFLOWS=("$@")
if [[ -z "$OUT" || ${#INFLOWS[@]} -eq 0 ]]; then
    echo "usage: sweep-inflow.sh OUTDIR RATE [RATE...]     e.g. sweep-inflow.sh /tmp/sweep 1.2 1.6 2.0" >&2
    exit 2
fi
command -v sumo >/dev/null 2>&1 || { echo "FATAL: sumo not on PATH." >&2; exit 1; }

STEPS="${STEPS:-7200}"
NET="$REPO_ROOT/scenarios/_ped/demo_city/box/net.xml"
mkdir -p "$OUT"
REPORT="$OUT/sweep-report.txt"
: > "$REPORT"

{
    echo "# A3 open-loop inflow sweep -- max sustainable density per engine"
    echo "# steps=$STEPS  net=$NET"
    echo "# demand model = OPEN-LOOP (fixed inflow, occupancy cap IGNORED). A capacity claim from"
    echo "# closed-loop demand would be invalid -- see DENSITY-DIFF-HARNESS-DESIGN.md section 1b."
    echo
    printf '%-8s | %-28s | %-28s | %s\n' "inflow" "OURS" "SUMO s-honest" "SUMO s-default"
    printf '%-8s-+-%-28s-+-%-28s-+-%s\n' "--------" "----------------------------" "----------------------------" "----------------------------"
} >> "$REPORT"

for RATE in "${INFLOWS[@]}"; do
    TAG="in${RATE}"
    ROU="$OUT/$TAG.rou.xml"
    CSV="$OUT/$TAG.ours.csv"

    echo "=== inflow $RATE : our engine ===" >&2
    OURS=$(dotnet run -c Release --no-build --project "$REPO_ROOT/src/Sim.DensityDiff" -- \
             --inflow "$RATE" --steps "$STEPS" --out "$ROU" --series "$CSV" 2>&1 \
           | grep -E "VERDICT|resident at horizon|arrived total" || true)
    OURS_V=$(echo "$OURS" | grep VERDICT | sed 's/.*VERDICT *: *//' | cut -c1-26)
    OURS_R=$(echo "$OURS" | grep "resident at horizon" | grep -oE '[0-9]+$' || echo "?")
    OURS_A=$(echo "$OURS" | grep "arrived total" | grep -oE '[0-9]+$' || echo "?")

    echo "=== inflow $RATE : SUMO on the same demand ===" >&2
    "$REPO_ROOT/scripts/run-density-diff.sh" --net "$NET" --routes "$ROU" \
        --out "$OUT/$TAG.sumo" --steps "$STEPS" >/dev/null 2>&1 || {
            echo "WARNING: sumo failed at inflow $RATE" >&2; }

    # Same steady-state rule as the driver, applied to SUMO's own per-step `running` count.
    read -r HON DEF < <(python3 - "$OUT/$TAG.sumo" <<'PY'
import re, sys
base = sys.argv[1]
out = []
for col in ("s-honest", "s-default"):
    try:
        t = open(f"{base}/{col}/summary.xml").read()
    except OSError:
        out.append("NO-DATA"); continue
    rows = [int(m.group(1)) for m in re.finditer(r'running="(\d+)"', t)]
    if len(rows) < 4:
        out.append("SHORT"); continue
    n = len(rows); q = n // 4
    em = sum(rows[n-2*q:n-q]) / q
    lm = sum(rows[n-q:]) / len(rows[n-q:])
    g = (lm - em) / em * 100 if em else 0.0
    out.append(f"{'RUNAWAY' if g > 5 else 'STEADY'}({lm:.0f},{g:+.1f}%)")
print(" ".join(out))
PY
    )

    printf '%-8s | %-28s | %-28s | %s\n' \
        "$RATE" "$OURS_V (r=$OURS_R,a=$OURS_A)" "${HON:-?}" "${DEF:-?}" >> "$REPORT"
    echo "  -> ours: $OURS_V (resident=$OURS_R arrived=$OURS_A) | s-honest: $HON | s-default: $DEF" >&2
done

{
    echo
    echo "# READING THIS: the highest inflow whose row says STEADY is that engine's max sustainable"
    echo "# density. A row where OURS is RUNAWAY while SUMO is STEADY is a DRAIN DEFICIT at that inflow --"
    echo "# same demand, same net, and SUMO holds an equilibrium we cannot."
} >> "$REPORT"

cat "$REPORT"
