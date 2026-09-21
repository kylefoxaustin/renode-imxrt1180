#!/usr/bin/env bash
# The oracle's MAGNETIC-SATURATION sweep, run on Renode against their closed-form
# golden. Their tests/imxrt1180-motor-sat measures the d-axis current RISE TIME at
# two drive levels and checks the RATIO, because a ratio cancels the absolute
# inductance and isolates the saturation law Ld_eff = Ld0/(1 + |id|/i_sat).
# QEMU passes rate-hz and sat-isat-ma as `-global`; Renode takes them at
# construction, so one .repl per config. Golden arithmetic copied verbatim.
set -u
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ORACLE="${ORACLE:-$HOME/Documents/GitHub/rt1180emulator}"
ELF="$ORACLE/tests/imxrt1180-motor-sat/sat.elf"
SECS="${SECS:-20}"
WORK="${WORK:-/tmp/rt1180renode-motorsat}"; mkdir -p "$WORK"
[ -f "$ELF" ] || { echo "missing $ELF"; exit 2; }

ISATS=(0 10000 5000 3000)
results=""
for isat in "${ISATS[@]}"; do
    sed -e "s/^motor: Miscellaneous.IMXRT1180_Motor @ none/motor: Miscellaneous.IMXRT1180_Motor @ none\n    rateHz: 500000\n    satIsatMilliA: $isat/" \
        -e "s|^using \"mimxrt1189_cm33.repl\"|using \"$HERE/platforms/mimxrt1189_cm33.repl\"|" \
        "$HERE/platforms/mimxrt1189_cm33_motor.repl" > "$WORK/plat_$isat.repl"
    cat > "$WORK/run_$isat.resc" <<RESC
using sysbus
include @$HERE/scripts/load_peripherals.resc
mach create "m"
machine LoadPlatformDescription @$WORK/plat_$isat.repl
sysbus Redirect 0x50000000 0x40000000 0x10000000
RESC
    out="$WORK/out_$isat.txt"; rm -f "$out"
    ( cd "$(dirname "$RENODE")" && timeout -k 20 400 "$RENODE" --console --disable-xwt --plain \
        -e "include @$WORK/run_$isat.resc" \
        -e "cpu.semihosting.semihosting_uart CreateFileBackend @$out true" \
        -e "sysbus LoadELF @$ELF" \
        -e "emulation RunFor \"$SECS\"" -e quit </dev/null ) > "$WORK/log_$isat.txt" 2>&1
    if grep -aqE "There was an error executing command|Error E[0-9]+:" "$WORK/log_$isat.txt"; then
        echo "  HARNESS ERROR for i_sat=$isat:"; grep -aE "Error E[0-9]+:" "$WORK/log_$isat.txt" | head -1; exit 4
    fi
    ts="$(tr -d '\0\r' < "$out" 2>/dev/null | grep -oE 'ts=[0-9]+' | head -1 | cut -d= -f2)"
    tl="$(tr -d '\0\r' < "$out" 2>/dev/null | grep -oE 'tl=[0-9]+' | head -1 | cut -d= -f2)"
    results+="$isat ${ts:-NA} ${tl:-NA};"
    printf '  i_sat=%-7s ts=%-8s tl=%s\n' "$isat" "${ts:-NA}" "${tl:-NA}"
done

echo "$results" | python3 -c '
import sys, math
Rs, Vbus, Xs, Xl = 0.54, 24.0, 40, 320       # SDK M1 + board constants
vds, vdl = Xs/1000*Vbus, Xl/1000*Vbus
TOL = 0.05
def t_over_Ld(vd, isat):
    i = 0.5*vd/Rs
    if isat <= 0: return math.log(2)/Rs
    C = isat/(vd + Rs*isat)
    return C*math.log((1+i/isat)/(1-Rs*i/vd))
ok = True
for r in [x for x in sys.stdin.read().strip().split(";") if x]:
    isat_ma, ts, tl = r.split()
    isat = int(isat_ma)/1000.0
    golden = t_over_Ld(vdl, isat) / t_over_Ld(vds, isat)
    if ts == "NA" or tl == "NA":
        print(f"  i_sat={isat_ma}mA: golden {golden:.3f} -> NO OUTPUT  FAIL"); ok=False; continue
    meas = int(tl)/int(ts)
    err = abs(meas-golden)/golden
    v = "ok" if err <= TOL else "FAIL"
    if err > TOL: ok = False
    print(f"  i_sat={isat_ma}mA: golden {golden:.3f}  measured {meas:.3f}  ({err*100:.1f}%)  {v}")
sys.exit(0 if ok else 1)
'
rc=$?
[ "$rc" -eq 0 ] && echo "MOTOR-SAT: PASS (rise-time ratio matches the saturation law across the i_sat sweep)" \
                || echo "MOTOR-SAT: FAIL"
exit "$rc"
