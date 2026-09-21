//
// NXP i.MX RT1180 LPADC — 12/16-bit SAR ADC (command list + result FIFO).
//
// A trigger (software SWTRIG, or a hardware trigger input such as an eFlexPWM
// edge routed through XBAR1) launches a command chain; each command samples a
// channel and pushes a tagged result into a result FIFO the CPU reads. This is
// the motor-control front end that samples phase currents synchronously with PWM.
//
// ⭐ PORTED FROM THE ORACLE, NOT DERIVED FROM THE RM.
// Source: ~/Documents/GitHub/rt1180emulator/hw/misc/imxrt1180_adc.c (510 lines).
// Every offset, reset value, field mask and behavioural rule below comes from
// that model, which is mutation-proven on their side by
// `tests/imxrt1180-adc-fifo-align`. The project's standing rule is that the QEMU
// model is the reference oracle precisely because it encodes WHAT THE DRIVER
// POLLS; that matters more here than in any other block in this tree, because
// two of its reset values are load-bearing in ways the RM's reset column alone
// would not warn you about (see VERID and GCR below).
//
// WHY THIS BLOCK EXISTS HERE: MEASURED on the cm7 platform running the stock SDK
// `demo_apps/mc_pmsm/pmsm_enc` image — after mapping FlexSPI1 (the previous wall)
// the image polls ADC1 STAT at 0x42600014 **9320 times** in a 2 s window and gets
// no further. ADC1 is the measured next wall on the last open corpus row.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Analog
{
    public class IMXRT1180_LPADC : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput, IGPIOReceiver
    {
        public IMXRT1180_LPADC(IMachine machine)
        {
            this.machine = machine;
            regs = new uint[Size / 4];
            fifo = new Fifo[NumFifos];
            for(var i = 0; i < NumFifos; i++)
            {
                fifo[i] = new Fifo();
            }
            channelInput = new ushort[2, 32];

            // GPIO 0 is the NVIC line; 1..NumFifos are the eDMA result-FIFO
            // request lines, in FIFO order. Named indices rather than separate
            // connectors because Renode wires DMA requests as plain GPIOs.
            var connections = new System.Collections.Generic.Dictionary<int, IGPIO>();
            for(var i = 0; i < 1 + NumFifos; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = connections;

            Reset();
        }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);

            // ⭐ POWER-ON RESET VALUES, from the oracle's reset column (verified
            // there by tests/imxrt1180-reset-values). A memset-to-zero is NOT a
            // neutral default — it is a claim about every bit, and the SDK driver
            // read-modify-writes both of these.
            //   CFG[PUDLY]       = 0x80  (bits 23:16)
            //   CTRL[CALOFSMODE] = 1     (bit 5)
            regs[RegCfg / 4] = 0x00800000;
            regs[RegCtrl / 4] = 0x00000020;

            // ⭐⭐ GCR[GCALR] = 0x10000 IS UNITY GAIN, AND ZERO IS NOT "NO OPINION".
            // GCALR is a 17-bit Q16 gain: bit 16 is the integer 1, bits 15:0 the
            // fraction. 0x10000 == 1.0. Resetting it to zero is a gain of NOTHING,
            // and firmware that reads the calibration back and applies it scales
            // every conversion to zero — on this row, a phase current of zero,
            // forever, from an ADC that reports itself perfectly healthy.
            // RDY (bit 24) stays CLEAR: calibration has not been requested yet.
            // The write path sets RDY only when CTRL.CAL_REQ actually asks.
            regs[RegGcr0 / 4] = 0x00010000;
            regs[(RegGcr0 + 4) / 4] = 0x00010000;

            for(var f = 0; f < NumFifos; f++)
            {
                fifo[f].Clear();
            }
            // Neutral mid-scale until a plant drives a channel. Flagged on first
            // use rather than silently returned — an un-driven ADC that reports a
            // plausible number is the exact failure this project keeps finding.
            for(var side = 0; side < 2; side++)
            {
                for(var c = 0; c < 32; c++)
                {
                    channelInput[side, c] = ResultPlaceholder;
                }
            }
            noAfeLogged = false;
            UpdateIrq();
            UpdateDma();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size)
            {
                this.Log(LogLevel.Warning, "OOB read at 0x{0:X}", offset);
                return 0;
            }

            if(offset == RegVerid)
            {
                // ⭐ THE FEATURE BITS ARE LOAD-BEARING, NOT DECORATION.
                // 0x0200_2C1B (v2.0). NUM_SEC (bit 11) advertises two simultaneous
                // single-ended conversions and DIFFEN (bit 1) differential support.
                // LPADC_SetConvCommandConfig ASSERTS on both before it will accept
                // the FOC demo's kLPADC_SampleChannelDualSingleEndBothSide command.
                // A zero-feature VERID trips that assert — so a "version register,
                // firmware won't care" stub fails this row.
                return 0x02002C1B;
            }
            if(offset == RegParam)
            {
                return 0x0F041008;
            }
            if(offset >= RegResFifo0 && offset < RegResFifo0 + 4 * NumFifos)
            {
                var f = (int)((offset - RegResFifo0) / 4);
                var v = fifo[f].Pop();
                UpdateIrq();
                // ⭐⭐ THE DMA REQUEST IS A LEVEL, AND A LEVEL MUST KEEP REQUESTING.
                // On silicon the request stays asserted while the FIFO is above its
                // watermark, and the eDMA keeps running minor loops until the major
                // loop ends. Renode's eDMA acts on an EDGE (OnGPIO -> one
                // HardwareServiceRequest), so a held line produces exactly ONE
                // transfer and then stalls.
                // MEASURED by the oracle's lpadc-dma before this fix: the channel
                // fetched its TCD, executed ONE 4-byte transfer of CITER=8, and the
                // test reported "DMA channel never completed its major loop".
                // With FWMARK=0 the line never falls while results remain, so there
                // is no second edge to find. Re-asserting here -- and ONLY here, on
                // the drain path -- regenerates the edge that a level-sensitive
                // controller would have kept servicing, without re-requesting on
                // unrelated config writes.
                UpdateDma(regenerateHeldRequest: true);
                return v;
            }
            if(offset >= RegFctrl0 && offset < RegFctrl0 + 4 * NumFifos)
            {
                var f = (int)((offset - RegFctrl0) / 4);
                // FCOUNT (low 5 bits) reflects the LIVE fill level, not a stored value.
                return (regs[offset / 4] & ~0x1Fu) | (uint)fifo[f].Count;
            }
            return regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                this.Log(LogLevel.Warning, "OOB write at 0x{0:X}", offset);
                return;
            }

            switch(offset)
            {
            case RegCtrl:
                if((value & CtrlRst) != 0)
                {
                    for(var f = 0; f < NumFifos; f++)
                    {
                        fifo[f].Clear();
                    }
                    regs[RegStat / 4] = 0;
                }
                if((value & CtrlRstFifo0) != 0)
                {
                    fifo[0].Clear();
                }
                if((value & CtrlRstFifo1) != 0)
                {
                    fifo[1].Clear();
                }
                if((value & CtrlCalReq) != 0)
                {
                    // Auto-calibration completes now. GCC[n] is the coefficient
                    // LPADC_FinishAutoCalibration polls for RDY then reads:
                    // gain = 131072/(131072 - GAIN_CAL). GAIN_CAL = 0 is exactly
                    // unity, which is the truthful answer for an ADC with no gain
                    // error — rather than inventing a trim.
                    regs[RegStat / 4] |= StatCalRdy;
                    regs[RegGcr0 / 4] = GcrRdy | 0x10000;
                    regs[(RegGcr0 + 4) / 4] = GcrRdy | 0x10000;
                    regs[RegGcc0 / 4] = GccRdy;
                    regs[(RegGcc0 + 4) / 4] = GccRdy;
                }
                if((value & CtrlCalOfs) != 0)
                {
                    // LPADC_DoOffsetCalibration sets CALOFS then spins on CAL_RDY.
                    regs[RegStat / 4] |= StatCalRdy;
                }
                // Store CTRL with the self-clearing bits masked off.
                regs[RegCtrl / 4] = value & ~(CtrlRst | CtrlRstFifo0 | CtrlRstFifo1 | CtrlCalReq);
                UpdateIrq();
                UpdateDma();
                return;

            case RegStat:
                regs[RegStat / 4] &= ~(value & StatWFlags);      // W1C
                UpdateIrq();
                return;

            case RegDe:
                regs[RegDe / 4] = value;
                UpdateDma();
                return;

            case RegSwTrig:
                for(var t = 0; t < NumTriggers; t++)
                {
                    if((value & (1u << t)) != 0)
                    {
                        RunTrigger(t);
                    }
                }
                return;                                          // SWTRIG self-clears

            case RegIe:
                regs[RegIe / 4] = value;
                UpdateIrq();
                return;

            default:
                regs[offset / 4] = value;
                if(offset >= RegFctrl0 && offset < RegFctrl0 + 4 * NumFifos)
                {
                    // A watermark change moves both STAT.RDYn and the DMA request level.
                    UpdateIrq();
                    UpdateDma();
                }
                return;
            }
        }

        // ⭐ IGPIOReceiver so the XBAR's outputs can actually reach the trigger
        // inputs. Edge-sensitive: a conversion launches on the RISING edge only,
        // which is why the eFlexPWM emits a pulse (set then clear) per period
        // rather than a level.
        public void OnGPIO(int number, bool value)
        {
            if(value)
            {
                OnHardwareTrigger(number);
            }
        }

        // Hardware trigger input, wired from XBAR1 / eFlexPWM edges.
        public void OnHardwareTrigger(int line)
        {
            if(line >= 0 && line < NumTriggers && (regs[(RegTctrl0 + 4 * line) / 4] & TctrlHten) != 0)
            {
                RunTrigger(line);
            }
        }

        // A virtual-motor plant calls this to inject phase-current samples.
        public void SetChannelInput(int channel, int side, ushort code)
        {
            if(channel >= 0 && channel < 32 && side >= 0 && side < 2)
            {
                channelInput[side, channel] = code;
            }
        }

        public long Size => 0x1000;
        public System.Collections.Generic.IReadOnlyDictionary<int, IGPIO> Connections { get; }

        private void RunTrigger(int t)
        {
            if((regs[RegCtrl / 4] & CtrlAdcEn) == 0)
            {
                return;
            }
            var tctrl = regs[(RegTctrl0 + 4 * t) / 4];
            var cmd = (tctrl & TctrlTcmdMask) >> TctrlTcmdShift;     // 1-based
            var guard = 0;

            while(cmd != 0 && cmd <= 15 && guard++ < 32)
            {
                var cmdh = regs[(RegCmd0 + 8 * (cmd - 1) + 4) / 4];
                var loops = ((cmdh & CmdhLoopMask) >> CmdhLoopShift) + 1;

                var cmdl = regs[(RegCmd0 + 8 * (cmd - 1)) / 4];
                var ctype = (cmdl & CmdlCtypeMask) >> CmdlCtypeShift;
                var cha = (int)(cmdl & CmdlAdchMask);                // A-side channel
                var chb = ((cmdl & CmdlAltbEn) != 0)                 // B-side channel
                    ? (int)((cmdl & CmdlAltbAdchMask) >> CmdlAltbAdchShift)
                    : cha;

                for(var l = 0u; l < loops; l++)
                {
                    // Result FIFO routing by CTYPE: an A-side conversion lands in
                    // FIFO0, a B-side conversion in FIFO1. A DualSingleEndBothSide
                    // command converts both sides at once and pushes ONE result to
                    // EACH FIFO — which is how mc_pmsm reads Ia (A-side -> RESFIFO0)
                    // and Ib (B-side -> RESFIFO1) from a single command.
                    switch(ctype)
                    {
                    case CtypeSingleB:
                        PushResult(1, t, l, Sample(SideB, chb));
                        break;
                    case CtypeDiff:
                        PushResult(0, t, l, (ushort)((int)Sample(SideA, cha) - (int)Sample(SideB, chb)));
                        break;
                    case CtypeDualBoth:
                        PushResult(0, t, l, Sample(SideA, cha));
                        PushResult(1, t, l, Sample(SideB, chb));
                        break;
                    case CtypeSingleA:
                    default:
                        PushResult(0, t, l, Sample(SideA, cha));
                        break;
                    }
                }
                cmd = (cmdh & CmdhNextMask) >> CmdhNextShift;        // 0 = end of chain
            }
            regs[RegStat / 4] |= StatTcompInt;
            UpdateIrq();
            UpdateDma();
        }

        private ushort Sample(int side, int channel)
        {
            var code = channelInput[side, channel & 0x1F];
            if(code == ResultPlaceholder && !noAfeLogged)
            {
                noAfeLogged = true;
                // ⭐ SAY SO ONCE, LOUDLY. An ADC with no plant behind it returns a
                // perfectly plausible mid-scale code, and a FOC loop fed on it runs
                // and means nothing. This log is the difference between a stub that
                // admits what it is and a green row built on one.
                this.Log(LogLevel.Warning,
                    "No plant drives channel {0} ({1}-side) — result is a FIXED MID-SCALE PLACEHOLDER, " +
                    "not a conversion. Any control loop reading it is running open-loop on a constant.",
                    channel, side == SideA ? "A" : "B");
            }
            return code;
        }

        private void PushResult(int f, int t, uint loop, ushort code)
        {
            var entry = ResFifoValid
                | ((uint)t << ResFifoTsrcShift)
                | (loop << ResFifoLoopCntShift)
                | code;
            if(!fifo[f].Push(entry))
            {
                regs[RegStat / 4] |= (f == 0) ? StatFof0 : StatFof1;    // overflow
            }
        }

        private uint FifoWatermark(int f)
        {
            return (regs[(RegFctrl0 + 4 * f) / 4] & FctrlFwmarkMask) >> FctrlFwmarkShift;
        }

        private void UpdateIrq()
        {
            uint stat = 0;
            if(fifo[0].Count > FifoWatermark(0))
            {
                stat |= StatRdy0;
            }
            if(fifo[1].Count > FifoWatermark(1))
            {
                stat |= StatRdy1;
            }
            // Keep the sticky/W1C flags, refresh the level-driven RDY bits.
            regs[RegStat / 4] = (regs[RegStat / 4] & ~(StatRdy0 | StatRdy1)) | stat;

            var ie = regs[RegIe / 4];
            var active = (((stat & StatRdy0) != 0) && ((ie & IeFwmie0) != 0))
                      || (((stat & StatRdy1) != 0) && ((ie & IeFwmie1) != 0));
            Connections[0].Set(active);
        }

        private void UpdateDma(bool regenerateHeldRequest = false)
        {
            // The request is CONVERSION-driven, not FIFO-space-driven: it rises when
            // a result FIFO fills ABOVE its watermark (the same level event as
            // STAT.RDYn), gated by DE.FWMDEn and CTRL.ADCEN, and falls when RESFIFO
            // reads drain it back to/below the watermark.
            var en = (regs[RegCtrl / 4] & CtrlAdcEn) != 0;
            var de = regs[RegDe / 4];
            for(var f = 0; f < NumFifos; f++)
            {
                var req = en && ((de & (DeFwmde0 << f)) != 0) && (fifo[f].Count > FifoWatermark(f));
                if(req && regenerateHeldRequest)
                {
                    // ⚠ DEFERRED, NOT SYNCHRONOUS -- AND THAT DISTINCTION COST A
                    // WRONG RESULT. Re-asserting inside the RESFIFO read re-enters
                    // the eDMA's HardwareServiceRequest() from within the bus
                    // transaction it is currently servicing, BEFORE the controller
                    // has advanced DADDR for that minor loop. MEASURED: the oracle's
                    // lpadc-dma then ran exactly the right NUMBER of transfers (8 of
                    // 8) and wrote every one of them to the SAME address 0x20000000,
                    // so the count looked perfect and the data was wrong -- the
                    // failure mode that a "did the DMA run?" check waves through and
                    // a byte-compare against a PIO golden catches.
                    // Handing the re-assert to the time source runs it after the
                    // current access completes, which is what a real level-sensitive
                    // request does: it is sampled again on the NEXT cycle.
                    var line = Connections[1 + f];
                    line.Set(false);
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => line.Set(true));
                    continue;
                }
                Connections[1 + f].Set(req);
            }
        }

        private class Fifo
        {
            public bool Push(uint entry)
            {
                if(count >= Depth)
                {
                    return false;
                }
                data[(head + count) % Depth] = entry;
                count++;
                return true;
            }

            public uint Pop()
            {
                if(count == 0)
                {
                    return 0;               // VALID clear -> empty
                }
                var v = data[head];
                head = (head + 1) % Depth;
                count--;
                return v;
            }

            public void Clear()
            {
                head = 0;
                count = 0;
            }

            public uint Count => (uint)count;

            private const int Depth = 16;
            private readonly uint[] data = new uint[Depth];
            private int head;
            private int count;
        }

        private readonly IMachine machine;
        private readonly uint[] regs;
        private readonly Fifo[] fifo;
        private readonly ushort[,] channelInput;
        private bool noAfeLogged;

        private const int NumFifos = 2;
        private const int NumTriggers = 8;
        private const ushort ResultPlaceholder = 0x8000;
        private const int SideA = 0;
        private const int SideB = 1;

        // Register offsets [oracle: hw/misc/imxrt1180_adc.c]
        private const long RegVerid = 0x00;
        private const long RegParam = 0x04;
        private const long RegCtrl = 0x10;
        private const long RegStat = 0x14;
        private const long RegIe = 0x18;
        private const long RegDe = 0x1C;
        private const long RegCfg = 0x20;
        private const long RegSwTrig = 0x34;
        private const long RegTstat = 0x38;
        private const long RegTctrl0 = 0xA0;    // [8], step 4
        private const long RegFctrl0 = 0xE0;    // [2], step 4
        private const long RegGcc0 = 0xF0;      // [2], step 4
        private const long RegGcr0 = 0xF8;      // [2], step 4
        private const long RegCmd0 = 0x100;     // CMDL/CMDH [15], step 8
        private const long RegResFifo0 = 0x300; // [2], step 4

        private const uint CtrlAdcEn = 0x00000001;
        private const uint CtrlRst = 0x00000002;
        private const uint CtrlCalReq = 0x00000008;
        private const uint CtrlCalOfs = 0x00000010;
        private const uint CtrlRstFifo0 = 0x00000100;
        private const uint CtrlRstFifo1 = 0x00000200;

        private const uint StatRdy0 = 0x00000001;
        private const uint StatFof0 = 0x00000002;
        private const uint StatRdy1 = 0x00000004;
        private const uint StatFof1 = 0x00000008;
        private const uint StatTcompInt = 0x00000200;
        private const uint StatCalRdy = 0x00000400;
        private const uint StatWFlags = StatFof0 | StatFof1 | StatTcompInt;

        private const uint IeFwmie0 = 0x00000001;
        private const uint IeFwmie1 = 0x00000004;
        private const uint DeFwmde0 = 0x00000001;

        private const uint TctrlHten = 0x00000001;
        private const int TctrlTcmdShift = 24;
        private const uint TctrlTcmdMask = 0x0F000000;
        private const int FctrlFwmarkShift = 16;
        private const uint FctrlFwmarkMask = 0x000F0000;

        private const uint CmdlAdchMask = 0x0000001F;
        private const int CmdlCtypeShift = 5;
        private const uint CmdlCtypeMask = 0x00000060;
        private const int CmdlAltbAdchShift = 16;
        private const uint CmdlAltbAdchMask = 0x001F0000;
        private const uint CmdlAltbEn = 0x00200000;
        private const uint CtypeSingleA = 0;
        private const uint CtypeSingleB = 1;
        private const uint CtypeDiff = 2;
        private const uint CtypeDualBoth = 3;
        private const int CmdhLoopShift = 16;
        private const uint CmdhLoopMask = 0x000F0000;
        private const int CmdhNextShift = 24;
        private const uint CmdhNextMask = 0x0F000000;

        private const uint ResFifoValid = 0x80000000;
        private const int ResFifoTsrcShift = 16;
        private const int ResFifoLoopCntShift = 20;
        private const uint GcrRdy = 0x01000000;
        private const uint GccRdy = 0x01000000;
    }
}
