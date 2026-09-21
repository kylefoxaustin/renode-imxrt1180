#!/usr/bin/env bash
# M1 verification: a stock, SELF-CHECKING NXP SDK driver example must print its
# own PASS string. Oracles are the EXTERNAL strings the firmware itself emits,
# taken from the QEMU scorecard (docs/validation/corpus.tsv) so the two tools are
# judged by the identical criterion.
#   usage: run_m1.sh <elf> <oracle-string> [logfile]
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ELF="$1"; ORACLE="$2"; LOG="${3:-/tmp/rt1180renode-tierA.log}"

# DERIVED per-image, exactly as the QEMU oracle does: base = reset_pc & 0xFFFF0000
PC=$(od -A none -t x4 -N4 -j4 "$ELF" | tr -d " ")
LOADBASE=$(printf "0x%08X" $(( 0x$PC & 0xFFFF0000 )))

cd "$(dirname "$RENODE")" || exit 2
timeout -k 20 300 "$RENODE" --console --disable-xwt --plain \
    -e "\$bin=@$ELF" \
    -e "\$loadbase=$LOADBASE" \
    -e "include @$HERE/scripts/rt1180_tierA.resc" \
    -e 'emulation RunFor "5"' \
    -e "quit" > "$LOG" 2>&1
rc=$?

if grep -qF "$ORACLE" "$LOG"; then
    echo "TIERA PASS [$(basename "$ELF")] — oracle: '$ORACLE'"
    exit 0
fi
echo "TIERA FAIL [$(basename "$ELF")] — oracle '$ORACLE' not found (rc=$rc)"
tail -5 "$LOG"
exit 1
