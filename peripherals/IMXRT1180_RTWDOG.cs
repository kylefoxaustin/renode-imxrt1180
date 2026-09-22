//
// RT1180 RTWDOG — the watchdog's CONFIGURATION interface. Ported from the QEMU model
// (hw/misc/imxrt1180_rtwdog.c). Like the oracle, this models the unlock/configure
// handshake so SystemInit proceeds; it does NOT model the countdown or the bite.
//
// ⭐ TWO RESET-VALUE LESSONS CARRIED OVER VERBATIM, BECAUSE THEY WERE PAID FOR ONCE
//    ALREADY ON THE OTHER SIDE AND THERE IS NO REASON TO PAY AGAIN.
//
// 1. CS RESETS TO 0x900, NOT 0. The oracle's comment: a zero CS is "a DISABLED
//    watchdog with a ZERO timeout". Firmware that read-modify-writes CS -- which is
//    exactly what RTWDOG_Init does after unlocking -- reads that zero and writes back
//    a configuration the silicon never had. 0x900 is CLK=1 | ULK, with EN (0x80)
//    CLEAR: this part comes up DISABLED and UNLOCKED. TOVAL resets to 0x400.
//
// 2. RCS MUST NOT BE ORed IN UNCONDITIONALLY. RCS means "the last RECONFIGURATION
//    SUCCEEDED", and at reset none has been attempted. Reporting it anyway is a
//    watchdog claiming success for work it was never asked to do -- the same defect
//    class this project keeps finding, and the reason it is a latched flag here.
//
// Found missing the same way RGPIO was: a Renode boot logged 9 unmapped accesses each
// at 0x442D0000 and 0x442E0000, with 0xC520 then 0xD928 written to +0x4 -- the RTWDOG
// unlock key sequence, going nowhere.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_RTWDOG : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_RTWDOG(IMachine machine)
        {
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case Registers.Cs:
                return cs | (reconfigured ? CsRcs : 0u) | (unlocked ? CsUlk : 0u);
            case Registers.Cnt:
                return cnt;
            case Registers.Toval:
                return toval;
            case Registers.Win:
                return win;
            default:
                this.Log(LogLevel.Noisy, "Unhandled read from offset 0x{0:X}", offset);
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
            case Registers.Cs:
                // RCS and ULK are read-only status; a write must not plant them.
                cs = value & ~(CsRcs | CsUlk);
                reconfigured = true;   // THIS is the reconfiguration RCS reports
                break;
            case Registers.Cnt:
                // Unlock sequence: 0xC520 then 0xD928, written to CNT.
                if((value & 0xFFFF) == UnlockKey0)
                {
                    unlockStep = 1;
                }
                else if(unlockStep == 1 && (value & 0xFFFF) == UnlockKey1)
                {
                    unlocked = true;
                    unlockStep = 0;
                }
                else
                {
                    unlockStep = 0;
                }
                break;
            case Registers.Toval:
                toval = value;
                break;
            case Registers.Win:
                win = value;
                break;
            default:
                this.Log(LogLevel.Noisy, "Unhandled write to offset 0x{0:X}, value 0x{1:X}", offset, value);
                break;
            }
        }

        public void Reset()
        {
            cs = 0x00000900;     // CLK = 1, ULK set; EN and RCS CLEAR
            reconfigured = false;
            unlocked = false;
            cnt = 0;
            toval = 0x00000400;
            win = 0;
            unlockStep = 0;
        }

        public long Size => 0x1000;

        private uint cs;
        private uint cnt;
        private uint toval;
        private uint win;
        private bool reconfigured;
        private bool unlocked;
        private int unlockStep;

        private const uint CsRcs = 0x00000400;   // reconfiguration success (RO)
        private const uint CsUlk = 0x00000800;   // unlocked (RO)
        private const uint UnlockKey0 = 0xC520;
        private const uint UnlockKey1 = 0xD928;

        private static class Registers
        {
            public const long Cs    = 0x0;
            public const long Cnt   = 0x4;
            public const long Toval = 0x8;
            public const long Win   = 0xC;
        }
    }
}
