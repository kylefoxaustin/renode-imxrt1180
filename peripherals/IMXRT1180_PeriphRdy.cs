//
// i.MX RT1180 generic peripheral-readiness block.
// PORTED FROM THE QEMU ORACLE: hw/misc/imxrt1180_periphrdy.c. Not re-derived.
//
// A register-backed window that optionally forces ONE "ready/settled" bit set
// at ONE offset. That is the whole model. It exists because the recurring RT1180
// failure is a driver spinning on a status bit of a block nobody has modelled —
// an infinite loop against silence.
//
// ⚠ IT MODELS NOTHING ELSE. Reads return what was written (plus the ready bit);
// writes are stored. No behaviour, no side effects. Used where the firmware
// needs a block to *exist and be settled*, not to *do* anything.
//
// Instances and parameters come from the oracle's imxrt1180_add_rdy* call sites
// in hw/arm/imxrt1180_soc.c.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_PeriphRdy : IDoubleWordPeripheral, IBytePeripheral, IKnownSize
    {
        // readyOffset/readyMask: the single status bit to force set on read.
        // readyMask 0 => a plain register-backed window (the oracle's add_rdy).
        public IMXRT1180_PeriphRdy(IMachine machine, ulong size = 0x1000,
                                   long readyOffset = 0, uint readyMask = 0)
        {
            Size = (long)size;
            this.readyOffset = readyOffset;
            this.readyMask = readyMask;
            regs = new uint[Size / 4];
        }

        public long Size { get; }

        public void Reset()
        {
            for(var i = 0; i < regs.Length; i++)
            {
                regs[i] = 0;
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size || offset < 0)
            {
                return 0;
            }
            var v = regs[offset / 4];
            if(offset == readyOffset)
            {
                v |= readyMask;
            }
            return v;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 <= Size && offset >= 0)
            {
                regs[offset / 4] = value;
            }
        }

        // Secure firmware byte-pokes these windows; an over-strict window faults it.
        public byte ReadByte(long offset)
            => (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8));

        public void WriteByte(long offset, byte value)
        {
            var aligned = offset & ~3L;
            var shift = (int)((offset & 3) * 8);
            WriteDoubleWord(aligned, (ReadDoubleWord(aligned) & ~(0xFFu << shift)) | ((uint)value << shift));
        }

        private readonly uint[] regs;
        private readonly long readyOffset;
        private readonly uint readyMask;
    }
}
