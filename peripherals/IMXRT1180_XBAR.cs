//
// NXP i.MX RT1180 XBAR1 — inter-peripheral signal crossbar.
//
// The SEL[] registers are 16-bit, each holding TWO 8-bit output-source selects:
// output N is driven by the input whose index sits in SEL[N/2], byte (N & 1).
// When an input changes level, every output selecting it follows.
//
// On this part that is the wire that carries an eFlexPWM trigger edge to the
// LPADC hardware-trigger inputs, so the ADC samples synchronously with the PWM
// period — the FOC current-sense path
// (`XBAR_SetSignalsConnection(FlexpwmPwmOutTrig, Adc12HwTrig)`).
//
// ⭐ PORTED FROM THE ORACLE: ~/Documents/GitHub/rt1180emulator/hw/misc/imxrt1180_xbar.c,
// which is register-accurate against the MIMXRT1189 DFP PERI_XBAR_NUM_OUT221.h.
// MEASURED here: the stock SDK pmsm_enc_cm7 image touches XBAR1 at 0x42750032,
// 0x42750056/58, 0x4275008C/90 — 16-bit accesses, matching the SEL[] layout.
//
// FIDELITY NOTE, carried over from the oracle and NOT quietly dropped: the CTRL[]
// registers (per-output edge detection, DMA/interrupt generation) are
// register-accurate STORAGE but are not behaviourally modelled. An input level is
// propagated straight through to the selected outputs, so a source pulse yields
// one output pulse — enough for an edge-triggered consumer like the ADC.
// **Edge/DMA modes are flagged, not faked**, and this model says so on first use
// rather than letting a configured-but-inert mode look like a working one.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_XBAR : IWordPeripheral, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput, IGPIOReceiver
    {
        public IMXRT1180_XBAR()
        {
            regs = new ushort[Size / 2];
            inLevel = new bool[NumInputs];

            var connections = new System.Collections.Generic.Dictionary<int, IGPIO>();
            for(var i = 0; i < NumOutputs; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = connections;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            Array.Clear(inLevel, 0, inLevel.Length);
            for(var o = 0; o < NumOutputs; o++)
            {
                Connections[o].Set(false);
            }
            ctrlModeLogged = false;
        }

        public ushort ReadWord(long offset)
        {
            if(offset + 2 > Size)
            {
                this.Log(LogLevel.Warning, "OOB read at 0x{0:X}", offset);
                return 0;
            }
            return regs[offset / 2];
        }

        public void WriteWord(long offset, ushort value)
        {
            if(offset + 2 > Size)
            {
                this.Log(LogLevel.Warning, "OOB write at 0x{0:X}", offset);
                return;
            }
            regs[offset / 2] = value;

            if(offset <= SelLast)
            {
                // A SEL write re-routes its two outputs: refresh them to the new source.
                var b = (int)(offset / 2) * 2;
                for(var o = b; o < b + 2 && o < NumOutputs; o++)
                {
                    Connections[o].Set(inLevel[Source(o)]);
                }
            }
            else if(value != 0 && !ctrlModeLogged)
            {
                ctrlModeLogged = true;
                this.Log(LogLevel.Warning,
                    "CTRL[] written (offset 0x{0:X} = 0x{1:X}): per-output edge detection and " +
                    "DMA/interrupt generation are STORED BUT NOT MODELLED here. Levels are passed " +
                    "straight through, which is enough for an edge-triggered consumer like the ADC. " +
                    "Anything relying on XBAR-generated edge interrupts or DMA requests will NOT see them.",
                    offset, value);
            }
        }

        // The SDK writes SEL[] as 16-bit, but a 32-bit access covers two SEL registers;
        // accept both rather than faulting, and keep the routing refresh identical.
        public uint ReadDoubleWord(long offset)
        {
            return (uint)(ReadWord(offset) | (ReadWord(offset + 2) << 16));
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            WriteWord(offset, (ushort)(value & 0xFFFF));
            WriteWord(offset + 2, (ushort)(value >> 16));
        }

        // ⭐ IGPIOReceiver so the PLATFORM can wire real signals in. Without this
        // the crossbar has inputs in principle and none in practice: MEASURED, the
        // oracle's pwmadc.elf reported
        //   "PWM->XBAR->ADC: FAIL - sync chain did not trigger a conversion"
        // because PWM1 SM0's output trigger had nowhere to go. The models each had
        // the machinery; nothing connected them, and only running a test that
        // exercises the CHAIN rather than the blocks could show that.
        public void OnGPIO(int number, bool value)
        {
            OnInput(number, value);
        }

        // An input signal changed level: every output selecting it follows.
        public void OnInput(int line, bool level)
        {
            if(line < 0 || line >= NumInputs)
            {
                return;
            }
            inLevel[line] = level;
            for(var o = 0; o < NumOutputs; o++)
            {
                if(Source(o) == line)
                {
                    Connections[o].Set(level);
                }
            }
        }

        public long Size => 0x200;
        public System.Collections.Generic.IReadOnlyDictionary<int, IGPIO> Connections { get; }

        private int Source(int output)
        {
            var sel = regs[output / 2];         // SEL registers start at offset 0
            return ((output & 1) != 0) ? (sel >> 8) & 0xFF : sel & 0xFF;
        }

        private readonly ushort[] regs;
        private readonly bool[] inLevel;
        private bool ctrlModeLogged;

        private const int NumOutputs = 221;     // output signals
        private const int NumInputs = 256;      // input signals (8-bit select)
        private const long SelLast = 0xDC;      // SEL[110] is the last select register
    }
}
