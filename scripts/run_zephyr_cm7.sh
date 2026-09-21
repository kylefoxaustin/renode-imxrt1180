#!/usr/bin/env bash
# Zephyr verification. Unlike the SDK images, Zephyr's XIP build puts a boot
# header before the vector table, so VTOR/SP/PC are extracted from the ELF and
# handed to Renode rather than guessed.
#   usage: run_zephyr.sh <zephyr.elf> <oracle-string> [logfile]
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ELF="$1"; ORACLE="$2"; LOG="${3:-/tmp/rt1180renode-zephyr.log}"
NM="$(command -v arm-none-eabi-nm || echo nm)"
OBJDUMP="$(command -v arm-none-eabi-objdump || echo objdump)"

VT=$($NM "$ELF" | awk '/ _vector_table$/{print "0x"$1; exit}')
[ -n "$VT" ] || { echo "could not find _vector_table in $ELF"; exit 2; }
# initial SP/PC are the first two words of the vector table, read from the ELF
read SP PC < <($OBJDUMP -s --start-address="$VT" --stop-address="$((VT+8))" "$ELF" \
  | awk '/^ /{print strtonum("0x" substr($2,7,2) substr($2,5,2) substr($2,3,2) substr($2,1,2)), strtonum("0x" substr($3,7,2) substr($3,5,2) substr($3,3,2) substr($3,1,2)); exit}')
SP=$(printf '0x%X' "$SP"); PC=$(printf '0x%X' "$PC")

cd "$(dirname "$RENODE")" || exit 2
timeout -k 20 300 "$RENODE" --console --disable-xwt --plain \
    -e "\$elf=@$ELF" -e "\$vt=$VT" -e "\$sp=$SP" -e "\$pc=$PC" \
    -e "include @$HERE/scripts/rt1180_cm7.resc" \
    -e 'emulation RunFor "3"' -e "quit" > "$LOG" 2>&1
rc=$?
if grep -qF "$ORACLE" "$LOG"; then
    echo "ZEPHYR PASS — oracle: '$ORACLE'  (VTOR=$VT SP=$SP PC=$PC)"
    exit 0
fi
echo "ZEPHYR FAIL — oracle '$ORACLE' not found (rc=$rc, VTOR=$VT SP=$SP PC=$PC)"
grep -E '\[INFO\] lpuart1' "$LOG" | sed 's/.*\] //' | head -5
tail -4 "$LOG"
exit 1
