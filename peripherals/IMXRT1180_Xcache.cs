//
// i.MX RT1180 XCACHE (platform cache controller) — control-plane model.
// PORTED FROM THE QEMU ORACLE: hw/misc/imxrt1180_xcache.c. Not re-derived.
//
// It models COMPLETION, not caching: Renode's memory is coherent and there is
// no data cache, so every maintenance command is instantly done. The point of
// the model is that the driver's poll must READ "done" — the transient command
// bits and GO must self-clear on write.
//
// ⚠ WHY THIS EXISTS EVEN THOUGH BOOT "WORKED" WITHOUT IT: with XCACHE unmapped,
// reads returned 0, so BOARD_DeinitFlash saw CCR.ENCACHE == 0 and SKIPPED the
// whole cache-maintenance block. Boot advanced for the WRONG REASON — an
// accident of unmapped-reads-as-zero, not a modelled behaviour. That is exactly
// the silent-wrong class the brief warns about, so it is modelled properly.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_Xcache : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_Xcache(IMachine machine)
        {
            regs = new uint[Size / 4];
            Reset();
        }

        // Two instances (XCACHE_PC @ +0x000, XCACHE_PS @ +0x800) share one
        // register layout; the bank offset is irrelevant per-register. [oracle]
        public long Size => 0x1000;

        public void Reset()
        {
            for(var i = 0; i < regs.Length; i++)
            {
                regs[i] = 0;
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            return (offset + 4 > Size || offset < 0) ? 0u : regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size || offset < 0)
            {
                return;
            }
            switch(offset & 0x7FF)
            {
                case CCR:
                    // A maintenance command completes instantly: drop the
                    // transient command + GO bits so the driver's poll reads
                    // "done" at once. Config bits (ENCACHE, ...) stick. [oracle]
                    regs[offset / 4] = value & ~CcrCmdMask;
                    break;
                case CSAR:
                    // Line command (LGO) likewise completes instantly. [oracle]
                    regs[offset / 4] = value & ~CsarLgo;
                    break;
                default:
                    regs[offset / 4] = value;   // CLCR, CCVR: plain storage
                    break;
            }
        }

        private readonly uint[] regs;

        private const long CCR = 0x0;    // Cache Control
        private const long CLCR = 0x4;   // Cache Line Control
        private const long CSAR = 0x8;   // Cache Search Address
        private const long CCVR = 0xC;   // Cache R/W Value

        private const uint CcrEncache = 0x00000001u;
        private const uint CcrInvW0 = 0x01000000u;
        private const uint CcrPushW0 = 0x02000000u;
        private const uint CcrInvW1 = 0x04000000u;
        private const uint CcrPushW1 = 0x08000000u;
        private const uint CcrGo = 0x80000000u;
        private const uint CcrCmdMask = CcrInvW0 | CcrPushW0 | CcrInvW1 | CcrPushW1 | CcrGo;
        private const uint CsarLgo = 0x00000001u;
    }
}
