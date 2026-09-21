#!/usr/bin/env bash
# Classify every DIFFER row by WHAT differs, not just THAT something differs.
#
# ⭐ WHY THIS EXISTS. "The consoles differ" and "the tools disagree" are not the
# same claim -- I conflated them for one row (kernel-device) and had to correct it
# to @rt1180emulator. A raw DIFFER count materially OVERSTATES the number of real
# model deltas, because most differences are:
#   timing        self-reported durations / timestamps / tick counts
#   interleave    same lines, different order (two schedulers)
#   nondet        values that are SUPPOSED to differ (stack canaries, RNG)
# Only what is left after those is a candidate fidelity delta.
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
CON="$HERE/results/console-delta"
IN="$HERE/results/zephyr-delta.tsv"
OUT="$HERE/results/zephyr-delta-classified.tsv"

printf 'target\tqemu_lines\trenode_lines\tclass\tdiff_lines\tunexplained\n' > "$OUT"
while IFS=$'\t' read -r t ql rl verdict <&3; do
    [ "$t" = "target" ] && continue
    if [ "$verdict" != "DIFFER" ]; then
        printf '%s\t%s\t%s\t%s\t0\t0\n' "$t" "$ql" "$rl" "$verdict" >> "$OUT"; continue
    fi
    q="$CON/qemu_$t.norm"; r="$CON/renode_$t.norm"
    d=$(diff "$q" "$r" 2>/dev/null | grep '^[<>]' || true)
    tot=$(printf '%s\n' "$d" | grep -c '^[<>]' || true)
    # Lines explainable by a known non-fidelity cause.
    rest=$(printf '%s\n' "$d" \
        | grep -v 'in [0-9]*\.[0-9]* seconds' \
        | grep -v 'duration = ' \
        | grep -v '\[00:00:[0-9][0-9]\.[0-9,]*\]' \
        | grep -v 'r[0-9]*/a[0-9]*:' \
        | grep -v 'Elapsed cycles:' \
        | grep -v 'delta: [0-9]* *expected:' \
        | grep -c '^[<>]' || true)
    if [ "$tot" -gt 0 ] && [ "$rest" -eq 0 ]; then cls="timing/nondet"
    elif [ "$ql" != "$rl" ] && [ "$rest" -gt 0 ]; then cls="len+content"
    else cls="CONTENT"; fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$t" "$ql" "$rl" "$cls" "$tot" "$rest" >> "$OUT"
done 3< "$IN"

echo "=== classified ==="
awk -F'\t' 'NR>1{c[$4]++} END{for(k in c) printf "  %-14s %d\n", k, c[k]}' "$OUT" | sort -k2 -rn
echo "=== rows with UNEXPLAINED differing lines (candidate real deltas) ==="
awk -F'\t' 'NR>1 && $6>0{printf "  %-46s q=%-5s r=%-5s unexplained=%s\n", $1,$2,$3,$6}' "$OUT"
