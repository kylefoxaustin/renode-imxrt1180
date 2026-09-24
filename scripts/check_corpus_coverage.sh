#!/usr/bin/env bash
# ┌──────────────────────────────────────────────────────────────────────────┐
# │ Which of the oracle's corpus examples does this side actually measure?   │
# └──────────────────────────────────────────────────────────────────────────┘
#
# ⭐ THIS SCRIPT EXISTS BECAUSE THE AD-HOC VERSION OF IT WAS WRONG THREE TIMES,
#    AND EACH WRONG ANSWER LOOKED EXACTLY LIKE A RIGHT ONE.
#
#   1. `diff -rq --include="*.cs"` reported "0 files differ" for two trees that
#      differ in 42 files. `--include` is a grep option; diff ignores it.
#   2. `comm` across the two example columns invented 32 missing rows -- the two
#      files spell the example differently (`A demo_apps/x` vs `demo_apps/x`).
#   3. `comm` again, after fixing that, invented 1 missing row (pmsm_enc) --
#      because our column can carry a long bracketed [note] after the path.
#
# Every one of those printed a confident number. The defence is not "be careful":
# it is a POSITIVE CONTROL that fails loudly when the comparison itself is broken.
#
#   ⭐ MAKE THE CHECK ASK A QUESTION THE WRONG ANSWER FAILS.
#
# Exit 0 = every oracle corpus example is measured here. Exit 1 = genuinely
# missing rows. Exit 2 = THE COMPARISON IS BROKEN, and no coverage claim is made.
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
ORACLE="${ORACLE:-/home/kyle/Documents/GitHub/rt1180emulator}"
CORPUS="$ORACLE/docs/validation/corpus.tsv"
MINE="$ROOT/results/equivalency.tsv"

for f in "$CORPUS" "$MINE"; do
    [ -r "$f" ] || { echo "BROKEN: cannot read $f" >&2; exit 2; }
done

# Oracle side: skip '#' comments and the header row.
grep -v '^#' "$CORPUS" | grep . | awk -F'\t' 'NR>1{print $2}' | sort -u > /tmp/.cov_oracle.$$
# Our side: strip the leading tier letter ("A ") and any trailing " [note]".
awk -F'\t' 'NR>1{e=$2; sub(/^[A-Z] /,"",e); sub(/ *\[.*/,"",e); if(e!="")print e}' \
    "$MINE" | sort -u > /tmp/.cov_mine.$$
trap 'rm -f /tmp/.cov_oracle.$$ /tmp/.cov_mine.$$' EXIT

no=$(wc -l < /tmp/.cov_oracle.$$); nm=$(wc -l < /tmp/.cov_mine.$$)

# ── POSITIVE CONTROL ─────────────────────────────────────────────────────────
# Examples that MUST be on both sides. If a control is missing from either list,
# the parsing is broken -- refuse to report coverage rather than report a number
# that is confidently wrong.
broken=0
for ctrl in "demo_apps/hello_world" "demo_apps/led_blinky"; do
    grep -qxF "$ctrl" /tmp/.cov_oracle.$$ || { echo "BROKEN: control '$ctrl' absent from the ORACLE list -- corpus parsing failed" >&2; broken=1; }
    grep -qxF "$ctrl" /tmp/.cov_mine.$$   || { echo "BROKEN: control '$ctrl' absent from OUR list -- equivalency parsing failed" >&2; broken=1; }
done
[ "$no" -ge 20 ] || { echo "BROKEN: oracle list has only $no entries; expected >= 20" >&2; broken=1; }
[ "$nm" -ge 20 ] || { echo "BROKEN: our list has only $nm entries; expected >= 20" >&2; broken=1; }
if [ "$broken" != 0 ]; then
    echo "NO COVERAGE CLAIM MADE -- fix the comparison first." >&2
    exit 2
fi

missing=$(comm -23 /tmp/.cov_oracle.$$ /tmp/.cov_mine.$$)
extra=$(comm -13 /tmp/.cov_oracle.$$ /tmp/.cov_mine.$$)

echo "oracle corpus examples : $no"
echo "measured on this side  : $((no - $(printf '%s' "$missing" | grep -c . || true)))"
[ -n "$extra" ] && { echo "beyond the oracle corpus:"; printf '%s\n' "$extra" | sed 's/^/    + /'; }
if [ -n "$missing" ]; then
    echo "NOT measured here:"; printf '%s\n' "$missing" | sed 's/^/    - /'
    exit 1
fi
echo "COVERAGE COMPLETE — every oracle corpus example is measured on this side."
