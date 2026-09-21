#!/usr/bin/env bash
# Drive @rt1180emulator's NETC wire tests against Renode with THEIR harness unchanged.
#
# ⭐ THE VERDICT IS RENDERED OUTSIDE THE GUEST, on frames the harness receives on the
# egress port's socket and byte-compares. That is the whole point: these are the
# PAYLOAD-CONSUMING tests, and they are the ones that would catch a regression of the
# EmitToWire-never-called bug -- a counter cannot tell "forwarded" from "accounted for".
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ORACLE="${ORACLE:-$HOME/Documents/GitHub/rt1180emulator}"
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
TEST="${1:-imxrt1180-netc-flood}"
PY="${2:-flood.py}"
OUT="$HERE/results/netc-wire"; mkdir -p "$OUT"

elf=$(ls "$ORACLE/tests/$TEST"/*.elf 2>/dev/null | head -1)
[ -n "$elf" ] || { echo "no elf for $TEST"; exit 1; }

# Nothing else may hold the wire sockets.
for p in 44001 44002 44011 44012 44021 44022; do
    if ss -lun 2>/dev/null | grep -q ":$p "; then
        echo "REFUSING: udp/$p is already bound -- a stale run would silently split the wires."
        exit 1
    fi
done

( cd "$(dirname "$RENODE")" && timeout -k 20 120 "$RENODE" --console --disable-xwt --plain \
    -e "include @/tmp/rt1180renode-wire3.resc" \
    -e "cpu.semihosting.semihosting_uart CreateFileBackend @$OUT/$TEST.txt true" \
    -e "sysbus LoadELF @$elf false cpu" \
    -e 'emulation RunFor "30"' -e quit </dev/null ) > "$OUT/$TEST.log" 2>&1 &
rpid=$!
sleep 6                                   # let the guest bring the rings up
python3 "$ORACLE/tests/$TEST/$PY"; rc=$?
# ⚠ KILL THE EMULATOR, NOT THE SUBSHELL. `kill $!` on a `( ... ) &` reaps the
# subshell and leaves renode holding the wire sockets, so the NEXT run binds
# nothing and reads as a forwarding failure. The bind guard above caught exactly
# that, which is the only reason it was not mistaken for a model bug.
pkill -f "rt1180renode-wire3.resc" 2>/dev/null
kill "$rpid" 2>/dev/null
wait "$rpid" 2>/dev/null
for _ in 1 2 3 4 5 6 7 8 9 10; do
    ss -lun 2>/dev/null | grep -qE ":4400[12] |:4401[12] |:4402[12] " || break
    sleep 1
done
echo "--- guest console ---"
tr -d '\0\r' < "$OUT/$TEST.txt" 2>/dev/null | tail -4
exit "$rc"
