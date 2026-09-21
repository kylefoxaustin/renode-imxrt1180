#!/usr/bin/env bash
# M0 verification: stock NXP SDK demo_apps/hello_world (cm33) must print the
# EXTERNAL oracle string "hello world." -- the same oracle the QEMU scorecard
# uses. Exit 0 = PASS.
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
LOG="${1:-/tmp/rt1180renode-m0.log}"
ORACLE='hello world.'

cd "$(dirname "$RENODE")" || exit 2
timeout -k 5 240 "$RENODE" --console --disable-xwt --plain \
    -e "include @$HERE/scripts/rt1180_hello.resc" \
    -e 'emulation RunFor "0.5"' \
    -e "quit" > "$LOG" 2>&1
rc=$?

if grep -qF "$ORACLE" "$LOG"; then
    echo "M0 PASS — console contains the oracle string: '$ORACLE'"
    grep -F "$ORACLE" "$LOG" | tail -1
    exit 0
fi
echo "M0 FAIL — oracle string '$ORACLE' not found (renode rc=$rc)"
echo "last PC / tail:"; tail -5 "$LOG"
exit 1
