//
// i.MX RT1180 TRDC — Trusted Resource Domain Controller (CONFIG STUB).
// PORTED FROM THE QEMU ORACLE: hw/misc/imxrt1180_trdc.c. Not re-derived.
//
// ⚠ WHAT THIS IS, STATED PLAINLY: the TRDC gates bus masters/regions by
// security domain. This model is register-backed and returns a sane hardware
// config (non-zero master/domain/region counts) so the SDK drivers proceed.
// It **does NOT enforce any access control** — every access is already
// permitted in this platform. That is FLAGGED, not silently pretended.
//
// Why it is needed: `fsl_trdc` drivers read TRDC_HWCFG0 to size their
// domain/master loops and ASSERT if the counts read back zero. Found
// empirically: multicore_manager traced
//   ELE_BaseAPI_ReleaseRDC -> BOARD_RequestTRDC -> BOARD_GrantTRDCFullPermissions
//   -> APP_CommonTrdcDACSetting -> TRDC_SetProcessorDomainAssignment -> __assert_func
// with TRDC unmapped (reads 0 -> zero master/domain counts -> assert).
//
// This also retro-justifies the S3MU whitelist entry for RELEASE_RDC (0xC4):
// the oracle answers it SUCCESS because "our TRDC grants full access anyway",
// so the outcome the guest checks for genuinely holds. That is only true with
// this stub present.
//
// HWCFG0 @0xF0 fields verified in the oracle against MIMXRT1189 CMSIS
// PERI_TRDC.h: NDID[4:0], NMSTR[15:8], NMRC[28:24].
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_TRDC : IDoubleWordPeripheral, IBytePeripheral, IKnownSize
    {
        // ncmMask marks this instance's non-CPU (DMA/peripheral) masters so the
        // SoC TRDC setup's Set{,Non}ProcessorDomainAssignment DACFG.NCM asserts
        // pass. Per-instance values from the oracle's trdc_ncm_mask[]:
        //   TRDC1 @0x44270000 -> 0x04 (DMA3)
        //   TRDC2 @0x42460000 -> 0x1C (DAP, CoreSight, DMA4)
        //   TRDC3 @0x42810000 -> 0x1B (USDHC1, USDHC2, Usb, FlexspiFlr)
        public IMXRT1180_TRDC(IMachine machine, uint ncmMask = 0)
        {
            this.ncmMask = ncmMask;
            regs = new uint[Size / 4];
        }

        public long Size => 0x20000;

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
            if(offset == Hwcfg0)
            {
                return Hwcfg0Default;
            }
            // DACFG[] — Domain Assignment Config, a uint8_t array @0x100 (one
            // byte per master): NMDAR[3:0] = #MDA registers, NCM[7] = 1 for a
            // non-CPU master. fsl_trdc's Set{,Non}ProcessorDomainAssignment
            // ASSERT on NCM. Synthesised from ncmMask. [oracle]
            if(offset >= DacfgBase && offset < DacfgBase + 8)
            {
                var startMaster = (uint)(offset - DacfgBase);
                var w = 0u;
                for(var b = 0; b < 4; b++)
                {
                    var m = startMaster + (uint)b;
                    var d = (uint)DacfgNmdar;
                    if(m < 8 && (ncmMask & (1u << (int)m)) != 0)
                    {
                        d |= DacfgNcm;
                    }
                    w |= d << (b * 8);
                }
                return w;
            }
            return regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size || offset < 0 || offset == Hwcfg0)
            {
                return;   // HWCFG0 is read-only
            }
            regs[offset / 4] = value;
        }

        // Secure firmware byte-pokes TRDC; an over-strict window would fault it.
        // Sub-word access is served out of the word handler, as in the oracle.
        public byte ReadByte(long offset)
        {
            return (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8));
        }

        public void WriteByte(long offset, byte value)
        {
            var aligned = offset & ~3L;
            var shift = (int)((offset & 3) * 8);
            var w = ReadDoubleWord(aligned);
            WriteDoubleWord(aligned, (w & ~(0xFFu << shift)) | ((uint)value << shift));
        }

        private readonly uint[] regs;
        private readonly uint ncmMask;

        private const long Hwcfg0 = 0xF0;
        private const long DacfgBase = 0x100;
        private const uint DacfgNcm = 0x80;
        private const uint DacfgNmdar = 0x01;   // nominal: 1 MDA register
        // NDID=16 domains, NMSTR=16 masters, NMBC=2, NMRC=2 blocks. Counts must
        // cover the domain/master indices the SoC TRDC setup uses (domainId up
        // to 0xB). The MBC/MRC block config lives past this window and falls
        // through permissively, so this stub still enforces nothing. [oracle]
        private const uint Hwcfg0Default = 16u | (16u << 8) | (2u << 16) | (2u << 24);
    }
}
