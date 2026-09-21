//
// Cirrus/Wolfson WM8962 audio codec -- I2C CONTROL PLANE ONLY.
//
// PORTED from the QEMU oracle's hw/audio/wm8962.c (170 lines). Protocol, register
// span and the write-sequencer behaviour all come from there; nothing re-derived.
//
// ⭐ WHY THIS IS NOT AN AUDIO DEVICE, AND MUST NOT BECOME ONE.
// On the MIMXRT1180-EVK the WM8962 is the I2S SLAVE -- the SAI is bit-clock and
// frame master -- so the codec never sees or shapes the audio SAMPLE stream. Its
// entire job here is to ANSWER the control driver over I2C so CODEC_Init /
// WM8962_Init completes instead of assert(false)-ing. Modelling it as an
// audio data-path device would be the fabrication: the sound genuinely leaves the
// SoC through the SAI. This is a faithful I2C register file and nothing more --
// it opens no audio voice, touches no samples, and makes no claim about analog out.
//
// WHY IT WAS NEEDED: MEASURED on stock driver_examples/sai/edma_transfer, the
// example does NOT spin on a missing SAI register -- it prints "SAI EDMA example
// started!" and then dies on
//     ASSERT ERROR " false ": sai_edma_transfer.c Line "139" function "main"
// which is the `if (CODEC_Init(...) != kStatus_Success) assert(false);` at :137.
// The blocker was the codec on LPI2C, not the SAI.
//
// Protocol (fsl_wm8962.c): a register is a 2-byte BIG-ENDIAN address followed by a
// 16-bit big-endian value. Reads latch the address in the preceding write phase,
// then clock the value out MSB-first. Both auto-increment for bursts.
//
using System;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.I2C;

namespace Antmicro.Renode.Peripherals.Sound
{
    public class WM8962 : II2CPeripheral
    {
        public WM8962()
        {
            regs = new ushort[NumberOfRegisters];
            Reset();
        }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            pointer = 0;
            phase = 0;
            dataHigh = 0;
            readLsb = false;
        }

        public void Write(byte[] data)
        {
            foreach(var b in data)
            {
                switch(phase)
                {
                    case 0:                                  // register address MSB
                        pointer = (ushort)(b << 8);
                        phase = 1;
                        break;
                    case 1:                                  // register address LSB
                        pointer |= b;
                        phase = 2;
                        break;
                    case 2:                                  // data MSB
                        dataHigh = b;
                        phase = 3;
                        break;
                    default:                                 // data LSB -> commit 16 bits
                        if(pointer < NumberOfRegisters)
                        {
                            regs[pointer] = (ushort)((dataHigh << 8) | b);
                        }
                        pointer++;                           // auto-increment for bursts
                        phase = 2;                           // another value may follow
                        break;
                }
            }
        }

        public byte[] Read(int count = 1)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++)
            {
                var value = ReadRegister(pointer);
                if(!readLsb)
                {
                    result[i] = (byte)(value >> 8);          // MSB first (big-endian)
                    readLsb = true;
                }
                else
                {
                    result[i] = (byte)(value & 0xFF);
                    readLsb = false;
                    pointer++;                               // auto-increment for bursts
                }
            }
            return result;
        }

        public void FinishTransmission()
        {
            // A fresh transaction always starts with a register address; the latched
            // pointer survives so the repeated-START read path still finds it.
            phase = 0;
            readLsb = false;
        }

        private ushort ReadRegister(ushort reg)
        {
            var value = reg < NumberOfRegisters ? regs[reg] : (ushort)0;

            // WM8962_StartSequence() writes the write-sequencer control register and
            // then POLLS 0x5D, looping while bit 0 (BUSY) is set. Every sequence
            // completes instantly here, so BUSY always reads 0 -- reported on the READ
            // so a driver-written BUSY bit can never hang the poll.
            if(reg == WriteSequencerBusyRegister)
            {
                value &= unchecked((ushort)~WriteSequencerBusyBit);
            }
            return value;
        }

        private readonly ushort[] regs;
        private ushort pointer;
        private byte phase;      // 0,1 = address byte; 2,3 = data byte
        private byte dataHigh;
        private bool readLsb;

        // Spans up to INT_STATUS_2 @ 0x231 (fsl_wm8962.h) plus the write sequencer;
        // hold the whole space so a stored write always reads back.
        private const int NumberOfRegisters = 0x300;
        private const ushort WriteSequencerBusyRegister = 0x5D;
        private const ushort WriteSequencerBusyBit = 0x0001;
    }
}
