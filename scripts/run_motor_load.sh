#!/usr/bin/env bash
# ⭐⭐ THE ORACLE'S COAST-DOWN SWEEP, RUN ON RENODE, AGAINST THEIR CLOSED-FORM GOLDEN.
#
# Their tests/imxrt1180-motor-load seeds the plant with an initial rotor speed and
# NO drive, so the rotor coasts to rest under a speed-SQUARED (fan/pump) load plus
# viscous damping. Dividing the mechanical ODE  J dw/dt = -(k w^2 + B w)  by
# w = dtheta/dt gives a CLOSED FORM for the total angle turned before stopping:
#
#     theta_total = (J/k) * ln(1 + k*w0/B)
#
# read as the final encoder count REV*CPR + LPOS. It depends only on J, B (SDK M1
# motor datasheet params), k (the load under test) and w0 (the seed) -- on NO line
# of either model's code. That is what makes it a golden rather than a range.
#
# QEMU passes the per-run plant parameters as `-global imxrt1180-motor.*`; Renode
# takes them as constructor arguments, so this generates one .repl per config.
# Same binary (their load.elf), same five configs, same tolerance, same arithmetic.
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ORACLE="${ORACLE:-$HOME/Documents/GitHub/rt1180emulator}"
ELF="$ORACLE/tests/imxrt1180-motor-load/load.elf"
SECS="${SECS:-20}"
WORK="${WORK:-/tmp/rt1180renode-motorload}"; mkdir -p "$WORK"
[ -f "$ELF" ] || { echo "missing $ELF"; exit 2; }

# "init_mrads load_fan_unms" -- w0 in milli-rad/s, k in micro-N*m/(rad/s)^2.
# Identical to the oracle's CONFIGS list.
CONFIGS=("150000 10" "100000 10" "200000 20" "150000 50" "250000 30")

results=""
for cfg in "${CONFIGS[@]}"; do
    set -- $cfg
    w0m="$1"; k="$2"
    # The generated .repl lives outside platforms/, so its `using` must be an
    # ABSOLUTE path -- a relative one resolves against the including file's
    # directory and fails with "Error E36: Using '...' does not exist", silently
    # producing an empty capture that the golden check then reads as NO OUTPUT.
    sed -e "s/^motor: Miscellaneous.IMXRT1180_Motor @ none/motor: Miscellaneous.IMXRT1180_Motor @ none\n    initMilliRadS: $w0m\n    loadFanMicroNms: $k/" \
        -e "s|^using \"mimxrt1189_cm33.repl\"|using \"$HERE/platforms/mimxrt1189_cm33.repl\"|" \
        "$HERE/platforms/mimxrt1189_cm33_motor.repl" > "$WORK/plat_${w0m}_${k}.repl"
    cat > "$WORK/run_${w0m}_${k}.resc" <<RESC
using sysbus
include @$HERE/scripts/load_peripherals.resc
mach create "m"
machine LoadPlatformDescription @$WORK/plat_${w0m}_${k}.repl
sysbus Redirect 0x50000000 0x40000000 0x10000000
RESC
    out="$WORK/out_${w0m}_${k}.txt"; rm -f "$out"
    ( cd "$(dirname "$RENODE")" && timeout -k 20 400 "$RENODE" --console --disable-xwt --plain \
        -e "include @$WORK/run_${w0m}_${k}.resc" \
        -e "cpu.semihosting.semihosting_uart CreateFileBackend @$out true" \
        -e "sysbus LoadELF @$ELF" \
        -e "emulation RunFor \"$SECS\"" -e quit </dev/null ) > "$WORK/log_${w0m}_${k}.txt" 2>&1
    if grep -aqE "There was an error executing command|Error E[0-9]+:" "$WORK/log_${w0m}_${k}.txt"; then
        # ⭐ A PLATFORM THAT DID NOT LOAD IS NOT A MOTOR THAT DID NOT TURN.
        echo "  HARNESS ERROR for w0=$w0m k=$k:"; grep -aE "Error E[0-9]+:" "$WORK/log_${w0m}_${k}.txt" | head -1
        exit 4
    fi
    tc="$(tr -d '\0\r' < "$out" 2>/dev/null | grep -oE 'total_counts=-?[0-9]+' | head -1 | cut -d= -f2)"
    results+="$w0m $k ${tc:-NA};"
    printf '  w0=%-8s k=%-4s -> total_counts=%s\n' "$w0m" "$k" "${tc:-NA}"
done

# ⭐ THE GOLDEN ARITHMETIC IS THE ORACLE'S, REPRODUCED VERBATIM -- same J, B, CPR,
# same 4% tolerance (their note: dormant-threshold cutoff + Euler discretisation).
echo "$results" | python3 -c '
import sys, math
J, B, CPR = 1e-5, 1e-4, 8000
TOL = 0.04
rows = [r for r in sys.stdin.read().strip().split(";") if r]
ok = True
for r in rows:
    w0_m, unms, tc = r.split()
    w0 = int(w0_m) / 1000.0
    k  = int(unms) / 1e6
    golden = (J / k) * math.log(1.0 + k * w0 / B) * CPR / (2 * math.pi)
    if tc == "NA":
        print(f"  w0={w0:.0f} k={k:.0e}: golden {golden:.0f} -> NO OUTPUT  FAIL"); ok = False; continue
    meas = int(tc)
    err = abs(meas - golden) / golden
    verdict = "ok" if err <= TOL else "FAIL"
    if err > TOL: ok = False
    print(f"  w0={w0:.0f} rad/s  k={k:.0e}: golden {golden:.0f}  measured {meas}  ({err*100:.1f}%)  {verdict}")
sys.exit(0 if ok else 1)
'
rc=$?
if [ "$rc" -eq 0 ]; then
    echo "MOTOR-LOAD: PASS (coast-down angle matches (J/k)ln(1+k*w0/B) across the w0/k sweep)"
else
    echo "MOTOR-LOAD: FAIL"
fi
exit "$rc"
