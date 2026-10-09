//
// i.MX RT1180 FlexSPI — ⚠ M0-SCOPE IDLE/RESET STUB, **NOT** A FlexSPI MODEL.
//
// SCOPE, STATED HONESTLY: the `--config debug` images this experiment runs link
// to TCM and never fetch from flash. The only thing they need from FlexSPI is
// BOARD_DeinitFlash's handshake: clear MCR0.MDIS, then poll STS0 for ARBIDLE
// then SEQIDLE. That handshake is ALL this class implements.
//
// It does NOT model: the LUT, sequence execution, IP/AHB command engines, the
// RX/TX FIFOs, or any attached NOR flash. There is no flash behind it.
// Anything that actually tries to run a command is logged as an ERROR rather
// than silently answered — a stub that quietly returns 0 to a real flash read
// is the "silent-wrong" failure the brief warns about, and the oracle's
// full model (hw/ssi/imxrt1180_flexspi.c, ~700 lines) is what would be needed.
//
// STS0 semantics are taken from the oracle, including the non-obvious part:
//   - STS0 resets to 0x2 (ARBIDLE alone): at cold reset the sequence engine has
//     never been clocked.
//   - SEQIDLE is NOT gated on MCR0.MDIS. A disabled module is trivially idle,
//     and fsl_flexspi RELIES on this — FLEXSPI_Init writes MCR0 with MDIS SET
//     and then immediately spins on GetBusIdleStatus(ARBIDLE && SEQIDLE).
//     Gating SEQIDLE on !MDIS wedged FLEXSPI_SetFlashConfig in the oracle.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_FlexSPI : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_FlexSPI(IMachine machine)
        {
            regs = new uint[Size / 4];
            Reset();
        }

        public long Size => 0x1000;

        public void Reset()
        {
            for(var i = 0; i < regs.Length; i++)
            {
                regs[i] = 0;
            }
            mcr0Written = false;

            // ── RM reset values ─────────────────────────────────────────────────
            // Found by scripts/reset_values_check.sh; SOURCED from rm-golden.json
            // (IMXRT1180RM.pdf + CMSIS). Plain config registers with no polling
            // semantics -- a driver reads these to discover the module's shape, and
            // reading 0 tells it the module has no sectors, no LUT and no AHB buffers.
            //
            // NOTE LUTKEY = 0x5AF05AF0. That is the LUT unlock key, and reading 0 for it
            // is not a benign wrong number: fsl_flexspi writes the key it read back when
            // it wants to unlock the LUT, so a zero key makes the unlock sequence a
            // no-op that still looks like it succeeded.
            regs[0x004 / 4] = 0xFFFFFFFF;   // MCR1   — seq/AHB timeout, all-ones at reset
            regs[0x008 / 4] = 0x200081F7;   // MCR2
            regs[0x00C / 4] = 0x00000018;   // AHBCR
            regs[0x018 / 4] = 0x5AF05AF0;   // LUTKEY
            regs[0x01C / 4] = 0x00000002;   // LUTCR
            regs[0x094 / 4] = 0x000000C3;   // FLSHCR4
            regs[0x0E8 / 4] = 0x01000100;   // STS2  — both lanes report "locked/ready"
            regs[0x000 / 4] = 0xFFFF80C2;   // MCR0

            // ⚠️ STS0 IS DELIBERATELY NOT SET HERE, AND THE READ PATH ABOVE KEEPS ITS
            //    mcr0Written GATE. The RM resets STS0 to 0x2 (ARBIDLE alone, SEQIDLE
            //    clear), but SEQIDLE must read 1 the moment firmware polls it:
            //    FLEXSPI_Init writes MCR0 with MDIS SET and then immediately spins on
            //    GetBusIdleStatus(ARBIDLE && SEQIDLE), and gating SEQIDLE wedged
            //    FLEXSPI_SetFlashConfig in the oracle. The gate satisfies both -- 0x2
            //    until MCR0 is written, 0x3 after -- which is why it is a gate and not a
            //    constant.
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size || offset < 0)
            {
                return 0;
            }
            if(offset == STS0)
            {
                // Commands complete inline -> arbiter always idle. SEQIDLE
                // reports the sequence engine, which at cold reset has never
                // been clocked; honour the RM's 0x2 reset until firmware first
                // configures the module (writes MCR0). [oracle]
                return mcr0Written ? (Sts0SeqIdle | Sts0ArbIdle) : Sts0ArbIdle;
            }
            return regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size || offset < 0)
            {
                return;
            }
            if(offset == MCR0)
            {
                mcr0Written = true;
                // SWRESET self-clears: the reset completes instantly.
                regs[offset / 4] = value & ~Mcr0SwReset;
                return;
            }
            if(offset == IPCMD && (value & IpcmdTrigger) != 0)
            {
                this.Log(LogLevel.Error,
                    "FlexSPI IP command triggered, but this is an M0-scope idle/reset STUB with no " +
                    "LUT engine and no flash behind it. The result would be fabricated, so nothing " +
                    "is executed. Port the oracle's hw/ssi/imxrt1180_flexspi.c if a rung needs this.");
                return;
            }
            regs[offset / 4] = value;
        }

        private readonly uint[] regs;
        private bool mcr0Written;

        private const long MCR0 = 0x000;
        private const long IPCMD = 0x0B0;
        private const long STS0 = 0x0E0;
        private const uint Mcr0SwReset = 1u << 0;
        private const uint IpcmdTrigger = 1u << 0;
        private const uint Sts0SeqIdle = 1u << 0;
        private const uint Sts0ArbIdle = 1u << 1;
    }
}
