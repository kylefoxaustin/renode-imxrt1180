#!/usr/bin/env bash
# cm7 mc_pmsm FOC value harness.
#
# ⭐ THIS ROW HAS NO CONSOLE ORACLE ON EITHER TOOL. Every other row in the corpus
# is settled by a string the firmware prints; this one is settled by whether a
# (virtual) rotor turns. So "proof" has to be DEFINED, and the definition is the
# deliverable as much as the result.
#
# ⭐⭐ THE PROBE READS THE MACHINE THROUGH ITS REAL REGISTERS, NOT THROUGH THE
# PLANT'S C# STATE. Rotor angle comes from EQDC1's hold registers UPOSH:LPOSH and
# REVH, and speed from POSDH/POSDPERH -- the very registers the FOC control loop
# reads. A proof built on the plant's own properties could pass while the
# firmware-visible path was broken; this one cannot. (It is also the only option:
# MEASURED, a Renode `@ none` peripheral is not addressable from the monitor --
# Renode's own nvicInput7 on stm32f0.repl behaves the same way.)
#
#   usage: run_pmsm.sh [seconds-per-sample] [samples]
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ELF="${ELF:-$HOME/.cache/rt1180-artifacts/sdk/pmsm_enc_cm7.elf}"
STEP="${1:-1}"; SAMPLES="${2:-3}"
OUT="${OUT:-/tmp/rt1180renode-pmsm.log}"
[ -f "$ELF" ] || { echo "missing image: $ELF"; exit 2; }

# EQDC1 hold registers (16-bit) -- read via sysbus, no side effects.
#   0x42710014 UPOSH   0x42710016 LPOSH   0x4271001C REVH
#   0x42710012 POSDH   0x4271001A POSDPERH
# ⚠ THE PROBE RUNS WITHOUT RENODE'S CONSOLE MONITOR, AND THAT IS NOT A STYLE
# CHOICE. Three harness bugs and one Renode crash shaped this:
#
#  1. The first version assembled the -e flags into a string and `eval`-ed it; the
#     monitor received `include;` with the path silently stripped, the platform
#     never loaded, NO SAMPLES were produced -- and the script still exited 0. A
#     harness that reports nothing and succeeds is indistinguishable from *a rotor
#     that did not turn*, which is precisely what this row measures.
#  2. Renode's `echo` needs its argument quoted, or it answers
#     "No such emulation element: SAMPLE".
#  3. The console emits CR CR LF, so every `$`-anchored pattern in the decoder
#     silently matched nothing.
#  4. And with `--console` + stdin redirected, RENODE ITSELF ABORTS several minutes
#     into a long run:
#        System.Threading.SemaphoreFullException
#          at Antmicro.Renode.UI.ConsoleIOSource.RedirectedHandling()
#     Observed TWICE, both times after sample 1 of 5. Flag count is ruled out
#     (10/30/60/120 `-e "echo"` flags all pass), and moving the probe into the
#     .resc -- which I had already written down as the fix -- did NOT help: the
#     second crash had exactly ONE -e flag. The smoke test that "confirmed" that
#     fix ran for seconds; the real one runs for minutes, which is the only
#     variable that matters.
#
# So: `-P -1` (no GUI, no port, no ConsoleIOSource) and the state reaches the LOG
# via `eqdc1 LogState` instead of `sysbus ReadWord` through the monitor's output.
probe=""
for i in $(seq 1 "$SAMPLES"); do
    probe="$probe
emulation RunFor \"$STEP\"
eqdc1 LogState"
done

cat > /tmp/rt1180renode-pmsm.resc <<RESC
using sysbus
include @$HERE/scripts/load_peripherals.resc
mach create "rt1180cm7"
machine LoadPlatformDescription @$HERE/platforms/mimxrt1189_cm7_sdk.repl
sysbus Redirect 0x50000000 0x40000000 0x10000000
cpu PerformanceInMips 792
# WARNING: Renode log levels are NOISY=-1 DEBUG=0 INFO=1 WARNING=2 ERROR=3.
# A level of 3 is ERROR -- it silently swallowed the probe own INFO lines and the
# harness reported "0 samples" on a run that had executed perfectly. Keep the
# global level at WARNING to suppress bring-up noise, and lift the ONE peripheral
# the probe reads back to INFO.
# (No backticks in this heredoc: it is unquoted, so they would be command-
# substituted by the SHELL before Renode ever saw the line. That happened.)
logLevel 2
logLevel 1 eqdc1
macro reset
"""
    sysbus LoadELF @$ELF
"""
runMacro \$reset
$probe
RESC

cd "$(dirname "$RENODE")" || exit 2
timeout -k 30 "${TIMEOUT:-1800}" "$RENODE" -P -1 --disable-xwt --plain \
    -e "include @/tmp/rt1180renode-pmsm.resc" -e quit > "$OUT" 2>&1
rc=$?

# Decode: UPOSH:LPOSH is a 32-bit count in [0, 8000) per rev; REVH counts revs.
tr -d '\r' < "$OUT" | awk '
/EQDCSTATE/ {
    n++
    for(i=1;i<=NF;i++) { split($i,kv,"="); if(kv[1] in want) v[kv[1]]=kv[2] }
    printf "sample %-3d  revh=%-6s pos=%-8s total_counts=%-10s posdh=%-8s posdperh=%s\n", \
           n, v["revh"], v["pos"], v["total"], v["posdh"], v["posdperh"]
}
BEGIN { want["revh"]=1; want["pos"]=1; want["total"]=1; want["posdh"]=1; want["posdperh"]=1 }'

echo
# ⭐ NO SAMPLES IS A HARNESS FAILURE, NOT A RESULT. Say so loudly and exit
# non-zero, so it can never be mistaken for "the rotor did not turn".
got=$(grep -ac 'EQDCSTATE' "$OUT" 2>/dev/null); got=${got:-0}
if [ "$got" -ne "$SAMPLES" ]; then
    echo "HARNESS BROKEN: expected $SAMPLES samples, the run produced $got."
    echo "This is NOT a measurement of the rotor. Check $OUT."
    grep -aiE "Bad parameters|Could not|error|No such" "$OUT" | head -3
    exit 4
fi
echo "rc=$rc  (full log: $OUT)"
# The verdict is deliberately NOT asserted here yet: a threshold invented before
# the first measurement is a threshold fitted to whatever came out. Record the
# numbers, then define the pass condition against the QEMU oracle's goldens.
