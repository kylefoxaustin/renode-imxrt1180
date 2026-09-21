//
// i.MX RT1180 TMR (quad timer). Base TMR1 0x42690000, 0x20 per channel.
//
// Anchors from @rt1180emulator (what fsl_qtmr POLLS), channel n at n*0x20:
//   COMP1 +0x00 (u16) compare = modulo
//   LOAD  +0x06 (u16) load value
//   CNTR  +0x0A (u16) live counter, COUNTS UP
//   CTRL  +0x0C (u16) CM bits[13:12] (CM=1 -> count primary rising edge);
//                     PCS bits[11:8] (0 -> IP bus / 1)
//   SCTRL +0x0E (u16) TCF bit15 compare flag; TCFIE bit14 compare IRQ enable
//   ENBL  +0x1E (u16) bit0 enables ch0 -- this is what STARTS it
//   IRQ: TMR1 ch0 = 0
//
// ⭐⭐⭐ THE TRAP, AND IT IS THE OPPOSITE OF THE USUAL CONVENTION:
// SCTRL.TCF IS CLEARED BY WRITING **0** TO IT. It is NOT write-1-to-clear.
// The driver clears the flag by writing SCTRL = TCFIE (TCF bit zero, TCFIE kept).
// A model that implements the common W1C convention here either never clears the
// flag (the driver's clear writes 0, which W1C ignores) or clears it on the wrong
// write. Flagged by @rt1180emulator as the non-obvious field in this block, and
// modelled per the RM rather than per habit.
//
// ⭐ CNTR is served from a real virtual-clock timer: the oracle's test reads it back
// and requires it to advance, so a flag-only model is caught here exactly as it is
// in the LPIT.
//
// NOTE: the mc_pmsm FOC slow loop uses a DIFFERENT flag -- CSCTRL.TCF1/TCF1EN plus a
// PCS divider and an ENBL reset. The basic tmr test needs only SCTRL.TCF; CSCTRL is
// recorded here as a known gap rather than half-implemented.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class IMXRT1180_QTMR : IWordPeripheral, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_QTMR(IMachine machine, ulong frequency = DefaultFrequency)
        {
            this.machine = machine;
            regs = new ushort[Size / 2];
            timers = new LimitTimer[ChannelCount];
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < ChannelCount; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = connections;

            for(var i = 0; i < ChannelCount; i++)
            {
                var channel = i;
                timers[i] = new LimitTimer(machine.ClockSource, frequency, this, "qtmr" + i,
                    limit: DefaultLimit, direction: Antmicro.Renode.Time.Direction.Ascending,
                    enabled: false, workMode: Antmicro.Renode.Time.WorkMode.Periodic, eventEnabled: true);
                timers[i].LimitReached += () => OnCompare(channel);
            }
            Reset();
        }

        public long Size => 0x1000;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            for(var i = 0; i < ChannelCount; i++)
            {
                timers[i].Enabled = false;
                Connections[i].Unset();
            }
        }

        public ushort ReadWord(long offset)
        {
            if(offset + 2 > Size)
            {
                return 0;
            }
            for(var i = 0; i < ChannelCount; i++)
            {
                if(offset == i * ChannelStride + Counter)
                {
                    return (ushort)timers[i].Value;    // live counter, must advance
                }
            }
            return regs[offset / 2];
        }

        public void WriteWord(long offset, ushort value)
        {
            if(offset + 2 > Size)
            {
                return;
            }
            if(offset == Enable)
            {
                regs[offset / 2] = value;
                UpdateAll();
                return;
            }
            for(var i = 0; i < ChannelCount; i++)
            {
                var b = i * ChannelStride;
                if(offset == b + StatusControl)
                {
                    // ⭐ TCF IS CLEARED BY WRITING ZERO TO IT, NOT BY WRITING ONE.
                    var previous = regs[offset / 2];
                    var keptFlag = ((value & CompareFlag) != 0) ? (previous & CompareFlag) : 0;
                    regs[offset / 2] = (ushort)((value & ~CompareFlag) | keptFlag);
                    UpdateInterrupt(i);
                    return;
                }
                if(offset == b + Compare1 || offset == b + Control || offset == b + Load)
                {
                    regs[offset / 2] = value;
                    UpdateChannel(i);
                    return;
                }
            }
            regs[offset / 2] = value;
        }

        public uint ReadDoubleWord(long offset)
        {
            return (uint)(ReadWord(offset) | (ReadWord(offset + 2) << 16));
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            WriteWord(offset, (ushort)value);
            WriteWord(offset + 2, (ushort)(value >> 16));
        }

        private void OnCompare(int channel)
        {
            var b = channel * ChannelStride;
            regs[(b + StatusControl) / 2] |= CompareFlag;      // sticky until written 0
            UpdateInterrupt(channel);
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
            var b = channel * ChannelStride;
            var enabled = (regs[Enable / 2] & (1u << channel)) != 0;
            var countMode = (regs[(b + Control) / 2] >> CountModeShift) & 0x7;
            var compare = regs[(b + Compare1) / 2];
            var timer = timers[channel];

            // CM == 0 means the counter is stopped regardless of ENBL, and a zero
            // compare would be a zero-period timer.
            if(!enabled || countMode == 0 || compare == 0)
            {
                timer.Enabled = false;
                return;
            }
            timer.Limit = compare;
            timer.ResetValue();
            timer.Enabled = true;
        }

        private void UpdateInterrupt(int channel)
        {
            var sctrl = regs[(channel * ChannelStride + StatusControl) / 2];
            var pending = (sctrl & CompareFlag) != 0 && (sctrl & CompareFlagInterruptEnable) != 0;
            Connections[channel].Set(pending);
        }

        private readonly IMachine machine;
        private readonly ushort[] regs;
        private readonly LimitTimer[] timers;

        private const int ChannelCount = 4;
        private const long ChannelStride = 0x20;
        private const ulong DefaultFrequency = 24000000;
        private const ulong DefaultLimit = 0xFFFF;

        private const long Compare1      = 0x00;
        private const long Load          = 0x06;
        private const long Counter       = 0x0A;
        private const long Control       = 0x0C;
        private const long StatusControl = 0x0E;
        private const long Enable        = 0x1E;

        private const int  CountModeShift = 12;
        private const ushort CompareFlag                = 1 << 15;
        private const ushort CompareFlagInterruptEnable = 1 << 14;
    }
}
