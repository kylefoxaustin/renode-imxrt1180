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
    MUA_RCR     = 0x1u;                /* RIE0: IRQ when MUA.RR[0] is full */
    NVIC_ISER0  = (1u << MU1_IRQ);

    /* release the M7 (two-gate: INITVTOR + WAIT clear, then BT_RELEASE_M7) */
    BLK_M7_CFG = M7_IMAGE_ADDR;
    SRC_SCR    = 0x1u;

    /* tell the M7 we are going down, THEN sleep. The M7 additionally delays. */
    SHARED_FLAG = M33_ASLEEP;
    asm volatile("dsb" ::: "memory");

    /* One WFI, not a loop: if the core wakes at all it wakes exactly here. A
     * loop would let a spurious wake retry and hide a single lost wake-up. */
    asm volatile("wfi");

    if (got == 0xBEEF0001u) {
        puts_("M33: PASS - woke from WFI and the handler ran\r\n");
    } else {
        puts_("M33: FAIL - did not wake from WFI with the handler's value\r\n");
    }
    sh(SYS_EXIT, (void *)0x20026u);
    for (;;) {
    }
}
