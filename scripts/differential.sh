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
suite_value() {
    local T=results/value-tests.tsv
    [ "$DO_RUN" = 1 ] && bash scripts/run_value_tests.sh >/dev/null 2>&1
    local rows; rows=$(awk 'NR>1' "$T" 2>/dev/null | wc -l)
    if [ ! -s "$T" ] || [ "$rows" -lt 1 ]; then
        emit "$(printf '%-22s %-10s %-30s %s' 'Oracle value tests' 'MISSING' "$T" 'no figure')"; broken=1; return
    fi
    # OUT-OF-SCOPE is NOT a failure and must NOT sit in the denominator -- it is a test
    # NOT ATTEMPTED, the same category as the Zephyr study's UNSCOREABLE free-runners.
    # Scoring it as a disagreement is the mistake this project keeps making in the other
    # direction (a short run scored as a verdict); scoring it as a pass would be worse.
    # So: excluded from the ratio, counted neither way, and the count PRINTED -- because
    # 42 unattempted tests is the single most important thing on this row and hiding it
    # in a denominator or dropping it silently are both ways of not saying it.
    # ASSERT THE VERDICT VOCABULARY. Found by mutation-testing this very script: a row
    # whose verdict was 'WAT' landed silently in the FAIL bucket, because FAIL was
    # defined as "not PASS and not OUT-OF-SCOPE". So a typo, or a verdict value added
    # upstream that this scorer has never heard of, would be SCORED rather than refused
    # -- and the next person to widen the vocabulary gets a wrong number, not an error.
    # An unknown verdict is a malformed table, not a failing test.
    local unknown
    unknown=$(awk -F'\t' 'NR>1 && $5!="PASS" && $5!="FAIL" && $5!="OUT-OF-SCOPE"{print $5}' "$T" | sort -u | tr '\n' ' ')
    if [ -n "$unknown" ]; then
        emit "$(printf '%-22s %-10s %s' 'Oracle value tests' 'BROKEN' "unknown verdict(s): $unknown")"; broken=1; return
    fi
    local p f oos
    p=$(awk -F'\t' 'NR>1 && $5=="PASS"' "$T" | wc -l)
    oos=$(awk -F'\t' 'NR>1 && $5=="OUT-OF-SCOPE"' "$T" | wc -l)
    f=$(awk -F'\t' 'NR>1 && $5=="FAIL"' "$T" | wc -l)
    [ "$((p+f+oos))" -eq "$rows" ] || { emit "$(printf '%-22s %-10s %s' 'Oracle value tests' 'BROKEN' "pass+fail+oos=$((p+f+oos)) != rows=$rows")"; broken=1; return; }
    [ "$f" -gt 0 ] && disagree=1
    local scored=$((p+f))
    emit "$(printf '%-22s %-10s %-30s %s' 'Oracle value tests' "$p/$scored" "fail $f · NOT ATTEMPTED $oos" "$(age "$T")")"
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

echo "── DIFFERENTIAL PASS$([ "$DO_RUN" = 1 ] && echo ' (--run: re-measuring)' || echo ' (reading existing tables; --run to re-measure)')"
echo
printf '%-22s %-10s %-30s %s\n' SUITE AGREE DETAIL MEASURED
for s in sdk zephyr value determinism; do
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
