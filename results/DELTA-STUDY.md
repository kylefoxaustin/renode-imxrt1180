# Zephyr console delta study — QEMU vs Renode

> # ⛔ SUPERSEDED — kept as a record of an earlier state, not as a result.
>
> Generated 2026-09-18T06:54Z, **before** @rt1180emulator's fix `3c9b8d266a`
> ("give the M7 its real 16-region MPU"). At that moment their ztest suites
> produced no console at all, so every ztest row below reads `renode only
> (not run on QEMU)` — which was true of the *tooling that morning* and is not
> true of either model now.
>
> **The live delta study is `results/zephyr-delta.tsv` +
> `results/zephyr-delta-classified.tsv`,** summarised in `SCORECARD.md`:
> 83 targets, **72/83 (87 %) agree**. Do not quote any number from this file.
>
> It is left in the tree deliberately. A study that was one-sided because the
> *other* tool could not run the corpus yet is exactly the kind of result that
> looks like a finding and is not one — the same failure mode as the wall-clock
> guard written up in EXPERIMENT.md, and worth being able to point at.

> ⚠️ **SCOPE OF THE QEMU COLUMN.** @rt1180emulator's model currently reaches a
> console verdict for the printf **samples** only. Their ztest kernel suites
> produce 0 console bytes, 0 SysTicks, and halt after the kernel's first SVC
> (with or without -icount), so they are an open bring-up gap on that side.
> **Any identical row below is therefore samples-only agreement and must NOT
> be read as ztest parity.**

Generated 2026-09-18T06:54:40Z. Each row is one firmware binary
run on both models; identical normalized console output = agreement.

| target | renode | qemu | verdict |
|---|---|---|---|
| `cm33-samples-hello_world` | 2 lines | — | **renode only** (not run on QEMU) |
| `cm33-samples-philosophers` | 339 lines | — | **renode only** (not run on QEMU) |
| `cm33-samples-synchronization` | 21 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-arch-arm-arm_custom_interrupt` | 21 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-arch-arm-arm_hardfault_validation` | 15 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-arch-arm-arm_interrupt` | 9 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-arch-arm-arm_irq_advanced_features` | 31 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-arch-arm-arm_irq_vector_table` | 18 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-arch-arm-arm_mpu_wt` | 50 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-arch-arm-arm_runtime_nmi` | 20 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-arch-arm-arm_thread_swap` | 10 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-cache` | 25 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-cleanup` | 49 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-common` | 394 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-condvar-condvar_api` | 123 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-context` | 92 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-device` | 114 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-early_sleep` | 22 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-events-event_api` | 33 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-fifo-fifo_api` | 210 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-fifo-fifo_timeout` | 42 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-lifo-lifo_api` | 208 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-mbox-mbox_api` | 85 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-mem_heap-k_heap_api` | 112 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-mem_protect-protection` | 9 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-mem_protect-stackprot` | 62 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-mem_slab-mslab_api` | 49 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-mem_slab-mslab` | 37 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-msgq-msgq_api` | 90 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-mutex-mutex_api` | 42 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-mutex-mutex_error_case` | 109 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-pipe-pipe_api` | 99 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-poll` | 216 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-queue` | 254 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-sched-schedule_api` | 144 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-semaphore-semaphore` | 218 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-sleep` | 25 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-threads-thread_apis` | 122 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-threads-thread_stack` | 179 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-timer-timer_api` | 73 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-timer-timer_monotonic` | 21 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-kernel-workq-work` | 144 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-lib-cbprintf_package` | 106 lines | — | **renode only** (not run on QEMU) |
| `cm33-tests-subsys-logging-log_api` | 235 lines | — | **renode only** (not run on QEMU) |
| `cm7-samples-hello_world` | ✓ | ✓ | **identical** |
| `cm7-samples-philosophers` | 345 lines | — | **renode only** (not run on QEMU) |
| `cm7-samples-synchronization` | 22 lines | 9 lines | **identical over the 9-line common prefix** (capture lengths differ: run duration, not model) |
| `cm7-tests-arch-arm-arm_hardfault_validation` | 39 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-arch-arm-arm_interrupt` | 83 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-common` | 394 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-context` | 92 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-fifo-fifo_api` | 210 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-mutex-mutex_api` | 42 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-poll` | 216 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-sched-schedule_api` | 144 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-threads-thread_apis` | 249 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-timer-timer_api` | 73 lines | — | **renode only** (not run on QEMU) |
| `cm7-tests-kernel-workq-work` | 144 lines | — | **renode only** (not run on QEMU) |

**Totals:** 58 compared · 2 identical · 0 differing · 56 renode-only · 0 qemu-only

## Per-target diffs
