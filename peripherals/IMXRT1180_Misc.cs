//
// SEMA42 / VREF / CMP — three small blocks that imxrt1180-misc1 needs for one row.
//
// Every assertion is in the test source, so these are modelled to what it polls and
// no further. Bases: SEMA42 0x44260000, VREF 0x42E30000, CMP1 0x42DC0000.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // ⭐ SEMA42 IS A MUTUAL-EXCLUSION GATE, AND THE INTERESTING CASE IS THE REFUSAL.
    // The test locks gate 0 to domain 1, then has a SECOND domain try to take it and
    // requires the gate to STILL read 1, then unlocks. A gate that simply stores what
    // you write passes the lock and the unlock and fails only the middle assertion --
    // which is the whole point of the block. "Stores the value" and "enforces
    // exclusion" are indistinguishable unless somebody else tries to take it.
    public class IMXRT1180_SEMA42 : IBytePeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_SEMA42(IMachine machine)
        {
            gates = new byte[GateCount];
        }

        public long Size => 0x1000;

        public void Reset()
        {
            Array.Clear(gates, 0, gates.Length);
        }

        public byte ReadByte(long offset)
        {
            return offset < GateCount ? gates[offset] : (byte)0;
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset >= GateCount)
            {
                return;
            }
            var domain = (byte)(value & 0xF);
            var current = gates[offset];
            if(domain == 0)
            {
                gates[offset] = 0;                  // unlock
            }
            else if(current == 0)
            {
                gates[offset] = domain;             // take a free gate
            }
            // else: already owned by another domain -- the write is REFUSED, silently,
            // exactly as the hardware does. This branch is the block's entire purpose.
        }

        public uint ReadDoubleWord(long offset)
        {
            return ReadByte(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            WriteByte(offset, (byte)value);
        }

        private readonly byte[] gates;
        private const int GateCount = 16;
    }

    // VREF: CSR bit0 enables, bit2 reports stable. The virtual reference settles
    // instantly, so STABLE tracks the enable -- the same "always settled" shape as the
    // DCDC STS_DC_OK bit, which exists because a driver spins on it forever otherwise.
    public class IMXRT1180_VREF : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_VREF(IMachine machine)
        {
            regs = new uint[Size / 4];
        }

        public long Size => 0x1000;
        public void Reset() { Array.Clear(regs, 0, regs.Length); }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size) { return 0; }
            if(offset == ControlStatus)
            {
                var value = regs[offset / 4];
                return (value & Enable) != 0 ? (value | Stable) : (value & ~Stable);
            }
            return regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size) { return; }
            regs[offset / 4] = offset == ControlStatus ? (value & ~Stable) : value;
        }

        private readonly uint[] regs;
        private const long ControlStatus = 0x08;
        private const uint Enable = 1u << 0;
        private const uint Stable = 1u << 2;
    }

    // CMP: the config field must STICK, and CFR/CFF are hardware-set status bits that
    // software can only CLEAR. The test writes CFR=1 and requires it to read back 0 --
    // a register that merely stored the write would report a comparator event that
    // never happened.
    public class IMXRT1180_CMP : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_CMP(IMachine machine)
        {
            regs = new uint[Size / 4];
        }

        public long Size => 0x1000;
        public void Reset() { Array.Clear(regs, 0, regs.Length); }

        public uint ReadDoubleWord(long offset)
        {
            return offset + 4 <= Size ? regs[offset / 4] : 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size) { return; }
            if(offset == Control0)
            {
                // CFR (26) and CFF (25) are W1C status: software can never set them.
                var sticky = regs[offset / 4] & StatusFlags & ~value;
                regs[offset / 4] = (value & ~StatusFlags) | sticky;
                return;
            }
            regs[offset / 4] = value;
        }

        private readonly uint[] regs;
        private const long Control0 = 0x08;
        private const uint StatusFlags = (1u << 26) | (1u << 25);
    }
}
