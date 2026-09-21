//
// i.MX RT1180 GPT — General Purpose Timer. GPT1 @ 0x446C0000, IRQ 209.
//
// Anchors from @rt1180emulator:
//   CR   0x00  SWR bit15 = software reset, **SELF-CLEARING/MOMENTARY**; EN bit0
//   SR   0x08  OF1 bit0, W1C
//   IR   0x0C  OF1IE bit0
//   OCR1 0x10  output compare
//   CNT  0x24  free-running UP counter
//
// ⭐ fslaudit needs ONLY the SWR self-clear: it spins on `while (CR & 0x8000)`.
// A CR.SWR that latches is an infinite loop in the guest -- the same shape as every
// other ready-bit hang this project has paid for. The compare path below is for the
// fuller timers2 case and is separable from that one bit.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class IMXRT1180_GPT : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_GPT(IMachine machine, ulong frequency = DefaultFrequency)
        {
            regs = new uint[Size / 4];
            Connections = new Dictionary<int, IGPIO> { { 0, new GPIO() } };
            timer = new LimitTimer(machine.ClockSource, frequency, this, "gpt",
                limit: DefaultLimit, direction: Antmicro.Renode.Time.Direction.Ascending,
                enabled: false, workMode: Antmicro.Renode.Time.WorkMode.Periodic, eventEnabled: true);
            timer.LimitReached += OnCompare;
            Reset();
        }

        public long Size => 0x1000;
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            timer.Enabled = false;
            Connections[0].Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size) { return 0; }
            if(offset == Counter) { return (uint)timer.Value; }
            return regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size) { return; }
            switch(offset)
            {
                case Control:
                    // ⭐ SWR IS MOMENTARY. Never observable as set on read-back, or the
                    // driver's `while (CR & 0x8000)` never exits.
                    if((value & SoftwareReset) != 0)
                    {
                        var frequencyPreserved = timer.Frequency;
                        Array.Clear(regs, 0, regs.Length);
                        timer.Enabled = false;
                        timer.Frequency = frequencyPreserved;
                        return;
                    }
                    regs[offset / 4] = value & ~SoftwareReset;
                    UpdateTimer();
                    return;

                case Status:
                    regs[offset / 4] &= ~value;              // OF1 is W1C
                    UpdateInterrupt();
                    return;

                case InterruptEnable:
                    regs[offset / 4] = value;
                    UpdateInterrupt();
                    return;

                case OutputCompare1:
                    regs[offset / 4] = value;
                    UpdateTimer();
                    return;

                default:
                    regs[offset / 4] = value;
                    return;
            }
        }

        private void OnCompare()
        {
            regs[Status / 4] |= OutputFlag1;
            UpdateInterrupt();
        }

        private void UpdateTimer()
        {
            var compare = regs[OutputCompare1 / 4];
            if((regs[Control / 4] & Enable) == 0 || compare == 0)
            {
                timer.Enabled = false;
                return;
            }
            timer.Limit = compare;
            timer.ResetValue();
            timer.Enabled = true;
        }

        private void UpdateInterrupt()
        {
            Connections[0].Set((regs[Status / 4] & regs[InterruptEnable / 4] & OutputFlag1) != 0);
        }

        private readonly uint[] regs;
        private readonly LimitTimer timer;

        private const ulong DefaultFrequency = 24000000;
        private const ulong DefaultLimit = 0xFFFFFFFF;
        private const long Control         = 0x00;
        private const long Status          = 0x08;
        private const long InterruptEnable = 0x0C;
        private const long OutputCompare1  = 0x10;
        private const long Counter         = 0x24;
        private const uint SoftwareReset = 1u << 15;
        private const uint Enable        = 1u << 0;
        private const uint OutputFlag1   = 1u << 0;
    }
}
