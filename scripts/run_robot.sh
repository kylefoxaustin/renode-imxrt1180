#!/usr/bin/env bash
# Run the whole RT1180 suite under Renode's native test runner (Robot Framework).
# One resident Renode process for all cases -- ~15 s for the full suite versus
# ~3 min for the one-process-per-case shell runners.
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
RENODE_DIR="${RENODE_DIR:-$HOME/.cache/renode/renode_1.17.0-portable}"
VENV="${ROBOT_VENV:-$HOME/.cache/renode-test-venv}"
export PATH="$VENV/bin:$PATH"
mkdir -p "$HERE/results"
cd "$HERE" || exit 2
timeout -k 20 1800 "$RENODE_DIR/renode-test" -r "$HERE/results" "$HERE/tests/rt1180.robot" "$@"
rc=$?
mv -f "$HERE"/robot_output.xml "$HERE"/log.html "$HERE"/report.html "$HERE/results/" 2>/dev/null
exit $rc
