#!/usr/bin/env bash
# Read the STACKED RETURN ADDRESS out of the exception frame on PRE-FIX tlib.
#
# ⭐ ONE FIELD, READ ON BOTH SIDES. @rt1180emulator measured QEMU's stacked PC as
# 0xffe0102 -- the WFI itself -- via the resumed pc after a successful exception
# return. On pre-fix tlib the core parks AT handler entry with the frame intact, so
# the same field is directly readable:
#
#   frame layout: R0 R1 R2 R3 R12 LR PC xPSR  ->  PC at sp+24
#   measured sp at the park: 0x2001ffe0       ->  stacked PC at 0x2001fff8
#
#   0xffe0104 (after the wfi) -> tlib stacks the ARM-correct return; #14 is purely
#                                the sleep-state bug, and the two cores have TWO
#                                DIFFERENT bugs in one window.
#   0xffe0102 (the wfi)       -> tlib has the SAME deviation as QEMU and #14 was
#                                MASKING it: one shared bug, plus a tlib-only
#                                sleep-in-handler bug stacked on top.
#
# I do not know which, and that is the point of reading it.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
LIB="$HOME/.cache/renode/renode_1.17.0-portable/platform-lib/linux-x64/translate-arm-m-le.so"
INSTALLED=d11d12d13d14-verified

for g in "bash scripts/equivalency.sh" "bash scripts/run_value_sweep.sh"; do
    if pgrep -x -f "$g" >/dev/null 2>&1; then
        echo "REFUSING: '$g' is running and this swaps the CPU library."; exit 1
    fi
done

for lib in d11d12d13-verified d11d12d13d14-verified; do
    cp "$LIB.$lib" "$LIB"
    log="$HERE/frame-$lib.log"
    ( cd "$(dirname "$RENODE")" && timeout -k 20 200 "$RENODE" --console --disable-xwt --plain \
        -e "include @/tmp/rt1180renode-oraclewfi.resc" \
        -e "sysbus LoadELF @$HERE/wfi_window.elf false cpu" \
        -e 'emulation RunFor "3"' \
        -e 'echo "SP"'        -e 'cpu SP' \
        -e 'echo "PC"'        -e 'cpu PC' \
        -e 'echo "STACKED0"'  -e 'sysbus ReadDoubleWord 0x2001fff8 cpu' \
        -e 'echo "STACKED1"'  -e 'sysbus ReadDoubleWord 0x2001fffc cpu' \
        -e quit </dev/null ) > "$log" 2>&1
    sp=$(grep -A1 "^SP" "$log" | tail -1 | tr -d ' ')
    pc=$(grep -A1 "^PC" "$log" | tail -1 | tr -d ' ')
    s0=$(grep -A1 "^STACKED0" "$log" | tail -1 | tr -d ' ')
    s1=$(grep -A1 "^STACKED1" "$log" | tail -1 | tr -d ' ')
    printf '  %-24s sp=%-12s pc=%-12s [sp+24]=%-12s [sp+28]=%s\n' "$lib" "$sp" "$pc" "$s0" "$s1"
done
cp "$LIB.$INSTALLED" "$LIB"
echo "  restored: $INSTALLED ($(md5sum "$LIB" | cut -c1-12))"
echo "  (wfi @ 0xffe0102 ; next instruction @ 0xffe0104 ; xPSR follows PC at sp+28)"
