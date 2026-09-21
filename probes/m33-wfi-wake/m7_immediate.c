/*
 * PROBE (Renode only) -- Cortex-M7 sender. See README.md.
 *
 * Waits until the M33 has published "I am asleep", then waits a long while
 * MORE, so the message provably lands on a core that is already in WFI. An
 * interrupt that arrives BEFORE the WFI completes it immediately, which would
 * pass the probe without ever exercising the defect.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include <stdint.h>

#define SYS_WRITE0 0x04
#define M7_STACK_TOP 0x30440000u

#define MUB_TR0 (*(volatile uint32_t *)0x44230200u)
#define SHARED_FLAG (*(volatile uint32_t *)0x20490000u)
#define M33_ASLEEP  0x5EEDA51Du

static long sh(long op, void *arg)
{
    register long r0 asm("r0") = op;
    register void *r1 asm("r1") = arg;
    asm volatile("bkpt 0xAB" : "+r"(r0) : "r"(r1) : "memory");
    return r0;
}

void m7_reset(void);

__attribute__((section(".vectors"), used))
void (* const vt[])(void) = {
    [0] = (void (*)(void))M7_STACK_TOP,
    [1] = m7_reset,
};

void m7_reset(void)
{
    sh(SYS_WRITE0, (void *)"M7:  up; waiting for the M33 to fall asleep...\r\n");

    volatile uint32_t guard = 200000000u;
    while (SHARED_FLAG != M33_ASLEEP && --guard) {
    }

    /* The flag says the M33 executed the store. WFI is a few instructions
     * later, so burn plenty of time before sending. */
    for (volatile uint32_t i = 0; i < 0u; i++) {
    }

    sh(SYS_WRITE0, (void *)"M7:  sending 0xBEEF0001 IMMEDIATELY (race window)...\r\n");
    MUB_TR0 = 0xBEEF0001u;

    /* Sleep rather than spin: a busy loop here costs 792 MHz of emulated
     * instructions per virtual second and made every run of this probe
     * unaffordably slow. The M7 has nothing left to do. */
    for (;;) {
        asm volatile("wfi");
    }
}
