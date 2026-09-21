#!/usr/bin/env bash
# Zephyr coverage sweep for the RT1180 Renode model.
#
# For each Zephyr sample/test: build it for a board target, run it on the model,
# capture the GUEST's console bytes (normalized -- Renode timestamps and the
# build hash stripped), and append a row to results/zephyr-corpus.tsv.
#
# The normalized console capture is the artifact the QEMU-vs-Renode delta study
# will diff, so it must contain ONLY what the firmware printed.
#
#   usage: zephyr_sweep.sh <corpus-file>
#   corpus-file lines:  <core>\t<zephyr-path>\t<oracle-string>
set -u
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ZEPHYR="${ZEPHYR_BASE:-$HOME/zephyrproject/zephyr}"
ART="${ART:-$HOME/.cache/rt1180-artifacts}"
OUT="$HERE/results"; mkdir -p "$OUT/console" "$ART/zephyr"
TSV="$OUT/zephyr-corpus.tsv"
BUILDROOT="${BUILDROOT:-$HOME/.cache/rt1180-zephyr-builds}"; mkdir -p "$BUILDROOT"
RENODE_DIR="${RENODE_DIR:-$HOME/.cache/renode/renode_1.17.0-portable}"
# ⚠ GUEST-TIME BUDGET. The original 12 s silently TRUNCATED long ztest suites and
# recorded them as FAIL -- mutex_api needs ~12.2 s and reports
# "TESTSUITE mutex_api succeeded" before being cut off mid second suite.
# A budget that is too short is indistinguishable from a model failure in the
# corpus unless you look at the console, which is why the default is now generous.
RUNFOR="${RUNFOR:-90}"

export PATH="$HOME/zephyrproject/.venv/bin:$HOME/.cache/rt1180-sdk/venv/bin:$PATH"
export ZEPHYR_TOOLCHAIN_VARIANT=gnuarmemb GNUARMEMB_TOOLCHAIN_PATH=/usr
NM="$(command -v arm-none-eabi-nm || echo nm)"
OBJDUMP="$(command -v arm-none-eabi-objdump || echo objdump)"

[ -f "$TSV" ] || printf '# core\tsample\tbuild\trun\toracle\tsha256\tconsole\tnote\n' > "$TSV"

run_one() {
  local core="$1" sample="$2" oracle="$3"
  local tag; tag="$(echo "${core}-${sample}" | tr '/' '-')"
  # skip if already recorded (resumable)
  #
  # ⚠ WAS `grep -qP "^${core}\t${sample}\t"`. Two implementation dependencies in
  # one line, both silent:
  #   * `-P` (PCRE) is a GNU/ugrep extension. busybox grep has NO -P at all
  #     ("grep: invalid option -- 'P'"), and the 2>/dev/null here SWALLOWED that
  #     error -- so the check would always answer "not done", and a resumed sweep
  #     would re-run every target and duplicate its rows.
  #   * `\t` inside a regex is interpreted by ugrep and by GNU, but NOT by a
  #     POSIX ERE (busybox returns 0 matches). MEASURED both ways on this box,
  #     which has ugrep as `grep` AND busybox available.
  #
  # awk is the portable answer and also the more exact one: FIELD EQUALITY rather
  # than an anchored prefix match, so a sample name that is a prefix of another
  # can never collide. awk's -F'\t' interprets the escape by its own spec, not by
  # whichever grep the PATH happens to resolve to.
  if awk -F'\t' -v c="$core" -v s="$sample" '$1==c && $2==s { found=1 } END { exit !found }' \
       "$TSV" 2>/dev/null; then
      echo "  [skip, already done] $core $sample"; return 0; fi

  local board="mimxrt1180_evk/mimxrt1189/${core}"
  local bdir="$BUILDROOT/$tag"
  rm -rf "$bdir"
  if ! (cd "$ZEPHYR" && timeout -k 10 900 west build -p always -b "$board" "$sample" -d "$bdir" >"$bdir.build.log" 2>&1 </dev/null); then
      printf '%s\t%s\tFAIL\t-\t%s\t-\t-\t%s\n' "$core" "$sample" "$oracle" "build failed; see $bdir.build.log" >> "$TSV"
      echo "  BUILD-FAIL  $core $sample"; return 0
  fi
  local elf="$bdir/zephyr/zephyr.elf"
  cp "$elf" "$ART/zephyr/$tag.elf"
  local sha; sha="$(sha256sum "$elf" | cut -d' ' -f1)"

  # vector table / initial SP,PC straight out of the ELF
  local vt sp pc
  vt="$($NM "$elf" | awk '/ _vector_table$/{print "0x"$1; exit}')"
  [ -n "$vt" ] || vt=0x0
  read sp pc < <($OBJDUMP -s --start-address="$vt" --stop-address="$((vt+8))" "$elf" \
    | awk '/^ /{print strtonum("0x" substr($2,7,2) substr($2,5,2) substr($2,3,2) substr($2,1,2)), strtonum("0x" substr($3,7,2) substr($3,5,2) substr($3,3,2) substr($3,1,2)); exit}')
  sp=$(printf '0x%X' "${sp:-0}"); pc=$(printf '0x%X' "${pc:-0}")

  local resc mips
  if [ "$core" = "cm7" ]; then resc=scripts/rt1180_cm7.resc; mips=798
  else resc=scripts/rt1180_zephyr.resc; mips=240; fi

  local raw="$OUT/console/$tag.raw" con="$OUT/console/$tag.txt"
  ( cd "$RENODE_DIR" && timeout -k 20 240 ./renode --console --disable-xwt --plain \
      -e "\$elf=@$elf" -e "\$vt=$vt" -e "\$sp=$sp" -e "\$pc=$pc" \
      -e "include @$HERE/$resc" -e "emulation RunFor \"$RUNFOR\"" -e quit </dev/null ) > "$raw" 2>&1

  # NORMALIZED guest console: only UART INFO lines, prefix stripped, build hash masked.
  grep -aE '\[INFO\] lpuart(1|12):' "$raw" \
    | sed -E 's/^.*\] //; s/\r$//' \
    | sed -E 's/(Booting Zephyr OS build )[^ ]+/\1<BUILD>/' > "$con"

  local status note=-
  if grep -qaF "$oracle" "$con"; then status=PASS; else
      status=FAIL; note="$(tail -1 "$con" 2>/dev/null | cut -c1-60)"; [ -n "$note" ] || note='no console output'; fi
  printf '%s\t%s\tOK\t%s\t%s\t%s\t%s\t%s\n' "$core" "$sample" "$status" "$oracle" "$sha" "console/$tag.txt" "$note" >> "$TSV"
  echo "  $status  $core $sample"
}

# ⚠ Read the corpus on FD 3, NOT stdin. `west build` and `renode` both read
# stdin, and inside a `while read ... done < file` loop they swallow the rest of
# the corpus -- which is exactly what happened: the sweep silently stopped after
# the first entry and exited 0, looking like a completed run.
while IFS=$'\t' read -r core sample oracle <&3; do
  case "$core" in ''|'#'*) continue ;; esac
  run_one "$core" "$sample" "$oracle" </dev/null
done 3< "$1"
