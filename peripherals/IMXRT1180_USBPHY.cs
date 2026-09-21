//
// NXP i.MX RT1180 USBPHY -- USB 2.0 high-speed PHY PLL readiness.
//
// PORTED from the QEMU oracle's hw/misc/imxrt1180_usbphy.c (196 lines). Every
// offset and reset value below comes from that file; none is re-derived here.
//
// WHY: USB bring-up (SDK CLOCK_EnableUsbhs0PhyPllClock / USB_EhciPhyInit) spins
//          while(0 == (USBPHY->PLL_SIC & USBPHY_PLL_SIC_PLL_LOCK)) { }
// MEASURED on the stock prebuilt usb_device_dfu images: 9555 reads of
// 0x42CA00A0 (USBPHY1 + PLL_SIC) in one 5 s run, console silent, nothing else
// spinning. That single register was the whole blocker for 3 corpus rows.
//
// ⭐ THE BLOCK IS BORN HELD IN RESET. CTRL @0x30 resets to 0xC000_0000 --
// SFTRST (bit 31) AND CLKGATE (bit 30) both SET. The silicon holds the PHY in
// soft reset with its clock gated until firmware releases it, which is exactly
// what USB_EhciPhyInit does (CTRL_CLR = SFTRST, then CTRL_CLR = CLKGATE). A
// zero-filled model is NOT a simpler model -- it is a model of a board that has
// already booted. Same for PWD @0x00 = 0x001E1C00: the transmitters, receivers
// and bandgap reset POWERED DOWN, and firmware read-modify-writes this register,
// so a zeroed PWD makes the guest read "everything is already powered up" and
// write that back as its own configuration.
// (That lesson is the oracle's, hand-read from the RM bit diagram because their
// automated reset-value extractor REFUSED the row -- the RM prints "See section"
// instead of a value. A refusal is not a check.)
//
// NOT MODELLED, flagged rather than faked: USB data-line signalling, UTMI, and
// charger detection. None is needed to bring the controller up.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_USBPHY : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_USBPHY()
        {
            regs = new uint[Size / 4];
            Reset();
        }

        public long Size => 0x1000;

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            foreach(var entry in PowerOnReset)
            {
                regs[entry.Item1 / 4] = entry.Item2;
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size)
            {
                this.Log(LogLevel.Warning, "Out-of-bounds read at 0x{0:X}", offset);
                return 0;
            }

            // SET/CLR/TOG aliases read back the underlying base register.
            var baseOffset = offset & ~0xCL;
            var value = regs[baseOffset / 4];

            if(baseOffset == (long)Registers.PllSic)
            {
                // The 480 MHz PHY PLL locks (near-)instantly once powered. Report
                // LOCK whenever firmware has powered the PLL or enabled the USB
                // clocks, so the "wait for lock" spin terminates.
                if((value & (PllSicPllPower | PllSicPllEnUsbClks)) != 0)
                {
                    value |= PllSicPllLock;
                }
            }
            return value;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                this.Log(LogLevel.Warning, "Out-of-bounds write at 0x{0:X}", offset);
                return;
            }

            // i.MX register-bank convention: for base register X,
            // X+4 = SET (OR), X+8 = CLR (AND NOT), X+C = TOG (XOR); X is a plain write.
            var index = (offset & ~0xCL) / 4;
            switch(offset & 0xCL)
            {
                case 0x0: regs[index]  = value;  break;
                case 0x4: regs[index] |= value;  break;
                case 0x8: regs[index] &= ~value; break;
                case 0xC: regs[index] ^= value;  break;
            }
        }

        private readonly uint[] regs;

        private const uint PllSicPllPower    = 0x00001000;  // bit 12: PLL powered up
        private const uint PllSicPllEnUsbClks = 0x00000040; // bit  6: gate USB clocks on
        private const uint PllSicPllLock     = 0x80000000;  // bit 31: RO, PLL locked

        private enum Registers : long
        {
            Pwd    = 0x000,
            Tx     = 0x010,
            Ctrl   = 0x030,
            Debug  = 0x050,
            Debug1 = 0x070,
            Version = 0x080,
            PllSic = 0x0A0,
        }

        // POR values from the RM's cold-POR column, via the QEMU oracle's usbphy_por[].
        private static readonly Tuple<long, uint>[] PowerOnReset =
        {
            Tuple.Create(0x030L, 0xC0000000u),  // CTRL -- SFTRST | CLKGATE: HELD IN RESET, GATED
            Tuple.Create(0x000L, 0x001E1C00u),  // PWD  -- analog blocks POWERED DOWN
            Tuple.Create(0x010L, 0x10080807u),  // TX
            Tuple.Create(0x050L, 0x7F180000u),  // DEBUG
            Tuple.Create(0x070L, 0x00001000u),  // DEBUG1
            Tuple.Create(0x080L, 0x05000000u),  // VERSION
            Tuple.Create(0x0A0L, 0x00D12000u),  // PLL_SIC
            Tuple.Create(0x0C0L, 0x00700004u),  // USB1_VBUS_DETECT
            Tuple.Create(0x0D0L, 0x00000001u),  // USB1_VBUS_DET_STAT
            Tuple.Create(0x0E0L, 0x80180000u),  // USB1_CHRG_DETECT
            Tuple.Create(0x100L, 0x82000402u),  // ANACTRL
            Tuple.Create(0x110L, 0x00550000u),  // USB1_LOOPBACK
            Tuple.Create(0x130L, 0x0000007Fu),  // TRIM_OVERRIDE_EN
        };
    }
}
