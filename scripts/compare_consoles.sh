#!/usr/bin/env bash
# QEMU-vs-Renode console delta study.
#
# Diffs NORMALIZED guest console captures for the same target, produced by the
# same firmware binary (verified by sha256) on the two models. Anything that
# differs here is a MODEL delta, not a build delta -- which is the only reason
# the comparison means anything.
#
#   usage: compare_consoles.sh <renode-dir> <qemu-dir> [report.md]
#
# Both dirs hold files named <core>-<sample>.txt containing guest UART bytes
# only. Renode's are produced by zephyr_sweep.sh; QEMU's can be raw -- pass them
# through normalize() below by dropping them in as .raw and they'll be cleaned.
set -u
# ⚠ ANCHOR TO THE SCRIPT, NOT THE CALLER'S CWD. OUT used to default to the
# RELATIVE "results/DELTA-STUDY.md", so running this from anywhere but the repo
# root silently wrote the delta study into a results/ directory it created
# wherever you happened to be standing -- and reported success. Every other script
# here resolves HERE from $0; this one was the exception. (Found by auditing all
# 12 scripts after a cwd-dependent `sha256sum -c` cried "103 mismatches" on a
# clean tree. zephyr_sweep.sh's relative-looking `resc=scripts/...` is NOT the
# same bug -- it is consumed as "$HERE/$resc", checked before leaving it alone.)
HERE="$(cd "$(dirname "$0")/.." && pwd)"
RD="${1:?renode console dir}"; QD="${2:?qemu console dir}"; OUT="${3:-$HERE/results/DELTA-STUDY.md}"

normalize() {  # stdin -> stdout: strip CR, mask the Zephyr build hash, drop blank lines
    sed -E 's/\r$//; s/(Booting Zephyr OS build )[^ ]+/\1<BUILD>/' | sed '/^[[:space:]]*$/d'
}

{
  echo "# Zephyr console delta study — QEMU vs Renode"
  echo
  echo "> ⚠️ **SCOPE OF THE QEMU COLUMN.** @rt1180emulator's model currently reaches a"
  echo "> console verdict for the printf **samples** only. Their ztest kernel suites"
  echo "> produce 0 console bytes, 0 SysTicks, and halt after the kernel's first SVC"
  echo "> (with or without -icount), so they are an open bring-up gap on that side."
  echo "> **Any identical row below is therefore samples-only agreement and must NOT"
  echo "> be read as ztest parity.**"
  echo
  echo "Generated $(date -u +%Y-%m-%dT%H:%M:%SZ). Each row is one firmware binary"
  echo "run on both models; identical normalized console output = agreement."
  echo
  echo "| target | renode | qemu | verdict |"
  echo "|---|---|---|---|"
  total=0; agree=0; only_r=0; only_q=0; differ=0
  for rf in "$RD"/*.txt; do
    [ -e "$rf" ] || continue
    t=$(basename "$rf" .txt); qf="$QD/$t.txt"; total=$((total+1))
    if [ ! -e "$qf" ]; then
      echo "| \`$t\` | $(wc -l < "$rf") lines | — | **renode only** (not run on QEMU) |"
      only_r=$((only_r+1)); continue
    fi
    rn=$(normalize < "$rf"); qn=$(normalize < "$qf")
    nr=$(printf '%s\n' "$rn" | wc -l); nq=$(printf '%s\n' "$qn" | wc -l)
    n=$(( nr < nq ? nr : nq ))
    # ⚠ COMPARE THE COMMON PREFIX ONLY.
    # Non-terminating samples (e.g. synchronization) print forever; the number of
    # lines captured is a function of how long each emulator was RUN, not of what
    # the models do. A naive full-file diff reports those as content deltas -- it
    # did exactly that here, flagging "13 differing lines" for two captures whose
    # content was identical and whose distinct-line sets matched.
    # The honest comparison is the prefix both runs actually reached.
    if diff -q <(printf '%s\n' "$rn" | head -n "$n") <(printf '%s\n' "$qn" | head -n "$n") >/dev/null 2>&1; then
      if [ "$nr" -eq "$nq" ]; then
        echo "| \`$t\` | ✓ | ✓ | **identical** |"; agree=$((agree+1))
      else
        echo "| \`$t\` | $nr lines | $nq lines | **identical over the $n-line common prefix** (capture lengths differ: run duration, not model) |"
        agree=$((agree+1))
      fi
    else
      d=$(diff <(printf '%s\n' "$rn" | head -n "$n") <(printf '%s\n' "$qn" | head -n "$n") | grep -cE '^[<>]')
      echo "| \`$t\` | ✓ | ✓ | ⚠ **$d differing lines within the common prefix** |"; differ=$((differ+1))
    fi
  done
  for qf in "$QD"/*.txt; do
    [ -e "$qf" ] || continue
    t=$(basename "$qf" .txt)
    [ -e "$RD/$t.txt" ] || { echo "| \`$t\` | — | $(wc -l < "$qf") lines | **qemu only** |"; only_q=$((only_q+1)); }
  done
  echo
  echo "**Totals:** $total compared · $agree identical · $differ differing · $only_r renode-only · $only_q qemu-only"
  echo
  echo "## Per-target diffs"
  for rf in "$RD"/*.txt; do
    [ -e "$rf" ] || continue
    t=$(basename "$rf" .txt); qf="$QD/$t.txt"; [ -e "$qf" ] || continue
    rn=$(normalize < "$rf"); qn=$(normalize < "$qf")
    nr=$(printf '%s\n' "$rn" | wc -l); nq=$(printf '%s\n' "$qn" | wc -l); n=$(( nr < nq ? nr : nq ))
    diff -q <(printf '%s\n' "$rn" | head -n "$n") <(printf '%s\n' "$qn" | head -n "$n") >/dev/null 2>&1 && continue
    echo; echo "### \`$t\`"; echo '```diff'
    diff -u --label renode --label qemu <(printf '%s\n' "$rn" | head -n "$n") <(printf '%s\n' "$qn" | head -n "$n") | head -60
    echo '```'
  done
} > "$OUT"
echo "wrote $OUT"
