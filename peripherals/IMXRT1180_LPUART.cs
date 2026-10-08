//
// RT1180 LPUART — Renode's stock NXP_LPUART with its RESET VALUES corrected.
//
// Found by scripts/reset_values_check.sh, which reads every register the RT1180
// Reference Manual names and diffs against @rt1180emulator's RM-extracted golden.
// Four registers disagreed, on ALL ELEVEN instances -- 44 of the run's 186
// Renode-only mismatches, and ONE root cause:
//
//   reg     offset   RM golden     stock Renode
//   BAUD    0x010    0x0F000004    0x00000000
//   FIFO    0x028    0x00C00033    0x00C10033     (bit 16, RXUF, set at reset)
//   DATARO  0x030    0x00001000    0x00000000     (bit 12, RXEMPT, clear at reset)
//   TOSR    0x05C    0x0000000F    0x00000000     (not modelled at all)
//
// ⭐ WHY THIS MATTERS EVEN THOUGH EVERY TEST PASSES WITHOUT IT.
//
// The whole corpus is green with the stock model -- 75/75 Zephyr, 30/30 SDK -- so no
// firmware in the corpus reads these four. That is exactly the shape of a latent
// defect: a wrong reset value is silent until the one driver that read-modify-writes
// the register arrives, and then it writes back a configuration the silicon never had.
// This project has already paid for that once, on RTWDOG CS: reading 0 instead of
// 0x900 meant RTWDOG_Init built its config on a zero that silicon never reports.
//
// And FIFO's bit 16 is the SAME BUG AS RTWDOG's RCS, in a different block. RXUF means
// "a receive underflow HAS OCCURRED". At reset none has. The stock model asserts it
// anyway -- a peripheral claiming an event that never happened, which is the defect
// class this project keeps finding. The RTWDOG model in this tree carries that lesson
// in a comment; this is its second instance.
//
// ⚠️ WHAT THIS IS AND IS NOT.
//
// It is a RESET-VALUE correction. It is NOT a behavioural model of the four fields.
// FIFO.RXUF and DATARO.RXEMPT are STATUS bits whose true value tracks FIFO state, and
// a subclass cannot see the base model's private FIFO. So each corrected register
// returns the RM reset value until the guest WRITES that register, after which the
// base model governs exactly as before. That is correct at reset -- which is what the
// RM specifies and what a driver reads before it touches the block -- and unchanged
// from today's behaviour afterwards. Claiming more would be modelling I have not done.
//
// Implementation note: NXP_LPUART.ReadDoubleWord is NOT virtual, so this cannot
// `override` it. Re-declaring IDoubleWordPeripheral in the base list REIMPLEMENTS the
// interface, and since Renode reaches peripherals through that interface, `new` binds
// correctly. Verified by measurement, not assumed: a probe subclass returned the
// corrected 0x0F000004 through a plain `sysbus ReadDoubleWord`.
//
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.UART;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_LPUART : NXP_LPUART, IDoubleWordPeripheral
    {
        public IMXRT1180_LPUART(IMachine machine, long frequency = 8000000,
                                bool hasGlobalRegisters = true, bool hasFifoRegisters = true,
                                uint fifoSize = 4, bool separateIRQs = false)
            : base(machine, frequency, hasGlobalRegisters, hasFifoRegisters, fifoSize, separateIRQs)
        {
            written = new HashSet<long>();
        }

        // RM reset values, SOURCED from rm-golden.json (extracted from IMXRT1180RM.pdf
        // + CMSIS). Not one of these is inferred from a neighbouring register -- the
        // adc2 IRQ fabrication in m1.repl records what that costs.
        private static readonly Dictionary<long, uint> ResetValues = new Dictionary<long, uint>
        {
            { 0x010, 0x0F000004 },  // BAUD   — OSR=0x0F, SBR=4
            { 0x028, 0x00C00033 },  // FIFO   — RXUF (bit 16) CLEAR: no underflow has occurred
            { 0x030, 0x00001000 },  // DATARO — RXEMPT (bit 12) SET: the RX FIFO is empty
            { 0x05C, 0x0000000F },  // TOSR   — not modelled by the stock peripheral
        };

        public new uint ReadDoubleWord(long offset)
        {
            if(ResetValues.TryGetValue(offset, out var reset) && !written.Contains(offset))
            {
                return reset;
            }
            return base.ReadDoubleWord(offset);
        }

        public new void WriteDoubleWord(long offset, uint value)
        {
            // Once the guest writes a register, the reset value is history and the base
            // model owns it again. TOSR is the exception: the stock peripheral has no
            // register there at all, so a write would be dropped with an "unhandled
            // offset" warning either way -- recording it keeps the read path honest
            // rather than pinning TOSR to its reset value forever.
            written.Add(offset);
            base.WriteDoubleWord(offset, value);
        }

        public new void Reset()
        {
            base.Reset();
            written.Clear();
        }

        private readonly HashSet<long> written;
    }
}
