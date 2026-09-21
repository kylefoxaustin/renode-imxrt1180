/*
 * PROBE (Renode only): does the Cortex-M33 wake from WFI on an external NVIC IRQ?
 *
 * Mirror of the oracle's imxrt1180-mu test with the roles swapped: here the M33
 * is the sleeper and the M7 is the sender. See README.md -- this is a probe of
 * one emulator, not a fidelity oracle, and is never scored as an equivalency row.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include <stdint.h>

#define SYS_WRITE0 0x04
#define SYS_EXIT   0x18

/* All addresses below are from the project brief's verified-facts table, which
 * is grounded in the QEMU model + CMSIS. Nothing here is invented. */
#define BLK_M7_CFG (*(volatile uint32_t *)0x444F0080u) /* BLK_CTRL_S_AONMIX */
#define SRC_SCR    (*(volatile uint32_t *)0x44460010u) /* SRC_GENERAL       */
#define M7_IMAGE_ADDR 0x303C0000u
#define STACK_TOP     0x20020000u

/* MUA = CM33 side of MU1 @ 0x4422_0000. */
#define MUA_RSR (*(volatile uint32_t *)0x4422012Cu)
#define MUA_RCR (*(volatile uint32_t *)0x44220128u)   /* RIEn */
#define MUA_RSR (*(volatile uint32_t *)0x4422012Cu)
#define MUA_RR0 (*(volatile uint32_t *)0x44220280u)

/* Shared OCRAM handshake cell -- the same address the oracle's cm7wait test
 * uses for its cross-core flag, so the mapping is already proven on both sides. */
#define SHARED_FLAG (*(volatile uint32_t *)0x20490000u)
#define M33_ASLEEP  0x5EEDA51Du

#define NVIC_ISER0 (*(volatile uint32_t *)0xE000E100u)
#define MU1_IRQ    21

static long sh(long op, void *arg)
{
    register long r0 asm("r0") = op;
    register void *r1 asm("r1") = arg;
    asm volatile("bkpt 0xAB" : "+r"(r0) : "r"(r1) : "memory");
    return r0;
}
static void puts_(const char *s) { sh(SYS_WRITE0, (void *)s); }

void reset_handler(void);
void mu_isr(void);

static volatile uint32_t got;

__attribute__((section(".vectors"), used))
void (* const vt[])(void) = {
    [0]            = (void (*)(void))STACK_TOP,
    [1]            = reset_handler,
    [16 + MU1_IRQ] = mu_isr,
};

void mu_isr(void)
{
    got = MUA_RR0;      /* read clears RSR.RF0, the IRQ source */
}

void reset_handler(void)
{
    puts_("M33: arming MU1 receive IRQ, then sleeping in WFI...\r\n");

    SHARED_FLAG = 0u;

    /*
     * ⭐ THE TRIGGER IS AN INTERRUPT THAT IS *ALREADY PENDING* WHEN WFI EXECUTES.
     *
     * The first version of this probe had the M7 send only AFTER the M33 was
     * asleep, and it PASSED on the pre-fix library -- which looked like "the M33 is
     * immune" and was really "this probe never entered the window". In the oracle's
     * mu test the M33 sends BEFORE the M7 sleeps, so the IRQ is pending the moment
     * WFI runs. That is not an exotic race: it is the ordinary
     * "mask, set up, enable, sleep" idiom every RTOS idle path uses.
     *
     * So: mask with PRIMASK, arm everything, WAIT for the message to actually
     * arrive (pending but masked), and only then enable-and-sleep back to back.
     */
    MUA_RCR     = 0x1u;                /* RIE0 armed; NVIC still DISABLED, so the
                                        * IRQ will sit pending-but-masked */

    /* release the M7 (two-gate: INITVTOR + WAIT clear, then BT_RELEASE_M7) */
    BLK_M7_CFG = M7_IMAGE_ADDR;
    SRC_SCR    = 0x1u;

    SHARED_FLAG = M33_ASLEEP;
    asm volatile("dsb" ::: "memory");

    /* Spin until the word has genuinely landed: RSR.RF0 set => IRQ 21 is pending
     * in the NVIC, held off only by PRIMASK. */
    {
        volatile uint32_t guard = 200000000u;
        while (!(MUA_RSR & 0x1u) && --guard) {
        }
        if (!(MUA_RSR & 0x1u)) {
            puts_("M33: FAIL - the M7 never sent; the probe never armed its window\r\n");
            sh(SYS_EXIT, (void *)0x20026u);
        }
    }

    /*
     * ⭐ ENABLE AND SLEEP WITH NOTHING BETWEEN THEM -- the shape of the mu test.
     *
     * tlib checks for interrupts at TRANSLATION BLOCK boundaries. In mu, the M7's
     * `NVIC_ISER0 = ...` and its `wfi` sit in the same block, so no check happens
     * between them: the block runs to the end, HELPER(wfi) sets wfi=1, and only
     * THEN is the pending interrupt processed -- with the sleep flag still set.
     *
     * My previous attempt put `cpsie i` before the WFI. That ends a block (it
     * changes PRIMASK), so the interrupt was taken BEFORE the WFI, the handler ran,
     * and the core then slept on a WFI with nothing left to wake it. `got` was
     * 0xBEEF0001 the whole time: THE PROBE FAILED, NOT THE CORE -- and it failed in
     * the direction that looks like a finding.
     *
     * The loop form is also deliberate. A bare WFI cannot tell "never woke" from
     * "woke before I slept"; the loop exits without sleeping if the handler already
     * ran, and still hangs if the core parks inside its handler.
     */
    NVIC_ISER0 = (1u << MU1_IRQ);
    while (got != 0xBEEF0001u) {
        asm volatile("wfi");
    }

    if (got == 0xBEEF0001u) {
        puts_("M33: PASS - woke from WFI and the handler ran\r\n");
    } else {
        puts_("M33: FAIL - did not wake from WFI with the handler's value\r\n");
    }
    sh(SYS_EXIT, (void *)0x20026u);
    for (;;) {
    }
}
