#!/usr/bin/env bash
#
# M-G half 2 — ONE differential pass over every artifact, both tools, one verdict.
#
# ⭐ THIS IS AN ORCHESTRATOR, NOT A HARNESS. It deliberately re-implements NOTHING.
#
# Each suite already carries guards that were paid for with false findings:
# content-based completion checking (not line counts), per-tool wall budgets, a short
# run scored `truncated` rather than as a verdict, and row-count assertions. Re-deriving
# any of that here would create a SECOND set of guards that drifts from the first — and
# a divergent guard is worse than no guard, because two numbers disagree and a reader
# has to guess which governs. So this script runs the suites and reads their tables.
#
# ⚠️ THE HARNESS HAS BEEN THE SINGLE LARGEST SOURCE OF FALSE FINDINGS IN THIS PROJECT —
#    six silent failures in one afternoon, two wall budgets that penalised the slower
#    tool, and an audit that could not see truncation manufacturing agreement. So this
#    one is built to REFUSE rather than to report:
#
#   - A suite whose table is missing, empty, or header-only yields NO figure. It is
#     reported MISSING and the whole pass exits 2. An absent suite scored as 0/0 reads
#     exactly like a suite that found nothing wrong.
#   - Every suite's row count is asserted against what it should contain.
#   - STALENESS IS PRINTED, never inferred. Scoring a 12-day-old table is legitimate;
#     doing it without knowing is not. ⚠️ BUT THE COLUMN IS FILE MTIME, NOT MEASUREMENT
#     TIME, and those differ: restoring a table with `cp` resets its mtime while the
#     CONTENT stays old. I did exactly that to value-tests.tsv while mutation-testing
#     this script, and its age flipped from "18d ago" to "0m ago" with identical bytes.
#     So mtime is a hint, not provenance. Use `cp -p` when moving these, and treat a
#     fresh mtime on an unchanged table as unproven rather than as a measurement.
#     doing it without knowing is not. Today a peer's stale lease string sent me to
#     protect a measurement that had finished an hour earlier — a status field nobody
#     timestamps is a claim that goes stale silently.
#   - `--run` is required to re-measure. The default READS, so nobody re-runs hours of
#     emulation by accident, and nobody mistakes a read for a measurement either.
#
# Usage: scripts/differential.sh [--run] [--suite NAME]
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
cd "$ROOT" || exit 2

DO_RUN=0; ONLY=""
for a in "$@"; do
  case "$a" in
    --run) DO_RUN=1 ;;
    --suite) ONLY="NEXT" ;;
    *) if [ "$ONLY" = "NEXT" ]; then ONLY="$a"; else
         echo "usage: $0 [--run] [--suite NAME]   (got '$a')" >&2; exit 2; fi ;;
  esac
done
[ "$ONLY" = "NEXT" ] && { echo "--suite needs a name" >&2; exit 2; }

age() {  # human age of a file, or ABSENT
  [ -r "$1" ] || { printf 'ABSENT'; return; }
  local s=$(( $(date +%s) - $(stat -c %Y "$1") ))
  if   [ $s -lt 3600 ]  ; then printf '%dm ago' $((s/60))
  elif [ $s -lt 86400 ] ; then printf '%dh ago' $((s/3600))
  else printf '%dd ago' $((s/86400)); fi
}

broken=0; disagree=0
declare -a LINES=()

emit() { LINES+=("$1"); }

# ── suite 1: vendor SDK console corpus ────────────────────────────────────────
suite_sdk() {
    local T=results/equivalency.tsv
    [ "$DO_RUN" = 1 ] && bash scripts/equivalency.sh >/dev/null 2>&1
    local rows; rows=$(awk 'NR>1' "$T" 2>/dev/null | wc -l)
    if [ ! -s "$T" ] || [ "$rows" -lt 1 ]; then
        emit "$(printf '%-22s %-10s %-30s %s' 'SDK console corpus' 'MISSING' "$T" 'no figure')"; broken=1; return
    fi
    local a d
    a=$(awk -F'\t' 'NR>1 && $6=="YES"' "$T" | wc -l)
    d=$(awk -F'\t' 'NR>1 && $6=="NO"'  "$T" | wc -l)
    [ "$((a+d))" -eq "$rows" ] || { emit "$(printf '%-22s %-10s %s' 'SDK console corpus' 'BROKEN' "agree+differ=$((a+d)) != rows=$rows")"; broken=1; return; }
    [ "$d" -gt 0 ] && disagree=1
    emit "$(printf '%-22s %-10s %-30s %s' 'SDK console corpus' "$a/$rows" "differ $d" "$(age "$T")")"
}

# ── suite 2: Zephyr console delta ─────────────────────────────────────────────
suite_zephyr() {
    local RAW=results/zephyr-delta-faulting.tsv
    local CLS=results/zephyr-delta-faulting-classified.tsv
    if [ "$DO_RUN" = 1 ]; then
        OUT="$ROOT/$RAW" CON="$ROOT/results/console-faulting" bash scripts/zephyr_delta.sh >/dev/null 2>&1
        IN="$ROOT/$RAW" CON="$ROOT/results/console-faulting" OUT="$ROOT/$CLS" bash scripts/classify_zephyr_delta.sh >/dev/null 2>&1
    fi
    [ -s "$CLS" ] || { emit "$(printf '%-22s %-10s %-30s %s' 'Zephyr console delta' 'MISSING' "$CLS" 'no figure')"; broken=1; return; }
    local out; out=$(IN="$ROOT/$RAW" CON="$ROOT/results/console-faulting" OUT=/tmp/claude-1000/diff-z.tsv \
                     bash scripts/classify_zephyr_delta.sh 2>/dev/null)
    # Read the line, not the tail of a pipe (M49: never read a verdict through something
    # that can truncate it).
    local adj; adj=$(printf '%s\n' "$out" | grep -m1 'ADJUDICATED AGREEMENT:')
    local unexp; unexp=$(printf '%s\n' "$out" | grep -m1 'unexplained content differences' | grep -oE '[0-9]+ unexplained' | grep -oE '^[0-9]+')
    unexp=${unexp:-?}
    [ -n "$adj" ] || { emit "$(printf '%-22s %-10s %s' 'Zephyr console delta' 'BROKEN' 'classifier printed no agreement line')"; broken=1; return; }
    local n t; n=$(echo "$adj" | grep -oE '[0-9]+ of [0-9]+' | awk '{print $1}'); t=$(echo "$adj" | grep -oE '[0-9]+ of [0-9]+' | awk '{print $3}')
    [ "$unexp" != "0" ] && [ "$unexp" != "?" ] && disagree=1
    emit "$(printf '%-22s %-10s %-30s %s' 'Zephyr console delta' "$n/$t" "unexplained $unexp" "$(age "$RAW")")"
}

# ── suite 3: oracle value tests ───────────────────────────────────────────────
# ⚠️ POINTS AT value-sweep-final45.tsv, NOT value-tests.tsv.
#
# The first version of this script read results/value-tests.tsv and reported
# "12/12 pass, NOT ATTEMPTED 42" -- which I then wrote up as "42 of 54 oracle value
# tests have never been attempted, the largest unknown in the whole comparison."
# THAT WAS FALSE. value-tests.tsv is an older, narrower table covering only the
# motor/ADC group. The authoritative sweep is value-sweep-final45.tsv (the latest of
# the sweeps, and the one EXPERIMENT.md cites twice):
#
#       45 PASS / 0 FAIL / 10 NEEDS-OWN-HARNESS
#
# The 10 are not unattempted either -- they are tests that ship their own harness and
# are run by dedicated runners (run_netc_lab3.sh, run_motor_load.sh, run_sai_value.sh
# and friends), scored in their own rows rather than bare here.
#
# ⭐ AN ORCHESTRATOR POINTED AT THE WRONG ARTIFACT PRODUCES A CONFIDENT WRONG NUMBER,
#    and reads exactly like one pointed at the right one. The table it names IS a
#    claim, and it was the only part of this script I did not mutation-test.
suite_value() {
    local T=results/value-sweep-final45.tsv
    [ "$DO_RUN" = 1 ] && OUT="$ROOT/$T" bash scripts/run_value_sweep.sh >/dev/null 2>&1
    local rows; rows=$(awk 'NR>1' "$T" 2>/dev/null | wc -l)
    if [ ! -s "$T" ] || [ "$rows" -lt 1 ]; then
        emit "$(printf '%-22s %-10s %-30s %s' 'Oracle value tests' 'MISSING' "$T" 'no figure')"; broken=1; return
    fi
    # Assert the verdict vocabulary: an unknown verdict is a MALFORMED TABLE, not a
    # failing test. Found by mutation-testing this script rather than by reading it.
    local unknown
    unknown=$(awk -F'\t' 'NR>1 && $6!="PASS" && $6!="FAIL" && $6!="NEEDS-OWN-HARNESS"{print $6}' "$T" | sort -u | tr '\n' ' ')
    if [ -n "$unknown" ]; then
        emit "$(printf '%-22s %-10s %s' 'Oracle value tests' 'BROKEN' "unknown verdict(s): $unknown")"; broken=1; return
    fi
    local p f own
    p=$(awk -F'\t' 'NR>1 && $6=="PASS"' "$T" | wc -l)
    f=$(awk -F'\t' 'NR>1 && $6=="FAIL"' "$T" | wc -l)
    own=$(awk -F'\t' 'NR>1 && $6=="NEEDS-OWN-HARNESS"' "$T" | wc -l)
    [ "$((p+f+own))" -eq "$rows" ] || { emit "$(printf '%-22s %-10s %s' 'Oracle value tests' 'BROKEN' "p+f+own=$((p+f+own)) != rows=$rows")"; broken=1; return; }
    [ "$f" -gt 0 ] && disagree=1
    emit "$(printf '%-22s %-10s %-30s %s' 'Oracle value tests' "$p/$((p+f))" "fail $f · own-harness $own" "$(age "$T")")"
}

# ── suite 4: Renode determinism ───────────────────────────────────────────────
suite_determinism() {
    local L=/tmp/claude-1000/det-load.log
    [ "$DO_RUN" = 1 ] && { ./scripts/determinism_check.sh 3 --load >"$L" 2>&1; }
    [ -s "$L" ] || { emit "$(printf '%-22s %-10s %-30s %s' 'Renode determinism' 'MISSING' "$L" 'no figure')"; broken=1; return; }
    local res; res=$(grep -m1 '^RESULT:' "$L")
    local rowsok; rowsok=$(grep -cE '✓ identical' "$L"); rowsok=${rowsok:-0}
    local short; short=$(grep -cE 'ONLY [0-9]+/' "$L"); short=${short:-0}
    if [ "$short" -gt 0 ] || [ -z "$res" ]; then
        emit "$(printf '%-22s %-10s %s' 'Renode determinism' 'BROKEN' 'a row produced fewer runs than requested')"; broken=1; return
    fi
    case "$res" in *"UNDER LOAD"*) local cond="under load" ;; *) local cond="IDLE ONLY — weaker" ;; esac
    grep -q 'NON-DETERMINISTIC' "$L" && disagree=1
    emit "$(printf '%-22s %-10s %-30s %s' 'Renode determinism' "$rowsok/$rowsok" "$cond" "$(age "$L")")"
}

# ── suite 5: SUITE COVERAGE against the oracle's own test tree ────────────────
# The control this script was MISSING. Every other row answers "do the rows we ran
# agree?" -- none answered "do we run the rows that exist?" A pass that scores 45/45
# on a sweep covering 55 of the oracle's 64 tests is a true statement about the wrong
# denominator, and it is exactly how "42 unattempted" (wrong) and "45/45" (right but
# silent about coverage) could both come out of the same tree an hour apart.
#
# `corpus` and `zephyr` are the oracle's META-suites -- the SDK console corpus and the
# Zephyr corpus -- which this pass already scores as suites 1 and 2. They are excluded
# as DUPLICATES, named explicitly rather than filtered by a pattern, so the exclusion
# cannot silently grow.
suite_coverage() {
    local QT=/home/kyle/Documents/GitHub/rt1180emulator/tests
    local T=results/value-sweep-final45.tsv
    [ -d "$QT" ] || { emit "$(printf '%-22s %-10s %s' 'Suite coverage' 'MISSING' "oracle tree not at $QT")"; broken=1; return; }
    local oracle mine
    oracle=$(ls -d "$QT"/imxrt1180-* 2>/dev/null | sed 's|.*/imxrt1180-||' | sort -u)
    mine=$(awk -F'\t' 'NR>1{sub(/^imxrt1180-/,"",$1); print $1}' "$T" 2>/dev/null | sort -u)
    # POSITIVE CONTROL: names that must appear on both sides. If these miss, the name
    # extraction is broken and the coverage figure is about my regex, not my coverage.
    local ctl=0
    for n in adc motor pwm; do
        printf '%s\n' "$oracle" | grep -qx "$n" && printf '%s\n' "$mine" | grep -qx "$n" && ctl=$((ctl+1))
    done
    [ "$ctl" -eq 3 ] || { emit "$(printf '%-22s %-10s %s' 'Suite coverage' 'BROKEN' "positive control $ctl/3 -- name extraction is wrong")"; broken=1; return; }
    local nmeta=0 missing=""
    while read -r n; do
        [ -z "$n" ] && continue
        case "$n" in corpus|zephyr) nmeta=$((nmeta+1)); continue ;; esac
        printf '%s\n' "$mine" | grep -qx "$n" || missing="$missing $n"
    done < <(printf '%s\n' "$oracle")
    local no nm nmiss
    no=$(printf '%s\n' "$oracle" | grep -c .)
    nm=$(printf '%s\n' "$mine" | grep -c .)
    nmiss=$(printf '%s\n' $missing | grep -c .)
    [ "$nmiss" -gt 0 ] && disagree=1
    emit "$(printf '%-22s %-10s %-30s %s' 'Suite coverage' "$((no-nmeta-nmiss))/$((no-nmeta))" "UNATTEMPTED${missing:- none} · meta $nmeta" 'live')"
}

echo "── DIFFERENTIAL PASS$([ "$DO_RUN" = 1 ] && echo ' (--run: re-measuring)' || echo ' (reading existing tables; --run to re-measure)')"
echo
printf '%-22s %-10s %-30s %s\n' SUITE AGREE DETAIL 'FILE MTIME'
for s in sdk zephyr value determinism coverage; do
    [ -n "$ONLY" ] && [ "$ONLY" != "$s" ] && continue
    "suite_$s"
done
printf '%s\n' "${LINES[@]}"

echo
if [ "$broken" = 1 ]; then
    echo "BROKEN: at least one suite yielded no figure. NO overall verdict is reported —" >&2
    echo "        an absent suite scored as 0/0 reads exactly like a suite that found" >&2
    echo "        nothing wrong." >&2
    exit 2
fi
if [ "$disagree" = 1 ]; then
    echo "RESULT: suites ran clean, but at least one DISAGREEMENT remains — see DETAIL."
    exit 1
fi
echo "RESULT: all suites green, no unexplained disagreement on any observable."
