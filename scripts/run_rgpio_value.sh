#!/usr/bin/env bash
# ┌──────────────────────────────────────────────────────────────────────────┐
# │ led_blinky / RGPIO4[27] — MEASURED ON BOTH EMULATORS IN ONE RUN.         │
# └──────────────────────────────────────────────────────────────────────────┘
#
# This row used to read RAN / RAN -> "agree = YES" on the equivalency table. Both
# columns were reporting the weakest observable there is -- the firmware did not
# fault -- while the QEMU corpus asserts something real for it:
#
#     "RGPIO4[27] toggle observable in PDOR"   (docs/validation/corpus.tsv)
#
# Renode could not assert that, because it had no RGPIO block at all. Two
# silences that happen to match are not agreement.
#
# ⭐ SO THE ASSERTION IS A TOGGLE, NOT A LEVEL. A single sample of PDOR proves
#    nothing: bit 27 reads 0 both when the LED is off and when the register does
#    not exist. The test demands the bit be seen BOTH HIGH AND LOW across the
#    sampling window, on each emulator independently -- an observable that a
#    missing block cannot fake and a stuck one cannot pass.
#
# ⭐ AND BOTH COLUMNS ARE MEASURED HERE. Neither side is quoted from a README.
#    Under fleet Law 1 a SOURCED-vs-MEASURED comparison may not carry a headline.
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
ORACLE=/home/kyle/Documents/GitHub/rt1180emulator
RENODE="${RENODE:-$HOME/.cache/renode/renode_1.17.0-portable/renode}"
QEMU="${QEMU:-$ORACLE/build/qemu-system-arm}"
BIN="${BIN:-$HOME/.cache/rt1180-artifacts/sdk/led_blinky_cm33.bin}"

RGPIO4_PDOR=0x43830040
RGPIO4_PDDR=0x43830054
LED_BIT=27

[ -r "$BIN" ] || { echo "MISSING ARTIFACT: $BIN"; exit 2; }

# ── Renode side ──────────────────────────────────────────────────────────────
# The load base is DERIVED from the image's own reset vector, exactly as the
# QEMU machine's !is_elf branch does -- a flat .bin carries no load address and
# the SDK cm33 images do not all link to the same one.
BASE=$(python3 - "$BIN" <<'PY'
import struct,sys
sp,pc = struct.unpack('<II', open(sys.argv[1],'rb').read(8))
print("0x%08X" % (pc & 0xFFFF0000))
PY
)
RCMD=""
for _ in $(seq 1 24); do
    RCMD="$RCMD -e \"emulation RunFor \\\"0.1\\\"\" -e \"sysbus ReadDoubleWord $RGPIO4_PDOR\""
done
RLOG=$(eval timeout -k 10 300 "$RENODE" --console --disable-xwt --plain \
    -e "'\$bin=@$BIN'" -e "'\$loadbase=$BASE'" \
    -e "'include @$ROOT/scripts/rt1180_tierA.resc'" \
    $RCMD -e "\"sysbus ReadDoubleWord $RGPIO4_PDDR\"" -e quit 2>&1)
RSAMP=$(echo "$RLOG" | grep -E "^0x" | head -24)
RDDR=$(echo "$RLOG" | grep -E "^0x" | tail -1)

# ── QEMU side ────────────────────────────────────────────────────────────────
QSAMP=$(QEMU="$QEMU" BIN="$BIN" PDOR="$RGPIO4_PDOR" PDDR="$RGPIO4_PDDR" python3 - <<'PY'
import json, os, signal, socket, subprocess, sys, time
qemu, binf = os.environ["QEMU"], os.environ["BIN"]
pdor, pddr = int(os.environ["PDOR"], 16), int(os.environ["PDDR"], 16)
sock = "/tmp/rgpio-val-%d.sock" % os.getpid()
if os.path.exists(sock):
    os.remove(sock)
p = subprocess.Popen([qemu, "-M", "mimxrt1180-evk", "-display", "none",
                      "-monitor", "none", "-serial", "none", "-kernel", binf,
                      "-qmp", "unix:%s,server,nowait" % sock],
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
for _ in range(200):
    if os.path.exists(sock):
        break
    time.sleep(0.05)
try:
    qs = socket.socket(socket.AF_UNIX); qs.connect(sock); qf = qs.makefile('rw')
    qf.readline()
    qf.write(json.dumps({"execute": "qmp_capabilities"}) + "\n"); qf.flush(); qf.readline()

    def rd(addr):
        qf.write(json.dumps({"execute": "human-monitor-command",
                             "arguments": {"command-line": "xp /1wx 0x%x" % addr}}) + "\n")
        qf.flush()
        r = json.loads(qf.readline()).get("return", "")
        return int(r.strip().split(":")[-1].strip(), 16)

    out = []
    for _ in range(24):
        time.sleep(0.1)
        out.append(rd(pdor))
    out.append(rd(pddr))
    print(" ".join("0x%08X" % v for v in out))
finally:
    p.send_signal(signal.SIGTERM)
    try: p.wait(timeout=3)
    except Exception: p.kill()
    if os.path.exists(sock):
        os.remove(sock)
PY
)

# -- verdict, rendered OUTSIDE both guests ------------------------------------
#
# THE VERDICT IS COMPUTED IN PYTHON, NOT IN SHELL ARITHMETIC. The first version
# used $(( v >> 27 )) and every sample died with "invalid arithmetic operator" --
# Renode's monitor lines carry a trailing CR, and (( )) refuses the token rather
# than erroring usefully. It then printed FAIL while the 0x08000000 samples were
# sitting in plain sight in its own error text. A verdict computed by a parser
# that can fail quietly is worse than no verdict: it said the MODEL was broken
# when the model was fine and the CHECKER was broken.
RALL="$RSAMP" QALL="$QSAMP" RDDR="$RDDR" LED_BIT="$LED_BIT" python3 - <<'VERDICT'
import os, re, sys

# The addresses we queried. Renode echoes the failing command when a read does
# not resolve, and those echoes contain 0x-prefixed 8-digit tokens that look
# exactly like samples. The mutation run scraped 0x43830054 -- the PDDR ADDRESS --
# and reported it as the PDDR VALUE. It did not flip that verdict, but a checker
# that can mistake an address for a reading can lie in the other direction too.
QUERIED = {0x43830040, 0x43830054}

def parse(text):
    vals = [int(m, 16) for m in re.findall(r"0x[0-9A-Fa-f]{8}", text or "")]
    return [v for v in vals if v not in QUERIED]

bit = int(os.environ["LED_BIT"])
r = parse(os.environ.get("RALL"))
q = parse(os.environ.get("QALL"))
qddr = q.pop() if len(q) > 24 else None
rd = parse(os.environ.get("RDDR"))
rddr = rd[0] if rd else None

def show(name, vals):
    print("  %-6s PDOR bit: %s" % (name, "".join("%d" % ((v >> bit) & 1) for v in vals)))

def toggled(vals):
    return {0, 1} <= {(v >> bit) & 1 for v in vals}

show("renode", r)
show("qemu", q)
print("  PDDR   renode=%s qemu=%s  (bit %d = EVK user LED)"
      % (("0x%08X" % rddr) if rddr is not None else "?",
         ("0x%08X" % qddr) if qddr is not None else "?", bit))

rc = 0
for name, vals in (("renode", r), ("qemu", q)):
    # Demand very nearly the full window. A short sample set means the reads did
    # not resolve, which is a BROKEN RUN -- it must never be reported as a model
    # verdict either way.
    if len(vals) < 20:
        print("FAIL: %s produced %d of 24 samples; the window did not run, so this "
              "is a harness failure and NOT a statement about the model"
              % (name, len(vals)))
        rc = 1
    elif not toggled(vals):
        print("FAIL: %s never toggled RGPIO4[%d] -- saw only %s"
              % (name, bit, sorted({(v >> bit) & 1 for v in vals})))
        rc = 1
if rc == 0:
    print("RGPIO PASS - RGPIO4[%d] observed BOTH high and low on BOTH emulators "
          "(measured on each, not quoted)" % bit)
sys.exit(rc)
VERDICT
