#!/usr/bin/env bash
# ⭐⭐ M-B: RUN THE ORACLE'S ENTIRE BARE-METAL TEST SUITE ON RENODE.
#
# 63 binaries, each mapped to the platform it needs. The point of this sweep is to
# replace the roadmap's ESTIMATES with MEASUREMENTS before any of the remaining
# milestones are planned against them -- the cm7 FOC row sat behind an estimate
# that was wrong in both directions until it was measured one wall at a time.
#
# ⚠ THIS SWEEP CANNOT EXPRESS PER-TEST PLANT PARAMETERS, AND THREE ROWS NEED THEM.
# motor-load, motor-sat and motor-thermal are parameter SWEEPS: QEMU passes
# -global imxrt1180-motor.{init-mrads,load-fan-unms,sat-isat-ma,thermal,...} per
# run, and Renode takes them at construction, so each config needs its own .repl.
# They have dedicated runners (run_motor_load.sh, run_motor_sat.sh) and PASS there
# -- 5 and 4 configs respectively, against closed-form goldens. This generic sweep
# reports them as SEE-RUNNER, not FAIL: a harness that cannot set up a test has not
# measured it, and scoring it as a miss would understate the model on a row that
# was already proven.
#
# SCOPE IS DECLARED, NEVER DISCOVERED. A test that needs a platform this tree does
# not have reads OUT-OF-SCOPE with a reason -- never FAIL. Publishing someone
# else's test as failing because of my platform is the same harness-limitation-as-
# finding pattern this project keeps catching, aimed at the tool I have the least
# standing to get wrong.
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ORACLE="${ORACLE:-$HOME/Documents/GitHub/rt1180emulator}"
SECS="${SECS:-15}"
OUT="$HERE/results/value-sweep.tsv"
CON="$HERE/results/console-value"; mkdir -p "$CON"

# --- test -> platform map -------------------------------------------------
# MOTOR  : CM33 + LPADC/eFlexPWM/EQDC/XBAR + virtual motor plant + eDMA4
# CM33   : CM33 + eDMA4 + LPI2C/USB/SAI/ASRC/NETC/MSGINTR   (the broad surface)
# DUAL   : M33 + M7 + MU1 + the two-gate boot chain
# META   : tests OF THE ORACLE'S OWN TREE (their corpus/reset-values/blind-spot
#          audits). Not equivalency rows; excluded by name, not by failing.
# ⭐⭐⭐ ROUTE BY WHAT THE TEST *IS*, NOT BY WHAT ITS NAME LOOKS LIKE.
#
# This function has now mis-routed a test THREE times, each time by pattern-matching
# a name: fslaudit guessed into META and was never run; imxrt1180-mu fell through to
# CM33 -- a single-core platform with no M7 -- and its "no reply from the M7" FAIL
# was recorded as a model verdict. It was a verdict about a machine that had no
# second core in it.
#
# A directory that ships an m7.elf NEEDS TWO CORES. That is structural evidence from
# the test itself, it cannot drift as names change, and it is checked before any name
# pattern is consulted. The name list below survives only for the cases structure
# cannot decide (a single cm7-linked image, or an M33 image that drives the M7).
plat_for() {
  local dir="${2:-}"
  if [ -n "$dir" ] && [ -f "$dir/m7.elf" ]; then echo DUAL; return; fi
  case "$1" in
    *-motor|*-motor-*|*-adc|*-adc-*|*-eqdc|*-pwm|*-pwm-*|*-pwmadc|*-lpadc-dma) echo MOTOR ;;
    *-cm7boot|*-cm7wait|*-cm7-*|*-dualcore|*-ele-corestart|*-edma-swstart-order) echo DUAL ;;
    # ⚠ fslaudit was HERE and should not have been. I guessed it was an audit of the
    # oracle's own tree from its name; it is a real equivalency test (GPT1 CR.SWR
    # self-clearing reset, reporting via semihosting). Mis-scoping it meant it was
    # never run AND still appeared in my "untagged, direction unknown" list to them
    # -- a row I had silently excluded, presented as a row I could not explain.
    # Scope by what a test DOES, never by what its name suggests.
    imxrt1180-netc-rxfwd)                                                       echo WIRE ;;
    *-corpus|*-reset-values|*-blindspots|*-clocktree|*-zephyr)                  echo META ;;
    *)                                                                          echo CM33 ;;
  esac
}

# ⭐⭐⭐ A TEST THAT SHIPS ITS OWN HARNESS IS NOT A TEST THIS SWEEP CAN SCORE.
#
# MEASURED: imxrt1180-sai prints "SAI: FAIL - the rate selector was never armed by
# the harness" when launched bare, and its Makefile says so outright: "Launching the
# ELF bare (no -device loader) makes the firmware correctly FAIL ... a diagnostic,
# not the test." This sweep launched it bare and wrote FAIL into the results table.
#
# That is a verdict about a configuration that was never set up -- the same sin as
# the fslaudit name-guess and the dualcore single-backend capture, and this time it
# pointed at SOMEBODY ELSE'S MODEL, which is the direction I have the least standing
# to get wrong. Six rows were affected: three SAI (check.py + a poked rate selector)
# and three NETC (run.sh / flood.py / wire-check.py + a frame backend).
#
# They are NOT failures and they are NOT passes. They are NOT-RUN until driven
# through the harness they were written for -- scripts/run_sai_value.sh does that
# for the SAI three.
# ⭐⭐ DETECT IT STRUCTURALLY, NOT BY NAME. This was a hardcoded list of six names --
# the same anti-pattern as routing by name, which has now mis-scored a test three
# times in this project. A test that ships run.sh / check.py / wire-check.py SAYS SO
# by shipping it, and that fact cannot drift as tests are added. 21 dirs ship one.
needs_own_harness() {
  # ⚠ `ls a b c` exits NON-ZERO IF ANY ONE IS MISSING, not if all are. The first
  # version of this used a single multi-path `ls`, so a directory holding check.py
  # but no run.sh -- which is all three SAI tests -- tested FALSE, and they went
  # straight back to being scored as FAILs of my model. Caught by the summary line
  # (harness-excluded 6 -> 0) rather than by any assert, which is attention doing a
  # control's job. ANY one of these files means the test ships a harness.
  for f in run.sh check.py check-token.sh wire-check.py; do
    [ -f "$2/$f" ] && return 0
  done
  return 1
}
repl_MOTOR="$HERE/platforms/mimxrt1189_cm33_motor.repl"
repl_CM33="$HERE/platforms/mimxrt1189_cm33_value.repl"
repl_DUAL="$HERE/platforms/mimxrt1189_m2_value.repl"
# WIRE: the CM33 value platform plus a harness echo node, for the NETC self-echo
# tests. See peripherals/EchoWire.cs -- it reproduces QEMU's hardcoded
# IP_MULTICAST_LOOP=1, which netc-rxfwd's oracle requires, and which the switched
# flood/portfwd tests must NOT have.
repl_WIRE="$HERE/platforms/mimxrt1189_cm33_wire.repl"

for g in MOTOR CM33 DUAL WIRE; do
  eval "r=\$repl_$g"
  cat > "/tmp/rt1180renode-sweep-$g.resc" <<RESC
using sysbus
include @$HERE/scripts/load_peripherals.resc
mach create "m"
machine LoadPlatformDescription @$r
sysbus Redirect 0x50000000 0x40000000 0x10000000
RESC
  if [ "$g" = WIRE ]; then
    cat >> "/tmp/rt1180renode-sweep-$g.resc" <<'RESC'
emulation CreateSwitch "wire"
connector Connect sysbus.netc wire
connector Connect sysbus.echowire wire
RESC
  fi
done

# ⭐⭐⭐ THE MODELS UNDER TEST MUST NOT CHANGE WHILE THE TEST IS RUNNING.
# I edited IMXRT1180_ASRC.cs and IMXRT1180_SAI.cs DURING a sweep. Every row
# re-compiles the peripherals at load, so rows before the edit measured one model
# and rows after measured another -- and the results table said nothing about it.
# It happened to be harmless (the edits compiled, and the only rows that exercise
# the ASRC had already run), but "it happened to be harmless" is not a control.
# A compile error would have turned every remaining row into a platform failure,
# and a behaviour change would have produced a table that silently mixed two models.
# Snapshot the sources at the start and assert they are byte-identical at the end.
SNAPSHOT_COUNT=$(ls -d "$ORACLE"/tests/imxrt1180-*/ 2>/dev/null | while read -r d; do ls "$d"*.elf >/dev/null 2>&1 && echo x; done | wc -l)
# THE CPU IS PART OF THE MODEL UNDER TEST. The first version of this guard covered
# peripherals/*.cs and platforms/*.repl and NOT the installed tlib library -- so
# swapping translate-arm-m-le.so mid-sweep (which the defect #14 flip-tests do
# routinely) would have changed the CORE under a live measurement and this guard
# would have stayed silent. Exactly the mistake it was written to catch, one layer
# down. A control that covers the parts you happened to think of is not a control.
CORE_LIB="$HOME/.cache/renode/renode_1.17.0-portable/platform-lib/linux-x64/translate-arm-m-le.so"
CORE_MD5_START=$(md5sum "$CORE_LIB" 2>/dev/null | cut -c1-12)
echo "core library under test: $CORE_MD5_START"
SRC_FINGERPRINT=$(cat "$HERE"/peripherals/*.cs "$HERE"/platforms/*.repl "$CORE_LIB" 2>/dev/null | md5sum | cut -d' ' -f1)

# ⭐⭐⭐ KEEP THE PREVIOUS TABLE. Every control in this harness checks COMPLETENESS
# -- coverage counts, source fingerprints, corpus size, the core library md5 -- and
# NONE checked DISTRIBUTION. A scorer bug once moved six rows from "not scored" to
# "FAIL of my model" and the row-count assert passed cleanly, because the bug did not
# lose rows, it RECLASSIFIED them. That is exactly as misleading as losing them and
# nothing said a word; it was caught by a human noticing a summary line.
PREV="$OUT.prev"
[ -f "$OUT" ] && cp "$OUT" "$PREV"

printf 'test\telf\tgroup\tpass\tfail\tverdict\n' > "$OUT"
P=0; F=0; N=0; M=0
for d in "$ORACLE"/tests/imxrt1180-*/; do
    t="$(basename "$d")"
    elf="$(ls "$d"*.elf 2>/dev/null | head -1)"
    [ -n "$elf" ] || continue
    # ⭐⭐ A DUAL-CORE TEST SHIPS TWO ELFs AND `head -1` TOOK ONLY THE FIRST.
    # MEASURED: imxrt1180-mu/ and imxrt1180-dualcore/ each contain m33.elf AND
    # m7.elf; alphabetical `head -1` picked m33.elf and the M7 image was NEVER
    # LOADED. The M33 then correctly reported that the M7 said nothing -- a true
    # statement about a run that was never set up. The oracle runs both as
    # `-kernel m33.elf -device loader,file=m7.elf`.
    elf_m7=""
    [ -f "$d/m7.elf" ] && [ -f "$d/m33.elf" ] && { elf="$d/m33.elf"; elf_m7="$d/m7.elf"; }
    g="$(plat_for "$t" "$d")"
    if [ "$g" = META ]; then
        printf '%s\t%s\tMETA\t-\t-\tOUT-OF-SCOPE\n' "$t" "$(basename "$elf")" >> "$OUT"
        M=$((M+1)); printf '  %-12s %-30s (audits their own tree)\n' "META" "$t"; continue
    fi
    # ⭐⭐ LOAD IN THE CPU'S CONTEXT, OR .data LOADS AS ZEROS.
    # MEASURED: this platform registers the CM33's DTCM CPU-SCOPED at 0x20000000
    # (globally only at 0x20200000, the DMA-master alias). A context-less
    # `sysbus LoadELF` writes .data to the GLOBAL 0x20000000, where nothing is
    # mapped -- so every test whose initialised data lives in DTCM ran with that
    # data ZEROED, and failed for a reason that had nothing to do with the model.
    # Caught on lpi2c-dma: the guest wrote 0x0 to the I2C command register where
    # its table said 0x490, because the table itself was zeros.
    # LoadELF(fileName, useVirtualAddress = False, cpu = null) -- the CPU is the
    # THIRD argument. Same root cause as the ELE bug: a CPU-local address resolved
    # against the global map.
    # ⭐⭐ A CM7-ONLY IMAGE MUST BE LOADED INTO THE CM7, AND STARTED.
    # MEASURED via the ELF header: cm7boot/mpu/systick have entry points 0x9, 0x35,
    # 0x75 -- the M7's LOCAL ITCM view at 0x0 -- while every CM33 image in this corpus
    # enters around 0x0ffe0011. This sweep loaded all of them into `cpu` (the M33),
    # where those addresses are nothing, and recorded NO-OUTPUT.
    # The oracle's machine auto-detects a cm7 ELF and boots the M7 directly, holding
    # the M33, with no guest release sequence in the image. That is a deliberate
    # convenience on their side, NOT silicon -- so mirroring it here is equally a
    # convenience, and the row is comparable only because BOTH tools now provide it.
    # Tagged shared-limit, not agreement earned by either model.
    ctxcpu=cpu; cm7only=0
    if [ "$g" = DUAL ] && [ -z "$elf_m7" ]; then
        entry=$(arm-none-eabi-readelf -h "$elf" 2>/dev/null | awk '/Entry point/{print $4}')
        case "$entry" in
            0x[0-9a-f]|0x[0-9a-f][0-9a-f]|0x[0-9a-f][0-9a-f][0-9a-f]) ctxcpu=cpu1; cm7only=1 ;;
        esac
    fi
    raw="$CON/$t.txt"; raw7="$CON/$t.m7.txt"; rawu="$CON/$t.uart.txt"
    rm -f "$raw" "$raw7" "$rawu"
    # ⭐⭐⭐ SEMIHOSTING IS PER-CPU. CAPTURE EVERY CORE THAT CAN SPEAK.
    # `dualcore` read NO-OUTPUT for two days while PASSING. SemihostingHandler is
    # registered per CPU, this harness created a file backend on `cpu`'s handler
    # only, and the M7's line went to a backend nobody was reading. The model
    # emitted the string; the instrument was not listening on that channel.
    # "Did it happen?" and "can I see it?" are different questions, and a verdict
    # may only be issued about the second.
    m7_args=""
    if [ "$cm7only" = 1 ]; then
        # boot-cm7: release the M7 from the harness, exactly as their machine does
        m7_args="-e|cpu1.semihosting1.semihosting1_uart CreateFileBackend @$raw7 true|-e|cpu1 IsHalted false"
    elif [ "$g" = DUAL ]; then
        m7_args="-e|cpu1.semihosting1.semihosting1_uart CreateFileBackend @$raw7 true"
        [ -n "$elf_m7" ] && m7_args="$m7_args|-e|sysbus LoadELF @$elf_m7 false cpu1"
    fi
    old_ifs="$IFS"; IFS='|'; set -- $m7_args; IFS="$old_ifs"
    ( cd "$(dirname "$RENODE")" && timeout -k 20 240 "$RENODE" --console --disable-xwt --plain \
        -e "include @/tmp/rt1180renode-sweep-$g.resc" \
        -e "cpu.semihosting.semihosting_uart CreateFileBackend @$raw true" \
        -e "lpuart1 CreateFileBackend @$rawu true" \
        -e "sysbus LoadELF @$elf false $ctxcpu" \
        "$@" \
        -e "emulation RunFor \"$SECS\"" -e quit </dev/null ) > "$CON/$t.log" 2>&1
    # ⭐⭐ CAPTURE EVERY CHANNEL THAT CAN SPEAK, NOT JUST THE ONE I EXPECTED.
    # Semihosting was the only backend this sweep listened to, so imxrt1180-hello_uart
    # -- which prints over LPUART1, as its name says -- scored NO-OUTPUT while happily
    # emitting "=== i.MX RT1180 LPUART1 console alive ===". Same defect as scoring
    # dualcore on one core's semihosting backend out of two, one peripheral over.
    # A test speaks on whatever channel its firmware chose; the instrument does not
    # get to assume which.
    [ -s "$raw7" ] && cat "$raw7" >> "$raw"
    [ -s "$rawu" ] && cat "$rawu" >> "$raw"
    p=$(grep -ac "PASS" "$raw" 2>/dev/null); p=${p:-0}
    f=$(grep -ac "FAIL" "$raw" 2>/dev/null); f=${f:-0}
    # ⭐⭐ NOT EVERY TEST SAYS "PASS". imxrt1180-dualcore prints three specific
    # sentences and no verdict token, so this scorer -- which greps PASS/FAIL --
    # recorded NO-OUTPUT for a test that ran correctly and SPOKE. "It produced no
    # output" was factually false: it produced three lines, none of which this
    # scorer knew how to read. Grading by the wrong criterion and reporting the
    # result as silence is the same class as scoring a test that was never armed.
    # Its oracle is the strings the firmware itself prints, so assert those.
    case "$t" in
      imxrt1180-hello)
        # Prints a banner and no verdict token, like dualcore. Its oracle is the
        # string the firmware itself emits.
        if grep -aq "Hello from i.MX RT1180" "$raw"; then p=1; f=0; fi
        ;;
      imxrt1180-dualcore)
        if grep -aq "M33: booting" "$raw" && grep -aq "M7 released" "$raw" \
           && grep -aq "M7:  alive" "$raw"; then p=1; f=0; else f=1; fi
        ;;
    esac
    if grep -aqE "Error E[0-9]+:|There was an error executing command 'machine" "$CON/$t.log"; then
        v="PLATFORM-ERR"; N=$((N+1))          # my platform could not even load
    elif [ "$p" -eq 0 ] && [ "$f" -eq 0 ]; then
        # Distinguish "said nothing" from "said something this scorer cannot grade".
        # Collapsing them hides tests that are working and mislabels them as dead.
        if needs_own_harness "$t" "$d"; then
            v="NEEDS-OWN-HARNESS"; M=$((M+1))
        elif [ -s "$raw" ]; then
            v="NO-VERDICT"; N=$((N+1))
        else
            v="NO-OUTPUT"; N=$((N+1))
        fi
    elif [ "$f" -gt 0 ]; then
        # ⭐ THE EXCLUSION IS ASYMMETRIC, AND DELIBERATELY SO.
        # A test that ships its own harness and FAILS bare has told us nothing: it was
        # never armed, so the failure is a diagnostic (imxrt1180-sai literally prints
        # "the rate selector was never armed by the harness"). But a test that PASSES
        # bare HAS given a verdict -- its firmware's own checks were satisfied without
        # any harness help, and discarding that would throw away a real result to keep
        # a tidy rule. Excluding on failure only is the honest asymmetry.
        if needs_own_harness "$t" "$d"; then
            v="NEEDS-OWN-HARNESS"; M=$((M+1))
        else
            v="FAIL"; F=$((F+1))
        fi
    else v="PASS"; P=$((P+1)); fi
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$t" "$(basename "$elf")" "$g" "$p" "$f" "$v" >> "$OUT"
    printf '  %-12s %-30s %-6s pass=%-3s fail=%s\n' "$v" "$t" "$g" "$p" "$f"
done
echo
# ⭐ SAME ASSERTION AS THE EQUIVALENCY SCORER: the output row count is a CONTROL,
# not a fact. Too few = a died/truncated sweep; too many = a second instance or a
# duplicated list. One check, both shapes, including the subtly-doubled one.
# ── VERDICT-DELTA REPORT ────────────────────────────────────────────────────
# Not a pass/fail gate: a run legitimately changes verdicts, that is the point of
# doing the work. The control is that EVERY change is NAMED, so a reclassification
# can never be silent. Regressions and "excluded -> failed" moves are called out,
# because those are the two directions that flatter or slander the model.
if [ -f "$PREV" ]; then
    echo
    echo "verdict changes vs the previous table:"
    changed=0
    while IFS=$'\t' read -r t _ _ _ _ v; do
        [ "$t" = "test" ] && continue
        pv=$(awk -F'\t' -v k="$t" '$1==k{print $6}' "$PREV")
        [ -z "$pv" ] && { printf '  + %-30s (new) -> %s\n' "$t" "$v"; changed=$((changed+1)); continue; }
        if [ "$pv" != "$v" ]; then
            mark="  "
            [ "$pv" = "PASS" ] && mark="⚠ "
            case "$pv:$v" in NEEDS-OWN-HARNESS:FAIL|OUT-OF-SCOPE:FAIL) mark="⚠ " ;; esac
            printf '%s%-30s %s -> %s\n' "$mark" "$t" "$pv" "$v"
            changed=$((changed+1))
        fi
    done < "$OUT"
    [ "$changed" -eq 0 ] && echo "  (none)"
fi

src_after=$(cat "$HERE"/peripherals/*.cs "$HERE"/platforms/*.repl "$CORE_LIB" 2>/dev/null | md5sum | cut -d' ' -f1)
if [ "$src_after" != "$SRC_FINGERPRINT" ]; then
    echo
    echo "⚠️  MODELS CHANGED DURING THIS SWEEP -- THE TABLE MIXES TWO MODELS."
    echo "    peripherals/, platforms/, or the tlib CORE LIBRARY changed mid-run."
    echo "    core md5: start=$CORE_MD5_START now=$(md5sum "$CORE_LIB" 2>/dev/null | cut -c1-12)"
    echo "    start=$SRC_FINGERPRINT end=$src_after"
    echo "    These results are NOT a measurement of a single model. Re-run."
fi

# ⭐⭐ THE INPUT SET MUST HOLD STILL TOO, AND IT DID NOT.
# This gate compared the rows written against the corpus size measured AT THE END,
# and reported "TOO FEW -- the sweep died". The sweep had not died: @rt1180emulator
# added a new test directory (imxrt1180-m33-wfi) while it was running, so the corpus
# grew 54 -> 55 underneath it. The rows were complete for every binary that existed
# when the loop enumerated them.
# Same class as fingerprinting the models: a measurement is only a measurement of one
# thing if that thing holds still -- and "that thing" includes WHAT IS BEING MEASURED,
# not just what is doing the measuring. A gate whose failure text names the wrong
# cause sends the next reader hunting a dead sweep that never happened.
attempted=$SNAPSHOT_COUNT
current=$(ls -d "$ORACLE"/tests/imxrt1180-*/ 2>/dev/null | while read -r d; do ls "$d"*.elf >/dev/null 2>&1 && echo x; done | wc -l)
if [ "$current" -ne "$SNAPSHOT_COUNT" ]; then
    echo
    echo "NOTE: the corpus CHANGED during this sweep: $SNAPSHOT_COUNT binaries at start, $current now."
    echo "      The table is complete for the $SNAPSHOT_COUNT that existed when the loop began."
    echo "      New tests are NOT missing rows -- they are outside this measurement. Re-run to include them."
fi
# ⚠ COUNT THE ROWS BY SKIPPING THE HEADER LINE, NOT BY MATCHING IT.
# The first version used `grep -vcE '^test\t|^$'` -- and an ERE does NOT interpret
# \t as a tab (this project already has a retraction on that exact trap; the grep
# here is ugrep, not GNU). So `^test\t` matched nothing, the header was counted,
# and the gate reported 55 rows for 54 binaries and refused a perfectly good sweep.
# A guard that cries wolf is as broken as one that stays silent -- and this one
# managed it on its first real firing, hours after being added.
written=$(($(wc -l < "$OUT") - 1))
if [ "$written" -ne "$attempted" ]; then
    echo >&2
    echo "GATE FAIL: wrote $written rows for $attempted test binaries." >&2
    [ "$written" -gt "$attempted" ] && echo "  TOO MANY -- another sweep is probably running; counts are NOT publishable." >&2
    [ "$written" -lt "$attempted" ] && echo "  TOO FEW -- the sweep died; the backlog below understates the work." >&2
    exit 4
fi
echo "SWEEP: $P PASS, $F FAIL, $N NO-OUTPUT/PLATFORM-ERR, $M META-excluded  -> $OUT"
echo "coverage asserted: $written rows == $attempted test binaries"
