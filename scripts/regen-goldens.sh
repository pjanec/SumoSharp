#!/usr/bin/env bash
#
# regen-goldens.sh
# ----------------
# (Re)generates the committed golden test data for every scenario, from the pinned
# SUMO version. This is the ONLY thing that needs SUMO. Run it deliberately, on a
# network-enabled VM, then COMMIT the produced goldens.
#
# For each scenario directory under /scenarios that contains a config.sumocfg it
# produces, next to the inputs:
#   golden.fcd.xml     -- full trajectory dump (the behavioral ground truth)
#   golden.state.xml   -- fully-resolved vehicle/vType parameters at t=1
#                         (the initialization ground truth: catches vType-default
#                          bugs directly instead of via drifting trajectories)
#   provenance.txt     -- SUMO version, exact command, date, input file hashes
#                         (so goldens are trustworthy and staleness is detectable)
#
# DETERMINISM: goldens for phase-1 parity are generated with randomness stripped.
# Each scenario's config is expected to set sigma=0, fixed depart, Euler stepping,
# and teleport off. This script does not override the config; it trusts the
# committed scenario. See DESIGN.md "determinism ladder".
#
# The VM is volatile: the SUMO install here does not persist and does not need to.
# The committed goldens carry all ground truth forward to the offline test loop.

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

# shellcheck disable=SC1091
source "$REPO_ROOT/SUMO_VERSION"
: "${SUMO_VERSION:?Set SUMO_VERSION in $REPO_ROOT/SUMO_VERSION}"

# Step 1: ensure SUMO is present (volatile install).
"$REPO_ROOT/scripts/install-sumo.sh"

SCENARIOS_DIR="$REPO_ROOT/scenarios"
if [[ ! -d "$SCENARIOS_DIR" ]]; then
  echo "ERROR: no scenarios directory at $SCENARIOS_DIR" >&2
  exit 1
fi

# Portable-ish sha256 helper.
hash_file() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

STATE_TIME="${STATE_TIME:-1}"   # step at which to dump resolved parameters

# FCD output precision (decimal places). SUMO's default is 2, which is COARSER than the
# per-scenario parity tolerance (e.g. 1e-3) and would make the golden a lossy, 2-decimal
# truncation of SUMO's full-precision internal trajectory -- capping real parity sensitivity
# at ~5e-3 no matter what tolerance.json says. We raise it well above the tolerance so the
# committed golden carries enough digits for the tolerance to be a genuine bar. The engine
# emits full double precision and must NOT round to match a coarse golden.
FCD_PRECISION="${FCD_PRECISION:-6}"
GENERATED_ANY=0

# A scenario is any directory containing config.sumocfg.
while IFS= read -r -d '' CFG; do
  SCEN_DIR="$(dirname "$CFG")"
  SCEN_NAME="$(basename "$SCEN_DIR")"
  echo "==> Scenario: ${SCEN_NAME}"

  FCD_OUT="$SCEN_DIR/golden.fcd.xml"
  STATE_OUT="$SCEN_DIR/golden.state.xml"
  PROV_OUT="$SCEN_DIR/provenance.txt"

  # FCD includes lane-relative pos + speed AND global x/y/angle so the harness can
  # compare at either fidelity (see DESIGN.md "layered comparison metric").
  SUMO_CMD=(sumo
    -c "$CFG"
    --fcd-output "$FCD_OUT"
    --fcd-output.acceleration
    --precision "$FCD_PRECISION"
    --save-state.times "$STATE_TIME"
    --save-state.files "$STATE_OUT"
    --no-step-log true
  )

  echo "    ${SUMO_CMD[*]}"
  ( cd "$SCEN_DIR" && "${SUMO_CMD[@]}" )

  # Provenance: what produced these goldens, so a future reader can trust/reproduce
  # them and detect staleness (input changed but goldens not regenerated).
  {
    echo "sumo_version=${SUMO_VERSION}"
    echo "sumo_version_reported=$(sumo --version 2>&1 | head -n 1)"
    echo "generated_utc=$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
    echo "command=${SUMO_CMD[*]}"
    echo "state_time=${STATE_TIME}"
    echo "# input file hashes (sha256):"
    for f in "$SCEN_DIR"/*.net.xml "$SCEN_DIR"/*.rou.xml "$SCEN_DIR"/*.sumocfg; do
      [[ -e "$f" ]] || continue
      echo "input=$(basename "$f") sha256=$(hash_file "$f")"
    done
  } > "$PROV_OUT"

  echo "    wrote: $(basename "$FCD_OUT"), $(basename "$STATE_OUT"), $(basename "$PROV_OUT")"
  GENERATED_ANY=1
done < <(find "$SCENARIOS_DIR" -name config.sumocfg -print0 | sort -z)

if [[ "$GENERATED_ANY" -eq 0 ]]; then
  echo "WARNING: no scenarios with config.sumocfg found under $SCENARIOS_DIR." >&2
fi

echo
echo "==> Done. Review diffs, then COMMIT the golden files:"
echo "      git add scenarios/**/golden.fcd.xml scenarios/**/golden.state.xml scenarios/**/provenance.txt"
echo "      git commit -m 'Regenerate goldens (SUMO ${SUMO_VERSION})'"
