#!/usr/bin/env bash
# Does the Cortex-M33 wake from WFI on an external NVIC interrupt?
#
# ⭐ THE VERDICT IS THE FLIP, NOT THE PASS. A single PASS on the fixed library
# proves nothing about scope: it is consistent both with "the M33 was affected and
# is now fixed" and with "the M33 was never affected". Only running BOTH libraries
# separates them, so this script always runs both and reports the pair.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
LIBDIR="$HOME/.cache/renode/renode_1.17.0-portable/platform-lib/linux-x64"
LIB="$LIBDIR/translate-arm-m-le.so"
INSTALLED="d11d12d13d14-verified"

cat > /tmp/rt1180renode-m33wfi.resc <<RESC
using sysbus
include @$ROOT/scripts/load_peripherals.resc
mach create "wfi"
machine LoadPlatformDescription @$ROOT/platforms/mimxrt1189_m2_value.repl
sysbus Redirect 0x50000000 0x40000000 0x10000000
RESC

run_one () {
    local out="$HERE/out-$1.txt"; rm -f "$out"
    ( cd "$(dirname "$RENODE")" && timeout -k 20 180 "$RENODE" --console --disable-xwt --plain \
        -e "include @/tmp/rt1180renode-m33wfi.resc" \
        -e "cpu.semihosting.semihosting_uart CreateFileBackend @$out true" \
        -e "cpu1.semihosting1.semihosting1_uart CreateFileBackend @$HERE/out-$1.m7.txt true" \
        -e "sysbus LoadELF @$HERE/m33.elf false cpu" \
        -e "sysbus LoadELF @$HERE/m7.elf  false cpu1" \
        -e 'emulation RunFor "15"' -e quit </dev/null ) > "$HERE/log-$1.txt" 2>&1
    local v; v=$(tr -d '\0\r' < "$out" 2>/dev/null | grep -oE "PASS|FAIL" | tail -1)
    echo "${v:-NO-OUTPUT}"
}

echo "=== M33 WFI wake probe — the flip is the verdict ==="
for lib in d11d12d13-verified d11d12d13d14-verified; do
    cp "$LIB.$lib" "$LIB"
    printf '  %-24s %s  -> M33: %s\n' "$lib" "$(md5sum "$LIB" | cut -c1-12)" "$(run_one "$lib")"
done
# ⭐ LEAVE THE BOX AS YOU FOUND IT. A probe that swaps the installed core and walks
# away poisons every measurement taken after it.
cp "$LIB.$INSTALLED" "$LIB"
echo "  restored: $INSTALLED ($(md5sum "$LIB" | cut -c1-12))"
