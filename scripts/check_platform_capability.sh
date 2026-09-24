#!/usr/bin/env bash
# ┌──────────────────────────────────────────────────────────────────────────┐
# │ Does the platform a harness LOADS have what the board's devicetree says  │
# │ the firmware will touch?                                                 │
# └──────────────────────────────────────────────────────────────────────────┘
#
# ⭐ THIS EXISTS BECAUSE THE BLOCK AUDIT ANSWERED THE WRONG QUESTION.
#
# That audit counted peripheral instances across the UNION of all .repl files and
# certified "22 of 22 families complete, 0 short". Every number in it was true.
# And at that same moment mimxrt1189_zephyr.repl -- the platform the entire
# 83-target Zephyr study runs on -- inherited bare cm33 and had NO LPADC, LPSPI,
# FlexCAN, GPT, LPTMR, RGPIO, RTWDOG or eDMA. LPADC was in NO harness platform at
# all. It took Zephyr's adc_api printing zero lines to notice.
#
#   ⭐ A CAPABILITY PRESENT SOMEWHERE IS NOT A CAPABILITY PRESENT WHERE THE
#      FIRMWARE LOOKS. The union is what a maintainer believes; the platform
#      under test is what the guest actually gets.
#
# The requirement list is SOURCED from the board's own devicetree -- the same
# file Zephyr builds against -- not from anyone's opinion about what matters:
#
#   zephyr/boards/nxp/mimxrt1180_evk/mimxrt1180_evk_mimxrt1189_cm33.dts
#     status="okay": dac edma3 edma4 flexcan3 flexspi gpt2 i3c2 kpp lpadc1
#                    lpspi3 lptmr1 lpuart1 mecc1 mecc2 sai1 systick usbphy1 usbphy2
#
# Exit 0 = every checked platform carries every required family.
# Exit 1 = a platform is missing something its firmware will address.
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
cd "$ROOT" || exit 2

# dts node -> the Renode class substring that implements it. Written out rather
# than inferred: the names genuinely differ (dts `lpadc1` is our `IMXRT1180_LPADC`,
# dts `gpt2` is `IMXRT1180_GPT`), and guessing that mapping is how you get a guard
# that passes because it looked for the wrong string.
declare -A NEED=(
  [lpadc1]='IMXRT1180_LPADC'
  [lpspi3]='IMXRT1180_LPSPI'
  [flexcan3]='IMXRT1180_FlexCAN'
  [gpt2]='IMXRT1180_GPT'
  [lptmr1]='IMXRT1180_LPTMR'
  [edma4]='IMXRT1180_eDMA'
  [sai1]='IMXRT1180_SAI'
  [mecc1]='IMXRT1180_MECC'
  [usbphy1]='IMXRT1180_USBPHY'
  [lpuart1]='NXP_LPUART'
)

# The platforms a harness actually loads, discovered from the .resc files rather
# than listed by hand -- a hand-written list goes stale exactly when a new harness
# is added, which is when the guard matters most.
mapfile -t PLATS < <(grep -ho "platforms/[a-z0-9_]*\.repl" scripts/*.resc 2>/dev/null \
                     | sed 's|platforms/||' | sort -u)
[ "${#PLATS[@]}" -ge 3 ] || { echo "BROKEN: found only ${#PLATS[@]} harness platforms; discovery failed" >&2; exit 2; }

# Resolve a platform's full peripheral set by following its `using` chain.
resolve () {
    local f="platforms/$1" seen=""
    while [ -n "$f" ] && [ -r "$f" ]; do
        cat "$f"
        local parent
        parent=$(grep -m1 '^using' "$f" | tr -d '"' | sed 's/using //; s/^ *//')
        case "$seen" in *"|$parent|"*) break ;; esac
        seen="$seen|$parent|"
        [ -n "$parent" ] && f="platforms/$parent" || f=""
    done
}

rc=0
for p in "${PLATS[@]}"; do
    [ -r "platforms/$p" ] || continue
    body=$(resolve "$p")
    miss=""
    for node in "${!NEED[@]}"; do
        printf '%s' "$body" | grep -qE "^[a-z0-9_]+: .*${NEED[$node]}" || miss="$miss $node"
    done
    if [ -n "$miss" ]; then
        printf '  %-34s MISSING:%s\n' "$p" "$miss"; rc=1
    else
        printf '  %-34s ok (all %d required families)\n' "$p" "${#NEED[@]}"
    fi
done
[ "$rc" = 0 ] && echo "PLATFORM CAPABILITY OK - every harness platform carries every family the board's dts enables"
exit "$rc"
