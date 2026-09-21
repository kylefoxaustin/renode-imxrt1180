//
// i.MX RT1180 LPIT — Low-Power Periodic Interrupt Timer.
//
// Anchors from @rt1180emulator (what the fsl_lpit driver POLLS), base 0x442F0000:
//   MCR    0x08  M_CEN bit0 — module clock enable. NOTHING COUNTS until this is set.
//   MSR    0x0C  TIF per channel (bit0 = ch0), **W1C** — the periodic interrupt flag
//   MIER   0x10  per-channel interrupt enable
//   SETTEN 0x14  SET_T_EN bit0 = ch0 — this is what STARTS the channel
//   CLRTEN 0x18  clear enable
//   TVAL0  0x20  reload value; the counter counts DOWN from it
//   CVAL0  0x24  current value, read-only, DECREMENTS
//   IRQ: LPIT1 ch0 = 15
//
// ⭐⭐ THE COUNTER HAS TO ACTUALLY MOVE, NOT JUST THE FLAG.
// The oracle's test reads BOTH: MSR.TIF as the interrupt signal AND CVAL0, which it
// requires to have advanced. A model that only toggles TIF passes "does the IRQ
// fire" and is caught by the CVAL read-back. So CVAL is served from a real
// virtual-clock timer rather than being synthesised at the flag.
//
// This is the same single-operating-point trap the SAI (one sample rate), the ASRC
// (one clock source) and the NETC PTP (one addend) each had: a model that gets the
// *rate* completely wrong still satisfies a check that only looks at the event.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class IMXRT1180_LPIT : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_LPIT(IMachine machine, ulong frequency = DefaultFrequency)
        {
            this.machine = machine;
            this.frequency = frequency;
            regs = new uint[Size / 4];
            timers = new LimitTimer[ChannelCount];
            var connections = new System.Collections.Generic.Dictionary<int, IGPIO>();
            for(var i = 0; i < ChannelCount; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = connections;

            for(var i = 0; i < ChannelCount; i++)
            {
                var channel = i;
                timers[i] = new LimitTimer(machine.ClockSource, frequency, this, "lpit" + i,
                    limit: DefaultLimit, direction: Antmicro.Renode.Time.Direction.Descending,
                    enabled: false, workMode: Antmicro.Renode.Time.WorkMode.Periodic, eventEnabled: true);
                timers[i].LimitReached += () => OnLimitReached(channel);
            }
            Reset();
        }

        public long Size => 0x1000;

        public System.Collections.Generic.IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            for(var i = 0; i < ChannelCount; i++)
            {
                timers[i].Enabled = false;
                timers[i].Limit = DefaultLimit;
                Connections[i].Unset();
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size)
            {
                return 0;
            }
            for(var i = 0; i < ChannelCount; i++)
            {
                if(offset == CurrentValue0 + i * ChannelStride)
                {
                    // ⭐ SERVED FROM THE RUNNING TIMER. A stuck counter is exactly what
                    // the oracle's read-back assertion is there to catch.
                    return (uint)timers[i].Value;
                }
            }
            return regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                return;
            }
            switch(offset)
            {
                case ModuleControl:
                    regs[offset / 4] = value;
                    UpdateAll();                                   // M_CEN gates everything
                    return;

                case ModuleStatus:
                    // TIF is W1C.
                    regs[offset / 4] &= ~value;
                    UpdateInterrupts();
                    return;

                case ModuleInterruptEnable:
                    regs[offset / 4] = value;
                    UpdateInterrupts();
                    return;

                case SetTimerEnable:
                    regs[TimerEnableState / 4] |= value;
                    UpdateAll();
                    return;

                case ClearTimerEnable:
                    regs[TimerEnableState / 4] &= ~value;
                    UpdateAll();
                    return;

                default:
                    regs[offset / 4] = value;
                    for(var i = 0; i < ChannelCount; i++)
                    {
                        if(offset == TimerValue0 + i * ChannelStride)
                        {
                            UpdateChannel(i);
                        }
                    }
                    return;
            }
        }

        private void OnLimitReached(int channel)
        {
            regs[ModuleStatus / 4] |= 1u << channel;               // TIF, sticky until W1C
            UpdateInterrupts();
        }

        private void UpdateAll()
        {
            for(var i = 0; i < ChannelCount; i++)
            {
                UpdateChannel(i);
            }
        }

        private void UpdateChannel(int channel)
        {
            var moduleEnabled = (regs[ModuleControl / 4] & ModuleClockEnable) != 0;
            var channelEnabled = (regs[TimerEnableState / 4] & (1u << channel)) != 0;
            var reload = regs[(TimerValue0 + channel * ChannelStride) / 4];
            var timer = timers[channel];

            // TVAL is the RELOAD value and the counter counts DOWN from it, so the
            // period is TVAL+1 ticks. A zero reload would be a zero-period timer;
            // refuse to run rather than spin.
            if(!moduleEnabled || !channelEnabled || reload == 0)
            {
                timer.Enabled = false;
                return;
            }
            // ⭐ SETTING Limit DOES NOT RELOAD Value. MEASURED: CVAL read back
            // 0xFF48E4FF and then 0xFF2445FF -- the counter was genuinely running
            // (so "is it ticking" looked fine) but descending from the
            // construction-time default of 0xFFFFFFFF, not from TVAL. It would have
            // reached zero eventually; the guest hung waiting. A counter that moves
            // is not a counter that is CONFIGURED, and only the read-back of the
            // actual value distinguishes them.
            timer.Limit = (ulong)reload + 1;
            timer.ResetValue();
            timer.Enabled = true;
        }

        private void UpdateInterrupts()
        {
            var status = regs[ModuleStatus / 4];
            var enable = regs[ModuleInterruptEnable / 4];
            for(var i = 0; i < ChannelCount; i++)
            {
                Connections[i].Set(((status & enable) & (1u << i)) != 0);
            }
        }

        private readonly IMachine machine;
        private readonly ulong frequency;
        private readonly uint[] regs;
        private readonly LimitTimer[] timers;

        private const int ChannelCount = 4;
        private const ulong DefaultFrequency = 24000000;
        private const ulong DefaultLimit = 0xFFFFFFFF;
        private const long ChannelStride = 0x10;

        private const long ModuleControl         = 0x08;
        private const long ModuleStatus          = 0x0C;
        private const long ModuleInterruptEnable = 0x10;
        private const long SetTimerEnable        = 0x14;
        private const long ClearTimerEnable      = 0x18;
        private const long TimerValue0           = 0x20;
        private const long CurrentValue0         = 0x24;
        // Not an architectural register: SETTEN/CLRTEN are write-only set/clear pairs,
        // so the live enable state needs somewhere to live. Parked in an unused slot.
        private const long TimerEnableState      = 0xF00;

        private const uint ModuleClockEnable = 1u << 0;
    }
}
