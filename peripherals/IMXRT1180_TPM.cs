//
// i.MX RT1180 TPM — Timer/PWM Module. TPM1 @ 0x44310000, IRQ 36.
//
// Anchors from @rt1180emulator:
//   SC             0x10  TOF bit7 (W1C), TOIE bit6, CMOD bits[4:3] (1 => module clock)
//   CNT            0x14  UP counter
//   MOD            0x18  modulo; CNT wraps here -> SC.TOF + IRQ when TOIE
//   CONTROLS[0].CnV 0x24 channel value
//
// ⭐⭐ CnV MUST ROUND-TRIP. fslaudit writes 1234 to CnV and READS IT BACK, requiring
// 1234. A "CnV read-back mismatch" FAIL means the register accepted the write and
// returned something else.
//
// This is the POSITIVE form of the rule this project keeps meeting from the other
// side: usually the sin is a register that ACCEPTS a write and changes nothing
// (the ELE ack, the TDR that swallowed samples). Here the driver *requires*
// write-then-readback to match, so a register that quietly normalises or drops the
// value fails a test that never looks at behaviour at all -- only at storage.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class IMXRT1180_TPM : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_TPM(IMachine machine, ulong frequency = DefaultFrequency)
        {
            regs = new uint[Size / 4];
            Connections = new Dictionary<int, IGPIO> { { 0, new GPIO() } };
            timer = new LimitTimer(machine.ClockSource, frequency, this, "tpm",
                limit: DefaultLimit, direction: Antmicro.Renode.Time.Direction.Ascending,
                enabled: false, workMode: Antmicro.Renode.Time.WorkMode.Periodic, eventEnabled: true);
            timer.LimitReached += OnOverflow;
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
                case StatusControl:
                {
                    var sticky = regs[offset / 4] & TimerOverflowFlag & ~value;   // TOF is W1C
                    regs[offset / 4] = (value & ~TimerOverflowFlag) | sticky;
                    UpdateTimer();
                    UpdateInterrupt();
                    return;
                }
                case Modulo:
                    regs[offset / 4] = value;
                    UpdateTimer();
                    return;
                default:
                    // CONTROLS[n].CnV and everything else: plain storage, so a
                    // write-then-readback returns exactly what was written.
                    regs[offset / 4] = value;
                    return;
            }
        }

        private void OnOverflow()
        {
            regs[StatusControl / 4] |= TimerOverflowFlag;
            UpdateInterrupt();
        }

        private void UpdateTimer()
        {
            var clockMode = (regs[StatusControl / 4] >> ClockModeShift) & 0x3;
            var modulo = regs[Modulo / 4];
            if(clockMode == 0 || modulo == 0)
            {
                timer.Enabled = false;
                return;
            }
            timer.Limit = modulo;
            timer.ResetValue();
            timer.Enabled = true;
        }

        private void UpdateInterrupt()
        {
            var sc = regs[StatusControl / 4];
            Connections[0].Set((sc & TimerOverflowFlag) != 0 && (sc & TimerOverflowInterruptEnable) != 0);
        }

        private readonly uint[] regs;
        private readonly LimitTimer timer;

        private const ulong DefaultFrequency = 24000000;
        private const ulong DefaultLimit = 0xFFFF;
        private const long StatusControl = 0x10;
        private const long Counter       = 0x14;
        private const long Modulo        = 0x18;
        private const int  ClockModeShift = 3;
        private const uint TimerOverflowFlag           = 1u << 7;
        private const uint TimerOverflowInterruptEnable = 1u << 6;
    }
}
