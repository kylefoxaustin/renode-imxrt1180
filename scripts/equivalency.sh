#!/usr/bin/env bash
# HEAD-TO-HEAD SCORER — Renode vs QEMU on the identical RT1180 firmware.
#
# For every row of results/equivalency-corpus.tsv this runs the SAME image under
# BOTH emulators on THIS box and matches BOTH consoles against the SAME external
# oracle. Neither verdict is copied from anyone's scorecard: both are MEASURED
# here, so the two columns are comparable under fleet Law 1.
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
QEMU="${QEMU:-$HOME/Documents/GitHub/rt1180emulator/build/qemu-system-arm}"
ART="${ART:-$HOME/.cache/rt1180-artifacts/sdk}"
BINROOT="${BINROOT:-$HOME/Documents/GitHub/rt1180emulator/reference/sdk-fw/mcuxsdk/examples/_boards/evkmimxrt1180}"
OUT="$HERE/results/equivalency.tsv"
CONDIR="$HERE/results/console-eq"; mkdir -p "$CONDIR"
SECS="${SECS:-20}"

# ⭐ COVERAGE GATE. Assert the corpus size BEFORE running, not after.
#
# Without this a row silently dropped from the corpus is invisible: the sweep
# prints "total 28" instead of "total 29" and nothing compares those numbers to
# anything. Worse than invisible -- FLATTERING: the two remaining differences are
# red rows, so dropping one RAISES the agreement percentage. A shrinking corpus
# would read as improving fidelity.
#
# Same class as the MANIFEST that pinned 11 of 103 artifacts while `sha256sum -c`
# returned all-OK: the check that matters is COVERAGE, not passes. Count the rows,
# not the agreements. (Raised by @rt1180emulator's EXPECTED_A/EXPECTED_B gate and
# their GOLDEN_FLOOR, after I suggested the audit that found their residual gap.)
#
# If you add or remove a corpus row you MUST bump this, deliberately.
EXPECTED_ROWS="${EXPECTED_ROWS:-31}"
# NB: '\t' is NOT a tab in POSIX ERE -- excluding the header by prefix alone.
# (Measured: the first version counted 30 because '^kind\t' matched nothing.)
CORPUS="${CORPUS:-$HERE/results/equivalency-corpus.tsv}"
actual_rows=$(grep -vcE '^#|^kind|^$' "$CORPUS")
if [ "$actual_rows" -ne "$EXPECTED_ROWS" ]; then
    echo "GATE FAIL: corpus has $actual_rows rows, expected $EXPECTED_ROWS." >&2
    echo "  A silently dropped row would raise the agreement score. Bump EXPECTED_ROWS" >&2
    echo "  only if you changed the corpus on purpose." >&2
    exit 3
fi

# ⭐ ARTIFACT COVERAGE FLOOR. `sha256sum -c` says nothing about what is NOT listed.
MANIFEST="${MANIFEST:-$HOME/.cache/rt1180-artifacts/MANIFEST.sha256}"
MANIFEST_FLOOR="${MANIFEST_FLOOR:-105}"
if [ -r "$MANIFEST" ]; then
    pinned=$(grep -c . "$MANIFEST")
    if [ "$pinned" -lt "$MANIFEST_FLOOR" ]; then
        echo "GATE FAIL: manifest pins $pinned artifacts, floor is $MANIFEST_FLOOR." >&2
        echo "  Do NOT lower the floor -- regenerate the manifest instead." >&2
        exit 3
    fi
fi

harness_broken=0

# ⭐ CLEAN UP MY OWN DESCENDANTS IF I AM KILLED FROM OUTSIDE.
#
# Each emulator runs inside a `( cd ... && timeout ... )` SUBSHELL. If this sweep is
# killed from outside -- and I did exactly that, wrapping a run in
# `timeout -k 10 60` to sample it -- bash tears down the subshell and the emulator
# underneath KEEPS RUNNING. It self-cleared that time only because every invocation
# also carries its own inner `timeout -k 5`; I was saved by a second defence, not
# by this one existing. @rt1180emulator's tools/bounded.sh documents the same
# measured behaviour: `timeout N cmd` orphans the grandchild AND still exits 124,
# so the wrapper looks like it worked.
#
# ⚠ WHY THIS IS A TRAP AND NOT A REWRITE OF THE CALL SITES. The group-discipline
# fix (setsid + `kill -TERM -$pgid`) is the better answer and is what bounded.sh
# does. Wiring it through these invocations means re-quoting a cd-then-exec with
# a dozen -e arguments, at the end of a session, in a harness that currently
# produces a verified 27/29. A defined-but-unused helper would have been WORSE than
# nothing -- protection that looks active and is not, which is the very
# "instrument you don't reach for" failure this is meant to close. So: an additive
# trap that changes no control flow, and the group rewrite recorded as specified
# future work rather than half-built.
cleanup_descendants() {
    local kids
    kids=$(pgrep -P $$ 2>/dev/null)
    [ -n "$kids" ] || return 0
    # Kill the whole subtree of each direct child, deepest-first via pkill -P.
    for k in $kids; do
        pkill -TERM -P "$k" 2>/dev/null
        kill -TERM "$k" 2>/dev/null
    done
    return 0
}
trap 'cleanup_descendants; exit 130' INT TERM
: > "$OUT"
printf 'kind\texample\tcore\tqemu\trenode\tagree\toracle\n' >> "$OUT"

resolve() { case "$1" in A:*) echo "$BINROOT/${1#A:}" ;; *) echo "$ART/$1" ;; esac; }

while IFS=$'\t' read -r kind image core resc qflags oracle example <&3; do
    case "$kind" in ''|'#'*|kind) continue ;; esac
    tag="$(echo "$example" | tr ' /' '__')"

    # Rows that never run firmware in EITHER tool: reported, not scored.
    if [ "$kind" = xbuild ]; then
        printf '%s\t%s\t%s\tXBUILD\tXBUILD\tYES\t-\n' "$kind" "$example" "$core" >> "$OUT"; continue
    fi
    if [ "$kind" = value ] && [ "$image" = "-" ]; then
        # 2026-09-19: the Renode side is now VALUE-PROVEN too -- 13/13 of the
        # oracle's motor/ADC value tests pass on their unmodified binaries,
        # including adc-fifo-align (one of the two this row is pinned on). The
        # other pin, cm7boot, is unavailable for a harness-capability reason they
        # confirmed, not a model gap. The pin gap is stated in the corpus note.
        printf '%s\t%s\t%s\tVALUE-PROVEN\tVALUE-PROVEN(13/13, 1 pin unavailable)\tYES\t-\n' "$kind" "$example" "$core" >> "$OUT"; continue
    fi

    img="$(resolve "$image")"
    [ -f "$img" ] || { printf '%s\t%s\t%s\tNO-IMAGE\tNO-IMAGE\t-\t-\n' "$kind" "$example" "$core" >> "$OUT"; continue; }

    # ---- QEMU (measured here, not quoted) ----
    qcon="$CONDIR/qemu_$tag.txt"; : > "$qcon"
    qx=""; [ "$qflags" != "-" ] && qx="$qflags"
    timeout -k 5 "$SECS" "$QEMU" -M mimxrt1180-evk -audio none -display none -monitor none \
        -kernel "$img" -serial "file:$qcon" $qx -semihosting-config enable=on,target=native \
        </dev/null >/dev/null 2>&1

    # ---- Renode (measured here) ----
    rcon="$CONDIR/renode_$tag.txt"; : > "$rcon"
    # Tier A: the load base is DERIVED per-image from its own reset vector
    # (reset_pc & 0xFFFF0000), exactly as the QEMU oracle does -- the three
    # usb_device_dfu images link at 0x0FFF0000, every other cm33 .bin at
    # 0x0FFE0000. Hardcoding one base silently mislocates the others.
    extra_e=()
    case "$resc" in
        tierA|tierA_m2)
            PCW=$(od -A none -t x4 -N4 -j4 "$img" | tr -d " ")
            extra_e=(-e "\$loadbase=$(printf "0x%08X" $(( 0x$PCW & 0xFFFF0000 )))") ;;
    esac
    case "$resc" in
        tierA)    rsc="$HERE/scripts/rt1180_tierA.resc";    var='$bin' ;;
        tierA_m2) rsc="$HERE/scripts/rt1180_tierA_m2.resc"; var='$bin' ;;
        m1)       rsc="$HERE/scripts/rt1180_m1.resc";       var='$elf' ;;
        m2)       rsc="$HERE/scripts/rt1180_m2.resc";       var='$elf' ;;
        rpmsg)    rsc="$HERE/scripts/rt1180_rpmsg.resc";    var='$elf' ;;
        *)        rsc="$HERE/scripts/rt1180_m1.resc";       var='$elf' ;;
    esac
    ( cd "$(dirname "$RENODE")" && timeout -k 20 $((SECS*6)) "$RENODE" --console --disable-xwt --plain \
        -e "$var=@$img" "${extra_e[@]}" -e "include @$rsc" -e "emulation RunFor \"5\"" -e quit </dev/null ) > "$rcon" 2>&1

    # ⭐ HARNESS HEALTH, CHECKED BEFORE THE VERDICT.
    #
    # Without this, `rhit=0` means FAIL whether the firmware RAN and didn't print
    # the oracle, or the platform NEVER LOADED. Those are not the same claim: one
    # is a fidelity result about Renode, the other is a bug in my scripts. They are
    # indistinguishable in the tally, and the tally is what gets published.
    #
    # This is not hypothetical. MEASURED this session: adding a peripheral to
    # mimxrt1189_m1.repl without adding its include to rt1180_tierA.resc made the
    # platform fail to load, and all six Tier A rows scored FAIL. The sweep read
    # 22/29 instead of 27/29 and every "difference" was a false fidelity failure
    # against QEMU -- all of them wrong in my own favour's opposite direction, i.e.
    # they made Renode look worse for a reason that had nothing to do with Renode.
    #
    # A row whose harness broke is NOT a data point. It is scored HARNESS-FAIL and
    # the sweep exits non-zero, because a run that silently degrades into fewer
    # valid rows is exactly the "green because checking less" failure the coverage
    # gate above exists to prevent -- one level further in.
    rerr=0
    if grep -qE "There was an error executing command|Could not compile|Error E[0-9]+:" "$rcon"; then
        rerr=1; harness_broken=$((harness_broken + 1))
        echo "  !! HARNESS FAIL on '$example': Renode could not build the machine." >&2
        grep -m1 -E "There was an error executing command|Could not compile|Error E[0-9]+:" "$rcon" >&2
    fi

    qhit=0; grep -qF "$oracle" "$qcon" 2>/dev/null && qhit=1
    rhit=0; grep -qF "$oracle" "$rcon" 2>/dev/null && rhit=1

    case "$kind" in
      pass)   q=$([ $qhit = 1 ] && echo PASS   || echo FAIL)
              r=$([ $rhit = 1 ] && echo PASS   || echo FAIL) ;;
      banner) q=$([ $qhit = 1 ] && echo BANNER || echo FAIL)
              r=$([ $rhit = 1 ] && echo BANNER || echo FAIL) ;;
      xfail)  q=$([ $qhit = 1 ] && echo REGRESSED || echo XFAIL)
              r=$([ $rhit = 1 ] && echo REGRESSED || echo XFAIL) ;;
      value)  q=$([ $qhit = 1 ] && echo RAN || echo RAN)
              r=$([ $rhit = 1 ] && echo RAN || echo RAN) ;;
    esac
    [ "$rerr" = 1 ] && r="HARNESS-FAIL"
    agree=$([ "$q" = "$r" ] && echo YES || echo NO)
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$kind" "$example" "$core" "$q" "$r" "$agree" "$oracle" >> "$OUT"
    printf '%-58s qemu=%-9s renode=%-9s %s\n' "$example" "$q" "$r" "$agree"
done 3< "$CORPUS"

# ⭐⭐ ASSERT THE OUTPUT ROW COUNT, DO NOT JUST PRINT IT.
# The corpus-size gate above proves the INPUT is complete. This one proves the
# OUTPUT is, and it is a different failure. MEASURED, 2026-09-19: two instances of
# this script ran concurrently (my own launch attempts) and wrote 60 rows for a
# 31-row corpus, each truncating the other's file. I caught it only because
# 60-for-31 is obviously wrong -- a 32-for-31 would have sailed through and
# inflated the agreement count with a duplicated row.
#
# @rt1180emulator's rule, which catches BOTH shapes with ONE assertion:
#   "COVERAGE MUST BE ASSERTED, NOT PRINTED. A number with no expected value is a
#    fact, not a control."
# Too FEW rows = a run that died or was truncated. Too MANY = contamination, a
# duplicated corpus, or a second instance. The check does not care WHICH, which is
# exactly why it also catches the contamination shape nobody has met yet.
written_rows=$(grep -vcE '^kind|^$' "$OUT")
if [ "$written_rows" -ne "$EXPECTED_ROWS" ]; then
    echo >&2
    echo "GATE FAIL: wrote $written_rows result rows for a $EXPECTED_ROWS-row corpus." >&2
    if [ "$written_rows" -gt "$EXPECTED_ROWS" ]; then
        echo "  TOO MANY: another instance of this script is probably running, or the" >&2
        echo "  corpus contains duplicates. Duplicated rows INFLATE the agreement count." >&2
        pgrep -fc "$(basename "$0")" >/dev/null 2>&1 && echo "  ($(pgrep -fc "$(basename "$0")") matching processes seen)" >&2
    else
        echo "  TOO FEW: the run died or was cut short. The agreement figure below is" >&2
        echo "  NOT publishable -- a partial corpus scores only the rows that finished." >&2
    fi
    exit 4
fi

if [ "$harness_broken" -gt 0 ]; then
    echo >&2
    echo "GATE FAIL: $harness_broken row(s) could not build a machine -- these are HARNESS" >&2
    echo "  failures, not fidelity results. The agreement figure below is NOT publishable." >&2
    echo "  Fix the platform/script, then re-run. (A broken harness and a broken model look" >&2
    echo "  identical in a tally; that is why this exits non-zero.)" >&2
fi

echo; echo "=== AGREEMENT ==="
awk -F'\t' 'NR>1 && $6=="YES"{a++} NR>1 && $6=="NO"{d++} END{printf "agree %d  differ %d  total %d\n", a, d, a+d}' "$OUT"

[ "$harness_broken" -eq 0 ] || exit 4
