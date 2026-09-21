#!/usr/bin/env bash
# SAI TX value test on Renode, against the oracle's own firmware and its own golden.
#
# ⭐ THE VERDICT IS RENDERED OUTSIDE THE GUEST. The firmware plays a waveform and
# says only that it played; the assertion is the SAMPLES, compared by check_sai.py.
# An in-guest oracle cannot tell a working SAI from one that accepts every write
# and emits nothing -- which is exactly what this model did until the sink landed.
#
# Two operating points, because one is not a test: a model that IGNORES TCR2[DIV]
# reproduces the golden perfectly at whichever single rate you happened to pick.
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ORACLE="${ORACLE:-$HOME/Documents/GitHub/rt1180emulator}"
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
# test:elf:divs:mclk -- the sweep points and the clock root each test programs.
# test:elf:divs:mclk:nsamples
TESTS="${TESTS:-imxrt1180-sai:sai.elf:14,29:24000000:4096 imxrt1180-sai-dma:saidma.elf:14,29:24000000:2048 imxrt1180-sai-audiopll:saiapll.elf:7,15:24576000:4096}"
OUTDIR="${OUTDIR:-$HERE/results/sai-value}"
mkdir -p "$OUTDIR"

SEL_ADDR=0x20001000
SEL_MAGIC=0x53414931          # "SAI1" -- the firmware REFUSES to run unarmed

rc=0
for spec in $TESTS; do
  tname="${spec%%:*}"; rest="${spec#*:}"
  telf="${rest%%:*}"; rest="${rest#*:}"
  divs="${rest%%:*}"; rest="${rest#*:}"
  mclk="${rest%%:*}"; nsamp="${rest#*:}"
  ELF="$ORACLE/tests/$tname/$telf"
  if [ ! -f "$ELF" ]; then echo "  SKIP $tname (no $telf)"; continue; fi
  echo "=== $tname (MCLK ${mclk} Hz) ==="
  for div in $(echo "$divs" | tr ',' ' '); do
    wav="$OUTDIR/$tname-$div.wav"
    con="$OUTDIR/$tname-$div.txt"
    log="$OUTDIR/$tname-$div.log"
    rm -f "$wav" "$con"
    cat > "/tmp/rt1180renode-sai-$tname-$div.resc" <<RESC
using sysbus
include @$HERE/scripts/load_peripherals.resc
mach create "sai"
machine LoadPlatformDescription @$HERE/platforms/mimxrt1189_cm33_value.repl
sysbus Redirect 0x50000000 0x40000000 0x10000000
RESC
    # ⭐⭐ ORDER AND CONTEXT BOTH MATTER.
    # The selector lives at 0x20001000 in the CM33's DTCM, which this platform
    # registers CPU-SCOPED. A context-less write lands on the global map where
    # nothing is mapped, and the firmware would report "never armed" against a
    # write that appeared to succeed -- the same CPU-local-vs-global trap that
    # made .data load as zeros and that the ELE block fabricated success over.
    # It must also come AFTER LoadELF, or the image's own .data overwrites it.
    ( cd "$(dirname "$RENODE")" && timeout -k 20 300 "$RENODE" --console --disable-xwt --plain \
        -e "include @/tmp/rt1180renode-sai-$tname-$div.resc" \
        -e "cpu.semihosting.semihosting_uart CreateFileBackend @$con true" \
        -e "sysbus LoadELF @$ELF false cpu" \
        -e "sysbus WriteDoubleWord $SEL_ADDR $SEL_MAGIC cpu" \
        -e "sysbus WriteDoubleWord $((SEL_ADDR+4)) $div cpu" \
        -e "sai1 CreateWavBackend @$wav" \
        -e "emulation RunFor \"20\"" \
        -e "sai1 CloseWavBackend" -e quit </dev/null ) > "$log" 2>&1
    echo "--- div=$div ---"
    tr -d '\0\r' < "$con" 2>/dev/null | sed 's/^/    /'
    python3 "$HERE/scripts/check_sai.py" "$wav" "$div" "$mclk" "$nsamp" || rc=1
  done
done
exit $rc
