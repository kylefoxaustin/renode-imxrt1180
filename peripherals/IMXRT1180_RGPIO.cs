//
// RT1180 RGPIO — functional GPIO. Ported from the QEMU model (hw/gpio/imxrt1180_rgpio.c),
// which is this project's reference oracle. Registers and semantics are SOURCED from it,
// not re-derived: PDOR/PSOR/PCOR/PTOR drive the output latch, PDDR selects direction,
// PDIR reads the pin level.
//
// ⭐ WHY THIS BLOCK EXISTS NOW, AND WHAT ITS ABSENCE COST.
// Renode had NO RGPIO at all while QEMU modelled all six. The cost was not a crash --
// it was an agreement row that meant nothing. `demo_apps/led_blinky` scored RAN/RAN ->
// "agree = YES" on the equivalency table, because both columns were reporting the
// weakest observable there is: the firmware did not fault. The QEMU corpus asserts
// something real for that row -- "RGPIO4[27] toggle observable in PDOR" -- and this
// side could not assert it, because the register the toggle lands in did not exist.
//
//   ⭐ A ROW WHERE BOTH SIDES REPORT "IT RAN" IS NOT AGREEMENT. It is two silences
//      that happen to match. Green is not fidelity.
//
// The symptom was in our own logs for hours before anyone read it: a Renode boot of the
// lab-3 firmware logged 18 unmapped writes to 0x43830044 and 18 to 0x43830054 --
// RGPIO4 PSOR and PDDR, i.e. the firmware setting a direction and driving a pin into
// nothing. Unmapped-access warnings are the model telling you which block is missing.
//
using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public class IMXRT1180_RGPIO : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput, IGPIOReceiver
    {
        public IMXRT1180_RGPIO(IMachine machine)
        {
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < PinCount; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = connections;
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case Registers.Pdor:
                return pdor;
            case Registers.Pdir:
                // Driven value on output pins; external level on inputs.
                return PinLevel();
            case Registers.Pddr:
                return pddr;
            default:
                this.Log(LogLevel.Noisy, "Unhandled read from offset 0x{0:X}", offset);
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            var oldDriven = pdor & pddr;
            switch(offset)
            {
            case Registers.Pdor:
                pdor = value;
                break;
            case Registers.Psor:
                pdor |= value;
                break;
            case Registers.Pcor:
                pdor &= ~value;
                break;
            case Registers.Ptor:
                pdor ^= value;
                break;
            case Registers.Pddr:
                pddr = value;
                break;
            default:
                this.Log(LogLevel.Noisy, "Unhandled write to offset 0x{0:X}, value 0x{1:X}", offset, value);
                return;
            }
            UpdateOutputs(oldDriven);
        }

        // An external driver (a board model, or a test) setting an input pin.
        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= PinCount)
            {
                this.Log(LogLevel.Warning, "Input on out-of-range pin {0}", number);
                return;
            }
            if(value)
            {
                inputs |= 1u << number;
            }
            else
            {
                inputs &= ~(1u << number);
            }
        }

        public void Reset()
        {
            // Matches the oracle's reset: everything clear.
            pdor = 0;
            pddr = 0;
            inputs = 0;
            foreach(var c in Connections.Values)
            {
                c.Unset();
            }
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }
        public long Size => 0x1000;

        private uint PinLevel()
        {
            // The oracle also masks with an input-disable register (idr); it has no
            // memory-mapped write path there, so it is always zero and is omitted
            // rather than modelled as a field nothing can reach.
            return (pdor & pddr) | (inputs & ~pddr);
        }

        private void UpdateOutputs(uint oldDriven)
        {
            var driven = pdor & pddr;
            var changed = driven ^ oldDriven;
            if(changed == 0)
            {
                return;
            }
            for(var i = 0; i < PinCount; i++)
            {
                if((changed & (1u << i)) != 0)
                {
                    Connections[i].Set(((driven >> i) & 1) != 0);
                }
            }
        }

        private uint pdor;
        private uint pddr;
        private uint inputs;

        private const int PinCount = 32;

        private static class Registers
        {
            public const long Pdor = 0x40;
            public const long Psor = 0x44;
            public const long Pcor = 0x48;
            public const long Ptor = 0x4C;
            public const long Pdir = 0x50;
            public const long Pddr = 0x54;
        }
    }
}
