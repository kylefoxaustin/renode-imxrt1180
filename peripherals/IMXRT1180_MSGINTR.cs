//
// NXP i.MX RT1180 MSGINTR -- message-signalled interrupt router.
//
// PORTED from the QEMU oracle's hw/intc/imxrt1180_msgintr.c. Register map and
// semantics are from that file.
//
// ⭐ WHY A NETWORK CONTROLLER NEEDED AN INTERRUPT ROUTER. NETC does not raise a
// wire; it performs an MSI-X WRITE. The target address the driver programs into
// the MSI-X table is MSGINTR1 (0x428A0000) -- MEASURED here: the emitted vector
// carried addr=0x428A0000 data=0x1. This block turns that MMIO write into an NVIC
// interrupt. Without it the write lands nowhere, the driver's TX-completion
// callback never runs, and exactly ONE of the four management frames is sent
// before the example stalls -- which is precisely what the console showed.
//
// Per channel c (3 channels):
//   MSIIR @ c*8      write-only: writing index N sets bit (1<<N) in MSIR[c]
//                    and raises the shared NVIC line
//   MSIR  @ c*8 + 4  read: returns the pending bits AND CLEARS them (the ISR
//                    consumes by reading)
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    public class IMXRT1180_MSGINTR : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_MSGINTR()
        {
            pending = new uint[NumberOfChannels];
            IRQ = new GPIO();
            Reset();
        }

        public long Size => 0x1000;
        public GPIO IRQ { get; private set; }

        public void Reset()
        {
            Array.Clear(pending, 0, pending.Length);
            Update();
        }

        public uint ReadDoubleWord(long offset)
        {
            var channel = offset / 8;
            if((offset & 0x4) != 0 && channel < NumberOfChannels)
            {
                // MSIR: return the pending bits and clear them.
                var value = pending[channel];
                pending[channel] = 0;
                Update();
                return value;
            }
            return 0;   // MSIIR is write-only
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            var channel = offset / 8;
            if((offset & 0x4) == 0 && channel < NumberOfChannels)
            {
                // MSIIR: the written value is an INDEX, not a mask.
                pending[channel] |= 1u << (int)(value & 0x1F);
                Update();
            }
        }

        private void Update()
        {
            var any = 0u;
            foreach(var p in pending)
            {
                any |= p;
            }
            IRQ.Set(any != 0);
        }

        private readonly uint[] pending;
        private const int NumberOfChannels = 3;
    }
}
