#!/usr/bin/env bash
# M2 (final): stock NXP SDK multicore_examples/rpmsg_lite_pingpong (sysbuild).
# EXTERNAL oracle, per the project brief: the TERMINAL DATA VALUE
#   "Message: Size=4, DATA = 101"
# and deliberately NOT the "RPMsg demo ends" banner -- the brief records that the
# failure form of this example also contains that substring, so banner-matching
# would produce a false PASS.
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
LOG="${1:-/tmp/rt1180renode-rpmsg.log}"
ORACLE='Message: Size=4, DATA = 101'

cd "$(dirname "$RENODE")" || exit 2
timeout -k 20 600 "$RENODE" --console --disable-xwt --plain \
    -e "include @$HERE/scripts/rt1180_rpmsg.resc" \
    -e 'emulation RunFor "10"' \
    -e "quit" > "$LOG" 2>&1
rc=$?

if grep -qF "$ORACLE" "$LOG"; then
    echo "RPMSG PASS — console contains the oracle string:"
    grep -F "$ORACLE" "$LOG" | tail -1
    echo "  ping-pong messages observed: $(grep -c 'Primary core received a msg' "$LOG")"
    exit 0
fi
echo "RPMSG FAIL — oracle string not found (renode rc=$rc)"
tail -6 "$LOG"
exit 1
