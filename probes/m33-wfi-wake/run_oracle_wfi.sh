#!/usr/bin/env bash
# Run @rt1180emulator's M33 WFI probe on tlib, pre-#14 and post-#14.
#
# ⭐ THE VERDICT IS THE FLIP, AND THE MARKERS ARE THE VERDICT -- NOT THE CONSOLE.
# Their ISR stamps 0x14C0FFEE at 0x20010000 (handler body ran) and main stamps
# 0x9E500DED at 0x20010004 (resumed past WFI); both are pre-stamped 0xBADF11A6
# before arming, so a stale word cannot masquerade as a result. Reading those two
# from OUTSIDE is the whole point: from the console alone, "the probe hung" and
# "the core hung" are the same observation -- which is what defeated three probes
# of my own before this one existed.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
ORACLE="${ORACLE:-$HOME/Documents/GitHub/rt1180emulator}"
ELF="$ORACLE/tests/imxrt1180-m33-wfi/wfi.elf"
EXPECT_SHA=168d72bb29a3db8931502e858863082f0c4c60c7303e93b1dc4bab874df84723
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
LIBDIR="$HOME/.cache/renode/renode_1.17.0-portable/platform-lib/linux-x64"
LIB="$LIBDIR/translate-arm-m-le.so"
INSTALLED=d11d12d13d14-verified

# ⭐ PROVENANCE GATE. The artifact behind a scope claim gets named, not assumed --
# their first message carried a hash that a later rebuild superseded, and this
# check is why that was caught instead of silently executed.
got=$(sha256sum "$ELF" 2>/dev/null | cut -d' ' -f1)
if [ "$got" != "$EXPECT_SHA" ]; then
    echo "REFUSING TO RUN: wfi.elf is not the validated artifact."
    echo "  expected $EXPECT_SHA"
    echo "  on disk  $got"
    exit 1
fi
echo "artifact verified: $got"

# ⭐ DO NOT SWAP THE CORE UNDER A RUNNING MEASUREMENT. The sweep fingerprints this
# library now, but the cheapest control is simply not to race it.
if pgrep -x -f "bash scripts/run_value_sweep.sh" >/dev/null 2>&1; then
    echo "REFUSING TO RUN: a value sweep is running and this swaps the CPU library."
    exit 1
fi

cat > /tmp/rt1180renode-oraclewfi.resc <<RESC
using sysbus
include @$ROOT/scripts/load_peripherals.resc
mach create "wfi"
machine LoadPlatformDescription @$ROOT/platforms/mimxrt1189_cm33_value.repl
sysbus Redirect 0x50000000 0x40000000 0x10000000
RESC

echo "=== @rt1180emulator M33 WFI probe on tlib -- the flip is the verdict ==="
for lib in d11d12d13-verified d11d12d13d14-verified; do
    cp "$LIB.$lib" "$LIB"
    out="$HERE/oracle-$lib.txt"; rm -f "$out"
    ( cd "$(dirname "$RENODE")" && timeout -k 20 240 "$RENODE" --console --disable-xwt --plain \
        -e "include @/tmp/rt1180renode-oraclewfi.resc" \
        -e "cpu.semihosting.semihosting_uart CreateFileBackend @$out true" \
        -e "sysbus LoadELF @$ELF false cpu" \
        -e 'emulation RunFor "5"' \
        -e 'echo "MARK_HANDLED"' -e 'sysbus ReadDoubleWord 0x20010000 cpu' \
        -e 'echo "MARK_RESUMED"' -e 'sysbus ReadDoubleWord 0x20010004 cpu' \
        -e 'echo "CPU_PC"'       -e 'cpu PC' \
        -e 'echo "CPU_SP"'       -e 'cpu SP' \
        -e quit </dev/null ) > "$HERE/oracle-$lib.log" 2>&1
    v=$(tr -d '\0\r' < "$out" 2>/dev/null | grep -oE "PASS" | tail -1)
    h=$(grep -A1 "^MARK_HANDLED$" "$HERE/oracle-$lib.log" | tail -1 | tr -d ' ')
    r=$(grep -A1 "^MARK_RESUMED$" "$HERE/oracle-$lib.log" | tail -1 | tr -d ' ')
    pc=$(grep -A1 "^CPU_PC$" "$HERE/oracle-$lib.log" | tail -1 | tr -d ' ')
    sp=$(grep -A1 "^CPU_SP$" "$HERE/oracle-$lib.log" | tail -1 | tr -d ' ')
    printf '  %-24s %s  console=%-8s handled=%-12s resumed=%-12s pc=%-12s sp=%s\n' \
        "$lib" "$(md5sum "$LIB" | cut -c1-12)" "${v:-none}" "$h" "$r" "$pc" "$sp"
done
cp "$LIB.$INSTALLED" "$LIB"
echo "  restored: $INSTALLED ($(md5sum "$LIB" | cut -c1-12))"
