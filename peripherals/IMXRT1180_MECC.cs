//
// NXP i.MX RT1180 MECC -- OCRAM Error-Correction-Code controller.
//
// PORTED from the QEMU oracle's hw/misc/imxrt1180_mecc.c. Offsets, bit layout,
// the injection semantics and the SECDED code are all from that file.
//
// MECC sits in front of an OCRAM bank and adds SECDED ECC: each 64-bit word is
// stored with an 8-bit code. A SINGLE-bit error is CORRECTED on read (and
// reported); a DOUBLE-bit error is DETECTED and the corrupted data is returned.
// The driver exercises this through the INJECTION path: ERR_DATA_INJ_* arms a
// bit-flip that the next write bakes into the stored codeword, and a later read
// sees the corrected-or-detected error plus the info registers and an interrupt.
//
// ⭐ THE DATA WINDOW IS INTERCEPTED, NOT MIRRORED. This peripheral OWNS the OCRAM2
// backing store: reads and writes to 0x20500000 pass through the ECC logic here.
// That is the only way an injected flip can be baked into a stored word and then
// surface on a later read -- a plain RAM plus a register block cannot express it.
//
// FIDELITY, stated because it is printed: the ECC code in {SINGLE,MULTI}_ERR_ADDR_ECC
// is a genuine (72,64) Hamming SECDED of the data but NOT NXP's exact (unpublished)
// bit assignment -- algorithm-class-faithful, not silicon-exact. The demo only ever
// PRINTS it; nothing branches on it. Everything the demo ASSERTS -- corrected data,
// raw errored data, bit position, address, single/multi status, the interrupt -- is
// modelled exactly.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_MECC : IDoubleWordPeripheral, IBytePeripheral, IWordPeripheral, IKnownSize
    {
        public IMXRT1180_MECC(IMachine machine, uint ocramSize = 0x40000)
        {
            this.ocramSize = ocramSize;
            data = new byte[ocramSize];
            poisoned = new bool[ocramSize / 8];
            poisonLow = new uint[ocramSize / 8];
            poisonHigh = new uint[ocramSize / 8];
            poisonEcc = new byte[ocramSize / 8];
            IRQ = new GPIO();
            Reset();
        }

        public long Size => 0x1000;
        public GPIO IRQ { get; private set; }

        public void Reset()
        {
            Array.Clear(data, 0, data.Length);
            Array.Clear(poisoned, 0, poisoned.Length);
            errorStatus = 0; errorStatusEnable = 0; errorSignalEnable = 0;
            pipeEccEnable = 0; pendingStatus = 0;
            Array.Clear(injectLow, 0, 4); Array.Clear(injectHigh, 0, 4); Array.Clear(injectEcc, 0, 4);
            Array.Clear(singleAddrEcc, 0, 4); Array.Clear(singleLow, 0, 4); Array.Clear(singleHigh, 0, 4);
            Array.Clear(singlePosLow, 0, 4); Array.Clear(singlePosHigh, 0, 4);
            Array.Clear(multiAddrEcc, 0, 4); Array.Clear(multiLow, 0, 4); Array.Clear(multiHigh, 0, 4);
            UpdateInterrupt();
        }

        // ── OCRAM DATA WINDOW (region "ocram") ───────────────────────────────────
        [ConnectionRegion("ocram")]
        public uint ReadDoubleWordFromOcram(long offset) => (uint)OcramRead(offset, 4);
        [ConnectionRegion("ocram")]
        public void WriteDoubleWordToOcram(long offset, uint value) => OcramWrite(offset, value, 4);
        [ConnectionRegion("ocram")]
        public byte ReadByteFromOcram(long offset) => (byte)OcramRead(offset, 1);
        [ConnectionRegion("ocram")]
        public void WriteByteToOcram(long offset, byte value) => OcramWrite(offset, value, 1);
        [ConnectionRegion("ocram")]
        public ushort ReadWordFromOcram(long offset) => (ushort)OcramRead(offset, 2);
        [ConnectionRegion("ocram")]
        public void WriteWordToOcram(long offset, ushort value) => OcramWrite(offset, value, 2);

        private ulong OcramRead(long offset, int size)
        {
            var word = offset & ~7L;
            var index = (int)(word >> 3);
            var bank = index & 3;

            if((pipeEccEnable & PipeEccEnable) != 0 && index < poisoned.Length && poisoned[index])
            {
                var host = BitConverter.ToUInt64(data, (int)word);
                var il = poisonLow[index];
                var ih = poisonHigh[index];
                var ie = poisonEcc[index];
                var bits = PopCount(il) + PopCount(ih) + PopCount(ie);

                if(bits == 1)
                {
                    // Single-bit: CORRECTABLE -> the read returns the CLEAN data.
                    errorStatus |= 1u << bank;
                    singleAddrEcc[bank] = ((uint)word << 8) | Secded(host);
                    singleLow[bank] = (uint)host ^ il;
                    singleHigh[bank] = (uint)(host >> 32) ^ ih;
                    // POS is a ONE-HOT MASK of the flipped bit (the driver takes log2
                    // of it with a shift loop), i.e. the injection mask itself -- NOT
                    // a bit index.
                    singlePosLow[bank] = il;
                    singlePosHigh[bank] = ih;
                    UpdateInterrupt();
                    // fall through: return the corrected bytes
                }
                else if(bits >= 2)
                {
                    // Multi-bit: UNCORRECTABLE -> the read returns the CORRUPTED data.
                    errorStatus |= 1u << (4 + bank);
                    multiAddrEcc[bank] = ((uint)word << 8) | Secded(host);
                    multiLow[bank] = (uint)host ^ il;
                    multiHigh[bank] = (uint)(host >> 32) ^ ih;
                    UpdateInterrupt();
                    var corrupt = host ^ (((ulong)ih << 32) | il);
                    var tmp = BitConverter.GetBytes(corrupt);
                    return Load(tmp, (int)(offset & 7), size);
                }
            }
            return Load(data, (int)offset, size);
        }

        private void OcramWrite(long offset, ulong value, int size)
        {
            Store(data, (int)offset, value, size);       // the CPU's clean data

            var word = offset & ~7L;
            var index = (int)(word >> 3);
            var bank = index & 3;
            if((pipeEccEnable & PipeEccEnable) == 0 || index >= poisoned.Length)
            {
                return;
            }
            if((injectLow[bank] | injectHigh[bank] | injectEcc[bank]) != 0)
            {
                // The hardware bakes the armed flip into the stored codeword.
                poisonLow[index] = injectLow[bank];
                poisonHigh[index] = injectHigh[bank];
                poisonEcc[index] = (byte)injectEcc[bank];
                poisoned[index] = true;
            }
            else
            {
                poisoned[index] = false;                 // a clean write scrubs the word
            }
        }

        // ── CONTROL REGISTERS ────────────────────────────────────────────────────
        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
                case ErrStatus:    return errorStatus;
                case ErrStatEn:    return errorStatusEnable;
                case ErrSigEn:     return errorSignalEnable;
                case PipeEccEn:    return pipeEccEnable;
                case PendingStat:  return pendingStatus;
            }
            if(offset >= InjBase && offset <= InjEnd)
            {
                var i = (int)(offset - InjBase) / 4; var b = i / 3; var sub = i % 3;
                return sub == 0 ? injectLow[b] : sub == 1 ? injectHigh[b] : injectEcc[b];
            }
            if(offset >= SingleBase && offset <= SingleEnd)
            {
                var i = (int)(offset - SingleBase) / 4; var b = i / 5; var sub = i % 5;
                switch(sub)
                {
                    case 0: return singleAddrEcc[b];
                    case 1: return singleLow[b];
                    case 2: return singleHigh[b];
                    case 3: return singlePosLow[b];
                    default: return singlePosHigh[b];
                }
            }
            if(offset >= MultiBase && offset <= MultiEnd)
            {
                var i = (int)(offset - MultiBase) / 4; var b = i / 3; var sub = i % 3;
                return sub == 0 ? multiAddrEcc[b] : sub == 1 ? multiLow[b] : multiHigh[b];
            }
            this.Log(LogLevel.Warning, "Unhandled read at offset 0x{0:X}", offset);
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch(offset)
            {
                case ErrStatus:   errorStatus &= ~value; UpdateInterrupt(); return;   // W1C
                case ErrStatEn:   errorStatusEnable = value; UpdateInterrupt(); return;
                case ErrSigEn:    errorSignalEnable = value; UpdateInterrupt(); return;
                case PipeEccEn:   pipeEccEnable = value; return;
                case PendingStat: pendingStatus = value; return;
            }
            if(offset >= InjBase && offset <= InjEnd)
            {
                var i = (int)(offset - InjBase) / 4; var b = i / 3; var sub = i % 3;
                if(sub == 0) { injectLow[b] = value; }
                else if(sub == 1) { injectHigh[b] = value; }
                else { injectEcc[b] = value; }
                return;
            }
            if(offset >= SingleBase && offset <= MultiEnd)
            {
                return;                                   // error info is read-only
            }
            this.Log(LogLevel.Warning, "Unhandled write at offset 0x{0:X}", offset);
        }

        public byte ReadByte(long offset) => (byte)(ReadDoubleWord(offset & ~3L) >> (int)(8 * (offset & 3)));
        public void WriteByte(long offset, byte value) { }
        public ushort ReadWord(long offset) => (ushort)(ReadDoubleWord(offset & ~3L) >> (int)(8 * (offset & 2)));
        public void WriteWord(long offset, ushort value) { }

        // A source contributes to the line only if its STATUS is enabled and its
        // SIGNAL (interrupt) is enabled.
        private void UpdateInterrupt()
        {
            IRQ.Set((errorStatus & errorStatusEnable & errorSignalEnable) != 0);
        }

        // Genuine (72,64) Hamming SECDED: 7 parity bits over the data at
        // non-power-of-two codeword positions, plus an overall parity bit.
        private static byte Secded(ulong d)
        {
            var parity = new byte[7];
            var dpos = 0;
            for(var pos = 1; dpos < 64; pos++)
            {
                if((pos & (pos - 1)) == 0) { continue; }      // skip parity positions
                if(((d >> dpos) & 1) != 0)
                {
                    for(var k = 0; k < 7; k++)
                    {
                        if((pos & (1 << k)) != 0) { parity[k] ^= 1; }
                    }
                }
                dpos++;
            }
            var ecc = 0;
            for(var k = 0; k < 7; k++) { ecc |= parity[k] << k; }
            ecc |= (int)(PopCount64(d) & 1) << 7;            // overall parity
            return (byte)ecc;
        }

        private static int PopCount(uint v) { var n = 0; while(v != 0) { n += (int)(v & 1); v >>= 1; } return n; }
        private static int PopCount64(ulong v) { var n = 0; while(v != 0) { n += (int)(v & 1); v >>= 1; } return n; }

        private static ulong Load(byte[] buf, int offset, int size)
        {
            ulong v = 0;
            for(var i = 0; i < size; i++) { v |= (ulong)buf[offset + i] << (8 * i); }
            return v;
        }

        private static void Store(byte[] buf, int offset, ulong value, int size)
        {
            for(var i = 0; i < size; i++) { buf[offset + i] = (byte)(value >> (8 * i)); }
        }

        private readonly uint ocramSize;
        private readonly byte[] data;
        private readonly bool[] poisoned;
        private readonly uint[] poisonLow, poisonHigh;
        private readonly byte[] poisonEcc;

        private uint errorStatus, errorStatusEnable, errorSignalEnable, pipeEccEnable, pendingStatus;
        private readonly uint[] injectLow = new uint[4], injectHigh = new uint[4], injectEcc = new uint[4];
        private readonly uint[] singleAddrEcc = new uint[4], singleLow = new uint[4], singleHigh = new uint[4];
        private readonly uint[] singlePosLow = new uint[4], singlePosHigh = new uint[4];
        private readonly uint[] multiAddrEcc = new uint[4], multiLow = new uint[4], multiHigh = new uint[4];

        private const long ErrStatus   = 0x00;   // W1C
        private const long ErrStatEn   = 0x04;
        private const long ErrSigEn    = 0x08;
        private const long InjBase     = 0x0C;   // 3 regs (LOW,HIGH,ECC) x 4 banks
        private const long InjEnd      = 0x38;
        private const long SingleBase  = 0x3C;   // 5 regs x 4 banks
        private const long SingleEnd   = 0x88;
        private const long MultiBase   = 0x8C;   // 3 regs x 4 banks
        private const long MultiEnd    = 0xB8;
        private const long PipeEccEn   = 0x100;
        private const long PendingStat = 0x104;
        private const uint PipeEccEnable = 1u << 4;
    }
}
