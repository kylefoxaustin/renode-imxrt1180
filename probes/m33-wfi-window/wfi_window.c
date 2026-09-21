/*
 * DISCRIMINATING TEST for Renode/tlib defect #14 on the Cortex-M33.
 *
 * ── WHAT MAKES THIS DIFFERENT FROM THE SAFE-PATH TEST ────────────────────────
 * @rt1180emulator's imxrt1180-m33-wfi probe PASSES on both the pre-#14 and post-#14
 * libraries, and the core itself explains why: it takes its exception with
 * `entry wfi=0` -- the SAFE path, where the IRQ arrives in a later cpu_exec() call,
 * cpu_has_work() sees it pending and clears the sleep flag first.
 *
 * Defect #14 lives ONLY at `entry wfi=1`: the interrupt must become pending while
 * env->wfi is ALREADY set, inside the SAME cpu_exec() invocation. tlib checks
 * interrupts at TRANSLATION BLOCK boundaries, so that means:
 *
 *     the enable and the WFI must sit in ONE BLOCK, with nothing between them,
 *     and the source must ALREADY BE SATISFIED when the enable lands.
 *
 * That is exactly the shape of the oracle's `mu` test, where the M7 arms an
 * already-delivered MU word and sleeps: `MUB_RCR = RIE0; NVIC_ISER0 = ...; wfi;`.
 *
 * ── HOW THE SOURCE IS PRE-SATISFIED ──────────────────────────────────────────
 * NVIC STIR (0xE000EF00) software-pends an external IRQ. Pend it while the IRQ is
 * still DISABLED, so nothing can be taken yet; then enable-and-sleep back to back.
 * At the moment ISER lands, the interrupt is instantly pending-and-enabled -- and
 * the WFI in the same block runs before tlib gets a boundary to deliver it.
 *
 * ⭐ NOT `cpsie i`. Changing PRIMASK ENDS A BLOCK, so the interrupt is taken BEFORE
 *    the WFI, the handler runs, and the core then sleeps forever with nothing left
 *    to wake it. That is a lost-wakeup race in the PROBE, and it killed an earlier
 *    attempt of mine. It reads exactly like a hung core.
 *
 * ⭐ THE ENABLE+WFI PAIR IS ONE INLINE-ASM SEQUENCE, because the compiler is
 *    entitled to put a block boundary where I did not write one (@rt1180emulator's
 *    design-review point). Verify with objdump that `str` and `wfi` are adjacent.
 *
 * ── THE ORACLE IS THE MARKERS, NOT THE CONSOLE ───────────────────────────────
 * From the console alone, "the probe hung" and "the core hung" are the SAME
 * observation -- which is what defeated three earlier probes of mine. So the ISR
 * stamps a distinctive value at a fixed address a monitor can read without symbols,
 * and both markers are pre-stamped "not filled" before arming so a stale word
 * cannot masquerade as a result. Same scheme as the oracle's probe, deliberately.
 *
 *   0x20010000  0x14C0FFEE  handler body executed
 *   0x20010004  0x9E500DED  main resumed past WFI
 *   both pre-stamped 0xBADF11A6
 *
 * ── READING THE RESULT ───────────────────────────────────────────────────────
 * The verdict is the FLIP between libraries, and it is only meaningful if the
 * instrumented core confirms `entry wfi=1` for this run. Without that confirmation
 * a PASS means "the window was missed", not "the core is fine".
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include <stdint.h>

#define SYS_WRITE0 0x04
#define SYS_EXIT   0x18
#define STACK_TOP  0x20020000u

#define MARK_HANDLED (*(volatile uint32_t *)0x20010000u)
#define MARK_RESUMED (*(volatile uint32_t *)0x20010004u)
#define MARK_UNFILLED 0xBADF11A6u
#define MARK_H_MAGIC  0x14C0FFEEu
#define MARK_R_MAGIC  0x9E500DEDu

/* A plain external IRQ with nothing modelled behind it: the source is STIR, so no
 * peripheral has to exist. IRQ 30 is inside the RT1180's 239 external lines. */
#define TEST_IRQ   30u
#define NVIC_ISER0 0xE000E100u
#define NVIC_STIR  (*(volatile uint32_t *)0xE000EF00u)

static long sh(long op, void *arg)
{
    register long r0 asm("r0") = op;
    register void *r1 asm("r1") = arg;
    asm volatile("bkpt 0xAB" : "+r"(r0) : "r"(r1) : "memory");
    return r0;
}
static void puts_(const char *s) { sh(SYS_WRITE0, (void *)s); }

void reset_handler(void);
void test_isr(void);

__attribute__((section(".vectors"), used))
void (* const vt[])(void) = {
    [0]              = (void (*)(void))STACK_TOP,
    [1]              = reset_handler,
    [16 + TEST_IRQ]  = test_isr,
};

void test_isr(void)
{
    MARK_HANDLED = MARK_H_MAGIC;
    /* Clear the pending bit so a re-entry cannot forge a second pass. */
    *(volatile uint32_t *)0xE000E280u = (1u << TEST_IRQ);   /* NVIC_ICPR0 */
}

void reset_handler(void)
{
    MARK_HANDLED = MARK_UNFILLED;
    MARK_RESUMED = MARK_UNFILLED;
    asm volatile("dsb" ::: "memory");

    puts_("WFI-WINDOW: pending the IRQ while disabled, then enable+WFI in one block\r\n");

    /* Pre-satisfy the source: pend IRQ 30 while it is still DISABLED in the NVIC.
     * Nothing can be taken here -- there is no enable yet. */
    NVIC_STIR = TEST_IRQ;
    asm volatile("dsb" ::: "memory");

    /*
     * ENABLE AND SLEEP WITH NOTHING BETWEEN THEM.
     * One inline-asm sequence so no compiler-inserted boundary can separate them.
     * At the `str`, IRQ 30 becomes pending AND enabled; the `wfi` in the same block
     * executes before tlib reaches a boundary at which to deliver it -- so
     * HELPER(wfi) sets env->wfi = 1 and the exception is then taken with the sleep
     * flag still set. That is defect #14's window.
     */
    {
        register uint32_t bit  asm("r2") = (1u << TEST_IRQ);
        register uint32_t addr asm("r3") = NVIC_ISER0;
        asm volatile("str %0, [%1]\n\t"
                     "wfi\n\t"
                     : : "r"(bit), "r"(addr) : "memory");
    }

    MARK_RESUMED = MARK_R_MAGIC;
    asm volatile("dsb" ::: "memory");

    if (MARK_HANDLED == MARK_H_MAGIC && MARK_RESUMED == MARK_R_MAGIC) {
        puts_("WFI-WINDOW: PASS - woke inside the window, handler ran, main resumed\r\n");
    } else {
        puts_("WFI-WINDOW: FAIL - markers not both set\r\n");
    }
    sh(SYS_EXIT, (void *)0x20026u);
    for (;;) {
    }
}
