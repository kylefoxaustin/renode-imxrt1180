#!/usr/bin/env bash
# Zephyr delta study: run the SAME pinned ELF on BOTH tools and compare verdicts.
# Covers cm33 and cm7. This is the study Kyle asked for at the start of the project.
#
# Unblocked by @rt1180emulator's fix 3c9b8d266a ("give the M7 its real 16-region
# MPU"): before it, their cm7 ztest targets panicked in init_mem_domain_module
# before the console came up, so every cm7 ztest row was Renode-only and the
# delta study was structurally one-sided.
#
# Both verdicts are MEASURED here, on the same box, from the same bytes.
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
QEMU="${QEMU:-$HOME/Documents/GitHub/rt1180emulator/build/qemu-system-arm}"
ART="${ART:-$HOME/.cache/rt1180-artifacts/zephyr}"
# ⭐ OUTPUT PATHS ARE OVERRIDABLE so a re-cut on a DIFFERENT CORE LIBRARY cannot
# overwrite the published baseline it is meant to be compared against. Every number
# in this project is tied to the binary it was measured on; a re-run that clobbers
# its own control is not a comparison.
OUT="${OUT:-$HERE/results/zephyr-delta.tsv}"
CON="${CON:-$HERE/results/console-delta}"; mkdir -p "$CON"
# SECS is retained only as the historical name; both tools now have their own
# guard (QSECS / RSECS) because they are not comparable quantities.
SECS="${SECS:-25}"
RUNFOR="${RUNFOR:-90}"

# ⭐ RENODE'S WALL-CLOCK GUARD IS ITS OWN KNOB, AND A TRUNCATED RUN IS NEVER
# SCORED AS A FIDELITY DELTA.
# This was a real defect in THIS instrument, caught 2026-09-18. The guard used to
# be `timeout -k 20 $((SECS*3))` = 75 s of HOST time, derived from QEMU's 25 s
# budget. But Renode is ~5x slower in host time per unit of virtual time, so the
# guard bound the Renode side long before RUNFOR did. MEASURED:
# cm33-tests-kernel-sched-schedule_api needs 14.66 s of guest time and ~72 s of
# host time; the old guard killed it at 13.93 s guest / ~75 s host, yielding a
# 150-line capture against QEMU's 217 -- scored DIFFER, published as a delta.
# Re-run with a real budget: 217 lines vs 217, and the ONLY differences are three
# reported durations rounding 1.807 -> 1.806 s.
# THE BUG WAS NOT THE NUMBER 75. It was that a harness budget could turn
# silently into a fidelity finding. So the guard is now (a) sized for Renode
# rather than borrowed from QEMU and (b) DETECTED: a run killed by `timeout`
# (rc 124/137) is reported as `truncated`, which is a harness verdict, never an
# agreement verdict and never a disagreement verdict.
RSECS="${RSECS:-420}"

# ⭐ AND THE SAME BIAS EXISTED ON THE QEMU SIDE. Having fixed Renode's guard I
# went looking for the mirror image, because a fix applied to one column of a
# two-column comparison is not a fix -- it is a new bias.
# It was there. QEMU ran under `timeout -k 5 $SECS` = 25 s, and MEASURED:
# cm7-tests-kernel-mutex-mutex_api reached 30 lines in 25 s and 68 lines with a
# 300 s budget -- and those 68 lines are BYTE-IDENTICAL to Renode's 68. A perfect
# two-tool agreement had been published as `partial`.
#
# Note the structural asymmetry that remains and cannot be removed: Renode is
# bounded by VIRTUAL time (`RunFor`) with a host-time guard behind it, while QEMU
# has only the host-time guard. For a terminating ztest suite both sides should
# reach the end, so both budgets must be generous. For a free-running sample
# (philosophers, synchronization) NEITHER side terminates and the captures are
# budget-bounded by construction -- which is why the comparison is over the common
# prefix, and why a `timeout` kill is normal on the QEMU side and is NOT scored as
# `truncated` there.
QSECS="${QSECS:-300}"

# ⚠ NO SHARED ORACLE, AND NO SHARED PASS/FAIL. Two reasons, both mistakes I made
# in the first version of this script and had already recorded earlier in the
# project:
#   1. A BLANKET ORACLE OVER A WHOLE TREE IS UNSAFE. My first cut matched
#      'PROJECT EXECUTION SUCCESSFUL' -- a ZTEST string -- against every target,
#      so every SAMPLE (hello_world, philosophers, synchronization) scored FAIL on
#      both tools while printing perfectly. Same fault as the philosophers and
#      cbprintf_fp rows earlier.
#   2. A DELTA STUDY IS NOT A PASS/FAIL STUDY. The question is whether the two
#      tools produce the SAME CONSOLE from the same bytes -- so compare the
#      consoles, which needs no per-target oracle at all and cannot be wrong about
#      what a target was supposed to print.
#
# Capture lengths legitimately differ (run duration, not model), so the comparison
# is over the COMMON PREFIX -- the fix established earlier when a whole-file diff
# reported 13 false differences on `synchronization`.
NM="$(command -v arm-none-eabi-nm || echo nm)"
OBJDUMP="$(command -v arm-none-eabi-objdump || echo objdump)"

printf 'target\tqemu_lines\trenode_lines\tverdict\n' > "$OUT"
for elf in "$ART"/cm33-*.elf "$ART"/cm7-*.elf; do
    t="$(basename "$elf" .elf)"

    # ---- QEMU (cm7 image; -icount shift=3 per the QEMU corpus' cm7 policy) ----
    q="$CON/qemu_$t.txt"; : > "$q"
    timeout -k 5 "$QSECS" "$QEMU" -M mimxrt1180-evk -audio none -display none -monitor none \
        -kernel "$elf" -serial "file:$q" -icount shift=3 \
        -semihosting-config enable=on,target=native </dev/null >/dev/null 2>&1

    # ⭐ CAPTURE THE UART RAW, NOT BY SCRAPING RENODE'S LOG.
    # Renode's line-based UART logging DROPS LEADING WHITESPACE: MEASURED, the
    # firmware's "\tAssert occurring inside kernel panic" reached the log as
    # "Assert occurring inside kernel panic", and the study scored that row a
    # content DIFFER against QEMU's byte-exact capture. That is an artifact of HOW
    # I CAPTURE, not a difference between the models -- and it was about to be
    # published as a fidelity delta.
    # CreateFileBackend writes the UART's bytes, so both sides are now byte-exact
    # and no normalisation of the payload is needed at all.
    case "$t" in
      cm7-*) RESC="$HERE/scripts/rt1180_cm7.resc"; UART=lpuart12 ;;
      *)     RESC="$HERE/scripts/rt1180_zephyr.resc"; UART=lpuart1 ;;
    esac

    # ---- Renode (VTOR/SP/PC read from the ELF: Zephyr's XIP build puts a boot
    # header before the vector table, so they are extracted, never guessed) ----
    r="$CON/renode_$t.txt"; : > "$r"; rrc=0
    rraw="$CON/renode_$t.raw"; rm -f "$rraw"
    VT=$($NM "$elf" | awk '/ _vector_table$/{print "0x"$1; exit}')
    if [ -n "$VT" ]; then
        read SP PC < <($OBJDUMP -s --start-address="$VT" --stop-address="$((VT+8))" "$elf" \
          | awk '/^ /{print strtonum("0x" substr($2,7,2) substr($2,5,2) substr($2,3,2) substr($2,1,2)), strtonum("0x" substr($3,7,2) substr($3,5,2) substr($3,3,2) substr($3,1,2)); exit}')
        ( cd "$(dirname "$RENODE")" && timeout -k 20 "$RSECS" "$RENODE" --console --disable-xwt --plain \
            -e "\$elf=@$elf" -e "\$vt=$VT" -e "\$sp=$(printf '0x%X' "$SP")" -e "\$pc=$(printf '0x%X' "$PC")" \
            -e "include @$RESC" \
            -e "$UART CreateFileBackend @$rraw true" \
            -e "emulation RunFor \"$RUNFOR\"" -e quit </dev/null ) > "$r" 2>&1
        rrc=$?
    fi

    # Normalise: strip Renode's log furniture and CRs, keep the firmware's text.
    # Both sides: strip NULs and CRs only. No payload rewriting.
    qn="$CON/qemu_$t.norm"; tr -d '\0\r' < "$q" > "$qn"
    rn="$CON/renode_$t.norm"; tr -d '\0\r' < "$rraw" 2>/dev/null > "$rn" || : > "$rn"

    ql=$(wc -l < "$qn"); rl=$(wc -l < "$rn")
    common=$(( ql < rl ? ql : rl ))
    # ⭐⭐ COMPLETION IS CHECKED FROM THE CONTENT, NOT FROM THE CLOCK.
    # The exit-code check below catches a Renode run the guard killed. It CANNOT
    # catch the worse case, and I know that because it happened: MEASURED,
    # cm7-tests-kernel-sched-schedule_api was truncated on BOTH sides at nearly
    # the same point (qemu 139 lines, renode 140, both stopping mid-suite inside
    # test_slice_scheduling) and therefore scored **identical** -- a FALSE
    # AGREEMENT, in the flattering direction, that my earlier audit could not see
    # because that audit only examined DIFFER rows.
    #
    # ⭐ TWO TRUNCATED CAPTURES AGREE WITH EACH OTHER PERFECTLY. A harness that
    # detects truncation only by disagreement is blind to exactly the case where
    # truncation costs the study its meaning.
    #
    # So: a ztest target announces itself ("Running TESTSUITE") and ends with
    # PROJECT EXECUTION SUCCESSFUL/FAILED. A ztest capture without that terminal
    # marker did not finish -- whether the clock cut it or not. A guest that
    # HALTED on a fatal error (the arm_interrupt / arm_thread_swap / arm_mpu_wt
    # rows) is a model difference and must NOT be reclassified, so a capture
    # carrying a Zephyr fatal-error marker counts as complete-for-this-purpose.
    is_ztest=0; grep -qaF 'Running TESTSUITE' "$qn" "$rn" 2>/dev/null && is_ztest=1
    finished() {  # $1 = capture file
        grep -qaE 'PROJECT EXECUTION (SUCCESSFUL|FAILED)' "$1" && return 0
        grep -qaE 'ZEPHYR FATAL ERROR|Halting system' "$1" && return 0   # guest halt, not a cut
        return 1
    }
    incomplete=0
    if [ "$is_ztest" -eq 1 ]; then
        finished "$qn" || incomplete=1
        finished "$rn" || incomplete=1
    fi

    if [ "$rrc" -eq 124 ] || [ "$rrc" -eq 137 ] || [ "$incomplete" -eq 1 ]; then
        # Either the Renode side was killed by the wall-clock guard, or a ztest
        # capture never reached its terminal marker. Whatever the two captures
        # look like, this row measures the harness, not the models.
        verdict="truncated"
    elif [ "$ql" -eq 0 ] && [ "$rl" -eq 0 ]; then
        verdict="both-silent"
    elif [ "$ql" -eq 0 ]; then
        verdict="renode-only"
    elif [ "$rl" -eq 0 ]; then
        verdict="qemu-only"
    elif ! head -n "$common" "$qn" | diff -q - <(head -n "$common" "$rn") >/dev/null 2>&1; then
        verdict="DIFFER"
    elif [ "$ql" -eq "$rl" ]; then
        # ⭐ EQUAL LENGTH + MATCHING PREFIX = THE WHOLE CAPTURE MATCHES. This is
        # complete agreement at ANY length and must not be gated on a minimum line
        # count. MEASURED: cm33-hello_world prints exactly 2 lines on both tools --
        # total agreement -- and my first significance rule scored it "partial"
        # because 2 < the 4-line floor. A short program can never satisfy an
        # absolute floor, so the floor belongs on the COVERAGE case, not this one.
        # (Under-reporting agreement is the safer direction, but it is still wrong,
        # and a rule that structurally cannot pass certain inputs is a bug.)
        verdict="identical"
    elif [ "$common" -ge 4 ] && [ $(( common * 2 )) -ge "$ql" ] && [ $(( common * 2 )) -ge "$rl" ]; then
        # Different lengths: the prefixes match AND the shorter capture covers at
        # least half the longer one, over a non-trivial number of lines.
        verdict="identical"
    else
        # ⚠ PREFIXES MATCH BUT THE OVERLAP IS TOO SMALL TO MEAN ANYTHING.
        # MEASURED: cm33-samples-kernel-condition_variables-simple came back
        # qemu=162 lines, renode=1 -- and a 1-line common prefix scored
        # "identical". Comparing one line out of 162 and calling that agreement
        # INFLATES the headline in my own favour, which is the flattering
        # direction and therefore the one to distrust. Such rows are reported as
        # PARTIAL and are NOT counted as agreement.
        verdict="partial"
    fi
    printf '%s\t%s\t%s\t%s\n' "$t" "$ql" "$rl" "$verdict" >> "$OUT"
    printf '%-48s qemu=%-4s renode=%-4s %s\n' "$t" "$ql" "$rl" "$verdict"
done
echo
awk -F'\t' 'NR>1{n++; v[$4]++} END{printf "zephyr delta: %d targets\n", n; for(k in v) printf "  %-12s %d\n", k, v[k]}' "$OUT"
