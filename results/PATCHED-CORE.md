# Results on the patched core (defects #11 + #12 + #13)

> **Measured on `translate-arm-m-le.so` = stock Renode 1.17.0 + the PPB patch +
> `patches/tlib-defect11-defect12.patch` + `patches/tlib-defect13.patch`.**
> ABI-verified 2564 symbols (0 missing / 0 extra); M0 positive control green.
> Every other results file in this directory is measured on the **baseline**
> library (stock + PPB only) and is NOT superseded by this one — the two are kept
> apart deliberately, because a number is only reproducible against the binary it
> was measured on.

## Targeted delta re-cut

The full 83-row re-cut measured at **~27 hours** of wall time, so this is a
targeted run over the 14 rows the three defects can plausibly move, compared
against the published baseline captures. Rows outside this set are unchanged by
construction — none of the three patches touches anything they exercise.

| target | baseline class | on the patched core |
|---|---|---|
| `cm33-arm_thread_swap` | `len+content` | **IDENTICAL** ⬆ |
| `cm33-arm_interrupt` | `len+content` (30 of 83 lines) | **83/83, 3 changed lines — all printed durations** ⬆ |
| `cm33-early_sleep` | `CONTENT` | unchanged (1 line: `ticks: 5000` vs `5001`) |
| `cm7-arm_interrupt` | `CONTENT` | unchanged (3 lines; ESF-after-stacking-fault nondeterminism) |
| `cm33-arm_mpu_wt` | `len+content` | unchanged — **shared limit**, both tools fail |
| `cm33-kernel-device` | `len+content` | unchanged — CMSE/TT fault layer, both tools PASS |
| `cm33-thread_apis` | `len+content` | unchanged — same CMSE layer |
| 6 rows already `identical` | `identical` | **IDENTICAL** — no regressions |

**Net: two rows move from a real delta to agreement.**

| | baseline core | patched core |
|---|---:|---:|
| identical | 48 | **49** |
| timing/nondet | 25 | **26** |
| len+content | 7 | **5** |
| CONTENT | 2 | 2 |
| partial | 1 | 1 |
| **agree** | **73/83 (88 %)** | **75/83 (90 %)** |

## What the remaining 8 are, and why none is "unknown"

- **2 × CMSE/TT fault layer** (`kernel-device`, `thread_apis`) — both tools *pass*
  the test; only the fault text differs. Not a fidelity gap in either machine
  model.
- **1 × shared architectural limit** (`arm_mpu_wt`) — coherent-memory emulation
  cannot satisfy a stale-cache premise. **Both tools fail**, confirmed from two
  sides.
- **2 × nondeterminism** (`early_sleep` tick quantisation, `cm7-arm_interrupt`
  ESF-after-stacking-fault, which is architecturally UNKNOWN).
- **3 × free-running / interleave** (`philosophers` ×2, `logging-logger`) —
  scheduler order and timestamps, compared over their common prefix by design.

> ⭐ **THERE IS NO ROW LEFT WHOSE CAUSE IS UNKNOWN.** That was true at 73/83 and
> is still true at 75/83; what the patches changed is that two of them stopped
> being *Renode's* fault. The headline number matters less than the fact that every
> row has a name.

## Provenance

| library | contents | what it backs |
|---|---|---|
| `…so.orig-1.17.0` | stock Renode 1.17.0 | untouched upstream baseline |
| `…so.ppbpatch-baseline` | + PPB patch (without which the M7 cannot boot) | **every number in `SCORECARD.md`** and the 83-row delta study |
| `…so.d11d12-verified` | + defects #11, #12 | the mutation proof in `EXPERIMENT.md` |
| *(validation tree)* | + defect #13 | **this file** |
