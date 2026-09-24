#!/usr/bin/env bash
# ┌──────────────────────────────────────────────────────────────────────────┐
# │ Every base and IRQ in every .repl, checked against the QEMU oracle.      │
# └──────────────────────────────────────────────────────────────────────────┘
#
# ⭐ WRITTEN BECAUSE I FABRICATED FOUR ADDRESSES IN ONE DAY.
#
#   adc2    IRQ  94          -- inferred as "ADC1's 93, plus one"; real: 189
#   mecc1   base 0x44430000  -- real: 0x42920000
#   usbphy1 base 0x4C400000  -- real: 0x42CA0000
#   usbphy2 base 0x4C500000  -- real: 0x42CB0000
#
# Every one was typed from memory while moving fast, in a project whose own
# CLAUDE.md says in capitals: "Do NOT fabricate register offsets / bases / IRQs /
# reset values. A wrong offset is a silent hang; a wrong reset value is a
# silent-wrong." Three were caught only by SIDE EFFECTS -- a name collision with
# another platform, and a grep that happened to print both variants together.
# adc2's wrong IRQ would have mis-routed an interrupt with nothing to notice.
#
#   ⭐ A RULE YOU FOLLOW WHEN CAREFUL AND BREAK WHEN RUSHED IS NOT A CONTROL.
#      This is the control: it does not care how careful anyone was feeling.
#
# Exit 0 = every address that HAS an oracle entry matches it.
# Exit 1 = a mismatch (a fabricated or stale value).
# Exit 2 = the oracle could not be parsed -- NO claim is made either way.
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
ORACLE="${ORACLE:-/home/kyle/Documents/GitHub/rt1180emulator}"
SOC="$ORACLE/hw/arm/imxrt1180_soc.c"
HDR="$ORACLE/include/hw/arm/imxrt1180_soc.h"
for f in "$SOC" "$HDR"; do [ -r "$f" ] || { echo "BROKEN: cannot read $f" >&2; exit 2; }; done

python3 - "$SOC" "$HDR" "$ROOT" <<'PY'
import re, sys, glob, os
soc, hdr, root = sys.argv[1], sys.argv[2], sys.argv[3]
H = open(hdr).read(); S = open(soc).read()

defs = {m.group(1): int(m.group(2), 0)
        for m in re.finditer(r"#define\s+(IMXRT1180_[A-Z0-9_]+)\s+(0x[0-9A-Fa-f]+|\d+)\b", H)}
base_of = {v: k for k, v in defs.items() if k.endswith("_BASE")}
irq_of  = {k: v for k, v in defs.items() if k.endswith("_IRQ")}

# base -> irq, from the {base, irq} cfg tables
pairs = {}
for m in re.finditer(r"\{\s*(IMXRT1180_[A-Z0-9_]+|0x[0-9A-Fa-f]{8})\s*,\s*(IMXRT1180_[A-Z0-9_]+|\d+)\s*\}", S):
    b, i = m.group(1), m.group(2)
    b = defs.get(b, int(b, 0) if b.startswith("0x") else None)
    i = defs.get(i, int(i) if i.isdigit() else None)
    if b is not None and i is not None:
        pairs[b] = i
# stride-mapped families the tables do not spell out
if "IMXRT1180_TMR1_BASE" in defs:
    for k, irq in enumerate([0, 233, 164, 151, 4, 5, 6, 7]):
        pairs[defs["IMXRT1180_TMR1_BASE"] + k * 0x10000] = irq
if "IMXRT1180_EQDC1_BASE" in defs and "IMXRT1180_EQDC1_IRQ" in defs:
    for k in range(4):
        pairs[defs["IMXRT1180_EQDC1_BASE"] + k * 0x10000] = defs["IMXRT1180_EQDC1_IRQ"] + k

if len(base_of) < 20 or len(pairs) < 20:
    print("BROKEN: parsed only %d bases / %d base-irq pairs from the oracle; no claim made"
          % (len(base_of), len(pairs)), file=sys.stderr)
    sys.exit(2)

bad = checked = unknown = 0
for f in sorted(glob.glob(os.path.join(root, "platforms", "*.repl"))):
    txt = open(f).read()
    for m in re.finditer(r"^([a-z0-9_]+):\s*[\w.]+\s*@\s*sysbus\s*(0x[0-9A-Fa-f]+)((?:\n(?!\S).*)*)",
                         txt, re.M):
        name, base, body = m.group(1), int(m.group(2), 16), m.group(3)
        q = re.search(r"0 -> nvic@(\d+)", body)
        if q and base in pairs:
            checked += 1
            if pairs[base] != int(q.group(1)):
                bad += 1
                print("  MISMATCH  %-22s %-14s base=0x%08X  repl irq=%-4s oracle irq=%s"
                      % (os.path.basename(f), name, base, q.group(1), pairs[base]))
        elif q:
            unknown += 1
print("  addresses with an oracle IRQ entry: %d checked, %d mismatched, %d with no entry"
      % (checked, bad, unknown))
sys.exit(1 if bad else 0)
PY
