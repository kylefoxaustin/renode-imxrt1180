#!/usr/bin/env bash
#
# Reset-value cross-check: every register the RM names, read on Renode, diffed against
# the REFERENCE MANUAL golden. Recommended by @rt1180emulator as the highest-signal row
# in their value suite, and they are right: it is the only test that checks my model
# against the SILICON SPEC rather than against a console string a passing test prints.
#
# The golden is theirs and is reused UNMODIFIED -- tests/imxrt1180-reset-values/
# rm-golden.json, 7193 entries extracted from the RM register tables plus the CMSIS
# headers. Two documents neither of us wrote, which is exactly what makes it an oracle.
# Their check.py drives QEMU over `-qtest stdio`; Renode has no qtest, so this reads the
# same addresses through the monitor instead. Same golden, same question.
#
# ⭐ THE DESIGN DECISION THAT MAKES THIS INSTRUMENT HONEST
#
# 5278 of the 7193 entries have reset == 0, and AN UNMAPPED READ ALSO RETURNS 0. So a
# "match" on a zero-reset register proves NOTHING -- it is satisfied equally by a correct
# model, by a block I never implemented, and by an address that decodes to nothing.
# Reporting "7193 checked, N mismatched" would therefore be a true sentence carrying a
# 73%-inflated denominator, and it is the same confusion that cost two days on address 0:
# "reads 0 because unmodelled" and "reads 0 because that IS the reset value" are
# indistinguishable in the output, and only one of them is correct.
#
# So the score is over the 1915 NON-ZERO entries only. The zero-reset entries are counted
# and reported as NOT DISCRIMINATING rather than as passes, and unmapped addresses are
# detected separately from Renode's own "non existing peripheral" log and reported as a
# third category -- a missing block, not a wrong value.
#
# Classes, deliberately mirroring the oracle's known-deviations taxonomy:
#   MATCH        non-zero golden, model agrees            <- the only real pass
#   MISMATCH     non-zero golden, MAPPED block disagrees  <- DANGEROUS: a block that
#                                                            claims to work and lies
#   UNMAPPED     address decodes to nothing here          <- honest gap, lower priority
#   NOT-DISCRIM  golden reset is 0; proves nothing either way
#
# Usage: scripts/reset_values_check.sh [--platform FILE] [--limit N]
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
RN="$HOME/.cache/renode/renode_1.17.0-portable/renode"
GOLDEN="$HOME/Documents/GitHub/rt1180emulator/tests/imxrt1180-reset-values/rm-golden.json"
PLAT="$ROOT/platforms/mimxrt1189_m1.repl"
WORK="${WORK:-/tmp/claude-1000/resetvals}"
LIMIT=0

while [ $# -gt 0 ]; do
  case "$1" in
    --platform) PLAT="$2"; shift 2 ;;
    --limit) LIMIT="$2"; shift 2 ;;
    *) echo "usage: $0 [--platform FILE] [--limit N]  (got '$1')" >&2; exit 2 ;;
  esac
done
[ -r "$GOLDEN" ] || { echo "golden not readable: $GOLDEN" >&2; exit 2; }
mkdir -p "$WORK"

# ---- generate the read script, one self-ordered line per entry -------------------
python3 - "$GOLDEN" "$LIMIT" "$WORK" <<'PY'
import json, sys
golden, limit, work = sys.argv[1], int(sys.argv[2]), sys.argv[3]
g = json.load(open(golden))
if limit: g = g[:limit]
cmd = {32: "ReadDoubleWord", 16: "ReadWord", 8: "ReadByte"}
with open(f"{work}/reads.resc", "w") as f, open(f"{work}/order.tsv", "w") as o:
    for e in g:
        w = e["w"]
        if w not in cmd: continue
        f.write(f"sysbus {cmd[w]} 0x{e['addr']:08X}\n")
        o.write(f"{e['inst']}\t{e['reg']}\t{w}\t{e['addr']}\t{e['reset']}\n")
print(f"generated {sum(1 for _ in open(f'{work}/order.tsv'))} reads")
PY

# ---- run them: TWO PASSES, and the reason is not caution but correctness -----------
#
# ⚠️ RENODE WRITES VALUES AND LOG LINES TO ONE STREAM, UNSYNCHRONISED, AND A WARNING CAN
#    LAND IN THE MIDDLE OF A VALUE. Measured, on the first full run of this script:
#
#      0x0000[00:23:21] [WARNING] sysbus: ReadDoubleWord from non existing ... at 0x4B864000.
#      0000
#
#    One value, `0x00000000`, split across two lines by a warning emitted mid-token. 990
#    of 7193 values were mangled that way, leaving 6203 parseable. The alignment guard
#    caught it and refused to report -- which is the only reason this is a story about a
#    stream and not a story about 990 fabricated register comparisons.
#
#    `logFile` does not help: it DUPLICATES the log, it does not divert it.
#
# So the two signals are collected separately, because they have different requirements:
#   PASS 1  values     -- needs strict positional alignment, so the log is silenced
#                         (logLevel 3 = ERROR) and nothing can interleave.
#   PASS 2  unmapped   -- needs only a SET of addresses, which is order-independent, so
#                         interleaving is harmless here.
#
# A set does not care about order; a positional zip cares about nothing else. Collecting
# both from one stream forced the stricter requirement onto the looser one for no gain.

# PASS 1 — values, log silenced.
timeout -k 30 1800 "$RN" --console --disable-xwt --plain \
  -e "include @$ROOT/scripts/load_peripherals.resc" \
  -e 'mach create "rv"' \
  -e "machine LoadPlatformDescription @$PLAT" \
  -e 'sysbus UnhandledAccessBehaviour Report' \
  -e 'logLevel 3' \
  -e "include @$WORK/reads.resc" \
  -e quit > "$WORK/raw.log" 2>&1
rc=$?

# PASS 2 — unmapped address set, log at WARNING.
timeout -k 30 1800 "$RN" --console --disable-xwt --plain \
  -e "include @$ROOT/scripts/load_peripherals.resc" \
  -e 'mach create "rv"' \
  -e "machine LoadPlatformDescription @$PLAT" \
  -e 'sysbus UnhandledAccessBehaviour Report' \
  -e "include @$WORK/reads.resc" \
  -e quit > "$WORK/unmapped.log" 2>&1

# ---- score ------------------------------------------------------------------------
DEV="$HOME/Documents/GitHub/rt1180emulator/tests/imxrt1180-reset-values/known-deviations.txt"
python3 - "$WORK" "$rc" "$DEV" <<'PY'
import re, sys, collections
work, rc = sys.argv[1], int(sys.argv[2])
devfile = sys.argv[3] if len(sys.argv) > 3 else ""

# The oracle's own allowlist of registers where THEIR model knowingly deviates. Read it
# so my mismatches can be classified instead of merely counted:
#
#   SHARED      both models deviate from the RM, usually for the same documented reason
#               -> not my defect to fix alone; a joint note, and the oracle already
#                  explains why (their file: "STABLE/lock bit synthesised: we model the
#                  POST-BOOT-ROM state, and firmware polls STABLE before POWERUP")
#   RENODE-ONLY they match the RM and I do not  -> MINE, and the dangerous class
#
# And the converse, which the oracle flagged and I would otherwise have missed: a
# register they allowlist where I MATCH the golden is a row where RENODE IS MORE
# FAITHFUL THAN THE ORACLE. Those are not defects at all and must not be reported as
# though they were -- the reference oracle being wrong somewhere is a real finding, and
# silently filing it under my own mismatches would bury it.
oracle_dev = set()
try:
    for ln in open(devfile):
        ln = ln.split("#")[0].strip()
        if not ln: continue
        f = ln.split()
        if len(f) >= 2: oracle_dev.add((f[0], f[1]))
except OSError:
    pass
order = [l.rstrip("\n").split("\t") for l in open(f"{work}/order.tsv")]
log = open(f"{work}/raw.log", errors="replace").read()

# Values are the bare 0x... lines, in order. Unmapped addresses come from Renode's own
# warning text, which is a DIFFERENT channel -- so an unmapped read still emits a value
# line (0) and alignment is preserved.
vals = re.findall(r"^(0x[0-9A-Fa-f]+)\s*$", log, re.M)
ulog = open(f"{work}/unmapped.log", errors="replace").read()
unmapped = {int(a, 16) for a in re.findall(r"non existing peripheral at (0x[0-9A-Fa-f]+)", ulog)}

# ⭐ ALIGNMENT GUARD. Zipping two lists by position is only valid if they are the same
# length; a single dropped line silently shifts every subsequent register onto the wrong
# golden and produces a confident, plausible, entirely wrong report.
if len(vals) != len(order):
    print(f"BROKEN: {len(vals)} value lines for {len(order)} reads -- alignment cannot be")
    print( "        trusted and NO figure is reported. A positional zip of mismatched")
    print( "        lists yields a confident wrong answer, not a partial one.")
    sys.exit(2)

# ⭐ POSITIVE CONTROL — and the first version of this was NOT ONE.
#
# It printed "LPUART1.BAUD @ <addr> golden=<x> read=<y>" and called that a control. But
# the ADDRESS came from my own `order` list, not from the read, so a misaligned zip would
# have printed an equally plausible row and controlled NOTHING. A control has to be
# something that FAILS when the thing it guards is broken.
#
# So: assert on a register whose value is known-good in THIS model (ADC1.CTRL, golden
# 0x20, independently read as 0x20 before this script existed). If the zip slips by even
# one, that position holds a different register's value and the assert trips. The check
# REFUSES rather than reports, because a misaligned run yields a confident wrong answer.
# TWO controls, one near each END of the stream. One control at index 0 -- which is
# where ADC1.CTRL happens to sit -- guards almost nothing: a slip introduced anywhere
# after it passes unnoticed. The late control is the one that actually catches a drift,
# and it only works because it is late.
CONTROLS = [(("ADC1", "CTRL"), 0x20),
            (("USDHC1", "VEND_SPEC"), 0x30007809)]
for ctl_name, ctl_expect in CONTROLS:
    ctl = [(i, o) for i, o in enumerate(order) if (o[0], o[1]) == ctl_name]
    if not ctl:
        print(f"BROKEN: alignment control {ctl_name[0]}.{ctl_name[1]} is absent from the")
        print( "        golden; no figure is reported, because nothing would catch a")
        print( "        misaligned zip.")
        sys.exit(2)
    i, o = ctl[0]
    got_ctl = int(vals[i], 16)
    if got_ctl != ctl_expect:
        print(f"BROKEN: alignment control {ctl_name[0]}.{ctl_name[1]} @ 0x{int(o[3]):08X} "
              f"(index {i}) reads 0x{got_ctl:08X}, expected 0x{ctl_expect:08X}.")
        print( "        The value stream is misaligned against the golden; NO figure is")
        print( "        reported. A positional zip that has slipped produces a confident")
        print( "        wrong answer for EVERY row, not a partial one.")
        sys.exit(2)
    print(f"alignment control OK: {ctl_name[0]}.{ctl_name[1]} = 0x{got_ctl:08X} at index {i} of {len(order)}")

cls = collections.Counter()
mismatch, unmapped_rows = [], []
for (inst, reg, w, addr, reset), v in zip(order, vals):
    addr, reset, got = int(addr), int(reset), int(v, 16)
    if addr in unmapped:
        cls["UNMAPPED"] += 1; unmapped_rows.append((inst, reg, addr)); continue
    if reset == 0:
        cls["NOT-DISCRIM"] += 1; continue
    if got == reset: cls["MATCH"] += 1
    else:
        cls["MISMATCH"] += 1
        kind = "SHARED" if (inst, reg) in oracle_dev else "RENODE-ONLY"
        mismatch.append((inst, reg, addr, reset, got, kind))

# Rows where the oracle allowlists a deviation and Renode MATCHES the RM.
renode_right = []
for (inst, reg, w, addr, reset), v in zip(order, vals):
    if (inst, reg) in oracle_dev and int(reset) != 0 and int(addr) not in unmapped \
       and int(v, 16) == int(reset):
        renode_right.append((inst, reg))

scored = cls["MATCH"] + cls["MISMATCH"]
print()
for k in ("MATCH", "MISMATCH", "UNMAPPED", "NOT-DISCRIM"):
    print(f"  {k:<14s} {cls[k]}")
print()
print(f"SCORED (non-zero golden, mapped): {cls['MATCH']}/{scored}" if scored else "SCORED: 0 -- nothing discriminating was reachable")
print(f"  NOT-DISCRIM {cls['NOT-DISCRIM']} entries have a zero golden reset and are counted NEITHER WAY:")
print( "  an unmapped read returns 0 too, so agreement there is not evidence.")
print(f"  UNMAPPED {cls['UNMAPPED']} addresses decode to nothing on this platform -- a missing")
print( "  block, not a wrong value.")

if renode_right:
    print()
    print(f"RENODE MORE FAITHFUL THAN THE ORACLE on {len(renode_right)} register(s) they")
    print( "allowlist as their own known deviation, where Renode matches the RM golden:")
    for inst, reg in renode_right[:10]:
        print(f"    {inst:14s} {reg}")

if mismatch:
    print()
    print("MISMATCHES on MAPPED blocks (the dangerous class -- a block that claims to work):")
    by = collections.Counter(m[0] for m in mismatch)
    for inst, n in by.most_common(12):
        print(f"    {inst:<22s} {n}")
    print()
    nshared = sum(1 for m in mismatch if m[5] == "SHARED")
    print(f"    [{nshared} also deviate in the ORACLE's model (shared, documented); "
          f"{len(mismatch)-nshared} are RENODE-ONLY]")
    print()
    for inst, reg, addr, reset, got, kind in mismatch[:12]:
        print(f"    {kind:11s} {inst:14s} {reg:<18s} 0x{addr:08X}  golden=0x{reset:08X}  got=0x{got:08X}")
    if len(mismatch) > 12:
        print(f"    … {len(mismatch)-12} more (full list in {work}/mismatches.tsv)")
    with open(f"{work}/mismatches.tsv", "w") as f:
        f.write("inst\treg\taddr\tgolden\tgot\tkind\n")
        for inst, reg, addr, reset, got, kind in mismatch:
            f.write(f"{inst}\t{reg}\t0x{addr:08X}\t0x{reset:08X}\t0x{got:08X}\t{kind}\n")
PY
