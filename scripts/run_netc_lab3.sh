#!/usr/bin/env bash
# The fleet's lab-3 node on a SHARED multicast segment, asserted by a peer that is
# not a copy of it (@rt1180emulator's wire-check.py speaks the fleet's own protocol
# in a different language -- "a rehearsal whose other actors are copies of you
# cannot discover that you are wrong").
#
# ⚠ MULTICAST IS CORRECT HERE AND WOULD BE A BUG IN flood/portfwd: this lab IS a
# shared segment; a switched fabric on the same backend re-ingests its own flood.
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
ORACLE=/home/kyle/Documents/GitHub/rt1180emulator
RENODE=/home/kyle/.cache/renode/renode_1.17.0-portable/renode
OUT="$ROOT/results/netc-wire"; mkdir -p "$OUT"
TEST=imxrt1180-netc-lab3

cleanup() {
    [ -n "${rpid:-}" ] && kill "$rpid" 2>/dev/null
    pgrep -f "rt1180renode-lab3.resc" | while read -r p; do kill "$p" 2>/dev/null; done
}
trap cleanup EXIT

rm -f "$OUT"/lab3.txt* "$OUT"/lab3.uart.txt*
( cd "$(dirname "$RENODE")" && timeout -k 20 110 "$RENODE" --console --disable-xwt --plain \
    -e "include @/tmp/rt1180renode-lab3.resc" \
    -e "cpu.semihosting.semihosting_uart CreateFileBackend @$OUT/lab3.txt true" \
    -e "lpuart1 CreateFileBackend @$OUT/lab3.uart.txt true" \
    -e "sysbus LoadELF @$ORACLE/tests/$TEST/netc-lab3-0x88B6.elf false cpu" \
    -e 'emulation RunFor "45"' -e quit </dev/null ) > "$OUT/lab3.log" 2>&1 &
rpid=$!
sleep 12
timeout -k 10 30 python3 "$ORACLE/tests/$TEST/wire-check.py"; rc=$?
echo "--- guest console ---"
cat "$OUT/lab3.txt" "$OUT/lab3.uart.txt" 2>/dev/null | tr -d '\0\r' | tail -4
echo "--- wire hops: $(grep -cE 'switch -> UDP wire|UDP wire ->' "$OUT/lab3.log" 2>/dev/null) ---"
exit "$rc"
