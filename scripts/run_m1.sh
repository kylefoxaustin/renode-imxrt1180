#!/usr/bin/env bash
# M1 verification: a stock, SELF-CHECKING NXP SDK driver example must print its
# own PASS string. Oracles are the EXTERNAL strings the firmware itself emits,
# taken from the QEMU scorecard (docs/validation/corpus.tsv) so the two tools are
# judged by the identical criterion.
#   usage: run_m1.sh <elf> <oracle-string> [logfile]
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ELF="$1"; ORACLE="$2"; LOG="${3:-/tmp/rt1180renode-m1.log}"

cd "$(dirname "$RENODE")" || exit 2
timeout -k 20 300 "$RENODE" --console --disable-xwt --plain \
    -e "\$elf=@$ELF" \
    -e "include @$HERE/scripts/rt1180_m1.resc" \
    -e 'emulation RunFor "5"' \
    -e "quit" > "$LOG" 2>&1
rc=$?

if grep -qF "$ORACLE" "$LOG"; then
    echo "M1 PASS [$(basename "$ELF")] — oracle: '$ORACLE'"
    exit 0
fi
echo "M1 FAIL [$(basename "$ELF")] — oracle '$ORACLE' not found (rc=$rc)"
tail -5 "$LOG"
exit 1
