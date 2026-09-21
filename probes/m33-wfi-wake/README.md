# Probe: does the Cortex-M33 wake from WFI on an external NVIC interrupt?

**This is a PROBE OF RENODE, not a fidelity oracle.** It does not belong in the
QEMU corpus and is never scored as an equivalency row. It answers one question
about one emulator, and it needs no reference model to do so.

## Why it exists

Renode defect #14 — exception entry does not clear `env->wfi`, so the core sleeps
inside its own handler — was found on the **M7** via the oracle's `mu` test. I
wrote that the M33 was *probably* unaffected, because Zephyr `kernel.common`
passes 394 lines and that requires waking from WFI via SysTick.

**That was an INFERENCE, and I labelled it as one.** This probe replaces it with a
measurement.

## What it does

The exact mirror of the oracle's `mu` test, with the roles swapped:

- **M33** releases the M7, arms `MUA.RCR` RIE0 + NVIC IRQ 21, publishes a
  "I am about to sleep" flag to shared OCRAM, and parks in `WFI`.
- **M7** polls for that flag, then **waits a long delay anyway**, so the message
  provably arrives while the M33 is already asleep — an interrupt that arrives
  *before* WFI would complete it immediately and the probe would pass without ever
  exercising the defect.
- The M33's ISR replies and the main path prints the verdict.

## Reading the result

A single PASS proves nothing on its own. **The verdict that matters is the flip:**

| library | expected |
|---|---|
| `.d11d12d13-verified` (pre-fix) | FAIL if the M33 is affected, PASS if immune |
| `.d11d12d13d14-verified` (fixed) | PASS |

- FAIL → PASS means the M33 **was** affected and the scope of defect #14 is every
  Cortex-M core, not just the secondary one.
- PASS → PASS means the M33's wake path differs and the defect is narrower.

Either answer is worth having; only the unmeasured guess was not.

---

## Outcome — the measurement did not arrive, and that is the result

**Probe 1 (`m33.c` + `m7.c`) — the ordinary case.** `PASS` on *both* libraries, with a
working negative control: with no sender, the M33 prints its arming line and never
proceeds, so the WFI genuinely parks it. Real, but it only shows the M33 waking when the
interrupt arrives **long after** it is asleep — `cpu_has_work()` sees it pending, clears
`wfi`, all is well. **It never enters the defect's window.**

**Probe 2 (`m33_pending.c`, first form) — `cpsie i` immediately before `wfi`.** `cpsie`
changes PRIMASK and therefore *ends a translation block*, so the interrupt was taken
**before** the WFI; the handler ran, and the core then slept on a WFI with nothing left
to wake it. `got` read `0xBEEF0001` the whole time.

**Probe 3 — enable-and-sleep in one block, `while(!got) wfi;`.** `got` again read
`0xBEEF0001` and the PE sat at `0xffe0138`, the branch *after* the WFI: the handler had
run between the loop's test and the `wfi`. **A lost-wakeup race in this probe's own
firmware** — the bug real RTOS idle paths avoid by holding PRIMASK across the
test-and-sleep (WFI wakes even with PRIMASK set).

> ⭐⭐⭐ **WRITING A CORRECT WFI PROBE IS HARD FOR EXACTLY THE REASON THE DEFECT EXISTS:
> THE THING UNDER TEST IS A RACE, SO THE INSTRUMENT IS RACING TOO.** Three attempts,
> three different failures — and **two of the three failed in the direction that looks
> like a finding.** A probe that hangs is indistinguishable from a core that hangs. Had
> I not read `got` before believing the console, I would have reported "the M33 is
> affected too" and been wrong, with a reproducible test case to wave at it.

**Status: OPEN.** The defect's mechanism is not core-specific and nothing in the patched
path is, but that is an argument, not a measurement. Left recorded as unresolved rather
than promoted to a conclusion.

**If you pick this up:** the window needs the interrupt to become pending *while
`env->wfi` is already set and inside the same `cpu_exec()` invocation*. In the oracle's
`mu` test that happens for free — the M33 sends before the M7 sleeps, and the M7's
`NVIC_ISER0` store and its `wfi` sit in one translation block, so tlib never gets a
boundary at which to take the interrupt the ordinary way. Reproducing it on the M33
means controlling **block boundaries**, not just instruction order.
