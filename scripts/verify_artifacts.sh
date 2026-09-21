#!/usr/bin/env bash
# Verify the pinned artifact set: COVERAGE first, then integrity.
#
# ⚠ TWO TRAPS THIS SCRIPT EXISTS TO AVOID, both hit during this project:
#
#  1. `sha256sum -c` returning all-OK says NOTHING about what is not listed. The
#     manifest once pinned 11 of 103 artifacts and verified clean the whole time.
#     So the floor is asserted BEFORE integrity is checked.
#
#  2. The manifest stores RELATIVE paths, so it must be checked from the artifact
#     root. Run elsewhere, every entry reports "FAILED open or read" -- and a naive
#     `grep -cv ': OK$'` counts those as CHECKSUM MISMATCHES. MEASURED: a closing
#     verification reported "103 mismatches" on a tree where nothing was wrong.
#     A check that cries catastrophe when all is well is as broken as one that
#     reports success when nothing is -- and it is the one you are more likely to
#     act on. So: cd to the root explicitly, and count MISSING and MISMATCHED
#     separately, because they mean different things.
set -u
ROOT="${ARTIFACT_ROOT:-$HOME/.cache/rt1180-artifacts}"
FLOOR="${MANIFEST_FLOOR:-105}"
cd "$ROOT" || { echo "verify: no artifact root at $ROOT" >&2; exit 2; }

pinned=$(grep -c . MANIFEST.sha256)
if [ "$pinned" -lt "$FLOOR" ]; then
    echo "FAIL coverage: manifest pins $pinned, floor is $FLOOR. Regenerate, do not lower." >&2
    exit 3
fi

out=$(sha256sum -c MANIFEST.sha256 2>/dev/null)
ok=$(printf '%s\n' "$out" | grep -c ': OK$')
missing=$(printf '%s\n' "$out" | grep -c 'FAILED open or read$')
bad=$(printf '%s\n' "$out" | grep -c ': FAILED$')

printf 'artifacts pinned: %s (floor %s)\n  verified OK: %s\n  MISSING:     %s\n  MISMATCHED:  %s\n' \
    "$pinned" "$FLOOR" "$ok" "$missing" "$bad"
[ "$missing" -eq 0 ] && [ "$bad" -eq 0 ] && [ "$ok" -eq "$pinned" ] || exit 4
