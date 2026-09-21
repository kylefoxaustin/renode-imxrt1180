//
// NXP i.MX RT1180 USB 2.0 OTG (ChipIdea USB-HS) -- device-mode controller.
//
// PORTED from the QEMU oracle's hw/usb/imxrt1180_usb.c (296 lines). Every offset,
// reset value and capability number below comes from that file. None is
// re-derived, and none is invented -- these are the silicon's own numbers.
//
// The USB stack (USB_DeviceEhciInit / USB_DeviceRun) resets the controller, reads
// its capabilities, sets device mode, programs the endpoint queue-head list and
// starts the controller, then waits to be enumerated by a host.
//
// ⭐ TWO REGISTERS DESCRIBING ONE RESOURCE MUST NOT DISAGREE.
// The endpoint count is reported TWICE: DCCPARAMS[DEN] (bits 4:0), which
// USB_DeviceInit sizes its QH list from, and HWDEVICE[DEVEP] (bits 5:1). Both are
// derived from the single constant NumberOfEndpoints below, and the constructor
// asserts HWDEVICE == 0x11 (the RM's reset value) so a change that made them
// disagree fails loudly instead of shipping a chip that contradicts itself.
// (The oracle's lesson, from 91emulator and mcxn947qemu, who found a fabricated
// USB chip ID advertising 2 endpoints "while disagreeing with its own other
// register about it" -- the disagreement was the tell.)
//
// ⭐ FIDELITY: NO USB HOST IS ATTACHED, AND THIS DOES NOT FABRICATE ONE.
// Controller init/run completes so firmware progresses, but no USB reset or
// port-change is ever asserted, so the device stays UN-ENUMERATED -- the honest
// outcome for a headless target. PORTSC1's CCS bit is CLEAR: a driver polling for
// a connect is not waiting for an impossible event, it is correctly observing an
// empty port. No transfer engine, so no endpoint completion or interrupt is ever
// raised. Declared, not smuggled.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.USBDeprecated
{
    public class IMXRT1180_USB : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_USB()
        {
            // The RM's HWDEVICE reset value IS 0x11 (DC | 8 endpoints). Fail here
            // rather than silently disagree with the manual and with DCCPARAMS.
            if(HwDeviceValue != 0x00000011u)
            {
                throw new ConstructionException(
                    $"HWDEVICE would be 0x{HwDeviceValue:X} but the RM's reset value is 0x11 -- "
                    + "DCCPARAMS[DEN] and HWDEVICE[DEVEP] must describe the SAME endpoint count.");
            }
            regs = new uint[Size / 4];
            Reset();
        }

        public long Size => 0x200;

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            foreach(var entry in PowerOnReset)
            {
                regs[entry.Item1 / 4] = entry.Item2;
            }
            runningLogged = false;
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size)
            {
                this.Log(LogLevel.Warning, "Out-of-bounds read at 0x{0:X}", offset);
                return 0;
            }

            switch((Registers)offset)
            {
                case Registers.CapLengthHciVersion:
                    return ((uint)HciVersion << 16) | CapLength;
                case Registers.DciVersion:
                    return 0x00000001;
                case Registers.DccParams:
                    // Device + host capable. The endpoint count comes from the SAME
                    // constant HWDEVICE reports, so the two cannot drift apart.
                    return DccParamsHc | DccParamsDc | (NumberOfEndpoints & 0x1Fu);
                case Registers.UsbCmd:
                    // RST is self-clearing: report the reset already complete.
                    return regs[offset / 4] & ~UsbCmdRst;
                case Registers.EndptPrime:
                case Registers.EndptFlush:
                    // No transfer engine to stay busy: prime/flush complete instantly.
                    return 0;
                default:
                    return regs[offset / 4];
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                this.Log(LogLevel.Warning, "Out-of-bounds write at 0x{0:X}", offset);
                return;
            }

            switch((Registers)offset)
            {
                case Registers.UsbSts:
                case Registers.EndptSetupStat:
                case Registers.EndptComplete:
                    regs[offset / 4] &= ~value;   // W1C
                    return;
                case Registers.UsbCmd:
                    regs[offset / 4] = value;
                    if((value & UsbCmdRs) != 0 && !runningLogged)
                    {
                        runningLogged = true;
                        this.Log(LogLevel.Warning, "Controller started (RS=1) but no USB host is "
                            + "attached to this machine -- the device will not enumerate (not faked).");
                    }
                    return;
                default:
                    regs[offset / 4] = value;
                    return;
            }
        }

        private readonly uint[] regs;
        private bool runningLogged;

        private const uint NumberOfEndpoints = 8;
        private const uint HwDeviceDc        = 0x00000001;  // device capable
        private const int  HwDeviceDevEpShift = 1;          // endpoint count, bits 5:1
        private const uint HwDeviceValue     = HwDeviceDc | (NumberOfEndpoints << HwDeviceDevEpShift);

        private const uint UsbCmdRs   = 0x00000001;   // Run/Stop
        private const uint UsbCmdRst  = 0x00000002;   // controller reset (self-clearing)
        private const uint DccParamsDc = 0x00000080;  // device capable
        private const uint DccParamsHc = 0x00000100;  // host capable
        // CAPLENGTH = opregbase - capsbase = 0x140 - 0x100
        private const uint CapLength  = 0x40;
        private const uint HciVersion = 0x0100;

        private enum Registers : long
        {
            CapLengthHciVersion = 0x100,
            DciVersion          = 0x120,
            DccParams           = 0x124,
            UsbCmd              = 0x140,
            UsbSts              = 0x144,
            EndptSetupStat      = 0x1AC,
            EndptPrime          = 0x1B0,
            EndptFlush          = 0x1B4,
            EndptComplete       = 0x1BC,
        }

        // POR values from the RM's cold-POR column, via the oracle's usb_por[].
        // ⭐ ON A CAPABILITY REGISTER, UNDER-REPORTING IS THE SAFE ERROR DIRECTION.
        // Zero is NOT neutral: it claims "no ports, no endpoints, no buffers" from a
        // controller whose registers plainly exist. Change these only if you can name
        // a guest that waits forever because of them -- and record THAT reason here.
        private static readonly Tuple<long, uint>[] PowerOnReset =
        {
            Tuple.Create(0x000L, 0xE4A1FA05u),   // ID -- the controller's own identity
            Tuple.Create(0x004L, 0x00000015u),   // HWGENERAL
            Tuple.Create(0x008L, 0x10020001u),   // HWHOST
            Tuple.Create(0x00CL, HwDeviceValue), // HWDEVICE -- SAME endpoint count as DCCPARAMS
            Tuple.Create(0x010L, 0x80080B08u),   // HWTXBUF
            Tuple.Create(0x014L, 0x00000808u),   // HWRXBUF
            Tuple.Create(0x090L, 0x00000002u),   // SBUSCFG
            Tuple.Create(0x104L, 0x00010011u),   // HCSPARAMS
            Tuple.Create(0x108L, 0x00000006u),   // HCCPARAMS
            Tuple.Create(0x140L, 0x00080000u),   // USBCMD
            Tuple.Create(0x144L, 0x00000080u),   // USBSTS
            Tuple.Create(0x160L, 0x00000808u),   // BURSTSIZE
            Tuple.Create(0x180L, 0x00000001u),   // CONFIGFLAG
            Tuple.Create(0x184L, 0x1C000004u),   // PORTSC1 -- CCS CLEAR: nothing plugged in
            Tuple.Create(0x1A4L, 0x00202F20u),   // OTGSC
            Tuple.Create(0x1A8L, 0x00005000u),   // USBMODE
            Tuple.Create(0x1C0L, 0x00800080u),   // ENDPTCTRL0
        };
    }
}
