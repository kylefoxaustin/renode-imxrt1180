//
// NXP i.MX RT1180 ASRC -- asynchronous sample-rate converter (memory-to-memory path).
//
// PORTED from the QEMU oracle's hw/audio/imxrt1180_asrc.c. Register offsets, field
// masks, the ratio derivation and the resampler kernel all come from there.
//
// ⭐ THE FILTER IS ALGORITHM-CLASS-FAITHFUL, NOT BIT-EXACT, AND THAT IS DECLARED.
// NXP does not publish the ASRC's filter taps. This is a windowed-sinc (Hann)
// polyphase FIR -- the same class of filter the hardware uses and the same one the
// QEMU oracle implements -- normalized so the DC gain is EXACTLY 1 (a constant in
// is a constant out). It is tested against DSP first principles, NEVER against
// silicon values. Converted samples from this model and from real hardware WILL
// DIFFER in the low bits. Anyone comparing converted buffers byte-for-byte across
// tools or against silicon is comparing the wrong thing; the claim here is that a
// 48 kHz -> 32 kHz conversion produces the right NUMBER of frames, in band, with
// unity DC gain -- not that it reproduces NXP's taps.
//
// Cutoff `fc` is the output Nyquist when down-converting (anti-aliasing) and the
// input Nyquist when up-converting (anti-imaging) -- the whole reason the hardware
// uses a FIR rather than dropping samples.
//
// The output lags the input by ~HALF frames (the filter's group delay), so the
// first HALF outputs are an edge transient.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Sound
{
    public class IMXRT1180_ASRC : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_ASRC(IMachine machine)
        {
            Connections = new Dictionary<int, IGPIO> { { 0, new GPIO() } };
            regs = new uint[Size / 4];
            pairs = new Pair[PairCount];
            for(var i = 0; i < PairCount; i++)
            {
                pairs[i] = new Pair();
            }
            Reset();
        }

        public long Size => 0x100;

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            foreach(var pair in pairs)
            {
                pair.Reset();
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case (long)Registers.Asrstr:
                    return ComposeStatus();

                case (long)Registers.Asrcfg:
                    // Init completes as soon as the module is enabled: report
                    // INIRQ{A,B,C} (bits 21-23) so ASRC_SetChannelPairConfig's
                    // init-done poll retires. MEASURED before this existed: 3750
                    // reads of ASRCFG in a 5 s run, console stuck after the banner.
                    return regs[(long)Registers.Asrcfg / 4]
                        | (((regs[(long)Registers.Asrctr / 4] & AsrctrAsrcen) != 0) ? (0x7u << 21) : 0u);

                case (long)Registers.Asrdoa:
                case 0x6C:
                case 0x74:
                {
                    var pair = pairs[offset == (long)Registers.Asrdoa ? 0 : (offset == 0x6C ? 1 : 2)];
                    var popped = (uint)pair.PopOutput() & 0xFFFFFF;
                    UpdateInterrupts();
                    return popped;
                }

                case (long)Registers.Asrfsta:
                {
                    var inFill = (uint)pairs[0].InputFill;
                    var outFill = (uint)pairs[0].OutputFill;
                    return (inFill & 0x7F) | ((outFill & 0x7F) << 12);
                }

                default:
                    return offset + 4 <= Size ? regs[offset / 4] : 0u;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                return;
            }
            switch(offset)
            {
                case (long)Registers.Asrdia:
                case 0x68:
                case 0x70:
                {
                    var index = offset == (long)Registers.Asrdia ? 0 : (offset == 0x68 ? 1 : 2);
                    pairs[index].PushInput((short)(value & 0xFFFF));   // 16-bit signed
                    Resample(index);
                    UpdateInterrupts();
                    return;
                }
                case (long)Registers.Asrier:
                    regs[offset / 4] = value;
                    UpdateInterrupts();
                    return;

                case (long)Registers.Asrctr:
                    // ⭐⭐ ATSA/ATSB/ATSC ARE TASK-START BITS: WRITING ONE RE-INITIALISES
                    // THAT PAIR'S RESAMPLER. This model stored ASRCTR and did nothing
                    // else, so a pair carried its filter history, its input count and
                    // its fractional output position ACROSS a re-init.
                    //
                    // The oracle's asrc test writes `ASRCTR = ASRCEN | ATSA` before EACH
                    // of its two phases and then changes the ratio underneath. Ignoring
                    // the re-init meant phase 2 resampled a Nyquist tone at the new 2:1
                    // ratio through phase 1's stale 1:2 state -- so the stop-band result
                    // was not a measurement of my filter at all. The filter itself was
                    // already a windowed-sinc polyphase FIR with unity DC gain; the
                    // defect was that it was never restarted.
                    //
                    // Semantics taken from the oracle (hw/audio/imxrt1180_asrc.c): clear
                    // both FIFOs, in_count, out_pos, started, and the history.
                    for(var p = 0; p < PairCount; p++)
                    {
                        if((value & (1u << (AsrctrAtsaShift + p))) != 0)
                        {
                            pairs[p].Reset();
                        }
                    }
                    regs[offset / 4] = value;
                    return;

                default:
                    regs[offset / 4] = value;
                    return;
            }
        }

        // AIDEA (input has room) / AODFA (output ready) describe a RUNNING converter's
        // FIFOs. While the module is disabled they are meaningless, and the RM's
        // ASRSTR reset value is 0 -- but an empty input FIFO reads as "has room", so
        // reporting them unconditionally would hand a freshly-reset ASRC a 0x7 the
        // silicon never produces. Gate on ASRCEN.
        // ⭐⭐ THE BLOCK HAD NO INTERRUPT OUTPUT AT ALL.
        // Every ASRC test in this corpus POLLS ASRSTR (ASRC_TransferBlocking), so the
        // rows passed green with the IRQ line entirely absent -- and an
        // interrupt-driven fsl_asrc transfer would have waited forever. Same shape as
        // the FlexCAN freeze handshake and the uSDHC init path: MODEL WHAT THE DRIVER
        // WAITS ON, NOT WHAT THE TEST CHECKS. The reference wires IRQ 235; this did not.
        //
        // ASRIER mirrors ASRSTR's AIDE/AODF bits, so the line is simply the enabled
        // subset of the status the polling path already computes.
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        private void UpdateInterrupts()
        {
            var pending = (ComposeStatus() & regs[(long)Registers.Asrier / 4]) != 0;
            Connections[0].Set(pending);
        }

        private uint ComposeStatus()
        {
            if((regs[(long)Registers.Asrctr / 4] & AsrctrAsrcen) == 0)
            {
                return 0;
            }
            var status = 0u;
            var outWatermark = (regs[(long)Registers.Asrmcra / 4] >> AsrmcrOutFifoShift) & AsrmcrOutFifoMask;
            for(var p = 0; p < PairCount; p++)
            {
                if(pairs[p].InputFill < FifoSize / 2)
                {
                    status |= AsrstrAidea << p;
                }
                if(pairs[p].OutputFill > (outWatermark != 0 ? outWatermark : 1))
                {
                    status |= AsrstrAodfa << p;
                }
            }
            return status;
        }

        // Output frames per input frame, from the guest's ASRCDR1 dividers.
        //   sampleRate = srcClk / ((div + 1) * 2^presc)
        // The true-async in:out clock factor is NOT modelled here (it needs a live
        // SAI bit-clock rate for both selects); the divider ratio alone is used, and
        // that is said out loud rather than presented as the full answer.
        private double OutputPerInputRatio()
        {
            var dr = regs[(long)Registers.Asrcdr1 / 4];
            var inPrescale  = dr & 0x7;
            var inDivider   = ((dr >> 3) & 0x7) + 1;
            var outPrescale = (dr >> 12) & 0x7;
            var outDivider  = ((dr >> 15) & 0x7) + 1;
            var inPeriod  = (double)inDivider * (1u << (int)inPrescale);
            var outPeriod = (double)outDivider * (1u << (int)outPrescale);
            if(outPeriod <= 0)
            {
                return 1.0;
            }

            // ⭐⭐ THE DIVIDER RATIO IS ONLY HALF THE RATIO.
            //   in_rate  = inSrcHz  / (in_div  * 2^in_presc)
            //   out_rate = outSrcHz / (out_div * 2^out_presc)
            //   ratio    = out_rate/in_rate = (outSrcHz/inSrcHz) * in_period/out_period
            // Every m2m example selects ONE source for both sides (AICSA == AOCSA), so
            // the source factor is exactly 1 and the dividers alone are right -- which
            // is why a model that ignores ASRCSR entirely passes every m2m test and is
            // wrong the moment a conversion is genuinely asynchronous. Same shape as
            // the SAI's TCR2[DIV] blind spot: correct at the point everyone tests.
            var srcFactor = 1.0;
            var sr = regs[(long)Registers.Asrcsr / 4];
            var inSel  = sr & 0xF;                  // ASRCSR.AICSA
            var outSel = (sr >> 12) & 0xF;          // ASRCSR.AOCSA
            if(inSel != outSel)
            {
                var inHz = SourceHz(inSel);
                var outHz = SourceHz(outSel);
                if(inHz != 0 && outHz != 0)
                {
                    srcFactor = (double)outHz / inHz;
                }
                else
                {
                    // 0 is honest: fall back to the dividers, do NOT invent a rate.
                    this.Log(LogLevel.Warning, "async ASRC clock sources in={0} out={1}: only the "
                        + "SAIn TX bit clocks are resolvable here, so the in:out frequency factor "
                        + "is not modelled; using the ASRCDR divider ratio alone (inHz={2} outHz={3})",
                        inSel, outSel, inHz, outHz);
                }
            }
            return srcFactor * inPeriod / outPeriod;
        }

        // kASRC_ClockSourceBitClock{0,2,4,6}_SAI{1,2,3,4}_TX = 0,2,4,6. Everything else
        // (RX bit clocks, SPDIF, the SAIx clock ROOTs, MIC/MQS) has no live rate in this
        // model, and says so by returning 0 rather than guessing.
        private uint SourceHz(uint sel)
        {
            if(sel > 6 || (sel & 1) != 0)
            {
                return 0;
            }
            var sai = SaiForSelect(sel / 2);
            return sai != null ? sai.TxBitClockHz() : 0u;
        }

        private IMXRT1180_SAI SaiForSelect(uint index)
        {
            switch(index)
            {
                case 0: return SAI1;
                case 1: return SAI2;
                case 2: return SAI3;
                case 3: return SAI4;
                default: return null;
            }
        }

        // Wired from the .repl. Absent links resolve to 0 Hz, which the ratio code
        // treats as "cannot resolve" rather than as a frequency.
        public IMXRT1180_SAI SAI1 { get; set; }
        public IMXRT1180_SAI SAI2 { get; set; }
        public IMXRT1180_SAI SAI3 { get; set; }
        public IMXRT1180_SAI SAI4 { get; set; }

        // Windowed-sinc kernel. Hann window, sinc(2*fc*x). See the header note on
        // what this does and does not claim.
        private static double WindowedSinc(double x, double fc)
        {
            if(Math.Abs(x) >= FirHalf)
            {
                return 0.0;
            }
            var window = 0.5 * (1.0 + Math.Cos(Math.PI * x / FirHalf));
            var a = 2.0 * fc * x;
            var sinc = (a == 0.0) ? 1.0 : Math.Sin(Math.PI * a) / (Math.PI * a);
            return 2.0 * fc * sinc * window;
        }

        private void Resample(int index)
        {
            var pair = pairs[index];
            var channels = Math.Max(1u, regs[(long)Registers.Asrcncr / 4] & AsrcncrAncaMask);
            channels = Math.Min(channels, 2u);
            var ratio = OutputPerInputRatio();
            if(ratio <= 0)
            {
                ratio = 1.0;
            }
            var step = 1.0 / ratio;
            var fc = (ratio < 1.0) ? 0.5 * ratio : 0.5;    // min(in, out) Nyquist

            while(pair.InputFill >= channels)
            {
                var slot = (int)(pair.InputCount & (HistorySize - 1));
                for(var c = 0; c < channels; c++)
                {
                    pair.History[c][slot] = pair.PopInputRaw();
                }
                pair.InputCount++;
                if(!pair.Started)
                {
                    pair.OutputPosition = 0.0;
                    pair.Started = true;
                }

                // Emit every output whose right-hand context is now available; the
                // left side is zero-padded at the very start.
                while(pair.InputCount - 1.0 >= pair.OutputPosition + FirHalf)
                {
                    var i0 = (long)Math.Floor(pair.OutputPosition);
                    var frac = pair.OutputPosition - i0;
                    for(var c = 0; c < channels; c++)
                    {
                        double sum = 0.0, weightSum = 0.0;
                        for(var k = -(FirHalf - 1); k <= FirHalf; k++)
                        {
                            var g = WindowedSinc(frac - k, fc);
                            var f = i0 + k;
                            var sample = (f >= 0 && f < pair.InputCount)
                                ? pair.History[c][(int)(f & (HistorySize - 1))]
                                : 0;
                            sum += sample * g;
                            weightSum += g;
                        }
                        // Normalizing by the tap sum makes the DC gain EXACTLY 1
                        // regardless of the window.
                        pair.PushOutput(weightSum != 0.0 ? (int)Math.Round(sum / weightSum) : 0);
                    }
                    pair.OutputPosition += step;
                }
            }
        }

        private readonly uint[] regs;
        private readonly Pair[] pairs;

        private const int PairCount = 3;
        private const int FifoSize = 256;
        private const int HistorySize = 32;
        private const int FirHalf = 8;          // 16-tap window

        private const uint AsrctrAsrcen = 1u << 0;
        private const int  AsrctrAtsaShift = 20;   // task-start A; B/C at 21/22
        private const uint AsrcncrAncaMask = 0xF;
        private const uint AsrstrAidea = 1u << 0;
        private const uint AsrstrAodfa = 1u << 3;
        private const int  AsrmcrOutFifoShift = 12;
        private const uint AsrmcrOutFifoMask = 0x3F;

        private enum Registers : long
        {
            Asrctr  = 0x00,
            Asrier  = 0x04,
            Asrcncr = 0x0C,
            Asrcfg  = 0x10,
            Asrcsr  = 0x14,
            Asrcdr1 = 0x18,
            Asrcdr2 = 0x1C,
            Asrstr  = 0x20,
            Asrdia  = 0x60,
            Asrdoa  = 0x64,
            Asrmcra = 0xA0,
            Asrfsta = 0xA4,
        }

        private class Pair
        {
            public Pair()
            {
                inputFifo = new int[FifoSize];
                outputFifo = new int[FifoSize];
                History = new int[2][];
                History[0] = new int[HistorySize];
                History[1] = new int[HistorySize];
            }

            public void Reset()
            {
                inHead = inTail = outHead = outTail = 0;
                InputCount = 0;
                OutputPosition = 0.0;
                Started = false;
                Array.Clear(History[0], 0, HistorySize);
                Array.Clear(History[1], 0, HistorySize);
            }

            public int InputFill => (inTail - inHead) & (FifoSize - 1);
            public int OutputFill => (outTail - outHead) & (FifoSize - 1);

            public void PushInput(int sample)
            {
                if(InputFill < FifoSize - 1)
                {
                    inputFifo[inTail] = sample;
                    inTail = (inTail + 1) & (FifoSize - 1);
                }
            }

            public int PopInputRaw()
            {
                var value = inputFifo[inHead];
                inHead = (inHead + 1) & (FifoSize - 1);
                return value;
            }

            public void PushOutput(int sample)
            {
                if(OutputFill < FifoSize - 1)
                {
                    outputFifo[outTail] = sample;
                    outTail = (outTail + 1) & (FifoSize - 1);
                }
            }

            public int PopOutput()
            {
                if(OutputFill == 0)
                {
                    return 0;
                }
                var value = outputFifo[outHead];
                outHead = (outHead + 1) & (FifoSize - 1);
                return value;
            }

            public long InputCount;
            public double OutputPosition;
            public bool Started;
            public int[][] History;

            private readonly int[] inputFifo;
            private readonly int[] outputFifo;
            private int inHead, inTail, outHead, outTail;
        }
    }
}
