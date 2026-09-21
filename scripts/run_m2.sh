#!/usr/bin/env bash
# M2 verification: stock NXP SDK multicore_examples/multicore_manager (sysbuild).
# EXTERNAL oracle, same string the QEMU scorecard uses:
#   "The secondary core application has been started."
# That line prints only after the M7 boots its own image, completes the MCMGR
# startup-data handshake over the MU, and triggers the app event back to the M33.
# Exit 0 = PASS.
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
LOG="${1:-/tmp/rt1180renode-m2.log}"
ORACLE='The secondary core application has been started.'

cd "$(dirname "$RENODE")" || exit 2
timeout -k 20 600 "$RENODE" --console --disable-xwt --plain \
    -e "include @$HERE/scripts/rt1180_m2.resc" \
    -e 'emulation RunFor "8"' \
    -e "quit" > "$LOG" 2>&1
rc=$?

if grep -qF "$ORACLE" "$LOG"; then
    echo "M2 PASS — console contains the oracle string:"
    grep -F "$ORACLE" "$LOG" | tail -1
    exit 0
fi
echo "M2 FAIL — oracle string not found (renode rc=$rc)"
tail -6 "$LOG"
exit 1
