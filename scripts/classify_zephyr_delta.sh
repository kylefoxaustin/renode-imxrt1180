#!/usr/bin/env bash
# ┌──────────────────────────────────────────────────────────────────────────┐
# │ Turn raw delta verdicts into an HONEST agreement figure, by a rule.      │
# └──────────────────────────────────────────────────────────────────────────┘
#
# The raw delta says identical / DIFFER / partial from LINE COUNTS and a prefix
# compare. That is the right primitive and the wrong headline, for two reasons
# this project measured rather than assumed:
#
# 1. ⭐ SOME TARGETS CANNOT BE SCORED AT ALL. `synchronization` and
#    `philosophers` are FREE-RUNNING samples: they print until a guard stops
#    them and NEVER reach a terminal marker on EITHER side (verified: 0
#    occurrences of PROJECT EXECUTION in both captures, all six rows). Their
#    line count measures HOW LONG THE RUN LASTED, not the model. On 2026-09-23
#    all four `synchronization` rows moved identical -> partial while nothing
#    about them changed: the runs simply lasted longer (cm33 33->324 qemu,
#    55->151 renode). Counting such a row as agreement OR disagreement is noise
#    either way, so it is counted as neither.
#
# 2. A ztest whose only differences are DURATIONS or performance counters is
#    agreement. `0.102` vs `0.101 seconds`, `executed:1632` vs `1636`,
#    `Average CPU load:13%` -- two different emulators cannot tie on those, and
#    the suite verdict on both sides is PASS.
#
# ⭐ THE RULE IS APPLIED, NOT ASSERTED. Every row is classified from its own two
#    captures here; nothing is hand-labelled. A row that reaches a terminal
#    marker on one side but not the other is TRUNCATED -- a harness verdict,
#    never a statement about the model.
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
CON="${CON:-$ROOT/results/console-delta}"
IN="${IN:-$ROOT/results/zephyr-delta.tsv}"
OUT="${OUT:-$ROOT/results/zephyr-delta-classified.tsv}"

[ -r "$IN" ] || { echo "BROKEN: no $IN" >&2; exit 2; }
n=$(awk 'NR>1' "$IN" | wc -l)
[ "$n" -ge 20 ] || { echo "BROKEN: only $n rows in $IN" >&2; exit 2; }

printf 'target\tqemu_lines\trenode_lines\traw\tclass\tdiff_lines\tnondur_lines\n' > "$OUT"
awk 'NR>1' "$IN" | while IFS=$'\t' read -r t ql rl raw; do
    q="$CON/qemu_$t.norm"; r="$CON/renode_$t.norm"
    if [ ! -r "$q" ] || [ ! -r "$r" ]; then
        printf '%s\t%s\t%s\t%s\tNO-CAPTURE\t-\t-\n' "$t" "$ql" "$rl" "$raw" >> "$OUT"; continue
    fi
    qm=$(grep -acE "PROJECT EXECUTION (SUCCESSFUL|FAILED)" "$q" 2>/dev/null); qm=${qm:-0}
    rm_=$(grep -acE "PROJECT EXECUTION (SUCCESSFUL|FAILED)" "$r" 2>/dev/null); rm_=${rm_:-0}
    d=$(diff "$q" "$r" 2>/dev/null | grep -c '^[<>]'); d=${d:-0}
    # Differences that are clocks or counters, not content.
    nd=$(diff "$q" "$r" 2>/dev/null | grep '^[<>]' \
         | grep -vcE "seconds|duration|executed:|CPU load|cycles|ticks"); nd=${nd:-0}
    if [ "$qm" = 0 ] && [ "$rm_" = 0 ]; then
        cls="UNSCOREABLE"          # free-runner: never terminates on either side
    elif [ "$qm" = 0 ] || [ "$rm_" = 0 ]; then
        cls="TRUNCATED"            # one side cut: a harness verdict
    elif [ "$d" = 0 ]; then
        cls="identical"
    elif [ "$nd" = 0 ]; then
        cls="timing-only"
    else
        cls="CONTENT-DIFFER"
    fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$t" "$ql" "$rl" "$raw" "$cls" "$d" "$nd" >> "$OUT"
done

rows=$(awk 'NR>1' "$OUT" | wc -l)
if [ "$rows" != "$n" ]; then
    echo "BROKEN: classified $rows rows from $n inputs -- the classifier is emitting" >&2
    echo "        malformed records; NO agreement figure is reported." >&2
    exit 2
fi
echo "── classified $rows targets -> $OUT"
awk -F'\t' 'NR>1{c[$5]++} END{for(k in c) printf "  %-15s %d\n", k, c[k]}' "$OUT" | sort
agree=$(awk -F'\t' 'NR>1 && ($5=="identical" || $5=="timing-only")' "$OUT" | wc -l)
scoreable=$(awk -F'\t' 'NR>1 && $5!="UNSCOREABLE" && $5!="NO-CAPTURE"' "$OUT" | wc -l)
echo
echo "AGREEMENT: $agree of $scoreable scoreable targets (unscoreable free-runners excluded, not counted either way)"
echo "Content differences remaining:"
awk -F'\t' 'NR>1 && $5=="CONTENT-DIFFER"{printf "  %-46s %s non-clock lines\n", $1, $7}' "$OUT"
