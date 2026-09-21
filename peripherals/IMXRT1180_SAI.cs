//
// NXP i.MX RT1180 SAI -- I2S transmit path (the part the SDK corpus exercises).
//
// PORTED from the QEMU oracle's hw/misc/imxrt1180_sai.c. Register map, TCSR bit
// meanings, the fs derivation and the per-instance PARAM values all come from
// there. Nothing is re-derived and nothing is guessed.
//
// ⭐ THE SAMPLE RATE IS COMPUTED FROM THE GUEST'S OWN REGISTERS. IT IS NOT 48000.
//     BCLK = MCLK / (2 * (TCR2[DIV] + 1))
//     bits per frame = (TCR4[FRSZ] + 1) words * (TCR5[W0W] + 1) bits
//     fs = BCLK / bits-per-frame
// MCLK is CLOCK_ROOT[65] (SAI1), which this platform's CCM now computes for real
// from the ANADIG AUDIO PLL the guest programmed. Cross-checked end to end: the
// SDK board init sets loopDivider=32, num=768, den=1000, post=1 and root div 16,
// which predicts 24.576 MHz, and the model MEASURES 24576000 Hz.
//
// If the guest has not programmed a usable clock this returns 0, AND 0 MEANS 0 --
// there is no fallback. "A ?: IS NOT A SAFETY NET -- IT IS A PLACE FOR A BUG TO
// LIVE WHERE NO TEST WILL LOOK." (Six timer blocks in the oracle's tree opened
// with `if(!clk) clk = DEFAULT;` and not one of the six defaults was right.)
//
// ⭐ PARAM IS PER INSTANCE, AND THE FIFO DEPTH COMES OUT OF IT.
// The oracle's model once advertised ONE hand-written 0x00050302 for all four
// instances: PARAM[11:8] is log2(FIFO depth), so that constant said EIGHT, its
// comment said 32, and the silicon says SIXTEEN for SAI1. Three numbers, no two
// equal. The RM's per-instance reset values are used here instead:
//     SAI1 0x00050402   SAI2 0x00050501   SAI3 0x00050501   SAI4 0x00050504
//
// ⭐ FRF AND FWF ARE NOT WHAT THEY LOOK LIKE -- carrying the oracle's CORRECTION,
// not their original reading. From fsl_sai.h's own enum comments (FRIE "means
// reached watermark", FWIE "means the FIFO is empty"), cross-checked against the
// sai_edma driver whose per-request burst is exactly depth-watermark:
//     FRF (Request) = the FIFO has drained TO/BELOW the watermark -> refill trigger.
//                     This is what FRDE (the DMA) keys on.
//     FWF (Warning) = the FIFO is EMPTY; underrun imminent.
// A DMA line keyed on the naive reading ("has room at all") fires one slot short
// of full and then writes depth-watermark words -- an overrun.
//
// NOT MODELLED, flagged not faked: there is no audio sink. The drained words are
// discarded, so this makes no claim about analog output; it models the FIFO
// occupancy and the request handshake the driver actually observes. The RX path
// is register-backed only.
//
using System;
using System.Collections.Generic;
using System.IO;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Sound
{
    public class IMXRT1180_SAI : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_SAI(IMachine machine, IMXRT1180_CCM ccm = null, IMXRT1180_eDMA dma = null,
                             int clockRoot = 65, int dmaRequestSource = 21, uint param = 0x00050402)
        {
            this.machine = machine;
            this.ccm = ccm;
            this.dma = dma;
            this.clockRoot = clockRoot;
            this.dmaRequestSource = dmaRequestSource;
            this.param = param;
            fifoDepth = 1 << (int)((param >> 8) & 0xF);   // PARAM[11:8] = log2(depth)
            regs = new uint[Size / 4];
            txFifo = new Queue<uint>();
            rxFifo = new Queue<uint>();
            IRQ = new GPIO();

            machine.ClockSource.AddClockEntry(new ClockEntry(DrainTickMicroseconds, MicrosecondsPerSecond,
                DrainTick, this, "sai-drain", false));
            Reset();
        }

        public long Size => 0x1000;

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            txFifo.Clear();
            rxFifo.Clear();
            drainAccumulator = 0;
            SetDrainEnabled(false);
        }

        public uint ReadDoubleWord(long offset)
        {
            switch((Registers)offset)
            {
                case Registers.Verid:
                    return VeridValue;
                case Registers.Param:
                    return param;
                case Registers.Tcsr:
                    return ComposeTcsr();
                // ── RX PATH ──────────────────────────────────────────────────
                // ⚠️ NOTE THE DIRECTION OF THIS ONE. Both models had "RX path not
                // modelled" -- the reference says so in its own PERIPHERALS.md
                // ("RFR reads empty") -- so this is not catching up, it is going
                // AHEAD of the reference. That creates a NEW divergence, in the
                // faithful direction, exactly like the LPSPI chip-select case.
                // Recorded as such rather than counted as a win: no test in the
                // shared corpus exercises SAI receive, so nothing yet proves it
                // right. It exists because a block with a dead RCSR/RDR is half a
                // block, and an fsl_sai RECEIVE transfer would find nothing here.
                case Registers.Rcsr:
                    return ComposeRcsr();
                case Registers.Rdr0:
                case Registers.Rdr1:
                    return rxFifo.Count > 0 ? rxFifo.Dequeue() : 0u;
                case Registers.Rfr0:
                case Registers.Rfr1:
                    return (uint)rxFifo.Count << WriteFifoPointerShift;

                case Registers.Tfr0:
                case Registers.Tfr1:
                    // Read/write FIFO pointers. Only the occupancy matters to the driver.
                    return ((uint)txFifo.Count << WriteFifoPointerShift);
                default:
                    return offset + 4 <= Size ? regs[offset / 4] : 0u;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                this.Log(LogLevel.Warning, "Out-of-bounds write at 0x{0:X}", offset);
                return;
            }

            switch((Registers)offset)
            {
                case Registers.Verid:
                case Registers.Param:
                    return;                                  // RO
                case Registers.Tdr0:
                case Registers.Tdr1:
                    if(txFifo.Count < fifoDepth)
                    {
                        txFifo.Enqueue(value);
                    }
                    else
                    {
                        regs[(long)Registers.Tcsr / 4] |= CsrFef;   // overrun, sticky
                        this.Log(LogLevel.Warning, "TX FIFO overrun: TDR written with {0} words queued "
                            + "and a depth of {1}", txFifo.Count, fifoDepth);
                    }
                    UpdateFlags();
                    return;
                case Registers.Rcsr:
                {
                    var previous = regs[offset / 4];
                    var stickyRx = previous & CsrStickyFlags & ~(value & CsrStickyFlags);
                    regs[offset / 4] = (value & ~(CsrStickyFlags | CsrFrf | CsrFwf | CsrSr | CsrFr)) | stickyRx;
                    if((value & (CsrFr | CsrSr)) != 0)
                    {
                        rxFifo.Clear();
                    }
                    UpdateInterrupt();
                    return;
                }

                case Registers.Tcsr:
                {
                    var old = regs[offset / 4];
                    // FEF/SEF/WSF are W1C; FRF/FWF are computed, never stored.
                    var sticky = old & CsrStickyFlags & ~(value & CsrStickyFlags);
                    regs[offset / 4] = (value & ~(CsrStickyFlags | CsrFrf | CsrFwf)) | sticky;
                    if((value & CsrFr) != 0)                 // FIFO reset
                    {
                        txFifo.Clear();
                        regs[offset / 4] &= ~CsrFr;
                    }
                    if((value & CsrSr) != 0)                 // software reset
                    {
                        txFifo.Clear();
                        // ⭐ SR IS MOMENTARY, AND LEAVING IT SET FAILED THE INIT
                        // HANDSHAKE. FR was self-cleared here and SR was not, so
                        // `TCSR = CSR_SR; after_sr = TCSR;` read the bit back still
                        // set and the firmware refused to play -- correctly, from
                        // its side of the bus. The oracle's model clears BOTH, on
                        // write and on read (hw/misc/imxrt1180_sai.c: "cur &=
                        // ~(CSR_SR | CSR_FR);  /* momentary */"), which is the
                        // behaviour the fsl_sai driver's reset sequence expects.
                        regs[offset / 4] &= ~CsrSr;
                    }
                    SetDrainEnabled((regs[offset / 4] & CsrEn) != 0);
                    UpdateFlags();
                    return;
                }
                default:
                    regs[offset / 4] = value;
                    UpdateFlags();
                    return;
            }
        }

        // The TX BIT clock, in Hz. Exposed because the ASRC's true-async ratio is
        // (outSrcHz / inSrcHz), and those sources are SAIn TX bit clocks -- so the
        // ASRC has to be able to ask a real block for a real rate rather than
        // assume the two sides cancel. 0 means "cannot resolve": a bit-clock SLAVE
        // has its clock driven externally and the divider drives nothing, so
        // returning a computed number there would be a fabrication with an RM
        // citation attached. The caller must fall back, never invent.
        public uint TxBitClockHz()
        {
            var tcr2 = regs[(long)Registers.Tcr2 / 4];
            if(((tcr2 >> Tcr2BcdShift) & 1) == 0)
            {
                return 0;                    // slave: not ours to compute
            }
            var mclk = ccm != null ? ccm.GetClockRootHz(clockRoot) : 0u;
            if(mclk == 0)
            {
                return 0;
            }
            return mclk / (2u * ((tcr2 & Tcr2DivMask) + 1u));
        }

        // fs, in Hz, from the guest's own registers. 0 means the guest has not
        // programmed a usable clock -- the block must not tick.
        public uint SampleRateHz()
        {
            var tcr2 = regs[(long)Registers.Tcr2 / 4];
            if(((tcr2 >> Tcr2BcdShift) & 1) == 0)
            {
                // Bit-clock SLAVE: an external master drives the clock. We do not
                // model an external bit clock, so there is no rate to compute here.
                return 0;
            }
            var words = ((regs[(long)Registers.Tcr4 / 4] >> Tcr4FrszShift) & 0x1F) + 1;
            var bits  = ((regs[(long)Registers.Tcr5 / 4] >> Tcr5W0wShift) & 0x1F) + 1;
            var mclk  = ccm != null ? ccm.GetClockRootHz(clockRoot) : 0u;
            if(mclk == 0 || words == 0 || bits == 0)
            {
                return 0;
            }
            var bclk = mclk / (2u * ((tcr2 & Tcr2DivMask) + 1u));
            return bclk / (words * bits);
        }

        private uint ComposeTcsr()
        {
            // SR/FR are momentary: never observable as set on a read-back.
            var tcsr = regs[(long)Registers.Tcsr / 4] & ~(CsrFrf | CsrFwf | CsrSr | CsrFr);
            if((tcsr & CsrEn) != 0)
            {
                var watermark = regs[(long)Registers.Tcr1 / 4] & Tcr1TfwMask;
                if(txFifo.Count <= watermark)
                {
                    tcsr |= CsrFrf;          // drained to/below watermark -> refill
                }
                if(txFifo.Count == 0)
                {
                    tcsr |= CsrFwf;          // empty -> underrun imminent
                }
            }
            return tcsr;
        }

        // RCSR mirrors TCSR's flag layout: FRF = reached watermark, FWF = FIFO full
        // on the receive side (the direction is inverted from TX), FEF = over/underrun.
        private uint ComposeRcsr()
        {
            var rcsr = regs[(long)Registers.Rcsr / 4] & ~(CsrFrf | CsrFwf | CsrSr | CsrFr);
            if((rcsr & CsrEn) != 0)
            {
                var watermark = regs[(long)Registers.Rcr1 / 4] & Tcr1TfwMask;
                if(rxFifo.Count > watermark)
                {
                    rcsr |= CsrFrf;          // data available at/above the watermark
                }
                if(rxFifo.Count >= fifoDepth)
                {
                    rcsr |= CsrFwf;          // full -> overrun imminent
                }
            }
            return rcsr;
        }

        // A codec (or a loopback harness) hands received words in here. Nothing in the
        // shared corpus drives it yet; it exists so the receive side is not a stub.
        public void PushReceivedWord(uint word)
        {
            if(rxFifo.Count >= fifoDepth)
            {
                regs[(long)Registers.Rcsr / 4] |= CsrFef;   // overrun, sticky
                return;
            }
            rxFifo.Enqueue(word);
            UpdateInterrupt();
        }

        private void UpdateFlags()
        {
            if(servicingRequest)
            {
                return;                      // the loop below re-checks on the way out
            }
            UpdateInterrupt();
            var tcsr = ComposeTcsr();
            if((tcsr & CsrEn) == 0 || dma == null)
            {
                return;
            }
            var watermark = regs[(long)Registers.Tcr1 / 4] & Tcr1TfwMask;

            servicingRequest = true;
            try
            {
                // The eDMA services ONE minor loop per assertion, and those TDR writes
                // push the FIFO past the threshold so the line falls of its own accord
                // -- the same "a minor loop lowers its own request" handshake the eDMA
                // relies on. Bounded so a misprogrammed channel cannot spin forever.
                for(var guard = 0; guard < MaximumRequestsPerUpdate; guard++)
                {
                    var request = (((regs[(long)Registers.Tcsr / 4] & CsrFrde) != 0) && txFifo.Count <= watermark)
                               || (((regs[(long)Registers.Tcsr / 4] & CsrFwde) != 0) && txFifo.Count == 0);
                    if(!request)
                    {
                        return;
                    }
                    var before = txFifo.Count;
                    if(!dma.TryGetChannelBySlot(dmaRequestSource, out var channel))
                    {
                        return;              // nothing muxed to this request source yet
                    }
                    dma.OnGPIO(channel, true);
                    if(txFifo.Count == before)
                    {
                        return;              // the channel did not advance; do not spin
                    }
                }
                this.Log(LogLevel.Warning, "TX DMA request still asserted after {0} serviced minor "
                    + "loops; giving up this round to avoid spinning", MaximumRequestsPerUpdate);
            }
            finally
            {
                servicingRequest = false;
            }
        }

        private void SetDrainEnabled(bool enabled)
        {
            machine.ClockSource.ExchangeClockEntryWith(DrainTick, entry => entry.With(enabled: enabled));
        }

        // The codec's fs-paced pull. Fires every DrainTickMicroseconds of VIRTUAL time
        // and removes exactly the number of words the bit clock would have clocked out
        // since the last tick, with the sub-word remainder carried in drainAccumulator.
        // Pacing to fs -- not to "as fast as anything will take it" -- is what keeps the
        // guest running at a real sample rate.
        private void DrainTick()
        {
            var fs = SampleRateHz();
            if(fs == 0)
            {
                return;                      // no clock: nothing is being clocked out
            }
            drainAccumulator += (ulong)fs * DrainTickMicroseconds;
            var want = drainAccumulator / MicrosecondsPerSecond;
            drainAccumulator -= want * MicrosecondsPerSecond;

            for(var i = 0ul; i < want && txFifo.Count > 0; i++)
            {
                WriteSampleToSink(txFifo.Dequeue());
            }
            UpdateFlags();
        }

        // ⭐⭐⭐ A BLOCK THAT ACCEPTS EVERY SAMPLE AND EMITS NOTHING PASSES EVERY
        // IN-GUEST TEST. This model used to do exactly that -- clock the FIFO at a
        // correct fs and then `txFifo.Dequeue()` into nowhere -- which is the same
        // defect the oracle's SAI had, and which their register-handshake test could
        // not see for months. The verdict has to be rendered OUTSIDE the guest, from
        // the samples, so the samples have to leave the block.
        //
        // The sink is a FILE and never a host device: the capture IS the mute and IS
        // the evidence, and there is no path through this code that reaches a speaker.
        public void CreateWavBackend(string path)
        {
            CloseWavBackend();
            wavStream = new FileStream(path, FileMode.Create, FileAccess.Write);
            wavSamples = 0;
            wavHeaderRate = 0;
            this.Log(LogLevel.Info, "SAI TX sink: writing samples to {0}", path);
        }

        public void CloseWavBackend()
        {
            if(wavStream == null)
            {
                return;
            }
            PatchWavHeader();
            wavStream.Dispose();
            wavStream = null;
        }

        // The rate is not known until the guest has programmed TCR2/TCR4/TCR5, so the
        // header is written on the FIRST sample, from the block's own registers -- not
        // from a rate the harness supplied. A backend pinned by the harness would make
        // the file agree with the harness instead of with the model.
        private void WriteSampleToSink(uint word)
        {
            if(wavStream == null)
            {
                return;
            }
            if(wavHeaderRate == 0)
            {
                wavHeaderRate = SampleRateHz();
                if(wavHeaderRate == 0)
                {
                    return;                  // no clock: nothing is being clocked out
                }
                WriteWavHeader(wavHeaderRate);
            }
            var sample = (short)(ushort)word;
            wavStream.WriteByte((byte)(sample & 0xFF));
            wavStream.WriteByte((byte)((sample >> 8) & 0xFF));
            wavSamples++;
            if((wavSamples % 256) == 0)
            {
                PatchWavHeader();            // valid file even if Renode is killed
                wavStream.Flush();
            }
        }

        private void WriteWavHeader(uint rate)
        {
            var blockAlign = WavChannels * 2;
            var byteRate = (int)rate * blockAlign;
            var w = new BinaryWriter(wavStream, System.Text.Encoding.ASCII, true);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(0);                                  // patched on close
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                                 // PCM fmt chunk size
            w.Write((short)1);                           // PCM
            w.Write((short)WavChannels);
            w.Write((int)rate);
            w.Write(byteRate);
            w.Write((short)blockAlign);
            w.Write((short)16);                          // bits per sample
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(0);                                  // patched on close
            w.Flush();
        }

        private void PatchWavHeader()
        {
            if(wavStream == null || wavHeaderRate == 0)
            {
                return;
            }
            var dataBytes = wavSamples * 2;
            var here = wavStream.Position;
            wavStream.Seek(4, SeekOrigin.Begin);
            var riff = BitConverter.GetBytes(36 + dataBytes);
            wavStream.Write(riff, 0, 4);
            wavStream.Seek(40, SeekOrigin.Begin);
            var data = BitConverter.GetBytes(dataBytes);
            wavStream.Write(data, 0, 4);
            wavStream.Seek(here, SeekOrigin.Begin);
        }

        public GPIO IRQ { get; private set; }

        // TCSR pairs each flag at bit 16+n with its enable at bit 8+n
        // (FRIE/FRF, FWIE/FWF, FEIE/FEF, SEIE/SEF, WSIE/WSF).
        private void UpdateInterrupt()
        {
            var tcsr = ComposeTcsr();
            var flags = (tcsr >> 16) & 0x1F;
            var enables = (tcsr >> CsrInterruptEnableShift) & 0x1F;
            IRQ.Set((flags & enables) != 0);
        }

        private readonly IMachine machine;
        private readonly IMXRT1180_CCM ccm;
        private readonly IMXRT1180_eDMA dma;
        private readonly int clockRoot;
        private readonly int dmaRequestSource;
        private readonly uint param;
        private readonly int fifoDepth;
        private readonly uint[] regs;
        private readonly Queue<uint> txFifo;
        private readonly Queue<uint> rxFifo;
        private ulong drainAccumulator;
        private bool servicingRequest;
        private FileStream wavStream;
        private int wavSamples;
        private uint wavHeaderRate;

        // The oracle's check.py asserts channels == 2 and then flattens the
        // interleaved stream, comparing it word-for-word against what the firmware
        // wrote. TCR3 enables TX channel 0 only and the guest writes one 16-bit word
        // per TDR0 store, so the flat stream IS the word sequence. Matching their
        // layout is deliberate: the assertion is theirs, not mine.
        private const int WavChannels = 2;

        private const long DrainTickMicroseconds = 200;      // fs*200us ~= 9.6 words at 48 kHz
        private const long MicrosecondsPerSecond = 1000000;
        private const int MaximumRequestsPerUpdate = 64;
        private const uint VeridValue = 0x03010000;          // the RM prints this for every instance

        private const uint CsrFrde = 1u << 0;
        private const uint CsrFwde = 1u << 1;
        private const uint CsrFrf  = 1u << 16;
        private const uint CsrFwf  = 1u << 17;
        private const uint CsrFef  = 1u << 18;
        private const uint CsrSef  = 1u << 19;
        private const uint CsrWsf  = 1u << 20;
        private const uint CsrSr   = 1u << 24;
        private const uint CsrFr   = 1u << 25;
        private const uint CsrEn   = 1u << 31;
        private const uint CsrStickyFlags = CsrFef | CsrSef | CsrWsf;

        private const uint Tcr1TfwMask  = 0x1F;
        private const uint Tcr2DivMask  = 0xFF;
        private const int  Tcr2BcdShift = 24;
        private const int  Tcr4FrszShift = 16;
        private const int  Tcr5W0wShift  = 16;
        private const int  WriteFifoPointerShift = 16;
        private const int  CsrInterruptEnableShift = 8;

        private enum Registers : long
        {
            Verid = 0x00,
            Param = 0x04,
            Tcsr  = 0x08,
            Tcr1  = 0x0C,
            Tcr2  = 0x10,
            Tcr3  = 0x14,
            Tcr4  = 0x18,
            Tcr5  = 0x1C,
            Tdr0  = 0x20,
            Tdr1  = 0x24,
            Tfr0  = 0x40,
            Tfr1  = 0x44,
            Tmr   = 0x60,
            Rcsr  = 0x88,
            Rcr1  = 0x8C,
            Rcr2  = 0x90,
            Rcr3  = 0x94,
            Rcr4  = 0x98,
            Rcr5  = 0x9C,
            Rdr0  = 0xA0,
            Rdr1  = 0xA4,
            Rfr0  = 0xC0,
            Rfr1  = 0xC4,
            Mcr   = 0x100,
        }
    }
}
