//
// i.MX RT1180 LPTMR — Low-Power Timer. LPTMR1 @ 0x44300000, IRQ 18.
//
// Anchors from @rt1180emulator:
//   CSR 0x00  TEN bit0 (enable), TIE bit6 (IRQ enable), TCF bit7 (**W1C**)
//   CMR 0x08  compare
//   CNR 0x0C  counter
//
// ⚠️ THE CNR LATCH -- AND I IMPLEMENTED IT STRICTER THAN THE REFERENCE, WHICH BROKE
// THE TEST IT WAS MEANT TO PASS.
// On real hardware a CNR read is preceded by a CNR write to latch the value. I read
// @rt1180emulator's one-line anchor ("my test/model handle this") as "ENFORCE the
// latch", and made a read without a preceding write return the last latched value.
// Their model does the opposite and says so outright:
//     imxrt1180_lptmr.c: "CNR reads the live count"
//     mcxn_lptmr.c:      case R_CNR: return;   /* writing CNR latches on HW; ignore */
// and their timers2 test reads CNR in a loop with NO write at all -- so against my
// strict version CNR read 0 forever and the counter looked stuck.
//
// ⭐ BEING STRICTER THAN THE REFERENCE IS STILL BEING WRONG. I have spent this
// project catching PERMISSIVE shortcuts; this is the same error with the sign
// flipped, and it is not more faithful -- it is a different model of the hardware
// that the shared corpus does not describe. The brief says to read the QEMU model
// when exact behaviour is needed. I implemented from a one-line summary instead,
// and the summary was about their TEST, not their REGISTER.
//
// So: CNR returns the live count, and a CNR write is accepted and ignored.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class IMXRT1180_LPTMR : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_LPTMR(IMachine machine, ulong frequency = DefaultFrequency)
        {
            regs = new uint[Size / 4];
            Connections = new Dictionary<int, IGPIO> { { 0, new GPIO() } };
            timer = new LimitTimer(machine.ClockSource, frequency, this, "lptmr",
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
            if(offset == CounterRegister)
            {
                return (uint)timer.Value;       // live count, per the reference model
            }
            return regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size) { return; }
            switch(offset)
            {
                case ControlStatus:
                {
                    var sticky = regs[offset / 4] & CompareFlag & ~value;    // TCF is W1C
                    regs[offset / 4] = (value & ~CompareFlag) | sticky;
                    UpdateTimer();
                    UpdateInterrupt();
                    return;
                }
                case CompareRegister:
                    regs[offset / 4] = value;
                    UpdateTimer();
                    return;
                case CounterRegister:
                    // On silicon this write latches CNR; the reference model ignores
                    // it and serves the live count on read, so accept and drop it.
                    return;
                default:
                    regs[offset / 4] = value;
                    return;
            }
        }

        private void OnCompare()
        {
            regs[ControlStatus / 4] |= CompareFlag;
            UpdateInterrupt();
        }

        private void UpdateTimer()
        {
            var compare = regs[CompareRegister / 4];
            if((regs[ControlStatus / 4] & TimerEnable) == 0 || compare == 0)
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
            var csr = regs[ControlStatus / 4];
            Connections[0].Set((csr & CompareFlag) != 0 && (csr & InterruptEnable) != 0);
        }

        private readonly uint[] regs;
        private readonly LimitTimer timer;

        private const ulong DefaultFrequency = 24000000;
        private const ulong DefaultLimit = 0xFFFFFFFF;
        private const long ControlStatus   = 0x00;
        private const long CompareRegister = 0x08;
        private const long CounterRegister = 0x0C;
        private const uint TimerEnable    = 1u << 0;
        private const uint InterruptEnable = 1u << 6;
        private const uint CompareFlag    = 1u << 7;
    }
}
