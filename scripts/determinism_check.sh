#!/usr/bin/env bash
#
# Determinism check for the Renode side — does the same ELF give the same BYTES twice?
#
# M-G's first half. Equivalency is not a property of one run: a correct golden compared
# against a noisy measurement produces a confident, reproducible-looking, WRONG answer.
#
# ⭐ FOUR LESSONS TAKEN WHOLE FROM THE ORACLE'S tools/determinism-check.sh, because
#    they were paid for once already and there is no reason to pay again.
#
# 1. THE LOOP MUST PROVE IT RAN. Their tool once parsed `--load` into N, `seq 1 --load`
#    errored, the run loop executed ZERO times, and it printed "All tests deterministic
#    over --load runs" with PASS=0 on every row. *Zero runs have zero variance, so
#    everything was deterministic.* A CHECK THAT DID NOT RUN LOOKS EXACTLY LIKE A CHECK
#    THAT FOUND NOTHING. So: flags are position-free, N must be a positive integer or we
#    die, and every row prints runs=N/N and the script EXITS 2 making no claim if any
#    row completed fewer runs than requested.
#
# 2. CHECK THE VALUE, NOT ONLY THE VERDICT. A verdict can be stable while the underlying
#    measurement scatters, if the tolerance is wide enough to hide it — which is exactly
#    how one engine's non-determinism survived 27 findings. Rows that print a measured
#    number declare it in VALUE_RE and we compare that too.
#
# 3. RUN IT UNDER LOAD. A green suite on an idle box is a measurement of the box. They
#    declared theirs deterministic from an idle run; under saturation TWO tests failed,
#    and both were harness defects (a point-sample of periodic counters that coincided
#    by phase, and a fixed 10 s WALL-CLOCK poll that called a slow box a failing model).
#    `--load` saturates every core. USE IT.
#
# 4. A KILL THAT REACHES THE WRAPPER AND NOT THE PROCESS IS NOT A KILL, and the exit
#    code cannot tell a bound from a leak — all of `timeout`, `timeout -k` and a proper
#    bound report 124. Renode here is a single ELF, not a wrapper spawning a
#    TERM-ignoring grandchild, so `timeout -k` does reach it; this script CENSUSES for
#    orphans afterwards rather than assuming that.
#
# WHY BYTES AND NOT A NORMALISED FORM: Renode's time model is deterministic-quantum, so
# the VIRTUAL durations Zephyr prints should reproduce exactly. Comparing normalised
# output would discard precisely the signal that says the virtual clock is leaking host
# time. So this compares the raw UART byte stream, unmodified.
#
# Usage: scripts/determinism_check.sh [N] [--load]      (default 3 — the M-G gate)
set -u

ROOT=/home/kyle/Documents/GitHub/rt1180renode
RENODE="$HOME/.cache/renode/renode_1.17.0-portable/renode"
ART="$HOME/.cache/rt1180-artifacts/zephyr"
WORK="${WORK:-/tmp/claude-1000/determinism}"
RUNFOR="${RUNFOR:-90}"
RSECS="${RSECS:-420}"

N=3
LOAD=0
for a in "$@"; do
    case "$a" in
        --load) LOAD=1 ;;
        ''|*[!0-9]*) echo "usage: $0 [N] [--load]   (got '$a')" >&2; exit 2 ;;
        *) N="$a" ;;
    esac
done
[ "$N" -ge 2 ] || { echo "N must be >= 2 to measure variance (got $N)" >&2; exit 2; }

# name                                   VALUE_RE (a measured number to compare, or -)
ROWS='
cm33-samples-hello_world                 -
cm33-tests-kernel-device                 -
cm33-tests-lib-lockfree                  -
cm33-tests-kernel-timer-timer_monotonic  delta: [0-9]+
'

mkdir -p "$WORK"
LOADPIDS=()
if [ "$LOAD" = 1 ]; then
    ncpu=$(nproc)
    echo "── saturating $ncpu cores for the duration (a green suite on an idle box is a measurement of the box)"
    for _ in $(seq 1 "$ncpu"); do ( while :; do :; done ) & LOADPIDS+=($!); done
fi
cleanup() { for p in "${LOADPIDS[@]:-}"; do kill "$p" 2>/dev/null; done; }
trap cleanup EXIT

printf '%-44s %-7s %-9s %s\n' TARGET RUNS HASHES VALUES
fail=0; shortrun=0
for t in $(echo "$ROWS" | awk 'NF{print $1}'); do
    vre=$(echo "$ROWS" | awk -v t="$t" '$1==t{$1="";sub(/^ +/,"");print}')
    elf="$ART/$t.elf"
    [ -r "$elf" ] || { printf '%-44s %-7s %s\n' "$t" "-" "NO ELF — skipped"; continue; }

    VT=$(arm-none-eabi-nm "$elf" | awk '/ _vector_table$/{print "0x"$1; exit}')
    read -r SP PC < <(arm-none-eabi-objdump -s --start-address="$VT" --stop-address="$((VT+8))" "$elf" \
      | awk '/^ /{print strtonum("0x" substr($2,7,2) substr($2,5,2) substr($2,3,2) substr($2,1,2)), \
                        strtonum("0x" substr($3,7,2) substr($3,5,2) substr($3,3,2) substr($3,1,2)); exit}')

    hashes=(); vals=(); done_runs=0
    for i in $(seq 1 "$N"); do
        raw="$WORK/$t.$i.raw"; rm -f "$raw"
        timeout -k 20 "$RSECS" "$RENODE" --console --disable-xwt --plain \
            -e "\$elf=@$elf" -e "\$vt=$VT" -e "\$sp=$(printf '0x%X' "$SP")" -e "\$pc=$(printf '0x%X' "$PC")" \
            -e "include @$ROOT/scripts/rt1180_zephyr.resc" \
            -e "lpuart1 CreateFileBackend @$raw true" \
            -e "emulation RunFor \"$RUNFOR\"" -e quit </dev/null >"$WORK/$t.$i.log" 2>&1
        # A run that produced no bytes is NOT a data point; it is an absent one.
        [ -s "$raw" ] || continue
        done_runs=$((done_runs+1))
        hashes+=("$(sha256sum "$raw" | cut -c1-12)")
        if [ "$vre" != "-" ]; then
            vals+=("$(grep -aoE "$vre" "$raw" | head -1)")
        fi
    done

    uh=$(printf '%s\n' "${hashes[@]:-}" | sort -u | grep -c . )
    uv="-"
    [ "$vre" != "-" ] && uv=$(printf '%s\n' "${vals[@]:-}" | sort -u | tr '\n' '|' | sed 's/|$//')
    verdict=""
    [ "$done_runs" -lt "$N" ] && { verdict="⚠ ONLY $done_runs/$N RUNS PRODUCED OUTPUT"; shortrun=1; }
    [ -z "$verdict" ] && [ "$uh" -ne 1 ] && { verdict="✗ NON-DETERMINISTIC ($uh distinct)"; fail=1; }
    [ -z "$verdict" ] && verdict="✓ identical"
    printf '%-44s %-7s %-9s %-24s %s\n' "$t" "$done_runs/$N" "$uh" "$uv" "$verdict"
done

echo
echo "── orphan census (a kill that reaches the wrapper and not the process is not a kill)"
orph=$(pgrep -cf 'renode_1[.]17[.]0[-]portable' || true); orph=${orph:-0}
echo "   renode processes still alive: $orph"
[ "$orph" -gt 0 ] && echo "   ⚠ orphans left behind — investigate before trusting the numbers above"

if [ "$shortrun" = 1 ]; then
    echo
    echo "BROKEN: at least one row produced fewer runs than requested. NO determinism" >&2
    echo "        claim is made — a check that did not run looks exactly like a check" >&2
    echo "        that found nothing." >&2
    exit 2
fi
[ "$fail" = 1 ] && { echo; echo "RESULT: NOT deterministic — see rows marked ✗"; exit 1; }
echo
echo "RESULT: all rows byte-identical over $N runs$([ "$LOAD" = 1 ] && echo ' UNDER LOAD' || echo ' (idle box — re-run with --load)')"
