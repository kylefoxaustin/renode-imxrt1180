#!/usr/bin/env bash
# ⭐⭐ RUN THE ORACLE'S OWN VALUE-TEST BINARIES ON RENODE.
#
# The cm7 `pmsm_enc` corpus row has NO console success string on either tool, so
# @rt1180emulator proves it with hand-written bare-metal tests whose goldens are
# derived from FIRST PRINCIPLES (Ohm's law, Clarke, datasheet motor constants) --
# not from either model's internals. Running THEIR binaries on MY models is the
# project's core method applied to the one row a console oracle cannot reach:
# same bytes, both tools, an expectation independent of both.
#
# Their tests link to ITCM 0x0FFE0000 / DTCM 0x20000000 (the CM33 map) and report
# via SEMIHOSTING (bkpt 0xAB / SYS_WRITE0), not a UART -- so the platform wires
# CPU.SemihostingHandler + UART.SemihostingUart, and the output is captured from
# `cpu.semihosting.semihosting_uart`.
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ORACLE="${ORACLE:-$HOME/Documents/GitHub/rt1180emulator}"
SECS="${SECS:-5}"
OUT="$HERE/results/value-tests.tsv"
CON="$HERE/results/console-value"; mkdir -p "$CON"

cat > /tmp/rt1180renode-value.resc <<RESC
using sysbus
include @$HERE/scripts/load_peripherals.resc
mach create "m"
machine LoadPlatformDescription @$HERE/platforms/mimxrt1189_cm33_motor.repl
sysbus Redirect 0x50000000 0x40000000 0x10000000
RESC

# ⭐⭐ SCOPE IS DECLARED, NOT DISCOVERED BY RUNNING EVERYTHING.
# The first version of this sweep ran EVERY oracle test on the CM33 motor
# platform and duly reported FAIL next to asrc.elf and edma/dma.elf, and
# NO-OUTPUT next to the dualcore and cm7-* binaries. Those are not their tests
# failing and not my models failing -- that platform has no ASRC, no SAI, no
# eDMA and one core. Publishing them as FAIL would have been exactly the sin this
# project keeps catching: a harness limitation wearing the shape of a finding,
# in the direction that makes the other tool look bad.
#
# So the in-scope set is listed explicitly, with the reason, and everything else
# is reported OUT-OF-SCOPE -- which is a statement about THIS PLATFORM, never a
# verdict on the model or on the test.
# ⭐ THE IN-SCOPE SET IS THE ORACLE'S OWN, NOT MY GUESS AT IT.
# @rt1180emulator supplied the mapping of their tests to platforms after seeing my
# first (wrongly scoped) table. Their MOTOR/ADC set is exactly this list; the rest
# belong on dual-core, audio, ethernet or misc-CM33 platforms this row does not
# need. Taking their mapping rather than inferring one removes the last place my
# scoping could quietly mark their tests as failures.
IN_SCOPE="imxrt1180-motor imxrt1180-motor-load imxrt1180-motor-sat imxrt1180-motor-thermal \
          imxrt1180-adc imxrt1180-adc-ab imxrt1180-adc-fifo-align imxrt1180-pwmadc \
          imxrt1180-eqdc imxrt1180-pwm imxrt1180-pwm-dma imxrt1180-lpadc-dma"
# Out of scope on mimxrt1189_cm33_motor.repl, and why:
#   *asrc*, *sai*      -> audio subsystem not on this platform
#   *edma*, *dma*      -> no eDMA controller wired here
#   *dualcore*, *cm7*  -> single-core platform; the M7 needs mimxrt1189_m2.repl
#   *netc*, *usb*, ... -> unrelated subsystems

printf 'test\telf\tpass\tfail\tverdict\n' > "$OUT"
pass_rows=0; fail_rows=0; silent=0; skipped=0
for d in "$ORACLE"/tests/imxrt1180-*/; do
    t="$(basename "$d")"
    elf="$(ls "$d"*.elf 2>/dev/null | head -1)"
    [ -n "$elf" ] || continue
    case " $IN_SCOPE " in
      *" $t "*) ;;
      *) printf '%s\t%s\t-\t-\tOUT-OF-SCOPE\n' "$t" "$(basename "$elf")" >> "$OUT"
         skipped=$((skipped+1)); continue ;;
    esac

    raw="$CON/$t.txt"; rm -f "$raw"
    ( cd "$(dirname "$RENODE")" && timeout -k 20 300 "$RENODE" --console --disable-xwt --plain \
        -e "include @/tmp/rt1180renode-value.resc" \
        -e "cpu.semihosting.semihosting_uart CreateFileBackend @$raw true" \
        -e "sysbus LoadELF @$elf" \
        -e "emulation RunFor \"$SECS\"" -e quit </dev/null ) > "$CON/$t.log" 2>&1

    p=$(grep -ac "PASS" "$raw" 2>/dev/null); p=${p:-0}
    f=$(grep -ac "FAIL" "$raw" 2>/dev/null); f=${f:-0}
    if [ "$p" -eq 0 ] && [ "$f" -eq 0 ]; then
        # ⭐ SILENCE IS NOT A PASS AND NOT A FAIL. A test that printed nothing did
        # not run -- treat it as a harness result, never as a model verdict.
        v="NO-OUTPUT"; silent=$((silent+1))
    elif [ "$f" -gt 0 ]; then
        v="FAIL"; fail_rows=$((fail_rows+1))
    else
        v="PASS"; pass_rows=$((pass_rows+1))
    fi
    printf '%s\t%s\t%s\t%s\t%s\n' "$t" "$(basename "$elf")" "$p" "$f" "$v" >> "$OUT"
    printf '  %-10s %-28s pass=%-3s fail=%-3s\n' "$v" "$t" "$p" "$f"
done
echo
echo "value tests (in scope): $pass_rows PASS, $fail_rows FAIL, $silent NO-OUTPUT"
echo "out of scope on this platform (NOT a verdict): $skipped"
echo "  -> $OUT"
